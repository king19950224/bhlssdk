<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import EChart from '../components/EChart.vue'
import { boltApi, deviceApi, runtimeApi } from '../api'
import { onRealtime, realtimeState } from '../api/realtime'

// 实时预紧力波形：紧了上升、松了下降。
// 数据口径与 BoltDetail 的“实时预紧力”一致——客户端按热路径同款公式连续换算
// （温补归一 + K_L 截距，见 preloadFromEcho），不走服务端 GB 30~90% 范围过滤，
// 所以能“看着拧松一路掉到底、拧紧一路涨上去”。
// 驱动：channelsLive 轮询（逐设备拿各通道最新回波）为主 + SignalR OnMeasurement 即时加速。

interface LiveChan {
  /** `${deviceId}:${channelNo}`——多设备部署时物理通道号会重复，所有索引/选中都必须带设备维度，
   *  单用 channelNo 会让两台设备的同号通道互相覆盖、曲线混流（B 设备的回波画进 A 螺栓） */
  key: string
  deviceId: number
  channelNo: number
  channelId: number
  boltId: number
  code: string
  nominalDiam: string
  kL: number
  refTimeNs: number
  refTempC: number | null
  kLInterceptNs: number
  tempCoefficientPerC: number
  target: number | null // N
  min: number | null // N
  series: { t: number; v: number }[] // v = 预紧力 N
  lastTs: string
  cur: number | null // 当前预紧力 N
}

const PALETTE = ['#22d3ee', '#a78bfa', '#f59e0b', '#34d399', '#f472b6', '#60a5fa', '#fb923c', '#4ade80', '#e879f9', '#2dd4bf']
const POLL_MS = 2500
const BUFFER_MS = 12 * 60 * 1000 // 缓存上限 12 分钟，超出裁剪
const WINDOWS = [
  { key: '2m', label: '2 分', sec: 120 },
  { key: '5m', label: '5 分', sec: 300 },
  { key: '10m', label: '10 分', sec: 600 },
] as const

const channels = ref<LiveChan[]>([])
const live = ref(true)
const unit = ref<'kN' | 'pct'>('kN')
const windowKey = ref<(typeof WINDOWS)[number]['key']>('5m')
const selected = ref<Set<string>>(new Set()) // 选中显示的通道 key（deviceId:channelNo；默认一颗，可多选）
// 用户主动清空标志：置位后 watch(visible) 不再自动回选（visible 依赖 now 每秒重算,watch 每秒触发）
let userCleared = false
const smooth = ref(true) // 平滑：滑动平均压住声时量化抖动（±1ns≈±2kN）
const loading = ref(true)
const error = ref<string | null>(null)
const rev = ref(0) // 递增触发图表重算
const lastUpdate = ref<number>(0)
const now = ref(Date.now()) // 每秒心跳：Date.now() 非响应式，用它驱动“陈旧度”随时间走动

const MED_WIN = 5 // 中位数窗口：免疫单点声时量化尖刺
const MEAN_WIN = 11 // 均值窗口：压住低 SNR 通道数秒周期的回波识别波动（≈10s @ 多通道轮询）

function median(vals: number[]): number {
  if (!vals.length) return 0
  const s = [...vals].sort((a, b) => a - b)
  const m = Math.floor(s.length / 2)
  return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2
}

// 两级尾部滤波：先 MED_WIN 中位数压住声时量化尖刺，再 MEAN_WIN 均值压低 SNR 通道
// 数秒周期的回波识别随机波动（弱信号通道 ±5~7kN 锯齿的主因）。只用历史点，无未来滞后；
// 代价是拧动响应慢几秒，要即时跟手可切“原始”。
function smoothSeries(pts: { t: number; v: number }[]): { t: number; v: number }[] {
  if (!smooth.value || pts.length < 2) return pts
  const med: { t: number; v: number }[] = []
  for (let i = 0; i < pts.length; i++) {
    const seg: number[] = []
    for (let j = Math.max(0, i - MED_WIN + 1); j <= i; j++) seg.push(pts[j].v)
    med.push({ t: pts[i].t, v: median(seg) })
  }
  const out: { t: number; v: number }[] = []
  for (let i = 0; i < med.length; i++) {
    let s = 0
    let n = 0
    for (let j = Math.max(0, i - MEAN_WIN + 1); j <= i; j++) {
      s += med[j].v
      n++
    }
    out.push({ t: med[i].t, v: s / n })
  }
  return out
}

// 运行时全部设备 id：轮询逐台拉 channelsLive。只轮主设备的话，多设备部署下
// 其余设备的通道没有轮询兜底，SignalR 一断它们的曲线就冻住。
let deviceIds: number[] = []
let pollTimer: number | undefined
let calibTimer: number | undefined
let nowTimer: number | undefined
let unsubMeas: (() => void) | undefined

