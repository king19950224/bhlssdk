<script setup lang="ts">
// 页面:螺栓详情——实时预紧力与趋势、A-Scan 波形、标定/告警/审计记录、预测窗口与参数设置。
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import EChart from '../components/EChart.vue'
import PreloadScale from '../components/PreloadScale.vue'
import CalibrationWizard from '../components/CalibrationWizard.vue'
import { computePreloadHealth, pct, signedN, fmtAgo, STATUS_META } from '../utils/preload'
import { useNow } from '../utils/useNow'
import { analyzeAscan } from '../utils/ascan'
import { boltApi, alarmApi, auditApi, waveApi, configChangeApi, deviceApi, calibrationApi, type BoltListItem, type TrendPoint, type CalibRecord, type WaveFile, type WaveFeature, type BoltChannelConfig, type BoltPrediction } from '../api'
import { ApiError } from '../api/client'
import { onRealtime } from '../api/realtime'
import { useAuth } from '../stores/auth'

const route = useRoute()
const router = useRouter()
const auth = useAuth()
// let 而非 const：Vue Router 在 /bolts/1 → /bolts/2 时复用组件实例，路由参数变化要重置全部状态并重新加载
let boltId = Number(route.params.boltId)

// 实时波形 + 松紧状态
const live = ref(true)
let liveTimer: number | undefined
let unsubMeas: (() => void) | undefined
const liveEchoNs = ref<number | null>(null)
const liveTempC = ref<number | null>(null)
const baselining = ref(false)

// 实时预紧力滚动序列（拧松/拧紧肉眼可见升降）；livePreloadN/liveRatio 在 tightness 下方按 echo 连续计算
const liveSeries = ref<{ t: number; v: number }[]>([])
// 实时最新测量时刻（SignalR/轮询驱动）：让头部 KPI 与陈旧度判定随实时数据更新,而非停在页面加载那一刻
const liveTs = ref<string | null>(null)

const MEAS_MODE: Record<number, string> = { 0: '自动峰值', 1: '位置法(RefTime)', 2: '阈值法' }
const TC_MODE: Record<number, string> = { 0: '不补偿', 1: '材料补偿', 2: '自定义系数' }
// 设备测量状态码（SDK ChannelResult 口径）：0 正常,负值为各类信号异常
const STATUS_CODE: Record<number, string> = {
  0: '正常', [-1]: '信号过小', [-2]: '阈值异常', [-3]: '信号溢出', [-4]: '相似度低', [-5]: '未知异常', [-6]: '信噪比低',
}

const bolt = ref<BoltListItem | null>(null)
const trend = ref<TrendPoint[]>([])
const measurements = ref<{ ts: string; echoTimeNs: number | null; spTempC: number | null; statusCode: number; preloadN: number | null; preloadRatio: number | null }[]>([])
const calibrations = ref<CalibRecord[]>([])
const events = ref<{ at: string; kind: string; title: string; note: string; color: string }[]>([])
const rangeKey = ref<'1h' | '24h' | '7d'>('24h')
const showTemp = ref(false)
const loading = ref(true)

const wave = ref<WaveFile | null>(null)
const waveFeat = ref<WaveFeature | null>(null)

const chCfg = ref<BoltChannelConfig | null>(null)
const adjust = reactive({ visible: false, saving: false })
const adjForm = reactive({ gain: null as number | null, probeFreqMhz: null as number | null, echoWinNs: null as number | null })

const settings = reactive({ visible: false, saving: false })
// 引导式标定向导(默认入口);标定完成后重载档案与配置
const wizardOpen = ref(false)
async function onWizardDone() {
  try {
    bolt.value = await boltApi.get(boltId)
    chCfg.value = await boltApi.channelConfig(boltId)
    if (bolt.value) calibrations.value = (await boltApi.calibrations(bolt.value.channelId)).items
  } catch { /* 刷新尽力 */ }
}
const form = reactive({
  targetPreloadN: null as number | null,
  minPreloadN: null as number | null,
  positionLabel: '' as string,
  installTorqueNm: null as number | null,
  material: '' as string,
  elasticModulusGpa: null as number | null,
  soundVelocityMs: null as number | null,
  effectiveLengthMm: null as number | null,
  crossSectionMm2: null as number | null,
})

// 状态元数据用全局共享表（含 high/偏高——本地副本曾缺 high，凡偏高螺栓详情页渲染即崩、卡死在加载遮罩）
const STATUS = STATUS_META
// 标定方法枚举的中文映射（与标定中心/标定弹窗保持一致，未知值回退原值）
const METHOD_LABEL: Record<string, string> = {
  load_calibrated: '正式载荷标定', gb_single_wave: 'GB 单波法', gb_bi_wave: 'GB 双波法',
  torque_fit: '扭矩拟合', torque_estimate: '扭矩估算', quick: '快速标定', sim_baseline: '初始基线',
}
// 通道标定生命周期状态：仅在“过期/停用”时覆盖标签（避免把仍有 K_L、在出数的临时标定误标为未标定），
// 其余回退到 calibrated 布尔。让“标定过期”不再笼统显示为“已标定”，与总览“未标定/过期”桶口径一致。
const STATE_LABEL: Record<string, string> = { expired: '标定过期', disabled: '已停用' }
// 告警类型中文映射（与告警中心一致；履历时间线用）
const ALARM_TYPE_LABEL: Record<string, string> = {
  preload_low: '预紧力偏低', preload_loss: '预紧力脱落', preload_high: '预紧力偏高',
  signal_lost: '信号丢失', no_data: '数据中断',
}
// 趋势时段 Tab 的中文标签
const RANGE_LABEL: Record<string, string> = { '1h': '1 小时', '24h': '24 小时', '7d': '7 天' }
// 通道配置来源枚举的中文映射
const CONFIG_SOURCE: Record<string, string> = {
  device_readback: '设备读回', config_change: '参数审批下发', database: '数据库配置', manual: '人工录入',
}
const num = (v: number | string | null | undefined) => {
  if (v == null) return null
  const n = Number(v)
  return Number.isFinite(n) ? n : null
}

// 让 stale/nodata 判定与“N 分钟前”随时间刷新（离线后不再卡在最后一次加载的判定）
const now = useNow(30000)
// 展示值：实时链路有新读数时用实时值,否则用加载时的最后已知值——
// 修掉"头部 KPI 停在加载瞬间、下方实时区块却在跳"的同页两套数值问题。
// 未标定通道不显示预紧力数字(历史回填值会与"未标定"标签打架),温度不受标定影响可照常显示。
const effLastTs = computed(() => liveTs.value ?? bolt.value?.lastTs ?? null)
const displayPreloadN = computed(() => (bolt.value?.calibrated ? livePreloadN.value ?? num(bolt.value?.lastPreloadN) : null))
const displayRatio = computed(() => (bolt.value?.calibrated ? liveRatio.value ?? num(bolt.value?.lastRatio) : null))
const displayTempC = computed(() => liveTempC.value ?? num(bolt.value?.lastSpTempC))
// 统一的"状态 + 变化程度"模型（与服务端 bolt_status 口径一致）
const health = computed(() =>
  computePreloadHealth({
    calibrated: bolt.value?.calibrated,
    lastTs: effLastTs.value,
    current: displayPreloadN.value,
    target: bolt.value?.targetPreloadN ?? null,
    min: bolt.value?.minPreloadN ?? null,
  }, now.value),
)
const status = computed(() => health.value.status)
// 数据陈旧（nodata = 无有效预紧力或超过 10 分钟无新测量）时：头部数值标注为“最后已知”，并抑制趋势预测（陈旧/稀疏样本外推不可信）
const isStale = computed(() => status.value === 'nodata')
const liveLabel = computed(() => (isStale.value ? '最后已知' : '当前'))
// 区分两类 nodata：设备失联（>10min 无测量）vs 信号丢失（有测量但无有效预紧力读数），文案分别表述
const offlineStale = computed(() => {
  const t = effLastTs.value
  return !t || now.value - new Date(t).getTime() > 10 * 60000
})

const marginStyle = computed(() => {
  const m = health.value.safetyMargin
  if (m == null) return ''
  return `color:${m < 0.1 ? '#f43f5e' : m < 0.3 ? '#f59e0b' : '#10b981'}`
})
const lossText = computed(() => {
  const l = health.value.lossN
  if (l == null) return '—'
  return l >= 0 ? `${fmtN(l)} N` : `超目标 ${fmtN(-l)} N`
})

// 到限预测：统一走服务端权威计算（固定窗口线性回归 + R²），与总览口径一致
const pred = ref<BoltPrediction | null>(null)
const prediction = computed(() => {
  const p = pred.value
  if (!p) return null
  return { trend: p.trend, perDay: p.ratePerDay ?? 0, days: p.daysToLimit, r2: p.r2, windowDays: p.windowDays, samples: p.sampleCount }
})

let trendSeq = 0
async function loadTrend() {
  const hours = rangeKey.value === '1h' ? 1 : rangeKey.value === '24h' ? 24 : 168
  const since = new Date(Date.now() - hours * 3600 * 1000).toISOString()
  const seq = ++trendSeq
  const items = (await boltApi.preloadTrend(boltId, `?bucket=auto&since=${since}`)).items
  // 快速切换 1h/24h/7d 时丢弃过期响应，避免旧时段数据覆盖新选择
  if (seq === trendSeq) trend.value = items
}

async function loadWave() {
  try {
    const wf = (await waveApi.featuresByBolt(boltId, 1)).items[0]
    if (wf) {
      waveFeat.value = wf
      wave.value = await waveApi.file(wf.waveUrl)
    }
  } catch {
    /* 该通道暂无波形 */
  }
}

