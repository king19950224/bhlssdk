<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import EChart from '../components/EChart.vue'
import { boltApi, deviceApi, runtimeApi } from '../api'
import { onRealtime, realtimeState } from '../api/realtime'

// 实时预紧力波形：紧了上升、松了下降。
// 数据口径与 BoltDetail 的“实时预紧力”一致——客户端按 (echo − RefTime) / K_L 连续换算，
// 不走服务端 GB 30~90% 范围过滤，所以能“看着拧松一路掉到底、拧紧一路涨上去”。
// 驱动：channelsLive 轮询（每通道最新回波，一次拿全部）为主 + SignalR OnMeasurement 即时加速。

interface LiveChan {
  channelNo: number
  channelId: number
  boltId: number
  code: string
  nominalDiam: string
  kL: number
  refTimeNs: number
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
const selected = ref<Set<number>>(new Set()) // 选中显示的 channelNo（默认一颗，可多选）
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

let deviceId: number | null = null
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

// 客户端换算（与 BoltDetail 同口径，便于两页数值一致）
function preloadFromEcho(echoNs: number, kL: number, refTimeNs: number): number {
  return (echoNs - refTimeNs) / kL
}

function findChan(channelNo: number): LiveChan | undefined {
  return channels.value.find((c) => c.channelNo === channelNo)
}

// 应用一条最新测量（声时）：去重（按 ts）后推入滚动序列并更新当前值
function applyMeasurement(channelNo: number, tsIso: string, echoNs: number | null) {
  if (!live.value || echoNs == null || !tsIso) return
  const c = findChan(channelNo)
  if (!c || tsIso === c.lastTs) return
  c.lastTs = tsIso
  const v = preloadFromEcho(Number(echoNs), c.kL, c.refTimeNs)
  c.cur = v
  const t = new Date(tsIso).getTime()
  c.series.push({ t, v })
  const cutoff = Date.now() - BUFFER_MS
  while (c.series.length && c.series[0].t < cutoff) c.series.shift()
  lastUpdate.value = Date.now()
  rev.value++
}

// 拉取标定参数（kL / RefTime / 目标 / 下限），建立或刷新通道清单；保留已有滚动序列
async function loadCalibration(seed: boolean) {
  const items = (await boltApi.list()).items
  // 只保留【当前仍已标定】的通道：以旧列表为缓存复用滚动序列，next 仅收录本次 API 返回的已标定通道，
  // 失标定/解绑的通道自然被剔除（原先把旧通道全部预置进 next,会导致失标定通道残留不消失）
  const prev = new Map<number, LiveChan>(channels.value.map((c) => [c.channelNo, c]))
  const next = new Map<number, LiveChan>()
  for (const b of items) {
    const kL = num(b.kL)
    const refTimeNs = num(b.refTimeNs)
    if (!b.calibrated || kL == null || kL <= 0 || refTimeNs == null) continue // 仅展示已标定通道
    const existing = prev.get(b.channelNo)
    if (existing) {
      existing.kL = kL
      existing.refTimeNs = refTimeNs
      existing.target = num(b.targetPreloadN)
      existing.min = num(b.minPreloadN)
      existing.code = b.code
      existing.boltId = b.id      // 通道可能换绑螺栓：同步 boltId/channelId，避免跳转到旧螺栓
      existing.channelId = b.channelId
      next.set(b.channelNo, existing)
      continue
    }
    const chan: LiveChan = {
      channelNo: b.channelNo,
      channelId: b.channelId,
      boltId: b.id,
      code: b.code,
      nominalDiam: b.nominalDiam,
      kL,
      refTimeNs,
      target: num(b.targetPreloadN),
      min: num(b.minPreloadN),
      series: [],
      lastTs: '',
      cur: num(b.lastPreloadN),
    }
    // 用最后一条已知回波播种首点，避免空图
    if (seed && b.lastEchoTimeNs != null && b.lastTs) {
      chan.cur = preloadFromEcho(Number(b.lastEchoTimeNs), kL, refTimeNs)
      chan.series.push({ t: new Date(b.lastTs).getTime(), v: chan.cur })
      chan.lastTs = b.lastTs
    }
    next.set(b.channelNo, chan)
  }
  channels.value = Array.from(next.values()).sort((a, b) => a.channelNo - b.channelNo)
  rev.value++
}

// channelsLive 轮询：一次拿全部通道最新回波（轻量，且作为 SignalR 断线兜底）
async function poll() {
  if (!live.value || deviceId == null || document.hidden) return
  try {
    const r = await deviceApi.channelsLive(deviceId)
    for (const it of r.items) {
      if (it.echoNs != null && it.lastTs) applyMeasurement(it.channelNo, it.lastTs, it.echoNs)
    }
    error.value = null
  } catch (e) {
    error.value = e instanceof Error ? e.message : '实时数据拉取失败'
  }
}

async function init() {
  loading.value = true
  try {
    // 取主设备 id（channelsLive 需要）
    try {
      const rt = await runtimeApi.devices()
      deviceId = (rt.items.find((d) => d.isPrimary) ?? rt.items[0])?.deviceId ?? null
    } catch {
      /* 无设备运行时也允许只看历史播种点 */
    }
    await loadCalibration(true)
    // 默认只显示一颗（首个有数据的通道），其余由用户点选加入，避免多线拥挤
    if (!selected.value.size && visible.value.length) selected.value = new Set([visible.value[0].channelNo])
    error.value = null
  } catch (e) {
    error.value = e instanceof Error ? e.message : '初始化失败'
  } finally {
    loading.value = false
  }
  startTimers()
  // SignalR 即时加速（按物理通道号匹配；单设备部署足够）。retry 会重入 init，故先退订上一次，避免句柄累积。
  unsubMeas?.()
  unsubMeas = onRealtime('OnMeasurement', (m) => applyMeasurement(m.channel, m.ts, m.echoTimeNs))
}

function startTimers() {
  stopTimers()
  pollTimer = window.setInterval(poll, POLL_MS)
  calibTimer = window.setInterval(() => void loadCalibration(false), 30000)
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
function toggleSelect(channelNo: number) {
  const s = new Set(selected.value)
  if (s.has(channelNo)) s.delete(channelNo)
  else s.add(channelNo)
  selected.value = s
}
function selectAll() {
  selected.value = new Set(visible.value.map((c) => c.channelNo))
}
function clearSel() {
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

// 默认选中第一个有数据的通道；页面打开时全无数据的话，等首个通道上线自动选中
watch(visible, (list) => {
  if (!selected.value.size && list.length) selected.value = new Set([list[0].channelNo])
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

const shown = computed(() => visible.value.filter((c) => selected.value.has(c.channelNo)))
const calibratedCount = computed(() => channels.value.length)
const staleSec = computed(() => (lastUpdate.value ? Math.round((now.value - lastUpdate.value) / 1000) : null))

const option = computed<Record<string, unknown>>(() => {
  void rev.value // 触发依赖
  const cutoff = now.value - windowSec.value * 1000 // 依赖 now：窗口随时间平滑左移,旧点滚出
  const list = shown.value
  const single = list.length === 1 ? list[0] : null

  const series: Record<string, unknown>[] = list.map((c) => {
    const color = PALETTE[channels.value.indexOf(c) % PALETTE.length]
    const raw = c.series.filter((p) => p.t >= cutoff)
    const data = smoothSeries(raw).map((p) => [p.t, toDisplay(p.v, c.target)])
    return {
      name: c.code || `CH${c.channelNo}`,
      type: 'line',
      smooth: true,
      symbol: 'none',
      connectNulls: true,
      data,
      color,
      itemStyle: { color },
      lineStyle: { color, width: single ? 2.4 : 1.6 },
      areaStyle: single ? { color: 'rgba(34,211,238,0.10)' } : undefined,
      emphasis: { focus: 'series' },
      z: 2,
    }
  })

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
  for (const s of series) for (const p of s.data as [number, number | null][]) if (p[1] != null) yVals.push(p[1] as number)
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
      backgroundColor: '#0d1320',
      borderColor: 'rgba(56,189,248,0.3)',
      textStyle: { color: '#e8eef8' },
      valueFormatter: (v: number) => (v == null ? '—' : v.toFixed(unit.value === 'kN' ? 1 : 0) + ' ' + unitLabel.value),
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
          :key="c.channelNo"
          class="chip"
          :class="{ active: selected.has(c.channelNo), dim: !selected.has(c.channelNo) }"
          role="button"
          tabindex="0"
          @click="toggleSelect(c.channelNo)"
          @keydown.enter="toggleSelect(c.channelNo)"
        >
          <div class="chip-top">
            <span class="chip-dot" :style="`background:${chipStatus(c).color}`" />
            <span class="chip-code">{{ c.code || 'CH' + c.channelNo }}</span>
            <span class="chip-ch mono">CH{{ c.channelNo }}</span>
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
      实时值为客户端按 <b class="mono">(回波声时 − 零载基线 RefTime) / K_L</b> 连续换算（与螺栓详情同口径），不经 GB 30~90% 范围过滤，故可完整呈现拧松→拧紧的全程升降；正式读数以螺栓详情 / 报告为准。
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