const num = (v: number | string | null | undefined) => {
  if (v == null || v === '') return null
  const n = Number(v)
  return Number.isFinite(n) ? n : null
}
const windowSec = computed(() => WINDOWS.find((w) => w.key === windowKey.value)!.sec)

// 客户端换算（与 BoltDetail / 采集热路径同口径）：温补归一 + K_L 截距。
//   normEcho = echo / (1 + tc × (当前温度 − RefTemp)); preloadN = (normEcho − RefTime − 截距) / K_L
// 此前缺温补与截距，带截距标定或温度偏离参考温度时本页读数与入库值系统性对不上。
function preloadFromEcho(c: LiveChan, echoNs: number, tempC: number | null): number {
  let norm = echoNs
  if (c.refTempC != null && tempC != null && Number.isFinite(c.tempCoefficientPerC)) {
    const f = 1 + c.tempCoefficientPerC * (tempC - c.refTempC)
    if (f > 0) norm = echoNs / f
  }
  return (norm - c.refTimeNs - c.kLInterceptNs) / c.kL
}

const chanKey = (deviceId: number, channelNo: number) => `${deviceId}:${channelNo}`
function findChan(deviceId: number, channelNo: number): LiveChan | undefined {
  return channels.value.find((c) => c.deviceId === deviceId && c.channelNo === channelNo)
}
// 多设备时通道芯片要能区分是哪台设备的 CH（单设备保持原样不加噪音）
const multiDevice = computed(() => new Set(channels.value.map((c) => c.deviceId)).size > 1)

// 应用一条最新测量（声时）：推入滚动序列并更新当前值。
// 去重按解析后的毫秒时间“只前进”（与 BoltDetail.applyLiveMeasurement 同款）：
// 同一条测量会从 SignalR 推送和 channelsLive 轮询两条路到达，且两边 ISO 字符串格式不同
// （推送带内存 7 位小数，轮询是入库后 6 位微秒），字符串比对挡不住，同一时刻会被塞成两个点
// ——axis 提示框在该时刻就列出同名螺栓两行（平滑窗口位置不同，两行数值还略有差异）。
// 乱序回灌的旧点也一并丢弃，避免曲线时间回跳。
function applyMeasurement(deviceId: number, channelNo: number, tsIso: string, echoNs: number | null, tempC: number | null) {
  if (!live.value || echoNs == null || !tsIso) return
  // 按 (deviceId, channelNo) 精确匹配：SignalR 向所有客户端广播所有设备的测量，
  // 不滤 deviceId 会把 B 设备同号通道的回波换算后画进 A 螺栓的曲线
  const c = findChan(deviceId, channelNo)
  if (!c) return
  const t = new Date(tsIso).getTime()
  const lastT = c.series.length ? c.series[c.series.length - 1].t : 0
  if (!Number.isFinite(t) || t <= lastT) return
  c.lastTs = tsIso
  const v = preloadFromEcho(c, Number(echoNs), tempC != null ? Number(tempC) : null)
  c.cur = v
  c.series.push({ t, v })
  const cutoff = Date.now() - BUFFER_MS
  while (c.series.length && c.series[0].t < cutoff) c.series.shift()
  lastUpdate.value = Date.now()
  rev.value++
}

