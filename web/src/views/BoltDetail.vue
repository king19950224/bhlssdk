<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import EChart from '../components/EChart.vue'
import PreloadScale from '../components/PreloadScale.vue'
import { computePreloadHealth, pct, signedN } from '../utils/preload'
import { boltApi, alarmApi, auditApi, waveApi, configChangeApi, deviceApi, calibrationApi, type BoltListItem, type TrendPoint, type CalibRecord, type WaveFile, type WaveFeature, type BoltChannelConfig, type BoltPrediction } from '../api'
import { onRealtime } from '../api/realtime'
import { useAuth } from '../stores/auth'

const route = useRoute()
const router = useRouter()
const auth = useAuth()
const boltId = Number(route.params.boltId)

// 实时波形 + 松紧状态
const live = ref(true)
let liveTimer: number | undefined
let unsubMeas: (() => void) | undefined
const liveEchoNs = ref<number | null>(null)
const liveTempC = ref<number | null>(null)
const baselining = ref(false)

// 实时预紧力滚动序列（拧松/拧紧肉眼可见升降）；livePreloadN/liveRatio 在 tightness 下方按 echo 连续计算
const liveSeries = ref<{ t: number; v: number }[]>([])
let lastLiveTs = ''

const MEAS_MODE: Record<number, string> = { 0: '自动峰值', 1: '位置法(RefTime)', 2: '阈值法' }
const TC_MODE: Record<number, string> = { 0: '不补偿', 1: '材料补偿', 2: '自定义系数' }

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

const STATUS: Record<string, { label: string; color: string; cls: string }> = {
  normal: { label: '正常', color: '#10b981', cls: 'led--ok' },
  warn: { label: '预警', color: '#f59e0b', cls: 'led--warn' },
  loose: { label: '松动', color: '#f43f5e', cls: 'led--crit' },
  uncalibrated: { label: '未标定', color: '#61748f', cls: 'led--off' },
  nodata: { label: '无数据', color: '#3b4659', cls: 'led--off' },
}
// 标定方法枚举的中文映射（与标定中心/标定弹窗保持一致，未知值回退原值）
const METHOD_LABEL: Record<string, string> = {
  load_calibrated: '正式载荷标定', gb_single_wave: 'GB 单波法', gb_bi_wave: 'GB 双波法',
  torque_fit: '扭矩拟合', torque_estimate: '扭矩估算', quick: '快速标定',
}
const num = (v: number | string | null | undefined) => (v == null ? null : Number(v))

// 统一的"状态 + 变化程度"模型（与服务端 bolt_status 口径一致）
const health = computed(() =>
  computePreloadHealth({
    calibrated: bolt.value?.calibrated,
    lastTs: bolt.value?.lastTs ?? null,
    current: bolt.value?.lastPreloadN ?? null,
    target: bolt.value?.targetPreloadN ?? null,
    min: bolt.value?.minPreloadN ?? null,
  }),
)
const status = computed(() => health.value.status)

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