// 应用一条最新测量（声时/温度）→ 更新实时显示与滚动序列；SignalR 推送与轮询兜底共用。
// 按毫秒时间戳做“只前进”去重：SignalR 队列(DropOldest)与 4s 轮询并存时可能乱序送达，
// 或同一时刻的 ISO 字符串格式不同——字符串不等≠新数据，旧点回灌会让曲线出现时间回跳。
let lastLiveMs = 0
function applyLiveMeasurement(tsIso: string, echoNs: number | null, tempC: number | null) {
  if (!tsIso) return
  const ms = new Date(tsIso).getTime()
  if (!Number.isFinite(ms) || ms <= lastLiveMs) return
  lastLiveMs = ms
  if (echoNs != null && Number.isFinite(Number(echoNs))) {
    liveEchoNs.value = Number(echoNs)
    liveTempC.value = tempC != null ? Number(tempC) : liveTempC.value
  }
  liveTs.value = tsIso
  if (livePreloadN.value != null) {
    liveSeries.value.push({ t: ms, v: livePreloadN.value })
    if (liveSeries.value.length > 90) liveSeries.value.shift()
  }
}

// 波形 + 兜底刷新：A-Scan 波形不走 SignalR（推送不含波形），仍需周期性拉取；
// 同时拉一条最新测量作为 SignalR 断线时的兜底（echo/温度由 applyLiveMeasurement 去重合并）。
async function loadLive() {
  try {
    const m = (await boltApi.measurements(boltId, 1)).items[0]
    if (m) applyLiveMeasurement(m.ts, m.echoTimeNs, m.spTempC)
  } catch {
    /* ignore */
  }
  await loadWave()
}
function startLive() {
  stopLive()
  if (!live.value) return
  // SignalR 实时驱动 echo/温度；轮询降到 4s 仅用于波形刷新与断线兜底
  liveTimer = window.setInterval(loadLive, 4000)
}
function stopLive() {
  if (liveTimer) {
    clearInterval(liveTimer)
    liveTimer = undefined
  }
}
function toggleLive() {
  live.value = !live.value
  startLive()
}

// 松紧状态：当前回波声时相对基线(RefTime)的偏移。回波偏晚(ΔTOF+)=偏紧(预紧↑),偏早(ΔTOF−)=偏松(松动方向)
const tightness = computed(() => {
  const refNs = chCfg.value?.refTimeNs != null ? Number(chCfg.value.refTimeNs) : null
  const echo = liveEchoNs.value
  if (refNs == null || echo == null) return null
  const dt = echo - refNs // ns
  const kL = chCfg.value?.kL != null ? Number(chCfg.value.kL) : null
  const dF = kL && kL > 0 ? dt / kL : null // N（已标定才有）
  let band: 'tight' | 'loose' | 'base'
  let label: string
  let color: string
  if (dt >= 8) { band = 'tight'; label = '偏紧'; color = '#10b981' }
  else if (dt <= -8) { band = 'loose'; label = '偏松'; color = '#f43f5e' }
  else { band = 'base'; label = '基线附近'; color = '#61748f' }
  return { dt, dF, band, label, color }
})

// 实时预紧力（客户端连续计算 = (echo−零载基线)/K_L，不走 GB 30~90% 范围过滤）。
// 服务端 preload_n 在 GB 适用范围外会置空（防欠张紧值被当真),适合“看着拧松一路掉到底”的实时视图。
const livePreloadN = computed<number | null>(() => {
  const refNs = chCfg.value?.refTimeNs != null ? Number(chCfg.value.refTimeNs) : null
  const kL = chCfg.value?.kL != null ? Number(chCfg.value.kL) : null
  const echo = liveEchoNs.value
  if (refNs == null || kL == null || kL <= 0 || echo == null) return null
  return (echo - refNs) / kL
})
const liveRatio = computed<number | null>(() => {
  const t = bolt.value?.targetPreloadN != null ? Number(bolt.value.targetPreloadN) : null
  return livePreloadN.value != null && t && t > 0 ? livePreloadN.value / t : null
})

// 实时预紧力滚动曲线（最近 ~90 点，拧松/拧紧时肉眼可见升降）
const liveSparkOption = computed<Record<string, unknown>>(() => {
  const data = liveSeries.value.map((p) => [p.t, p.v])
  const target = bolt.value?.targetPreloadN != null ? Number(bolt.value.targetPreloadN) : null
  const minN = bolt.value?.minPreloadN != null ? Number(bolt.value.minPreloadN) : null
  const mark: Record<string, unknown>[] = []
  if (target != null) mark.push({ yAxis: target, lineStyle: { color: '#10b981', type: 'dashed', width: 1 }, label: { formatter: '目标', color: '#10b981', fontSize: 10, position: 'insideEndTop' } })
  if (minN != null) mark.push({ yAxis: minN, lineStyle: { color: '#f43f5e', type: 'dashed', width: 1 }, label: { formatter: '下限', color: '#f43f5e', fontSize: 10, position: 'insideEndBottom' } })
  return {
    grid: { left: 54, right: 14, top: 12, bottom: 22 },
    xAxis: { type: 'time', axisLabel: { color: '#61748f', fontSize: 10 }, splitLine: { show: false } },
    yAxis: { type: 'value', scale: true, axisLabel: { color: '#61748f', fontSize: 10, formatter: (v: number) => Math.round(v) }, splitLine: { lineStyle: { color: 'rgba(148,163,184,0.08)' } } },
    tooltip: { trigger: 'axis', formatter: (ps: Array<{ value: [number, number] }>) => `${new Date(ps[0].value[0]).toLocaleTimeString()}<br/>${Math.round(ps[0].value[1])} N` },
    series: [{
      type: 'line', data, smooth: true, symbol: 'none',
      lineStyle: { color: '#22d3ee', width: 2 },
      areaStyle: { color: 'rgba(34,211,238,0.10)' },
      markLine: mark.length ? { silent: true, symbol: 'none', data: mark } : undefined,
    }],
    animationDuration: 250,
  }
})

async function setBaseline() {
  const c = chCfg.value
  if (!c) return
  try {
    await ElMessageBox.confirm(
      '将把【当前回波声时】设为该螺栓的零载/参考基线(RefTime)。之后回波偏早=偏松、偏晚=偏紧。请在螺栓已拧到目标状态时执行。',
      '设为基线', { confirmButtonText: '设为基线', cancelButtonText: '取消', type: 'warning' })
  } catch { return }
  baselining.value = true
  try {
    const r = await deviceApi.recalibrate(c.deviceId, c.channelNo)
    ElMessage.success(`基线已设为 ${Number(r.newRefTimeNs).toFixed(0)} ns。客户端实时预紧力将从新基线重新累计（旧 K_L + 新基线的换算在下一次标定前仅供参考）`)
    chCfg.value = await boltApi.channelConfig(boltId)
    // 基线变了，旧基线算出的实时序列不再可比：清空重新累计，避免曲线出现悬崖式跳变误导现场
    liveSeries.value = []
  } catch (e) {
    ElMessage.error((e as Error)?.message || '设基线失败(需复核员权限,或设备未测出稳定声时)')
  } finally {
    baselining.value = false
  }
}

// 会签激活待复核的正式载荷标定（必须由不同于提交人的复核员执行 → 两人四眼）
const countersigning = ref<string | null>(null)
async function countersign(c: CalibRecord) {
  if (!bolt.value) return
  try {
    await ElMessageBox.confirm(
      `会签激活该正式载荷标定（K_L=${Number(c.kL).toExponential(3)}，R²=${Number(c.rSquared).toFixed(3)}）。会签人须与提交人不同（两人四眼）。`,
      '会签激活标定', { confirmButtonText: '会签激活', cancelButtonText: '取消', type: 'warning' })
  } catch { return }
  countersigning.value = c.id
  try {
    await calibrationApi.countersign(bolt.value.channelId, c.id)
    ElMessage.success('已会签激活，标定生效')
    // 会签是"首次进入已标定"的关键路径：bolt.calibrated/state 变了，不重载会让 KPI 仍显示
    // "未标定"直到手动刷新；顺带重载预测与标定档案
    calibrations.value = (await boltApi.calibrations(bolt.value.channelId)).items
    chCfg.value = await boltApi.channelConfig(boltId)
    bolt.value = await boltApi.get(boltId)
    try { pred.value = await boltApi.prediction(boltId) } catch { /* 预测不可用 */ }
  } catch (e) {
    ElMessage.error((e as Error)?.message || '会签失败（需复核员权限，且不能与提交人相同）')
  } finally {
    countersigning.value = null
  }
}