// 拉取标定参数（kL / RefTime / 截距 / 温补 / 目标 / 下限），建立或刷新通道清单；保留已有滚动序列
async function loadCalibration(seed: boolean) {
  const res = await boltApi.list()
  // 超出 limit 护栏（默认 1000）被截断时必须让用户知道——排序在后的螺栓不在本页,不提示会被当"不存在"
  listTruncated.value = !!res.hasMore
  const items = res.items
  // 只保留【当前仍已标定】的通道（calibrated 含"标定过期"判定——过期通道与未标定一样不出实时数）。
  // 以旧列表为缓存复用滚动序列，next 仅收录本次 API 返回的已标定通道，
  // 失标定/解绑的通道自然被剔除（原先把旧通道全部预置进 next,会导致失标定通道残留不消失）。
  // 键为 deviceId:channelNo——多设备下通道号会撞，单 channelNo 作键会互相覆盖丢螺栓。
  const prev = new Map<string, LiveChan>(channels.value.map((c) => [c.key, c]))
  const next = new Map<string, LiveChan>()
  for (const b of items) {
    const kL = num(b.kL)
    const refTimeNs = num(b.refTimeNs)
    if (!b.calibrated || kL == null || kL <= 0 || refTimeNs == null) continue // 仅展示已标定通道
    const key = chanKey(b.deviceId, b.channelNo)
    const existing = prev.get(key)
    if (existing) {
      existing.kL = kL
      existing.refTimeNs = refTimeNs
      existing.refTempC = num(b.refTempC)
      existing.kLInterceptNs = num(b.kLInterceptNs) ?? 0
      existing.tempCoefficientPerC = num(b.tempCoefficientPerC) ?? 0
      existing.target = num(b.targetPreloadN)
      existing.min = num(b.minPreloadN)
      existing.code = b.code
      existing.boltId = b.id      // 通道可能换绑螺栓：同步 boltId/channelId，避免跳转到旧螺栓
      existing.channelId = b.channelId
      next.set(key, existing)
      continue
    }
    const chan: LiveChan = {
      key,
      deviceId: b.deviceId,
      channelNo: b.channelNo,
      channelId: b.channelId,
      boltId: b.id,
      code: b.code,
      nominalDiam: b.nominalDiam,
      kL,
      refTimeNs,
      refTempC: num(b.refTempC),
      kLInterceptNs: num(b.kLInterceptNs) ?? 0,
      tempCoefficientPerC: num(b.tempCoefficientPerC) ?? 0,
      target: num(b.targetPreloadN),
      min: num(b.minPreloadN),
      series: [],
      lastTs: '',
      cur: num(b.lastPreloadN),
    }
    // 用最后一条已知回波播种首点，避免空图
    if (seed && b.lastEchoTimeNs != null && b.lastTs) {
      chan.cur = preloadFromEcho(chan, Number(b.lastEchoTimeNs), num(b.lastSpTempC))
      chan.series.push({ t: new Date(b.lastTs).getTime(), v: chan.cur })
      chan.lastTs = b.lastTs
    }
    next.set(key, chan)
  }
  channels.value = Array.from(next.values()).sort((a, b) => a.deviceId - b.deviceId || a.channelNo - b.channelNo)
  rev.value++
}

// 螺栓列表被 limit 护栏截断的提示（规模超 1000 颗时排序在后的螺栓不在本页）
const listTruncated = ref(false)

// channelsLive 轮询：逐设备拿各通道最新回波（轻量，且作为 SignalR 断线兜底）
async function poll() {
  if (!live.value || !deviceIds.length || document.hidden) return
  // 快照本次轮询的设备集：deviceIds 是模块级变量，retry→init 会重赋；
  // 若重赋发生在 await 期间且顺序变化，results[i] 与新数组错位会把 A 设备的回波
  // 写进 B 设备的通道——正是组合键重构要防的跨设备串数据
  const ids = deviceIds
  try {
    const results = await Promise.allSettled(ids.map((id) => deviceApi.channelsLive(id)))
    let anyOk = false
    for (let i = 0; i < results.length; i++) {
      const r = results[i]
      if (r.status !== 'fulfilled') continue
      anyOk = true
      for (const it of r.value.items) {
        if (it.echoNs != null && it.lastTs)
          applyMeasurement(ids[i], it.channelNo, it.lastTs, it.echoNs, it.tempC)
      }
    }
    if (anyOk) error.value = null
    else if (results.length) error.value = '实时数据拉取失败'
  } catch (e) {
    error.value = e instanceof Error ? e.message : '实时数据拉取失败'
  }
}

async function init() {
  loading.value = true
  userCleared = false // 重载视为新会话,恢复"无选中自动选一颗"
  try {
    // 取全部运行时设备 id（channelsLive 逐台轮询；多设备时不能只轮主设备）
    try {
      const rt = await runtimeApi.devices()
      deviceIds = rt.items.map((d) => d.deviceId)
    } catch {
      /* 无设备运行时也允许只看历史播种点 */
    }
    await loadCalibration(true)
    // 默认只显示一颗（首个有数据的通道），其余由用户点选加入，避免多线拥挤
    if (!selected.value.size && visible.value.length) selected.value = new Set([visible.value[0].key])
    error.value = null
  } catch (e) {
    error.value = e instanceof Error ? e.message : '初始化失败'
  } finally {
    loading.value = false
  }
  startTimers()
  // SignalR 即时加速：按 (deviceId, channel) 组合匹配——推送是全设备广播，必须过滤设备维度。
  // retry 会重入 init，故先退订上一次，避免句柄累积。
  unsubMeas?.()
  unsubMeas = onRealtime('OnMeasurement', (m) => applyMeasurement(m.deviceId, m.channel, m.ts, m.echoTimeNs, m.spTempC))
}