async function loadTrend() {
  const hours = rangeKey.value === '1h' ? 1 : rangeKey.value === '24h' ? 24 : 168
  const since = new Date(Date.now() - hours * 3600 * 1000).toISOString()
  trend.value = (await boltApi.preloadTrend(boltId, `?bucket=auto&since=${since}`)).items
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
function applyLiveMeasurement(tsIso: string, echoNs: number | null, tempC: number | null) {
  if (echoNs != null) {
    liveEchoNs.value = Number(echoNs)
    liveTempC.value = tempC != null ? Number(tempC) : liveTempC.value
  }
  // 新测量到达 → 推入滚动序列（livePreloadN 已随 liveEchoNs 重算；按 ts 去重避免重复累计）
  if (tsIso && tsIso !== lastLiveTs) {
    lastLiveTs = tsIso
    if (livePreloadN.value != null) {
      liveSeries.value.push({ t: new Date(tsIso).getTime(), v: livePreloadN.value })
      if (liveSeries.value.length > 90) liveSeries.value.shift()
    }
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
    ElMessage.success(`基线已设为 ${Number(r.newRefTimeNs).toFixed(0)} ns`)
    chCfg.value = await boltApi.channelConfig(boltId)
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
    calibrations.value = (await boltApi.calibrations(bolt.value.channelId)).items
    chCfg.value = await boltApi.channelConfig(boltId)
  } catch (e) {
    ElMessage.error((e as Error)?.message || '会签失败（需复核员权限，且不能与提交人相同）')
  } finally {
    countersigning.value = null
  }
}

onMounted(async () => {
  try {
    bolt.value = (await boltApi.list()).items.find((b) => b.id === boltId) ?? null
    await loadTrend()
    try { pred.value = await boltApi.prediction(boltId) } catch { /* 预测不可用 */ }
    measurements.value = (await boltApi.measurements(boltId, 24)).items
    if (bolt.value) {
      calibrations.value = (await boltApi.calibrations(bolt.value.channelId)).items
      const [al, au] = await Promise.all([
        alarmApi.list('?includeCleared=true&limit=200'),
        auditApi.query(`?targetId=${bolt.value.channelId}&limit=80`),
      ])
      const ev: typeof events.value = []
      for (const c of calibrations.value)
        ev.push({ at: c.createdAt, kind: 'calib', title: `标定 · ${c.method}`, note: c.passed ? '通过' : '未通过', color: c.passed ? '#10b981' : '#f43f5e' })
      for (const a of al.items.filter((x) => x.boltId === boltId))
        ev.push({ at: a.triggeredAt, kind: 'alarm', title: `告警 · ${a.type}`, note: a.message || '', color: '#f59e0b' })
      for (const e of au.items)
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
      unsubMeas = onRealtime('OnMeasurement', (m) => {
        if (!live.value || !chCfg.value) return
        if (m.deviceId === chCfg.value.deviceId && m.channel === chCfg.value.channelNo)
          applyLiveMeasurement(m.ts, m.echoTimeNs, m.spTempC)
      })
    }
  } finally {
    loading.value = false
  }
})

onUnmounted(() => {
  stopLive()
  unsubMeas?.()
})

// A-Scan 解析：绝对时间轴(echoStart + i·clockInt)、检出点、零交叉、RefTime/ΔTOF、闸门
const waveStats = computed(() => {
  const w = wave.value
  if (!w || !w.waveData.length) return null
  const clk = w.clockInt || 1
  const x0 = w.echoStart || 0
  const n = w.waveData.length
  const xmin = x0
  const xmax = x0 + (n - 1) * clk
  const t = (i: number) => x0 + i * clk
  let peakIdx = 0, peakAbs = -1
  for (let i = 0; i < n; i++) { const a = Math.abs(w.waveData[i]); if (a > peakAbs) { peakAbs = a; peakIdx = i } }
  // 检出点：首个 |样值| ≥ |阈值|；无阈值或未越限则回退到峰值
  const thr = w.threshold != null ? Math.abs(w.threshold) : null
  let detIdx = -1
  if (thr != null && thr > 0) for (let i = 0; i < n; i++) { if (Math.abs(w.waveData[i]) >= thr) { detIdx = i; break } }
  if (detIdx < 0) detIdx = peakIdx
  const detNs = t(detIdx)
  // 零交叉：检出点之后首个过零，线性插值到亚采样时间（TOF 的精细参考点）
  let zeroNs: number | null = null
  for (let i = detIdx; i < n - 1; i++) {
    const a = w.waveData[i], b = w.waveData[i + 1]
    if ((a <= 0 && b > 0) || (a >= 0 && b < 0)) { zeroNs = t(i) + (a === b ? 0 : a / (a - b)) * clk; break }
  }
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
  return { w, clk, x0, n, xmin, xmax, peakIdx, peakAbs, detIdx, detNs, zeroNs, thr, refNs, refInWin, deltaTof, gate }
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
  if (s.refInWin) lines.push({ xAxis: s.refNs, lineStyle: { color: '#22d3ee', type: 'dashed', width: 1.4 }, label: { formatter: 'RefTime', color: '#22d3ee', position: 'insideEndBottom', fontSize: 10 } })
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
    bolt.value = (await boltApi.list()).items.find((b) => b.id === boltId) ?? bolt.value
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

  // 到限投影：把下降预测画到图上（从最后点外推到下限）
  if (prediction.value?.trend === 'falling' && prediction.value.days != null && min != null) {
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
function fmtTime(iso: string) {
  return new Date(iso).toLocaleString('zh-CN', { hour12: false })
}
function ratioStr(r: number | null | undefined) {
  return r == null ? '—' : (Number(r) * 100).toFixed(1) + '%'
}
</script>

<template>
  <div class="bd rise" v-loading="loading">
    <div class="bd-top">
      <div class="bd-back" @click="router.back()"><el-icon><ArrowLeft /></el-icon> 返回</div>
      <el-button v-if="bolt" size="small" @click="openSettings"><el-icon><Setting /></el-icon> 编辑预紧力设置</el-button>
    </div>

    <!-- 头部 -->
    <div class="panel panel--glow head" v-if="bolt">
      <div class="head-left">
        <div class="head-top">
          <span class="led" :class="STATUS[status].cls" />
          <h2>{{ bolt.code }}</h2>
          <span class="status-tag" :style="`color:${STATUS[status].color};border-color:${STATUS[status].color}`">{{ STATUS[status].label }}</span>
        </div>
        <div class="head-sub">{{ bolt.nominalDiam }} · {{ bolt.grade }} 级 · CH{{ bolt.channelNo }} · {{ bolt.calibrated ? '已标定' : '未标定' }}</div>
      </div>
      <div class="head-metrics">
        <div class="hm"><span class="hm-k">当前预紧力</span><b class="metric" :style="`color:${STATUS[status].color}`">{{ fmtN(bolt.lastPreloadN) }}<i>N</i></b></div>
        <div class="hm"><span class="hm-k">当前比值</span><b class="metric">{{ ratioStr(bolt.lastRatio) }}</b></div>
        <div class="hm"><span class="hm-k">目标 / 下限</span><b class="metric small">{{ fmtN(bolt.targetPreloadN) }} / {{ fmtN(bolt.minPreloadN) }}</b></div>
        <div class="hm"><span class="hm-k">温度</span><b class="metric small">{{ bolt.lastSpTempC != null ? Number(bolt.lastSpTempC).toFixed(1) + '°C' : '—' }}</b></div>
      </div>
    </div>

    <!-- 状态 · 预紧力变化程度 -->
    <div class="panel panel--glow" v-if="bolt">
      <div class="panel-hd">
        <span class="panel-title">状态 · 预紧力变化程度</span>
        <span class="hd-right chg-reason">
          <span v-if="health.relax" class="relax-tag" :style="`color:${health.relax.color};border-color:${health.relax.color}`">{{ health.relax.label }}</span>
          {{ health.reason }}
        </span>
      </div>
      <div class="chg-body">
        <div class="chg-metrics">
          <div class="cm">
            <span class="cm-k">残余预紧力率</span>
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
            <b class="cm-v" :class="prediction?.trend === 'falling' ? 'rate-down' : ''">
              {{ !prediction || prediction.trend === 'insufficient' ? '—' : prediction.trend === 'stable' ? '平稳' : signedN(prediction.perDay) + ' N/天' }}
            </b>
            <span class="cm-i">
              <template v-if="prediction?.trend === 'falling' && prediction.days != null">约 {{ Math.max(0, Math.round(prediction.days)) }} 天到下限</template>
              <template v-else-if="prediction?.trend === 'insufficient'">样本不足({{ prediction.samples }})</template>
              <template v-else>近{{ prediction?.windowDays ?? 7 }}天{{ prediction?.r2 != null ? ' · R² ' + prediction.r2.toFixed(2) : '' }}</template>
            </span>
          </div>
        </div>
        <PreloadScale :current="num(bolt.lastPreloadN)" :target="num(bolt.targetPreloadN)" :min="num(bolt.minPreloadN)" :height="14" />
      </div>
    </div>

    <!-- 趋势 -->
    <div class="panel panel--glow">
      <div class="panel-hd">
        <span class="panel-title">预紧力趋势</span>
        <div class="trend-ctrls">
          <span class="temp-toggle" :class="{ active: showTemp }" @click="showTemp = !showTemp"><el-icon><Sunny /></el-icon> 温度</span>
          <div class="range-tabs">
            <span v-for="k in (['1h', '24h', '7d'] as const)" :key="k" class="range-tab" :class="{ active: rangeKey === k }" @click="setRange(k)">{{ k }}</span>
          </div>
        </div>
      </div>
      <EChart :option="trendOption" height="280px" />
      <div v-if="!loading && !trend.length" class="chart-empty">该时段无测量数据</div>
      <div v-if="prediction && prediction.trend !== 'insufficient'" class="predict" :class="prediction.trend === 'falling' ? (prediction.days != null && prediction.days < 30 ? 'crit' : 'warn') : 'ok'">
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
    <div class="panel panel--glow">
      <div class="panel-hd">
        <span class="panel-title">实时回波波形 A-Scan <span v-if="live" class="live-dot" /></span>
        <div class="ascan-hd">
          <span v-if="waveStats" class="mono ascan-meta">检出 {{ waveStats.detNs.toFixed(0) }}ns · SNR {{ waveFeat?.snrDb != null ? Number(waveFeat.snrDb).toFixed(1) : '—' }}dB · 阈值 {{ wave?.threshold ?? '—' }}</span>
          <span class="ascan-ctrl" :class="{ active: live }" @click="toggleLive"><el-icon><component :is="live ? 'VideoPause' : 'VideoPlay'" /></el-icon> {{ live ? '实时' : '已暂停' }}</span>
          <el-button size="small" :loading="baselining" @click="setBaseline"><el-icon><Aim /></el-icon> 设当前为基线</el-button>
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
      <div v-else-if="wave" class="tight-bar tight-nobase">未设基线 — 拧到目标状态后点「设当前为基线」,之后即可看松紧</div>

      <EChart v-if="wave" :option="ascanOption" height="240px" />
      <div v-if="waveStats" class="ascan-legend">
        <span class="lg"><i class="lg-line" style="background:#22d3ee" />回波</span>
        <span class="lg"><i class="lg-dot" style="background:#f43f5e" />检出点 {{ waveStats.detNs.toFixed(0) }}ns</span>
        <span v-if="waveStats.zeroNs != null" class="lg"><i class="lg-dot" style="background:#a78bfa" />零交叉 {{ waveStats.zeroNs.toFixed(1) }}ns</span>
        <span class="lg"><i class="lg-line" style="background:#a78bfa" />包络</span>
        <span class="lg"><i class="lg-line" style="background:#f59e0b" />阈值带 ±{{ waveStats.thr ?? '—' }}</span>
        <span v-if="waveStats.refInWin" class="lg"><i class="lg-line" style="background:#22d3ee;border-top:1px dashed #22d3ee;background:none" />RefTime {{ Number(waveStats.refNs).toFixed(0) }}ns</span>
        <span v-if="waveStats.gate" class="lg"><i class="lg-area" />测量闸门</span>
      </div>
      <div v-if="!wave" class="chart-empty">该通道暂无回波波形(仅采样通道上传 EchoWave)</div>
    </div>

    <!-- 通道超声参数 + 标定基线 -->
    <div class="panel panel--glow" v-if="chCfg">
      <div class="panel-hd">
        <span class="panel-title">通道超声参数 · 标定基线</span>
        <el-button size="small" @click="openAdjust"><el-icon><MagicStick /></el-icon> 调整参数(提交审批)</el-button>
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
        增益 / 探头频率 / 回波窗可在线调整(经审批下发);<b>阈值、测量模式、RefTime/RefTemp/K_L 为标定基线,须经重标定流程变更</b>。配置来源:{{ chCfg.configSource || '—' }}
      </div>
    </div>

    <!-- 测量 + 标定 -->
    <div class="two-col">
      <div class="panel panel--glow">
        <div class="panel-hd"><span class="panel-title">近期测量</span></div>
        <div class="tbl">
          <div class="tr th"><span>时间</span><span>声时 ns</span><span>温度</span><span>状态</span></div>
          <div v-for="(m, i) in measurements" :key="i" class="tr">
            <span class="mono">{{ fmtTime(m.ts) }}</span>
            <span class="mono">{{ m.echoTimeNs != null ? Number(m.echoTimeNs).toFixed(0) : '—' }}</span>
            <span class="mono">{{ m.spTempC != null ? Number(m.spTempC).toFixed(1) + '°C' : '—' }}</span>
            <span><span class="led" :class="m.statusCode >= 0 ? 'led--ok' : 'led--crit'" /></span>
          </div>
          <div v-if="!loading && !measurements.length" class="tbl-empty">无测量记录</div>
        </div>
      </div>

      <div class="panel panel--glow">
        <div class="panel-hd"><span class="panel-title">标定档案</span></div>
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
    <div class="panel panel--glow">
      <div class="panel-hd"><span class="panel-title">螺栓履历</span></div>
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
  </div>
</template>

<style scoped>
.bd { display: flex; flex-direction: column; gap: 16px; }
.bd-top { display: flex; align-items: center; justify-content: space-between; }
.bd-back { display: inline-flex; align-items: center; gap: 6px; color: var(--text-3); cursor: pointer; font-size: 13px; width: fit-content; }
.bd-back:hover { color: var(--accent); }
.form-sec { font-size: 12px; color: var(--accent-2); letter-spacing: 0.06em; margin: 2px 0 14px; padding-bottom: 7px; border-bottom: 1px solid var(--hairline); }

.head { display: flex; align-items: center; justify-content: space-between; padding: 22px 26px; gap: 24px; flex-wrap: wrap; }
.head-top { display: flex; align-items: center; gap: 12px; }
.head-top h2 { margin: 0; font-size: 24px; font-weight: 700; }
.status-tag { font-size: 12px; padding: 2px 10px; border: 1px solid; border-radius: 12px; }
.head-sub { color: var(--text-3); font-size: 13px; margin-top: 8px; }
.head-metrics { display: flex; gap: 30px; }
.hm { display: flex; flex-direction: column; gap: 5px; }
.hm-k { font-size: 11px; color: var(--text-3); letter-spacing: 0.08em; }
.hm b { font-size: 24px; font-weight: 700; }
.hm b.small { font-size: 16px; color: var(--text-2); }
.hm b i { font-size: 13px; color: var(--text-3); font-style: normal; margin-left: 3px; }

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
.tbl { padding: 6px 16px 16px; }
.tr { display: grid; grid-template-columns: 1.6fr 1fr 1fr 0.5fr; align-items: center; padding: 8px 6px; border-bottom: 1px solid var(--hairline); font-size: 12.5px; }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; }
.tbl-empty, .calib-empty { color: var(--text-3); font-size: 13px; padding: 16px 6px; text-align: center; }

.calib-list { list-style: none; margin: 0; padding: 6px 16px 16px; }
.calib-list li { display: flex; align-items: center; justify-content: space-between; padding: 11px 6px; border-bottom: 1px solid var(--hairline); }
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