// 接口失败 ≠ 螺栓不存在：网络/服务异常时要给"加载失败+重试",不能误报"已删除"
const loadErr = ref<string | null>(null)
async function init() {
  loading.value = true
  loadErr.value = null
  try {
    try {
      bolt.value = await boltApi.get(boltId)
    } catch (e) {
      bolt.value = null
      if (!(e instanceof ApiError && e.status === 404))
        loadErr.value = (e as Error)?.message || '加载失败'
    }
    await loadTrend()
    try { pred.value = await boltApi.prediction(boltId) } catch { /* 预测不可用 */ }
    measurements.value = (await boltApi.measurements(boltId, 24)).items
    if (bolt.value) {
      // 单独兜底：标定档案拉取失败（网络抖动等）不应打断波形/实时订阅等后续区块
      try { calibrations.value = (await boltApi.calibrations(bolt.value.channelId)).items }
      catch { calibrations.value = [] }
      const al = await alarmApi.list('?includeCleared=true&limit=200')
      // 审计履历是 reviewer+ 专属接口：operator 请求会 403。仅 reviewer 拉取，且单独 try 兜底，
      // 避免因审计 403 让 Promise.all 整体失败、后续波形/实时订阅不执行（operator 打开详情页残缺）。
      let auditItems: { at: string; action: string; note: string | null }[] = []
      if (auth.isReviewer) {
        try {
          auditItems = (await auditApi.query(`?targetId=${bolt.value.channelId}&limit=80`)).items
        } catch { /* 审计不可用不阻断本页其它区块 */ }
      }
      const ev: typeof events.value = []
      for (const c of calibrations.value)
        ev.push({ at: c.createdAt, kind: 'calib', title: `标定 · ${c.method}`, note: c.passed ? '通过' : '未通过', color: c.passed ? '#10b981' : '#f43f5e' })
      for (const a of al.items.filter((x) => x.boltId === boltId))
        ev.push({ at: a.triggeredAt, kind: 'alarm', title: `告警 · ${ALARM_TYPE_LABEL[a.type] || a.type}`, note: a.message || '', color: '#f59e0b' })
      for (const e of auditItems)
        ev.push({ at: e.at, kind: 'audit', title: e.action, note: e.note || '', color: '#38bdf8' })
      events.value = ev.sort((x, y) => new Date(y.at).getTime() - new Date(x.at).getTime())

      try {
        chCfg.value = await boltApi.channelConfig(boltId)
      } catch {
        /* 通道配置不可用 */
      }

      await loadWave()
      liveEchoNs.value = measurements.value[0]?.echoTimeNs != null ? Number(measurements.value[0].echoTimeNs) : null
      liveTempC.value = measurements.value[0]?.spTempC != null ? Number(measurements.value[0].spTempC) : null
      startLive()
      // SignalR 实时：仅处理本通道的测量，立即更新 echo/温度/实时预紧力（替代纯轮询）
      unsubMeas?.()
      unsubMeas = onRealtime('OnMeasurement', (m) => {
        if (!live.value || !chCfg.value) return
        if (m.deviceId === chCfg.value.deviceId && m.channel === chCfg.value.channelNo)
          applyLiveMeasurement(m.ts, m.echoTimeNs, m.spTempC)
      })
    }
  } catch (e) {
    // 兜底：任一必需接口失败都归入"加载失败"可重试,不让异常静默飘走
    loadErr.value = (e as Error)?.message || '加载失败'
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  if (!Number.isFinite(boltId)) {
    ElMessage.error('无效的螺栓 ID')
    loading.value = false
    return
  }
  init()
})

// 路由参数变化（详情页跳详情页，如告警/榜单点击另一颗螺栓）：组件被复用不会重挂载，
// 必须显式重置全部状态再 init，否则 KPI/趋势/实时订阅仍是上一颗螺栓的数据。
watch(() => route.params.boltId, (nv, ov) => {
  if (nv === ov || route.name !== 'boltDetail') return
  const next = Number(nv)
  if (!Number.isFinite(next)) return
  boltId = next
  stopLive()
  unsubMeas?.()
  unsubMeas = undefined
  bolt.value = null
  trend.value = []
  measurements.value = []
  calibrations.value = []
  events.value = []
  wave.value = null
  waveFeat.value = null
  chCfg.value = null
  pred.value = null
  liveSeries.value = []
  lastLiveMs = 0
  liveTs.value = null
  liveEchoNs.value = null
  liveTempC.value = null
  loadErr.value = null
  init()
})

onUnmounted(() => {
  stopLive()
  unsubMeas?.()
})

// A-Scan 基础解析走共享 util；本页在其上叠加 RefTime/ΔTOF/闸门
const waveStats = computed(() => {
  const w = wave.value
  if (!w) return null
  const base = analyzeAscan(w)
  if (!base) return null
  const { xmin, xmax, detNs, zeroNs } = base
  // RefTime 标定基线：仅当落在波形窗内才叠加
  const refNs = chCfg.value?.refTimeNs != null ? Number(chCfg.value.refTimeNs) : null
  const refInWin = refNs != null && refNs >= xmin && refNs <= xmax
  // ΔTOF 以零交叉点为准（物理上等于真实声时,与采集链路算出的预紧力一致）；无零交叉时退回检出点
  const tofNs = zeroNs ?? detNs
  const deltaTof = refInWin ? tofNs - (refNs as number) : null
  // 测量闸门(time gate)：echoWin 已配置才画；位置法围绕 RefTime，否则自 echoStart 起
  const ew = chCfg.value?.echoWinNs != null ? Number(chCfg.value.echoWinNs) : null
  let gate: [number, number] | null = null
  if (ew != null && ew > 0) {
    gate = chCfg.value?.measMode === 1 && refInWin ? [(refNs as number) - ew / 2, (refNs as number) + ew / 2] : [xmin, xmin + ew]
    gate = [Math.max(xmin, gate[0]), Math.min(xmax, gate[1])]
  }
  return { ...base, w, refNs, refInWin, deltaTof, gate }
})

const ascanOption = computed(() => {
  const s = waveStats.value
  if (!s) return {}
  const data = s.w.waveData.map((v, i) => [s.x0 + i * s.clk, v])
  const lines: Record<string, unknown>[] = []
  if (s.thr != null && s.thr > 0) {
    lines.push({ yAxis: s.thr, lineStyle: { color: '#f59e0b', type: 'dashed', opacity: 0.65 }, label: { formatter: '阈值', color: '#f59e0b', position: 'insideStartTop', fontSize: 10 } })
    lines.push({ yAxis: -s.thr, lineStyle: { color: '#f59e0b', type: 'dashed', opacity: 0.65 }, label: { show: false } })
  }
  lines.push({ xAxis: s.detNs, lineStyle: { color: '#f43f5e', width: 1.4 }, label: { formatter: '检出', color: '#f43f5e', position: 'insideEndTop', fontSize: 10 } })
  if (s.refInWin) lines.push({ xAxis: s.refNs, lineStyle: { color: '#22d3ee', type: 'dashed', width: 1.4 }, label: { formatter: '基线', color: '#22d3ee', position: 'insideEndBottom', fontSize: 10 } })
  const areas: Record<string, unknown>[][] = []
  if (s.thr != null && s.thr > 0) areas.push([{ yAxis: -s.thr, itemStyle: { color: 'rgba(245,158,11,0.05)' } }, { yAxis: s.thr }])
  if (s.gate) areas.push([{ xAxis: s.gate[0], itemStyle: { color: 'rgba(34,211,238,0.13)', borderColor: 'rgba(34,211,238,0.35)', borderWidth: 1 } }, { xAxis: s.gate[1] }])
  const mp: Record<string, unknown>[] = [
    { coord: [s.x0 + s.peakIdx * s.clk, s.w.waveData[s.peakIdx]], symbol: 'pin', symbolSize: 38, itemStyle: { color: 'rgba(244,63,94,0.85)' }, label: { formatter: '峰', color: '#fff', fontSize: 10 } },
  ]
  if (s.zeroNs != null) mp.push({ coord: [s.zeroNs, 0], symbol: 'circle', symbolSize: 9, itemStyle: { color: '#a78bfa', borderColor: '#fff', borderWidth: 1 }, label: { show: false } })
  // 包络线（滑动峰值，勾出回波包络）
  const winN = 4
  const envUp = s.w.waveData.map((_, i) => {
    let m = 0
    for (let j = Math.max(0, i - winN); j <= Math.min(s.n - 1, i + winN); j++) m = Math.max(m, Math.abs(s.w.waveData[j]))
    return [s.x0 + i * s.clk, m]
  })
  const envLo = envUp.map((p) => [p[0], -(p[1] as number)])
  return {
    grid: { left: 52, right: 18, top: 18, bottom: 34 },
    tooltip: { trigger: 'axis', backgroundColor: '#0d1320', borderColor: 'rgba(56,189,248,0.3)', textStyle: { color: '#e8eef8' } },
    xAxis: { type: 'value', min: s.xmin, max: s.xmax, name: '时间 ns', nameTextStyle: { color: '#61748f', fontSize: 11 }, axisLabel: { color: '#61748f', fontSize: 11 }, axisLine: { lineStyle: { color: 'rgba(148,163,184,0.2)' } }, splitLine: { show: false } },
    yAxis: { type: 'value', scale: true, name: '幅值', nameTextStyle: { color: '#61748f', fontSize: 11 }, axisLabel: { color: '#61748f', fontSize: 11 }, splitLine: { lineStyle: { color: 'rgba(148,163,184,0.08)' } } },
    series: [
      { type: 'line', symbol: 'none', data: envUp, smooth: true, lineStyle: { color: 'rgba(167,139,250,0.45)', width: 1, type: 'dashed' }, silent: true, z: 1 },
      { type: 'line', symbol: 'none', data: envLo, smooth: true, lineStyle: { color: 'rgba(167,139,250,0.45)', width: 1, type: 'dashed' }, silent: true, z: 1 },
      {
        type: 'line', symbol: 'none', data, sampling: 'lttb', z: 3,
        lineStyle: { color: '#22d3ee', width: 1.4 },
        markLine: { symbol: 'none', data: lines, animation: false },
        markArea: areas.length ? { silent: true, data: areas } : undefined,
        markPoint: { data: mp },
      },
    ],
  }
})