function startTimers() {
  stopTimers()
  pollTimer = window.setInterval(poll, POLL_MS)
  // 标定刷新失败要落到错误横幅：后端不可用期间否则每 30s 一条 unhandled rejection,页面毫无表示
  calibTimer = window.setInterval(
    () => loadCalibration(false).catch((e) => { error.value = e instanceof Error ? e.message : '标定参数刷新失败' }),
    30000)
  nowTimer = window.setInterval(() => { now.value = Date.now() }, 1000)
  void poll()
}
function stopTimers() {
  if (pollTimer) { clearInterval(pollTimer); pollTimer = undefined }
  if (calibTimer) { clearInterval(calibTimer); calibTimer = undefined }
  if (nowTimer) { clearInterval(nowTimer); nowTimer = undefined }
}

function toggleLive() {
  live.value = !live.value
  if (live.value) void poll()
}
function toggleSelect(key: string) {
  const s = new Set(selected.value)
  if (s.has(key)) s.delete(key)
  else s.add(key)
  selected.value = s
  // 逐个取消到 0 也是主动清空;重新选中任意通道则恢复"无选中时自动选一颗"的默认行为
  userCleared = s.size === 0
}
function selectAll() {
  userCleared = false
  selected.value = new Set(visible.value.map((c) => c.key))
}
function clearSel() {
  // 记住"是用户主动清空的"：不打标志的话清空后 1 秒内第一颗通道又被自动回选,"清空"形同虚设
  userCleared = true
  selected.value = new Set()
}
async function retry() {
  await init()
}

onMounted(init)
onUnmounted(() => {
  stopTimers()
  unsubMeas?.()
})

// ---- 信号新鲜度：断数据超过 STALE_MS 的通道不得拿旧值装"正常" ----
// 阈值与后端 SignalHealthService 的 120s 无回波判信号丢失同口径。
const STALE_MS = 120_000
function lastDataMs(c: LiveChan): number | null {
  return c.lastTs ? new Date(c.lastTs).getTime() : null
}
function isStale(c: LiveChan): boolean {
  const t = lastDataMs(c)
  return t == null || now.value - t > STALE_MS
}
// 无数据不上屏：断数据的通道整体从芯片与曲线中隐藏（选中状态保留），恢复上报自动回来
const visible = computed(() => channels.value.filter((c) => !isStale(c)))
const onlineCount = computed(() => visible.value.length)

// 默认选中第一个有数据的通道；页面打开时全无数据的话，等首个通道上线自动选中。
// userCleared 置位期间不回选,否则"未选择通道"空态永远不可达（见 clearSel/toggleSelect）。
watch(visible, (list) => {
  if (userCleared) return
  if (!selected.value.size && list.length) selected.value = new Set([list[0].key])
})

// 展示用值：kN 或 保持率%（相对自身目标）
function toDisplay(valN: number | null, target: number | null): number | null {
  if (valN == null) return null
  if (unit.value === 'pct') return target && target > 0 ? (valN / target) * 100 : null
  return valN / 1000
}
const unitLabel = computed(() => (unit.value === 'kN' ? 'kN' : '%'))

// 当前值卡片：方向（紧/松）来自最近 ~15s 的变化
function chipDelta(c: LiveChan): number | null {
  const s = c.series
  if (s.length < 2) return null
  const last = s[s.length - 1]
  const past = [...s].reverse().find((p) => last.t - p.t >= 12000) ?? s[0]
  return last.v - past.v
}
// 卡片当前值用平滑后的读数，避免 ±量化抖动让大数字乱跳
function curOf(c: LiveChan): number | null {
  if (!c.series.length) return c.cur
  const s = smoothSeries(c.series)
  return s.length ? s[s.length - 1].v : c.cur
}
function chipStatus(c: LiveChan): { color: string; label: string } {
  // 信号断了就是断了：旧值只作参考显示，状态一律"无信号"（与告警中心 signal_lost 口径一致）
  if (isStale(c)) return { color: '#3b4659', label: '无信号' }
  const cur = curOf(c)
  if (cur == null) return { color: '#3b4659', label: '无数据' }
  // 标定基线不匹配时客户端连续换算会得出物理不可能的量级（如 -9000kN）：
  // 标为“数据异常”提示查标定，不能当有效读数打"低于下限"标签
  if (c.target != null && c.target > 0 && (cur < -c.target || cur > c.target * 5))
    return { color: '#f43f5e', label: '数据异常·查标定' }
  if (c.min != null && cur < c.min) return { color: '#f43f5e', label: '低于下限' }
  if (c.min != null && cur < c.min * 1.1) return { color: '#f59e0b', label: '接近下限' }
  // 偏高（>110% 目标）：与总览/告警中心 TriggerHighRatio 同口径,不能显示"正常"
  if (c.target != null && c.target > 0 && cur > c.target * 1.1) return { color: '#a78bfa', label: '偏高' }
  return { color: '#10b981', label: '正常' }
}
function fmtVal(valN: number | null, target: number | null): string {
  const d = toDisplay(valN, target)
  if (d == null) return '—'
  return unit.value === 'kN' ? d.toFixed(1) : d.toFixed(0)
}