function openSettings() {
  const b = bolt.value
  if (!b) return
  form.targetPreloadN = b.targetPreloadN != null ? Number(b.targetPreloadN) : null
  form.minPreloadN = b.minPreloadN != null ? Number(b.minPreloadN) : null
  form.positionLabel = b.positionLabel || ''
  form.installTorqueNm = b.installTorqueNm != null ? Number(b.installTorqueNm) : null
  form.material = b.material || ''
  form.elasticModulusGpa = b.elasticModulusGpa != null ? Number(b.elasticModulusGpa) : null
  form.soundVelocityMs = b.soundVelocityMs != null ? Number(b.soundVelocityMs) : null
  form.effectiveLengthMm = b.effectiveLengthMm != null ? Number(b.effectiveLengthMm) : null
  form.crossSectionMm2 = b.crossSectionMm2 != null ? Number(b.crossSectionMm2) : null
  settings.visible = true
}
async function saveSettings() {
  if (form.minPreloadN != null && form.targetPreloadN != null && form.minPreloadN > form.targetPreloadN)
    return ElMessage.warning('告警下限不能大于目标预紧力')
  settings.saving = true
  try {
    await boltApi.updateSettings(boltId, {
      targetPreloadN: form.targetPreloadN, minPreloadN: form.minPreloadN, positionLabel: form.positionLabel,
      installTorqueNm: form.installTorqueNm, material: form.material || null, elasticModulusGpa: form.elasticModulusGpa,
      soundVelocityMs: form.soundVelocityMs, effectiveLengthMm: form.effectiveLengthMm, crossSectionMm2: form.crossSectionMm2,
    })
    ElMessage.success('预紧力设置已保存')
    settings.visible = false
    bolt.value = await boltApi.get(boltId).catch(() => bolt.value)
  } catch (e) {
    ElMessage.error((e as Error)?.message || '保存失败')
  } finally {
    settings.saving = false
  }
}

function openAdjust() {
  const c = chCfg.value
  if (!c) return
  adjForm.gain = Number(c.gain)
  adjForm.probeFreqMhz = Number(c.probeFreqMhz)
  adjForm.echoWinNs = c.echoWinNs != null ? Number(c.echoWinNs) : null
  adjust.visible = true
}
async function submitAdjust() {
  const c = chCfg.value
  if (!c) return
  // 仅提交相对当前有改动的可写字段（增益/探头频率/回波窗）
  const updates: Record<string, number | undefined> = {}
  if (adjForm.gain != null && adjForm.gain !== Number(c.gain)) updates.gain = adjForm.gain
  if (adjForm.probeFreqMhz != null && adjForm.probeFreqMhz !== Number(c.probeFreqMhz)) updates.probeFreqMhz = adjForm.probeFreqMhz
  if (adjForm.echoWinNs != null && adjForm.echoWinNs !== (c.echoWinNs != null ? Number(c.echoWinNs) : null)) updates.echoWinNs = adjForm.echoWinNs
  if (Object.keys(updates).length === 0) return ElMessage.warning('未检测到参数改动')
  adjust.saving = true
  try {
    const res = await configChangeApi.submit(c.deviceId, c.channelNo, updates)
    adjust.visible = false
    ElMessage.success(`已提交变更请求 #${res.requestId}，待审批下发`)
    ElMessageBox.confirm('参数变更已进入审批队列（需另一名复核人审批→执行→读回验证）。是否前往参数审批页面？', '已提交待审批', {
      confirmButtonText: '前往审批', cancelButtonText: '留在本页', type: 'info',
    }).then(() => router.push({ name: 'configChanges' })).catch(() => {})
  } catch (e) {
    ElMessage.error((e as Error)?.message || '提交失败')
  } finally {
    adjust.saving = false
  }
}

function setRange(k: '1h' | '24h' | '7d') {
  rangeKey.value = k
  loadTrend()
}

const trendOption = computed(() => {
  const pts = trend.value
  const t = (p: TrendPoint) => new Date(p.ts).getTime()
  const avg = pts.map((p) => [t(p), p.avgPreloadN == null ? null : Number(p.avgPreloadN)])
  const lower = pts.map((p) => [t(p), p.minPreloadN == null ? null : Number(p.minPreloadN)])
  const band = pts.map((p) => [t(p), p.minPreloadN == null || p.maxPreloadN == null ? null : Number(p.maxPreloadN) - Number(p.minPreloadN)])
  const temp = pts.map((p) => [t(p), p.avgTempC == null ? null : Number(p.avgTempC)])
  const target = num(bolt.value?.targetPreloadN)
  const min = num(bolt.value?.minPreloadN)

  // y 轴域：保证下限/目标与安全区带都可见
  const vals = pts.flatMap((p) => [p.minPreloadN, p.maxPreloadN, p.avgPreloadN].filter((v) => v != null).map(Number))
  let yMin = Math.min(...(min != null ? [min] : []), ...vals)
  let yMax = Math.max(...(target != null ? [target] : []), ...vals)
  if (!isFinite(yMin)) yMin = 0
  if (!isFinite(yMax)) yMax = 1
  const padv = (yMax - yMin) * 0.1 || 1000
  yMin -= padv
  yMax += padv

  // 安全区带（危险/预警）
  const areas: Record<string, unknown>[][] = []
  if (min != null) {
    areas.push([{ yAxis: yMin, itemStyle: { color: 'rgba(244,63,94,0.07)' } }, { yAxis: min }])
    areas.push([{ yAxis: min, itemStyle: { color: 'rgba(245,158,11,0.06)' } }, { yAxis: min * 1.1 }])
  }
  const markLines: Record<string, unknown>[] = []
  if (target != null) markLines.push({ yAxis: target, lineStyle: { color: '#22d3ee', type: 'dashed' }, label: { formatter: '目标', color: '#22d3ee', position: 'insideEndTop', fontSize: 10 } })
  if (min != null) markLines.push({ yAxis: min, lineStyle: { color: '#f43f5e', type: 'dashed' }, label: { formatter: '下限', color: '#f43f5e', position: 'insideEndBottom', fontSize: 10 } })

  const series: Record<string, unknown>[] = [
    { name: 'lower', type: 'line', symbol: 'none', data: lower, lineStyle: { opacity: 0 }, stack: 'env', areaStyle: { opacity: 0 }, connectNulls: true, silent: true, z: 1 },
    { name: '波动区间', type: 'line', symbol: 'none', data: band, lineStyle: { opacity: 0 }, stack: 'env', areaStyle: { color: 'rgba(34,211,238,0.13)' }, connectNulls: true, silent: true, z: 1 },
    {
      name: '预紧力', type: 'line', smooth: true, symbol: 'none', connectNulls: true, data: avg, z: 3,
      lineStyle: { color: '#22d3ee', width: 2, shadowColor: 'rgba(34,211,238,0.5)', shadowBlur: 10 },
      markLine: { symbol: 'none', data: markLines, silent: true },
      markArea: areas.length ? { silent: true, data: areas } : undefined,
    },
  ]

  // 到限投影：把下降预测画到图上（从最后点外推到下限）。数据陈旧时不外推，避免误导。
  if (!isStale.value && prediction.value?.trend === 'falling' && prediction.value.days != null && min != null) {
    const last = [...pts].reverse().find((p) => p.avgPreloadN != null)
    if (last) {
      const x0 = t(last), y0 = Number(last.avgPreloadN)
      const x1 = x0 + prediction.value.days * 86400000
      series.push({
        name: '到限预测', type: 'line', symbol: 'none', data: [[x0, y0], [x1, min]], z: 4, silent: true,
        lineStyle: { color: '#f43f5e', type: 'dashed', width: 1.6 },
        markPoint: { symbol: 'pin', symbolSize: 34, itemStyle: { color: 'rgba(244,63,94,0.9)' }, label: { formatter: '到限', color: '#fff', fontSize: 9 }, data: [{ coord: [x1, min] }] },
      })
    }
  }
  if (showTemp.value) series.push({ name: '温度', type: 'line', symbol: 'none', smooth: true, connectNulls: true, data: temp, yAxisIndex: 1, z: 2, lineStyle: { color: '#fb923c', width: 1.3, opacity: 0.85 } })

  return {
    grid: { left: 56, right: showTemp.value ? 50 : 18, top: 16, bottom: 28 },
    tooltip: { trigger: 'axis', backgroundColor: '#0d1320', borderColor: 'rgba(56,189,248,0.3)', textStyle: { color: '#e8eef8' } },
    xAxis: { type: 'time', axisLine: { lineStyle: { color: 'rgba(148,163,184,0.2)' } }, axisLabel: { color: '#61748f', fontSize: 11 } },
    yAxis: [
      { type: 'value', min: yMin, max: yMax, name: '预紧力 N', nameTextStyle: { color: '#61748f', fontSize: 11 }, axisLabel: { color: '#61748f', fontSize: 11 }, splitLine: { lineStyle: { color: 'rgba(148,163,184,0.08)' } } },
      { type: 'value', scale: true, name: '°C', position: 'right', show: showTemp.value, nameTextStyle: { color: '#fb923c', fontSize: 10 }, axisLabel: { color: '#fb923c', fontSize: 10 }, splitLine: { show: false } },
    ],
    series,
  }
})

function fmtN(n: number | null | undefined) {
  return n == null ? '—' : Number(n).toLocaleString('en-US', { maximumFractionDigits: 0 })
}
function fmtTime(iso: string | null | undefined) {
  if (!iso) return '—'
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString('zh-CN', { hour12: false })
}
function ratioStr(r: number | null | undefined) {
  return r == null ? '—' : (Number(r) * 100).toFixed(1) + '%'
}

// 导出当前时段趋势 CSV（月报/年报原始数据；带 token 的接口用 fetch+blob 下载）
async function exportTrendCsv() {
  const hours = rangeKey.value === '1h' ? 1 : rangeKey.value === '24h' ? 24 : 168
  const since = new Date(Date.now() - hours * 3600 * 1000).toISOString()
  const until = new Date().toISOString()
  try {
    const { getAuthToken } = await import('../api/client')
    const res = await fetch(
      `/api/bolts/${boltId}/preload-trend/export.csv?since=${since}&until=${until}&bucket=auto`,
      { headers: { Authorization: `Bearer ${getAuthToken()}` } })
    if (!res.ok) throw new Error(String(res.status))
    const blob = await res.blob()
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `趋势-${bolt.value?.code || boltId}-${RANGE_LABEL[rangeKey.value]}.csv`
    a.click()
    URL.revokeObjectURL(url)
    ElMessage.success('趋势数据已导出')
  } catch {
    ElMessage.error('导出失败')
  }
}
</script>

<template>
  <div class="bd rise" v-loading="loading">
    <div class="bd-top">
      <div class="bd-back" @click="router.back()"><el-icon><ArrowLeft /></el-icon> 返回</div>
      <div class="bd-top-right">
        <el-button v-if="bolt && auth.isReviewer" size="small" :type="bolt.calibrated ? 'default' : 'primary'" @click="wizardOpen = true">
          <el-icon><Aim /></el-icon> {{ bolt.calibrated ? '重新标定' : '开始标定' }}
        </el-button>
        <el-button v-if="bolt" size="small" @click="openSettings"><el-icon><Setting /></el-icon> 编辑预紧力设置</el-button>
      </div>
    </div>

    <!-- 未标定引导横幅:告诉客户"下一步做什么",而不是让页面停在一排 — -->
    <div v-if="bolt && !bolt.calibrated && !loading" class="calib-cta">
      <el-icon><InfoFilled /></el-icon>
      <span>该螺栓尚未标定,暂无法显示预紧力。完成标定后自动开始监测(现场约 3~5 分钟)。</span>
      <el-button v-if="auth.isReviewer" size="small" type="primary" @click="wizardOpen = true">开始标定</el-button>
      <span v-else class="dim">请联系复核员执行标定</span>
    </div>

    <!-- 头部 -->
    <div class="panel panel--glow head" v-if="bolt">
      <div class="head-left">
        <div class="head-top">
          <span class="led" :class="STATUS[status].cls" />
          <h2>{{ bolt.code }}</h2>
          <span class="status-tag" :style="`color:${STATUS[status].color};border-color:${STATUS[status].color}`">{{ STATUS[status].label }}</span>
        </div>
        <div class="head-sub">{{ bolt.nominalDiam }} · {{ bolt.grade }} 级 · CH{{ bolt.channelNo }} · {{ STATE_LABEL[bolt.state] || (bolt.calibrated ? '已标定' : '未标定') }}</div>
      </div>
    </div>

    <!-- 关键指标卡（Grafana stat 风） -->
    <div class="kpi-row" v-if="bolt">
      <div class="stat panel" :class="status === 'loose' ? 'stat--crit' : status === 'normal' ? 'stat--ok' : ''">
        <div class="stat-hd"><span class="stat-label">{{ liveLabel }}预紧力</span><span class="led" :class="STATUS[status].cls" /></div>
        <div class="stat-val metric" :style="`color:${STATUS[status].color}`">{{ fmtN(displayPreloadN) }}<i>N</i></div>
      </div>
      <div class="stat panel">
        <div class="stat-hd"><span class="stat-label">{{ liveLabel }}比值</span><el-icon class="stat-ic"><Odometer /></el-icon></div>
        <div class="stat-val metric">{{ ratioStr(displayRatio) }}</div>
      </div>
      <div class="stat panel">
        <div class="stat-hd"><span class="stat-label">目标 / 下限</span><el-icon class="stat-ic"><Aim /></el-icon></div>
        <div class="stat-val metric stat-val--sm">{{ fmtN(bolt.targetPreloadN) }} / {{ fmtN(bolt.minPreloadN) }}</div>
      </div>
      <div class="stat panel">
        <div class="stat-hd"><span class="stat-label">温度</span><el-icon class="stat-ic"><Sunny /></el-icon></div>
        <div class="stat-val metric stat-val--sm">{{ displayTempC != null ? displayTempC.toFixed(1) + '°C' : '—' }}</div>
      </div>
    </div>

    <!-- 失联 / 数据陈旧横幅：明确标出数值为“最后已知”、趋势预测暂不可信，避免误读为实时 -->
    <div v-if="bolt && isStale" class="stale-banner">
      <el-icon><WarningFilled /></el-icon>
      <span v-if="offlineStale">该通道已 <b>{{ fmtAgo(effLastTs, now) }}</b> 无新测量（设备离线 / 失联）。下列数值为<b>最后已知值</b>，趋势预测已暂停。</span>
      <span v-else>最近一次测量<b>未得到有效预紧力读数</b>（信号丢失：探头脱落 / 接头松动 / 无回波）。下列为<b>最后已知值</b>，趋势预测已暂停。</span>
    </div>

    <!-- 状态 · 预紧力变化程度 -->
    <div class="panel panel--glow" v-if="bolt">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark" :class="status === 'loose' ? 'hd-mark--crit' : status === 'warn' ? 'hd-mark--warn' : status === 'normal' ? 'hd-mark--ok' : ''" />状态 · 预紧力变化程度</span>
        <span class="hd-right chg-reason">
          <span v-if="health.relax" class="relax-tag" :style="`color:${health.relax.color};border-color:${health.relax.color}`">{{ health.relax.label }}</span>
          {{ health.reason }}
        </span>
      </div>
      <div class="chg-body">
        <div class="chg-metrics">
          <div class="cm">
            <span class="cm-k">预紧力保持率</span>
            <b class="cm-v" :style="`color:${health.meta.color}`">{{ pct(health.residualRatio) }}</b>
            <span class="cm-i">当前 / 目标</span>
          </div>
          <div class="cm">
            <span class="cm-k">预紧力损失</span>
            <b class="cm-v" :style="health.relax ? `color:${health.relax.color}` : ''">{{ lossText }}</b>
            <span class="cm-i">较目标 {{ health.lossPct != null ? pct(Math.abs(health.lossPct)) : '—' }}</span>
          </div>
          <div class="cm">
            <span class="cm-k">安全裕度</span>
            <b class="cm-v" :style="marginStyle">{{ pct(health.safetyMargin) }}</b>
            <span class="cm-i">距安全下限</span>
          </div>
          <div class="cm">
            <span class="cm-k">变化速率</span>
            <b class="cm-v" :class="!isStale && prediction?.trend === 'falling' ? 'rate-down' : ''">
              {{ isStale ? '—' : !prediction || prediction.trend === 'insufficient' ? '—' : prediction.trend === 'stable' ? '平稳' : signedN(prediction.perDay) + ' N/天' }}
            </b>
            <span class="cm-i">
              <template v-if="isStale">数据陈旧 · 暂不预测</template>
              <template v-else-if="prediction?.trend === 'falling' && prediction.days != null">约 {{ Math.max(0, Math.round(prediction.days)) }} 天到下限</template>
              <template v-else-if="prediction?.trend === 'insufficient'">样本不足({{ prediction.samples }})</template>
              <template v-else>近{{ prediction?.windowDays ?? 7 }}天{{ prediction?.r2 != null ? ' · R² ' + prediction.r2.toFixed(2) : '' }}</template>
            </span>
          </div>
        </div>
        <PreloadScale :current="displayPreloadN" :target="num(bolt.targetPreloadN)" :min="num(bolt.minPreloadN)" :height="14" />
      </div>
    </div>

    <!-- 加载失败(网络/服务异常)与"螺栓不存在"分开表述 -->
    <div v-if="loadErr && !loading" class="panel bolt-missing">
      <el-icon><WarningFilled /></el-icon>
      <p>加载失败：{{ loadErr }}</p>
      <el-button type="primary" @click="init">重试</el-button>
    </div>
    <!-- 螺栓不存在：整页空态,不再渲染下方空壳区块 -->
    <div v-else-if="!bolt && !loading" class="panel bolt-missing">
      <el-icon><WarningFilled /></el-icon>
      <p>螺栓不存在或已被删除</p>
      <el-button type="primary" @click="router.push({ name: 'assets' })">返回资产监测</el-button>
    </div>

    <!-- 趋势 -->
    <div class="panel panel--glow" v-if="bolt">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark" />预紧力趋势</span>
        <div class="trend-ctrls">
          <span class="temp-toggle" @click="exportTrendCsv"><el-icon><Download /></el-icon> 导出</span>
          <span class="temp-toggle" :class="{ active: showTemp }" @click="showTemp = !showTemp"><el-icon><Sunny /></el-icon> 温度</span>
          <div class="range-tabs">
            <span v-for="k in (['1h', '24h', '7d'] as const)" :key="k" class="range-tab" :class="{ active: rangeKey === k }" @click="setRange(k)">{{ RANGE_LABEL[k] }}</span>
          </div>
        </div>
      </div>
      <EChart v-if="trend.length" :option="trendOption" height="280px" />
      <div v-if="!loading && !trend.length" class="chart-empty">该时段无测量数据</div>
      <div v-if="isStale && !loading && trend.length" class="predict stale">
        <el-icon><WarningFilled /></el-icon>
        <span v-if="offlineStale">数据已陈旧（{{ fmtAgo(effLastTs, now) }}无新测量），趋势预测已暂停，恢复采集后自动恢复。</span>
        <span v-else>无近期有效读数（信号丢失），趋势预测已暂停，恢复有效采集后自动恢复。</span>
      </div>
      <div v-else-if="prediction && prediction.trend !== 'insufficient'" class="predict" :class="prediction.trend === 'falling' ? (prediction.days != null && prediction.days < 30 ? 'crit' : 'warn') : 'ok'">
        <el-icon><component :is="prediction.trend === 'falling' ? 'Bottom' : prediction.trend === 'rising' ? 'Top' : 'Minus'" /></el-icon>
        <span v-if="prediction.trend === 'falling'">
          预紧力下降中 · 约 {{ Math.abs(prediction.perDay).toFixed(0) }} N/天 ·
          <b>预计 {{ prediction.days != null ? Math.max(0, Math.round(prediction.days)) : '—' }} 天后到下限</b>,建议安排复拧
        </span>
        <span v-else-if="prediction.trend === 'rising'">预紧力趋势上升 · 约 {{ prediction.perDay.toFixed(0) }} N/天</span>
        <span v-else>趋势平稳,近 {{ prediction.windowDays }} 天无明显松动风险</span>
        <span class="predict-conf">基于近 {{ prediction.windowDays }} 天 · {{ prediction.samples }} 点{{ prediction.r2 != null ? ' · 拟合 R² ' + prediction.r2.toFixed(2) : '' }}</span>
      </div>
    </div>

    <!-- 实时回波波形 A-Scan + 松紧状态 -->
    <div class="panel panel--glow" v-if="bolt">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark" />实时回波波形 A-Scan <span v-if="live" class="live-dot" /></span>
        <div class="ascan-hd">
          <span v-if="waveStats" class="mono ascan-meta">检出 {{ waveStats.detNs.toFixed(0) }}ns · SNR {{ waveFeat?.snrDb != null ? Number(waveFeat.snrDb).toFixed(1) : '—' }}dB · 阈值 {{ wave?.threshold ?? '—' }}</span>
          <span class="ascan-ctrl" :class="{ active: live }" @click="toggleLive"><el-icon><component :is="live ? 'VideoPause' : 'VideoPlay'" /></el-icon> {{ live ? '实时' : '已暂停' }}</span>
          <!-- 重标定(设基线)后端要求 reviewer+：非复核员隐藏按钮，避免点击必 403 的误导 -->
          <el-button v-if="auth.isReviewer" size="small" :loading="baselining" @click="setBaseline"><el-icon><Aim /></el-icon> 设当前为基线</el-button>
        </div>
      </div>

      <!-- 实时预紧力（标定后）：数字 + 滚动曲线，拧松/拧紧肉眼可见升降 -->
      <div v-if="livePreloadN != null" class="live-preload">
        <div class="lp-now">
          <span class="lp-k">实时预紧力 <span v-if="live" class="live-dot" /></span>
          <b class="lp-v">{{ (livePreloadN / 1000).toFixed(1) }}<i>kN</i></b>
          <span class="lp-sub mono">{{ Math.round(livePreloadN) }} N · 占目标 {{ liveRatio != null ? pct(liveRatio) : '—' }}</span>
        </div>
        <EChart v-if="liveSeries.length > 1" :option="liveSparkOption" height="96px" />
        <div v-else class="lp-wait">采集中…拧松/拧紧螺栓即可看到曲线实时升降</div>
      </div>

      <!-- 松紧状态条（回波相对基线偏移） -->
      <div v-if="tightness" class="tight-bar" :style="`border-color:${tightness.color}33;background:${tightness.color}0f`">
        <span class="tight-label" :style="`background:${tightness.color}`">{{ tightness.label }}</span>
        <span class="tight-dt">相对基线 ΔTOF <b class="mono" :style="`color:${tightness.color}`">{{ (tightness.dt >= 0 ? '+' : '') + tightness.dt.toFixed(1) }} ns</b><template v-if="tightness.dF != null"> · ≈ <b class="mono" :style="`color:${tightness.color}`">{{ signedN(tightness.dF) }} N</b></template></span>
        <span class="tight-hint">{{ tightness.band === 'loose' ? '回波偏早 → 预紧力下降 / 松动方向' : tightness.band === 'tight' ? '回波偏晚 → 预紧力高于基线' : '回波在基线附近 · 稳定' }}</span>
      </div>
      <div v-else-if="wave" class="tight-bar tight-nobase">未设基线 — 拧到目标状态后点“设当前为基线”,之后即可看松紧</div>

      <EChart v-if="wave" :option="ascanOption" height="240px" />
      <div v-if="waveStats" class="ascan-legend">
        <span class="lg"><i class="lg-line" style="background:#22d3ee" />回波</span>
        <span class="lg"><i class="lg-dot" style="background:#f43f5e" />检出点 {{ waveStats.detNs.toFixed(0) }}ns</span>
        <span v-if="waveStats.zeroNs != null" class="lg"><i class="lg-dot" style="background:#a78bfa" />零交叉 {{ waveStats.zeroNs.toFixed(1) }}ns</span>
        <span class="lg"><i class="lg-line" style="background:#a78bfa" />包络</span>
        <span class="lg"><i class="lg-line" style="background:#f59e0b" />阈值带 ±{{ waveStats.thr ?? '—' }}</span>
        <span v-if="waveStats.refInWin" class="lg"><i class="lg-line" style="background:#22d3ee;border-top:1px dashed #22d3ee;background:none" />基线 {{ Number(waveStats.refNs).toFixed(0) }}ns</span>
        <span v-if="waveStats.gate" class="lg"><i class="lg-area" />测量闸门</span>
      </div>
      <div v-if="!wave" class="chart-empty">该通道暂无回波波形(仅开启波形采样的通道上传波形)</div>
    </div>

    <!-- 通道超声参数 + 标定基线 -->
    <div class="panel panel--glow" v-if="chCfg">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark" />通道超声参数 · 标定基线</span>
        <!-- 提交参数变更请求走 diagnostic 组，后端要求 reviewer+：非复核员隐藏 -->
        <el-button v-if="auth.isReviewer" size="small" @click="openAdjust"><el-icon><MagicStick /></el-icon> 调整参数(提交审批)</el-button>
      </div>
      <div class="cfg-grid">
        <div class="cfg-cell"><span class="cfg-k">设备 / 通道</span><b class="mono">#{{ chCfg.deviceId }} · CH{{ chCfg.channelNo }}</b></div>
        <div class="cfg-cell"><span class="cfg-k">探头频率</span><b class="mono">{{ Number(chCfg.probeFreqMhz).toFixed(2) }} MHz</b></div>
        <div class="cfg-cell"><span class="cfg-k">增益 Gain</span><b class="mono">{{ Number(chCfg.gain).toFixed(1) }} dB</b></div>
        <div class="cfg-cell"><span class="cfg-k">回波窗 EchoWin</span><b class="mono">{{ chCfg.echoWinNs != null ? Number(chCfg.echoWinNs).toFixed(0) + ' ns' : '—' }}</b></div>
        <div class="cfg-cell"><span class="cfg-k">阈值 Threshold</span><b class="mono">{{ chCfg.threshold }}</b></div>
        <div class="cfg-cell"><span class="cfg-k">测量模式</span><b class="mono">{{ MEAS_MODE[chCfg.measMode] ?? chCfg.measMode }}</b></div>
        <div class="cfg-cell"><span class="cfg-k">温补模式</span><b class="mono">{{ TC_MODE[chCfg.tcMode] ?? chCfg.tcMode }}</b></div>
        <div class="cfg-cell cfg-base"><span class="cfg-k">RefTime 参考声时</span><b class="mono">{{ chCfg.refTimeNs != null ? Number(chCfg.refTimeNs).toFixed(1) + ' ns' : '— 未标定' }}</b></div>
        <div class="cfg-cell cfg-base"><span class="cfg-k">RefTemp 参考温度</span><b class="mono">{{ chCfg.refTempC != null ? Number(chCfg.refTempC).toFixed(1) + ' °C' : '—' }}</b></div>
        <div class="cfg-cell cfg-base"><span class="cfg-k">K_L 载荷因子</span><b class="mono">{{ chCfg.kL != null ? Number(chCfg.kL).toExponential(3) : '—' }}<i v-if="chCfg.kLUncertainty != null"> ±{{ Number(chCfg.kLUncertainty).toExponential(1) }}</i></b></div>
      </div>
      <div class="cfg-note">
        <el-icon><InfoFilled /></el-icon>
        增益 / 探头频率 / 回波窗可在线调整(经审批下发);<b>阈值、测量模式、RefTime/RefTemp/K_L 为标定基线,须经重标定流程变更</b>。配置来源:{{ CONFIG_SOURCE[chCfg.configSource || ''] || chCfg.configSource || '—' }}
      </div>
    </div>

    <!-- 测量 + 标定 -->
    <div class="two-col" v-if="bolt">
      <div class="panel panel--glow">
        <div class="panel-hd"><span class="panel-title"><i class="hd-mark" />近期测量</span></div>
        <div class="tbl">
          <div class="tr th"><span>时间</span><span>声时 ns</span><span>温度</span><span>状态</span></div>
          <div v-for="(m, i) in measurements" :key="i" class="tr">
            <span class="mono">{{ fmtTime(m.ts) }}</span>
            <span class="mono">{{ m.echoTimeNs != null ? Number(m.echoTimeNs).toFixed(0) : '—' }}</span>
            <span class="mono">{{ m.spTempC != null ? Number(m.spTempC).toFixed(1) + '°C' : '—' }}</span>
            <span class="mono"><span class="led" :class="m.statusCode >= 0 ? 'led--ok' : 'led--crit'" /> {{ STATUS_CODE[m.statusCode] || (m.statusCode >= 0 ? '正常' : `异常(${m.statusCode})`) }}</span>
          </div>
          <div v-if="!loading && !measurements.length" class="tbl-empty">无测量记录</div>
        </div>
      </div>

      <div class="panel panel--glow">
        <div class="panel-hd"><span class="panel-title"><i class="hd-mark" />标定档案</span></div>
        <ul class="calib-list">
          <li v-for="c in calibrations" :key="c.id">
            <div class="cl-left">
              <span class="led" :class="c.isActive && c.passed ? 'led--ok' : c.passed ? 'led--off' : 'led--crit'" />
              <div>
                <div class="cl-method">
                  {{ METHOD_LABEL[c.method] || c.method }}
                  <span v-if="c.isActive" class="cl-active">生效中</span>
                  <span v-if="c.reviewState === 'pending'" class="cl-pending">待会签</span>
                  <span v-else-if="c.countersigned" class="cl-signed">两人会签</span>
                  <span v-else-if="c.reviewState === 'self_signed'" class="cl-self">自签</span>
                </div>
                <div class="cl-meta mono">K_L={{ Number(c.kL).toExponential(3) }} · R²={{ Number(c.rSquared).toFixed(3) }}</div>
              </div>
            </div>
            <div class="cl-right">
              <el-button v-if="c.reviewState === 'pending' && auth.isReviewer" size="small" type="primary" text
                :disabled="c.operatorUserId === auth.userId"
                :title="c.operatorUserId === auth.userId ? '不能会签自己提交的标定（两人四眼）' : ''"
                :loading="countersigning === c.id" @click="countersign(c)">会签</el-button>
              <span class="cl-pass" :class="c.passed ? 'ok' : 'no'">{{ c.passed ? '通过' : '未过' }}</span>
              <span class="cl-date mono">{{ new Date(c.createdAt).toLocaleDateString('zh-CN') }}</span>
            </div>
          </li>
          <li v-if="!loading && !calibrations.length" class="calib-empty">该通道暂无标定档案</li>
        </ul>
      </div>
    </div>

    <!-- 螺栓履历 -->
    <div class="panel panel--glow" v-if="bolt">
      <div class="panel-hd"><span class="panel-title"><i class="hd-mark" />螺栓履历</span></div>
      <ul class="timeline">
        <li v-for="(e, i) in events" :key="i">
          <span class="tl-dot" :style="`background:${e.color}`" />
          <div class="tl-body">
            <div class="tl-row"><span class="tl-title">{{ e.title }}</span><span class="tl-time mono">{{ fmtTime(e.at) }}</span></div>
            <div v-if="e.note" class="tl-note">{{ e.note }}</div>
          </div>
        </li>
        <li v-if="!loading && !events.length" class="tl-empty">暂无履历事件</li>
      </ul>
    </div>

    <!-- 预紧力设置 -->
    <el-dialog v-model="settings.visible" title="编辑预紧力设置" width="520px">
      <!-- 后端语义是"留空 = 不修改"（COALESCE），清空输入框不会清掉库里已有的值——必须明示，
           否则用户"清空阈值→保存成功→旧值还在"会误以为已取消告警阈值 -->
      <el-alert type="info" :closable="false" show-icon style="margin-bottom: 12px">
        留空的字段表示<b>保持原值不变</b>（不会清空）。如需取消某项阈值，请联系管理员在数据库层处理。
      </el-alert>
      <el-form label-width="110px" label-position="left">
        <div class="form-sec">预紧力阈值（驱动状态判定与告警）</div>
        <el-form-item label="目标预紧力 N"><el-input-number v-model="form.targetPreloadN" :step="1000" :min="0" controls-position="right" style="width: 100%" /></el-form-item>
        <el-form-item label="告警下限 N"><el-input-number v-model="form.minPreloadN" :step="1000" :min="0" controls-position="right" style="width: 100%" /></el-form-item>
        <el-form-item label="安装扭矩 N·m"><el-input-number v-model="form.installTorqueNm" :step="10" :min="0" controls-position="right" style="width: 100%" /></el-form-item>
        <el-form-item label="位置标签"><el-input v-model="form.positionLabel" placeholder="如 12点钟方向" /></el-form-item>

        <div class="form-sec">材料与几何（简化模型计算用）</div>
        <el-form-item label="材料"><el-input v-model="form.material" placeholder="如 42CrMo" /></el-form-item>
        <el-form-item label="弹性模量 GPa"><el-input-number v-model="form.elasticModulusGpa" :step="1" :min="0" controls-position="right" style="width: 100%" /></el-form-item>
        <el-form-item label="纵波声速 m/s"><el-input-number v-model="form.soundVelocityMs" :step="10" :min="0" controls-position="right" style="width: 100%" /></el-form-item>
        <el-form-item label="有效长度 mm"><el-input-number v-model="form.effectiveLengthMm" :step="1" :min="0" controls-position="right" style="width: 100%" /></el-form-item>
        <el-form-item label="截面积 mm²"><el-input-number v-model="form.crossSectionMm2" :step="1" :min="0" controls-position="right" style="width: 100%" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="settings.visible = false">取消</el-button>
        <el-button type="primary" :loading="settings.saving" @click="saveSettings">保存</el-button>
      </template>
    </el-dialog>

    <!-- 调整通道超声参数（提交审批） -->
    <el-dialog v-model="adjust.visible" title="调整通道超声参数" width="480px">
      <el-alert type="warning" :closable="false" show-icon style="margin-bottom: 16px">
        提交后进入<b>参数变更审批队列</b>，须经另一名复核人审批→下发→读回验证方可生效，不会立即改变设备。
      </el-alert>
      <el-form label-width="120px" label-position="left">
        <el-form-item label="增益 Gain (dB)"><el-input-number v-model="adjForm.gain" :step="1" :min="0" :max="100" controls-position="right" style="width: 100%" /></el-form-item>
        <el-form-item label="探头频率 (MHz)"><el-input-number v-model="adjForm.probeFreqMhz" :step="0.5" :min="0.5" :max="20" :precision="2" controls-position="right" style="width: 100%" /></el-form-item>
        <el-form-item label="回波窗 (ns)"><el-input-number v-model="adjForm.echoWinNs" :step="100" :min="0" controls-position="right" style="width: 100%" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="adjust.visible = false">取消</el-button>
        <el-button type="primary" :loading="adjust.saving" @click="submitAdjust">提交变更请求</el-button>
      </template>
    </el-dialog>

    <CalibrationWizard
      v-model="wizardOpen"
      :bolt-id="bolt?.id ?? null"
      :channel-id="bolt?.channelId ?? null"
      :bolt-code="bolt?.code ?? ''"
      @done="onWizardDone"
    />
  </div>