const shown = computed(() => visible.value.filter((c) => selected.value.has(c.key)))
const calibratedCount = computed(() => channels.value.length)
const staleSec = computed(() => (lastUpdate.value ? Math.round((now.value - lastUpdate.value) / 1000) : null))

// 提示框取值容差：悬停时刻与通道最近数据点相差超过该值，视为"该时刻此通道无数据"，行内显示 —
const TOOLTIP_NEAR_MS = 15_000
const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
function fmtAxisTime(t: number): string {
  const d = new Date(t)
  const p2 = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p2(d.getMonth() + 1)}-${p2(d.getDate())} ${p2(d.getHours())}:${p2(d.getMinutes())}:${p2(d.getSeconds())}`
}

const option = computed<Record<string, unknown>>(() => {
  void rev.value // 触发依赖
  const cutoff = now.value - windowSec.value * 1000 // 依赖 now：窗口随时间平滑左移,旧点滚出
  const list = shown.value
  const single = list.length === 1 ? list[0] : null

  // 曲线数据先落到中间结构：series 渲染用它，自定义提示框也用它按悬停时刻就近取值
  const plotted = list.map((c) => {
    const color = PALETTE[channels.value.indexOf(c) % PALETTE.length]
    const raw = c.series.filter((p) => p.t >= cutoff)
    const data = smoothSeries(raw).map((p) => [p.t, toDisplay(p.v, c.target)] as [number, number | null])
    // 多设备时图例/提示框带设备前缀，否则两台设备的同名默认编号（如"通道1-1号螺栓"）无法区分
    const name = (c.code || `CH${c.channelNo}`) + (multiDevice.value ? `（设备${c.deviceId}）` : '')
    return { name, color, data }
  })

  const series: Record<string, unknown>[] = plotted.map((p) => ({
    name: p.name,
    type: 'line',
    smooth: true,
    symbol: 'none',
    connectNulls: true,
    data: p.data,
    color: p.color,
    itemStyle: { color: p.color },
    lineStyle: { color: p.color, width: single ? 2.4 : 1.6 },
    areaStyle: single ? { color: 'rgba(34,211,238,0.10)' } : undefined,
    emphasis: { focus: 'series' },
    z: 2,
  }))

  // 参考线：单通道聚焦时画自身目标/下限；全部视图下若各通道目标一致则画共线
  const tgts = list.map((c) => c.target).filter((x): x is number => x != null)
  const mins = list.map((c) => c.min).filter((x): x is number => x != null)
  const allSameTarget = tgts.length === list.length && tgts.every((t) => t === tgts[0])
  const allSameMin = mins.length === list.length && mins.every((m) => m === mins[0])
  const refBase = single ? single.target : tgts[0] ?? null // pct 归一化分母
  const targetN = (single ? single.target : tgts[0] ?? null)
  const minN = (single ? single.min : mins[0] ?? null)
  const targetY = (single || (allSameTarget && tgts.length)) && targetN != null ? toDisplay(targetN, refBase ?? targetN) : null
  const minY = (single || (allSameMin && mins.length)) && minN != null ? toDisplay(minN, refBase ?? (targetN ?? minN)) : null
  const markLines: Record<string, unknown>[] = []
  if (targetY != null) markLines.push({ yAxis: targetY, lineStyle: { color: '#10b981', type: 'dashed', width: 1 }, label: { formatter: '目标', color: '#10b981', fontSize: 10, position: 'insideEndTop' } })
  if (minY != null) markLines.push({ yAxis: minY, lineStyle: { color: '#f43f5e', type: 'dashed', width: 1 }, label: { formatter: '下限', color: '#f43f5e', fontSize: 10, position: 'insideEndBottom' } })
  if (series.length && markLines.length) (series[series.length - 1] as Record<string, unknown>).markLine = { silent: true, symbol: 'none', data: markLines }

  // y 轴范围：显式覆盖数据 + 参考线（否则 scale 紧贴数据会把目标/下限挤出视野，单通道时尤甚）
  const yVals: number[] = []
  for (const p of plotted) for (const pt of p.data) if (pt[1] != null) yVals.push(pt[1])
  if (targetY != null) yVals.push(targetY)
  if (minY != null) yVals.push(minY)
  let yMin: number | undefined
  let yMax: number | undefined
  if (yVals.length) {
    const lo = Math.min(...yVals)
    const hi = Math.max(...yVals)
    const pad = (hi - lo) * 0.12 || (unit.value === 'kN' ? 2 : 5)
    // 量化到固定栅格：每次刷新只要数据没越过栅格，轴范围就不变，避免参考线/网格每帧微动闪烁
    const step = unit.value === 'kN' ? 5 : 10
    yMin = Math.floor((lo - pad) / step) * step
    yMax = Math.ceil((hi + pad) / step) * step
  }

  return {
    grid: { left: 58, right: 18, top: 16, bottom: list.length > 1 ? 46 : 26 },
    legend: list.length > 1
      ? { type: 'scroll', bottom: 0, textStyle: { color: '#9fb0c8', fontSize: 11 }, inactiveColor: '#3b4659', pageIconColor: '#61748f', pageTextStyle: { color: '#9fb0c8' } }
      : undefined,
    tooltip: {
      trigger: 'axis',
      confine: true,
      backgroundColor: '#0d1320',
      borderColor: 'rgba(56,189,248,0.3)',
      textStyle: { color: '#e8eef8' },
      // 各通道测量时刻天然不对齐（设备按通道轮巡，时间戳彼此错开），原生 axis 提示框只列
      // "恰好命中吸附时刻"的那条序列——多选时悬停看着只剩一颗螺栓。改成自定义渲染：
      // 以悬停时刻为基准，每条已选曲线各自取最近的点，把所有通道一次列全
      formatter: (params: unknown) => {
        const first = (Array.isArray(params) ? params[0] : params) as { axisValue?: number | string } | undefined
        const t = Number(first?.axisValue)
        if (!Number.isFinite(t)) return ''
        const rows = plotted.map((p) => {
          let bestV: number | null = null
          let bestD = Infinity
          for (const pt of p.data) {
            const d = Math.abs(pt[0] - t)
            if (d < bestD) { bestD = d; bestV = pt[1] }
          }
          const val = bestD <= TOOLTIP_NEAR_MS && bestV != null
            ? bestV.toFixed(unit.value === 'kN' ? 1 : 0) + ' ' + unitLabel.value
            : '—'
          return '<div style="display:flex;align-items:center;gap:8px;line-height:1.9">'
            + `<span style="width:9px;height:9px;border-radius:50%;background:${p.color};flex:none"></span>`
            + `<span style="margin-right:16px">${esc(p.name)}</span>`
            + `<b style="margin-left:auto">${val}</b></div>`
        })
        return `<div style="color:#9fb0c8;font-size:12px;margin-bottom:2px">${fmtAxisTime(t)}</div>` + rows.join('')
      },
    },
    xAxis: { type: 'time', axisLine: { lineStyle: { color: 'rgba(148,163,184,0.2)' } }, axisLabel: { color: '#61748f', fontSize: 11 }, splitLine: { show: false } },
    yAxis: {
      type: 'value',
      ...(yMin != null ? { min: yMin, max: yMax } : { scale: true }),
      name: unit.value === 'kN' ? '预紧力 kN' : '保持率 %',
      nameTextStyle: { color: '#61748f', fontSize: 11 },
      axisLabel: { color: '#61748f', fontSize: 11, formatter: unit.value === 'kN' ? '{value}' : '{value}%' },
      splitLine: { lineStyle: { color: 'rgba(148,163,184,0.08)' } },
    },
    series,
    // 全图关动画：图表用 notMerge 全量重绘，开动画会让目标/下限参考线每次刷新都重放入场动画 → 闪烁。
    // 关掉后每次刷新瞬时重绘到最终态（曲线仍是平滑样条，只是不再淡入/生长），稳定不闪。
    animation: false,
  }
})

// 有 1 个点就开始画（刚接入的通道等第 2 个点期间不再显示空态"正在采集"）
const hasData = computed(() => shown.value.some((c) => c.series.length >= 1))
</script>

<template>
  <div class="lp rise" v-loading="loading">
    <!-- 控制条 -->
    <div class="panel panel--glow lp-hd">
      <div class="hd-left">
        <span class="panel-title"><i class="hd-mark" />实时预紧力波形 <span v-if="live && realtimeState !== 'disconnected'" class="live-dot" /></span>
        <span class="hd-sub">紧了上升 · 松了下降 · 在线 {{ onlineCount }}/{{ calibratedCount }} 通道<template v-if="staleSec != null"> · 最近数据 {{ staleSec }} 秒前</template></span>
      </div>
      <div class="hd-ctrls">
        <div class="seg">
          <span class="seg-btn" :class="{ active: unit === 'kN' }" @click="unit = 'kN'">kN</span>
          <span class="seg-btn" :class="{ active: unit === 'pct' }" @click="unit = 'pct'">保持率%</span>
        </div>
        <div class="seg">
          <span v-for="w in WINDOWS" :key="w.key" class="seg-btn" :class="{ active: windowKey === w.key }" @click="windowKey = w.key">{{ w.label }}</span>
        </div>
        <span class="ctrl" :class="{ active: smooth }" role="button" tabindex="0" @click="smooth = !smooth" @keydown.enter="smooth = !smooth" title="滑动平均，压住声时量化抖动">
          <el-icon><MagicStick /></el-icon> {{ smooth ? '平滑' : '原始' }}
        </span>
        <span class="ctrl" :class="{ active: live }" role="button" tabindex="0" @click="toggleLive" @keydown.enter="toggleLive">
          <el-icon><component :is="live ? 'VideoPause' : 'VideoPlay'" /></el-icon> {{ live ? '实时' : '已暂停' }}
        </span>
      </div>
    </div>

    <!-- 错误横幅 -->
    <div v-if="error" class="lp-error">
      <el-icon><WarningFilled /></el-icon>
      <span>{{ error }}</span>
      <el-button size="small" text @click="retry">重试</el-button>
    </div>

    <!-- 实时断开横幅：副标题小字不够——值班看波形页时长连接断了必须一眼看见（轮询仍在兜底） -->
    <div v-if="live && realtimeState === 'disconnected'" class="lp-error">
      <el-icon><WarningFilled /></el-icon>
      <span>实时通道已断开，正在自动重连；当前曲线依靠轮询更新，可能滞后数秒</span>
    </div>

    <!-- 螺栓清单被 limit 护栏截断：排序在后的螺栓不在本页，必须显式提示而不是静默缺通道 -->
    <div v-if="listTruncated" class="lp-error">
      <el-icon><WarningFilled /></el-icon>
      <span>螺栓数量超出单次加载上限（1000），本页仅显示前 1000 颗对应的通道，未显示的螺栓请从资产监测按部位进入</span>
    </div>

    <!-- 当前值卡片：点选要显示的螺栓（默认一颗，可多选）。无数据的通道自动隐藏，恢复上报自动回来 -->
    <div class="chips-wrap" v-if="visible.length">
      <div class="chips-bar">
        <span class="chips-hint">点选要显示的螺栓 · 已选 {{ shown.length }}/{{ visible.length }} · 无数据的通道自动隐藏</span>
        <div class="chips-acts">
          <span class="mini" role="button" tabindex="0" @click="selectAll" @keydown.enter="selectAll">全选</span>
          <span class="mini" role="button" tabindex="0" @click="clearSel" @keydown.enter="clearSel">清空</span>
        </div>
      </div>
      <div class="chips">
        <div
          v-for="c in visible"
          :key="c.key"
          class="chip"
          :class="{ active: selected.has(c.key), dim: !selected.has(c.key) }"
          role="button"
          tabindex="0"
          @click="toggleSelect(c.key)"
          @keydown.enter="toggleSelect(c.key)"
        >
          <div class="chip-top">
            <span class="chip-dot" :style="`background:${chipStatus(c).color}`" />
            <span class="chip-code">{{ c.code || 'CH' + c.channelNo }}</span>
            <span class="chip-ch mono">{{ multiDevice ? `D${c.deviceId}·` : '' }}CH{{ c.channelNo }}</span>
          </div>
          <div class="chip-val">
            <b>{{ fmtVal(curOf(c), c.target) }}</b><i>{{ unitLabel }}</i>
            <span
              v-if="chipDelta(c) != null && Math.abs(chipDelta(c) as number) >= 200"
              class="chip-arrow"
              :class="(chipDelta(c) as number) > 0 ? 'up' : 'down'"
            >{{ (chipDelta(c) as number) > 0 ? '▲' : '▼' }}</span>
          </div>
          <div class="chip-sub">
            {{ chipStatus(c).label }} · 目标 {{ c.target != null ? (c.target / 1000).toFixed(1) + 'kN' : '—' }}
          </div>
        </div>
      </div>
    </div>

    <!-- 主波形 -->
    <div class="panel panel--glow chart-wrap">
      <div class="cw-hd">
        <span class="panel-title"><i class="hd-mark" />
          {{ shown.length === 0 ? '未选择通道' : shown.length === 1 ? (shown[0].code || 'CH' + shown[0].channelNo) + ' · 单通道' : shown.length + ' 通道' }}
        </span>
        <span class="hd-sub">{{ smooth ? '平滑' : '原始' }} · {{ WINDOWS.find((w) => w.key === windowKey)?.label }}窗</span>
      </div>
      <EChart v-if="hasData" :option="option" height="440px" />
      <div v-else-if="!loading" class="cw-empty">
        <template v-if="!channels.length">暂无已标定通道——请先在标定中心 / 螺栓详情完成标定</template>
        <template v-else-if="!visible.length">当前无在测数据——插上探头、通道有数据后会自动出现</template>
        <template v-else-if="!shown.length">点上方卡片选择要显示的螺栓</template>
        <template v-else>正在采集…拧松 / 拧紧螺栓即可看到曲线实时升降</template>
      </div>
    </div>

    <div class="lp-foot">
      <el-icon><InfoFilled /></el-icon>
      实时值为客户端按 <b class="mono">(温补归一声时 − 基线 RefTime − K_L 截距) / K_L</b> 连续换算（与螺栓详情、采集入库同一套公式），不经 GB 30~90% 范围过滤，故可完整呈现拧松→拧紧的全程升降；正式读数以螺栓详情 / 报告为准。
    </div>
  </div>
</template>

<style scoped>
.lp { display: flex; flex-direction: column; gap: 16px; }

.lp-hd { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 16px 20px; flex-wrap: wrap; }
.hd-left { display: flex; flex-direction: column; gap: 4px; }
.hd-ctrls { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.seg { display: inline-flex; gap: 2px; padding: 3px; border: 1px solid var(--hairline); border-radius: 9px; }
.seg-btn { font-size: 12px; padding: 4px 11px; border-radius: 6px; cursor: pointer; color: var(--text-3); }
.seg-btn:hover { color: var(--text-1); }
.seg-btn.active { color: var(--accent); background: rgba(34, 211, 238, 0.1); }
.ctrl { display: inline-flex; align-items: center; gap: 5px; font-size: 12px; padding: 6px 12px; border-radius: 9px; cursor: pointer; color: var(--text-3); border: 1px solid var(--hairline); }
.ctrl.active { color: #f43f5e; border-color: rgba(244, 63, 94, 0.4); background: rgba(244, 63, 94, 0.08); }

.live-dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; background: #f43f5e; margin-left: 8px; vertical-align: middle; animation: livepulse 1.4s ease-in-out infinite; }
@keyframes livepulse { 0%, 100% { opacity: 1; box-shadow: 0 0 0 0 rgba(244, 63, 94, 0.5); } 50% { opacity: 0.5; box-shadow: 0 0 0 5px rgba(244, 63, 94, 0); } }

.lp-error { display: flex; align-items: center; gap: 10px; padding: 11px 16px; border-radius: var(--radius-sm); background: rgba(244, 63, 94, 0.08); border: 1px solid rgba(244, 63, 94, 0.35); color: var(--crit); font-size: 13px; }
.lp-error .el-icon { font-size: 16px; }

.chips-wrap { display: flex; flex-direction: column; gap: 10px; }
.chips-bar { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.chips-hint { font-size: 12px; color: var(--text-3); }
.chips-acts { display: flex; gap: 6px; }
.mini { font-size: 12px; padding: 3px 10px; border-radius: 7px; cursor: pointer; color: var(--text-3); border: 1px solid var(--hairline); }
.mini:hover { color: var(--accent); border-color: var(--panel-border-strong); }
.chips { display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); gap: 10px; }
.chip { padding: 12px 14px; border: 1px solid var(--panel-border); border-radius: 12px; background: var(--panel); cursor: pointer; transition: border-color 0.16s, transform 0.16s, opacity 0.16s; }
.chip:hover { border-color: var(--panel-border-strong); transform: translateY(-1px); }
.chip.active { border-color: var(--accent); box-shadow: inset 0 0 0 1px var(--panel-border-strong); }
.chip.dim { opacity: 0.5; }
.chip-top { display: flex; align-items: center; gap: 7px; }
.chip-dot { width: 9px; height: 9px; border-radius: 50%; flex-shrink: 0; }
.chip-code { font-size: 13px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.chip-ch { font-size: 11px; color: var(--text-3); margin-left: auto; }
.chip-val { display: flex; align-items: baseline; gap: 3px; margin: 6px 0 2px; }
.chip-val b { font-size: 26px; font-weight: 800; letter-spacing: -0.5px; line-height: 1; }
.chip-val i { font-size: 12px; color: var(--text-3); font-style: normal; }
.chip-arrow { font-size: 13px; margin-left: 4px; }
.chip-arrow.up { color: #10b981; }
.chip-arrow.down { color: #f43f5e; }
.chip-sub { font-size: 11px; color: var(--text-3); }

.chart-wrap { padding: 14px 18px 16px; }
.cw-hd { display: flex; align-items: center; justify-content: space-between; padding: 0 2px 10px; }
.cw-empty { text-align: center; color: var(--text-3); padding: 60px 20px; font-size: 13px; }

.lp-foot { display: flex; align-items: center; gap: 8px; font-size: 12px; color: var(--text-3); line-height: 1.6; padding: 0 4px; }
.lp-foot b { color: var(--text-2); font-weight: 600; }
.lp-foot .el-icon { flex-shrink: 0; }
</style>