</template>

<style scoped>
.bd { display: flex; flex-direction: column; gap: 16px; }
.bd-top { display: flex; align-items: center; justify-content: space-between; }
.bd-top-right { display: flex; gap: 8px; }
.bd-back { display: inline-flex; align-items: center; gap: 6px; color: var(--text-3); cursor: pointer; font-size: 13px; width: fit-content; }
.bd-back:hover { color: var(--accent); }
.calib-cta {
  display: flex; align-items: center; gap: 10px;
  padding: 11px 16px; border-radius: var(--radius-sm);
  border: 1px solid var(--panel-border-strong); background: rgba(34, 211, 238, 0.06);
  color: var(--text-2); font-size: 13px;
}
.calib-cta .el-icon { color: var(--accent); font-size: 17px; }
.calib-cta span:first-of-type { flex: 1; }
.form-sec { font-size: 12px; color: var(--accent-2); letter-spacing: 0.06em; margin: 2px 0 14px; padding-bottom: 7px; border-bottom: 1px solid var(--hairline); }

.stale-banner {
  display: flex; align-items: center; gap: 10px;
  padding: 11px 16px; border-radius: var(--radius-sm);
  background: rgba(148, 163, 184, 0.07); border: 1px solid var(--hairline);
  color: var(--text-2); font-size: 13px; line-height: 1.5;
}
.stale-banner .el-icon { color: var(--warn); font-size: 17px; flex-shrink: 0; }
.stale-banner b { color: var(--text-1); font-weight: 700; }

.head { display: flex; align-items: center; justify-content: space-between; padding: 20px 24px; gap: 24px; flex-wrap: wrap; }
.head-top { display: flex; align-items: center; gap: 12px; }
.head-top h2 { margin: 0; font-size: 24px; font-weight: 700; }
.status-tag { font-size: 12px; padding: 2px 10px; border: 1px solid; border-radius: 12px; }
.head-sub { color: var(--text-3); font-size: 13px; margin-top: 8px; }

/* 关键指标卡：套用全局 .stat（.stat-val--sm 用于较长的双值/温度） */
.kpi-row { display: grid; grid-template-columns: repeat(4, 1fr); gap: 14px; }
.stat-val.stat-val--sm { font-size: 18px; }
@media (max-width: 760px) { .kpi-row { grid-template-columns: repeat(2, 1fr); } }

.panel-hd { display: flex; align-items: center; justify-content: space-between; padding: 16px 20px 4px; }
.chg-reason { display: inline-flex; align-items: center; gap: 10px; font-size: 12.5px; color: var(--text-2); }
.relax-tag { font-size: 11px; padding: 1px 9px; border: 1px solid; border-radius: 10px; }
.chg-body { padding: 12px 20px 20px; }
.chg-metrics { display: grid; grid-template-columns: repeat(4, 1fr); gap: 18px; margin-bottom: 20px; }
.cm { display: flex; flex-direction: column; gap: 5px; }
.cm-k { font-size: 11px; color: var(--text-3); letter-spacing: 0.04em; }
.cm-v { font-size: 26px; font-weight: 700; line-height: 1.1; }
.cm-v.rate-down { color: var(--crit); }
.cm-i { font-size: 11px; color: var(--text-3); }
@media (max-width: 900px) { .chg-metrics { grid-template-columns: repeat(2, 1fr); } }
.trend-ctrls { display: flex; align-items: center; gap: 12px; }
.temp-toggle { display: inline-flex; align-items: center; gap: 5px; font-size: 12px; padding: 4px 12px; border-radius: 8px; cursor: pointer; color: var(--text-3); border: 1px solid transparent; }
.temp-toggle:hover { color: var(--text-1); }
.temp-toggle.active { color: #fb923c; border-color: rgba(251, 146, 60, 0.4); background: rgba(251, 146, 60, 0.08); }
.range-tabs { display: flex; gap: 4px; }
.range-tab { font-size: 12px; padding: 4px 12px; border-radius: 8px; cursor: pointer; color: var(--text-3); border: 1px solid transparent; }
.range-tab:hover { color: var(--text-1); }
.range-tab.active { color: var(--accent); border-color: var(--panel-border-strong); background: rgba(34, 211, 238, 0.08); }
.chart-empty { text-align: center; color: var(--text-3); padding: 16px; font-size: 13px; }
.bolt-missing { display: flex; flex-direction: column; align-items: center; gap: 12px; padding: 60px 20px; color: var(--text-2); }
.bolt-missing .el-icon { font-size: 34px; color: var(--warn); }
.bolt-missing p { margin: 0; font-size: 15px; }
.hd-right .dt-pos { color: var(--ok); }
.hd-right .dt-neg { color: var(--crit); }
.ascan-hd { display: flex; align-items: center; gap: 12px; }
.ascan-meta { font-size: 12px; color: var(--text-3); }
.live-dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; background: #f43f5e; margin-left: 8px; vertical-align: middle; animation: livepulse 1.4s ease-in-out infinite; }
@keyframes livepulse { 0%, 100% { opacity: 1; box-shadow: 0 0 0 0 rgba(244, 63, 94, 0.5); } 50% { opacity: 0.5; box-shadow: 0 0 0 5px rgba(244, 63, 94, 0); } }
.ascan-ctrl { display: inline-flex; align-items: center; gap: 5px; font-size: 12px; padding: 4px 11px; border-radius: 8px; cursor: pointer; color: var(--text-3); border: 1px solid var(--hairline); }
.ascan-ctrl.active { color: #f43f5e; border-color: rgba(244, 63, 94, 0.4); background: rgba(244, 63, 94, 0.08); }
.live-preload { margin: 4px 20px 0; padding: 12px 16px; border: 1px solid rgba(34, 211, 238, 0.22); background: rgba(34, 211, 238, 0.05); border-radius: 12px; }
.lp-now { display: flex; align-items: baseline; gap: 14px; flex-wrap: wrap; }
.lp-k { font-size: 12px; color: var(--text-3); }
.lp-v { font-size: 34px; font-weight: 800; color: #22d3ee; line-height: 1; letter-spacing: -0.5px; }
.lp-v i { font-size: 14px; font-weight: 600; color: var(--text-3); font-style: normal; margin-left: 4px; }
.lp-sub { font-size: 12px; color: var(--text-3); }
.lp-wait { font-size: 12px; color: var(--text-3); padding: 14px 0 6px; }
.tight-bar { display: flex; align-items: center; gap: 14px; margin: 8px 20px 0; padding: 10px 14px; border: 1px solid var(--hairline); border-radius: 10px; font-size: 13px; flex-wrap: wrap; }
.tight-label { font-size: 14px; font-weight: 700; color: #04121a; padding: 3px 14px; border-radius: 8px; }
.tight-dt { color: var(--text-2); }
.tight-dt b { font-weight: 700; font-size: 15px; }
.tight-hint { color: var(--text-3); font-size: 12px; margin-left: auto; }
.tight-nobase { color: var(--text-3); justify-content: center; }

.ascan-legend { display: flex; flex-wrap: wrap; gap: 16px; padding: 4px 20px 16px; }
.ascan-legend .lg { display: inline-flex; align-items: center; gap: 6px; font-size: 11px; color: var(--text-3); }
.lg-line { width: 14px; height: 2px; border-radius: 2px; }
.lg-dot { width: 9px; height: 9px; border-radius: 50%; }
.lg-area { width: 14px; height: 10px; border-radius: 2px; background: rgba(34, 211, 238, 0.18); border: 1px solid rgba(34, 211, 238, 0.4); }

.cfg-grid { display: grid; grid-template-columns: repeat(5, 1fr); gap: 1px; background: var(--hairline); margin: 8px 16px 0; border: 1px solid var(--hairline); border-radius: 10px; overflow: hidden; }
.cfg-cell { display: flex; flex-direction: column; gap: 6px; padding: 13px 14px; background: var(--panel-bg, #0d1320); }
.cfg-cell.cfg-base { background: rgba(34, 211, 238, 0.04); }
.cfg-k { font-size: 11px; color: var(--text-3); letter-spacing: 0.04em; }
.cfg-cell b { font-size: 14px; font-weight: 600; color: var(--text-1); }
.cfg-cell b i { font-size: 11px; color: var(--text-3); font-style: normal; }
.cfg-note { display: flex; align-items: center; gap: 8px; margin: 14px 16px 18px; font-size: 12px; color: var(--text-3); line-height: 1.6; }
.cfg-note b { color: var(--text-2); font-weight: 600; }
@media (max-width: 1100px) { .cfg-grid { grid-template-columns: repeat(3, 1fr); } }

.two-col { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
.tbl { padding: 4px 12px 16px; }
.tr { display: grid; grid-template-columns: 1.6fr 1fr 1fr 0.5fr; align-items: center; padding: 9px 8px; border-radius: 8px; font-size: 12.5px; }
.tr:not(.th) { box-shadow: inset 0 -1px 0 var(--hairline); }
.tr:not(.th):hover { background: rgba(56, 189, 248, 0.05); }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; text-transform: uppercase; }
.tbl-empty, .calib-empty { color: var(--text-3); font-size: 13px; padding: 16px 6px; text-align: center; }

.calib-list { list-style: none; margin: 0; padding: 4px 12px 16px; }
.calib-list li { display: flex; align-items: center; justify-content: space-between; padding: 11px 8px; border-radius: 8px; }
.calib-list li:not(.calib-empty) { box-shadow: inset 0 -1px 0 var(--hairline); }
.calib-list li:not(.calib-empty):hover { background: rgba(56, 189, 248, 0.05); }
.cl-left { display: flex; align-items: center; gap: 11px; }
.cl-method { font-size: 13px; font-weight: 600; }
.cl-active { font-size: 10px; color: var(--accent); border: 1px solid var(--panel-border-strong); border-radius: 8px; padding: 1px 6px; margin-left: 6px; }
.cl-pending { font-size: 10px; color: var(--warn); border: 1px solid var(--warn); border-radius: 8px; padding: 1px 6px; margin-left: 6px; }
.cl-signed { font-size: 10px; color: var(--ok); border: 1px solid rgba(16, 185, 129, 0.5); border-radius: 8px; padding: 1px 6px; margin-left: 6px; }
.cl-self { font-size: 10px; color: var(--text-3); border: 1px solid var(--panel-border-strong); border-radius: 8px; padding: 1px 6px; margin-left: 6px; }
.cl-meta { font-size: 11px; color: var(--text-3); margin-top: 2px; }
.cl-right { display: flex; flex-direction: column; align-items: flex-end; gap: 3px; }
.cl-pass { font-size: 11px; font-weight: 600; }
.cl-pass.ok { color: var(--ok); }
.cl-pass.no { color: var(--crit); }
.cl-date { font-size: 11px; color: var(--text-3); }

.predict { display: flex; align-items: center; gap: 10px; margin: 6px 20px 18px; padding: 12px 16px; border-radius: 10px; font-size: 13px; border: 1px solid; }
.predict.ok { color: var(--ok); border-color: rgba(16, 185, 129, 0.3); background: rgba(16, 185, 129, 0.06); }
.predict.warn { color: var(--warn); border-color: rgba(245, 158, 11, 0.3); background: rgba(245, 158, 11, 0.07); }
.predict.crit { color: var(--crit); border-color: rgba(244, 63, 94, 0.35); background: rgba(244, 63, 94, 0.08); }
.predict.stale { color: var(--text-3); border-color: var(--hairline); background: rgba(148, 163, 184, 0.05); }
.predict b { font-weight: 700; }
.predict-conf { margin-left: auto; font-size: 11px; color: var(--text-3); white-space: nowrap; opacity: 0.85; }

.timeline { list-style: none; margin: 0; padding: 12px 20px 18px; }
.timeline li { display: flex; gap: 14px; padding: 10px 0; position: relative; }
.timeline li:not(:last-child)::before { content: ''; position: absolute; left: 5px; top: 24px; bottom: -10px; width: 1px; background: var(--hairline); }
.tl-dot { width: 11px; height: 11px; border-radius: 50%; flex-shrink: 0; margin-top: 5px; }
.tl-body { flex: 1; min-width: 0; }
.tl-row { display: flex; justify-content: space-between; gap: 10px; }
.tl-title { font-size: 13px; font-weight: 600; }
.tl-time { font-size: 11px; color: var(--text-3); white-space: nowrap; }
.tl-note { font-size: 12px; color: var(--text-3); margin-top: 3px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.tl-empty { color: var(--text-3); font-size: 13px; justify-content: center; }

@media (max-width: 1000px) { .two-col { grid-template-columns: 1fr; } }
</style>
