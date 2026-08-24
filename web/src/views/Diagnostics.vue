<script setup lang="ts">
// 信号诊断（复核员以上）：看 A-Scan 波形、跟参考波形比一比，顺手把通道参数从设备读回来核对。
// 调试调参：全通道可在本页直调增益/回波窗等（直发设备+审计；2026-08-19 取消审批环节），
// 波形 1.5s 轮询即时反映效果——"看着波形调参"的现场闭环。基线类参数仍走重标定。
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import EChart from '../components/EChart.vue'
import { waveApi, channelApi, configChangeApi, type ChannelRow, type WaveFeature, type WaveCompareResult, type WaveCompareWave, type XcorrTofResult } from '../api'
import { analyzeAscan } from '../utils/ascan'

const features = ref<WaveFeature[]>([])
const cmp = ref<WaveCompareResult | null>(null)
const activeChannelId = ref<number | null>(null)
const loading = ref(false)
const loadingCmp = ref(false)
const settingRef = ref(false)
const error = ref<string | null>(null)

// 实时显示：勾选后每 1.5s 轮询，自动跟随最新波形所在通道刷新对比诊断
const live = ref(true)
const follow = ref(true)
const lastUpdate = ref<number | null>(null)
let liveTimer: number | undefined
let ticking = false
let disposed = false

// activeChannelId 是 DB 主键（compare API 需要它）；展示要用物理通道号 channel_no
const activeChannelNo = computed(() => features.value.find((f) => f.channelId === activeChannelId.value)?.channelNo ?? null)
const cur = computed(() => cmp.value?.current ?? null)
const refWave = computed(() => cmp.value?.reference ?? null)
const metrics = computed(() => cmp.value?.metrics ?? null)
const hasRef = computed(() => !!cmp.value?.hasReference)
// 波形文件损坏：请求是成功的（HTTP 200），所以不会进 error 那条横幅，但也绝不是"暂无波形"。
// 三种情况要有三句话：请求失败 / 文件损坏 / 确实还没采到波形。
const waveFileError = computed(() => cmp.value?.waveError ?? null)
const ascanEmptyText = computed(() => {
  if (loading.value) return '加载中…'
  if (error.value) return '波形加载失败，请点上方“重试”。'
  if (waveFileError.value) return waveFileError.value
  return '暂无波形数据。设备产生带波形的测量后，可在此实时回放并对比 A-Scan。'
})

async function loadFeatures() {
  const res = await waveApi.features(60)
  if (disposed) return
  features.value = res.items
}

async function loadCompare(channelId: number) {
  loadingCmp.value = true
  try {
    const res = await waveApi.compare(channelId)
    if (disposed) return
    cmp.value = res
    activeChannelId.value = channelId
    lastUpdate.value = Date.now()
    error.value = null
  } catch (e) {
    if (disposed) return
    cmp.value = null
    error.value = e instanceof Error ? e.message : '波形对比加载失败'
  } finally {
    if (!disposed) loadingCmp.value = false
  }
}

// 点选某通道波形：若点的不是最新一条则暂停跟随，停在该通道
async function selectChannel(f: WaveFeature) {
  follow.value = features.value.length > 0 && f.id === features.value[0].id
  await loadCompare(f.channelId)
}

async function tick() {
  if (ticking || disposed) return
  ticking = true
  try {
    await loadFeatures()
    if (disposed) return
    const latest = features.value[0]
    if (follow.value && latest) {
      await loadCompare(latest.channelId)
    } else {
      lastUpdate.value = Date.now()
      error.value = null
    }
  } catch (e) {
    if (!disposed) error.value = e instanceof Error ? e.message : '波形刷新失败'
  } finally {
    ticking = false
  }
}

function startLive() {
  stopLive()
  if (!live.value) return
  // 标签页隐藏时跳过这一拍（配合 visibilitychange 暂停），避免后台标签页持续拉全波形
  liveTimer = window.setInterval(() => { if (!document.hidden) tick() }, 1500)
}
function stopLive() {
  if (liveTimer) {
    clearInterval(liveTimer)
    liveTimer = undefined
  }
}
function onLiveChange() {
  if (live.value) {
    follow.value = true
    startLive()
    tick()
  } else {
    stopLive()
  }
}
function resumeFollow() {
  follow.value = true
  if (features.value[0]) loadCompare(features.value[0].channelId)
}

function retry() {
  error.value = null
  if (!follow.value && activeChannelId.value != null) loadCompare(activeChannelId.value)
  else tick()
}

// 标签页隐藏时暂停实时轮询，可见时恢复并立即刷新一次
function onVisibility() {
  if (!live.value) return
  if (document.hidden) stopLive()
  else { startLive(); tick() }
}

// ---- 调试调参（看着波形调，仅未标定通道；已标定走参数审批）----
const channels = ref<ChannelRow[]>([])
const activeChannelRow = computed(() => channels.value.find((c) => c.id === activeChannelId.value) ?? null)
// 已标定通道的直发不再被拦（审批环节已取消），标记只用于提示"下发后核对读数"
const activeCalibrated = computed(() => activeChannelRow.value?.kL != null)
const tune = reactive({
  gain: null as number | null,
  echoWinNs: null as number | null,
  echoWidthUs: null as number | null,
  probeFreqMhz: null as number | null,
  sending: false,
  open: false,
})
async function loadChannels() {
  try {
    const res = await channelApi.list()
    if (!disposed) channels.value = res.items
  } catch { /* 面板降级隐藏，不拖垮波形页 */ }
}
async function sendTune() {
  const row = activeChannelRow.value
  if (!row) return
  const updates: { gain?: number; probeFreqMhz?: number; echoWinNs?: number; echoWidthUs?: number } = {}
  if (tune.gain != null) updates.gain = tune.gain
  if (tune.echoWinNs != null) updates.echoWinNs = tune.echoWinNs
  if (tune.echoWidthUs != null) updates.echoWidthUs = tune.echoWidthUs
  if (tune.probeFreqMhz != null) updates.probeFreqMhz = tune.probeFreqMhz
  if (!Object.keys(updates).length) return ElMessage.warning('没有要下发的参数（留空 = 不改）')
  tune.sending = true
  try {
    await configChangeApi.commission(row.deviceId, row.channelNo, updates)
    ElMessage.success('参数已下发。观察波形变化，锁到真实回波后请做重标定设基线。')
  } catch (e) {
    ElMessage.error((e as Error)?.message || '下发失败')
  } finally {
    tune.sending = false
  }
}

// ---- 相关法声时稳定性诊断（旁路证据）----
// 阈值法声时对回波幅度敏感（耦合稍变、过阈时刻就漂）；相关法对齐整段波形形态、对幅度免疫。
// 这里按需触发一次对比：窗口内逐波形算相关法 ΔTOF，与设备阈值法 EchoTime 的波动并排给结论。
const xd = reactive({
  running: false,
  minutes: 60,
  result: null as XcorrTofResult | null,
  error: null as string | null,
  open: false,
})
async function runXcorr() {
  if (activeChannelId.value == null) return
  xd.running = true
  xd.error = null
  try {
    xd.result = await waveApi.xcorrTof(activeChannelId.value, xd.minutes)
  } catch (e) {
    xd.result = null
    xd.error = (e as Error)?.message || '诊断失败'
  } finally {
    xd.running = false
  }
}

// 把当前波形设为该通道的参考基线（健康基线）
async function setAsReference() {
  const fid = cur.value?.featureId
  if (!fid || activeChannelId.value == null) return
  settingRef.value = true
  try {
    await waveApi.setReference(fid, '波形诊断页设为基线')
    ElMessage.success('已设为该通道参考波形')
    await loadCompare(activeChannelId.value)
  } catch (e) {
    ElMessage.error((e as Error)?.message || '设参考失败（需写权限）')
  } finally {
    settingRef.value = false
  }
}

onMounted(async () => {
  loading.value = true
  try {
    await loadFeatures()
    if (!disposed && features.value.length) await loadCompare(features.value[0].channelId)
  } catch (e) {
    if (!disposed) error.value = e instanceof Error ? e.message : '加载失败'
  } finally {
    if (!disposed) loading.value = false
  }
  loadChannels()
  startLive()
  document.addEventListener('visibilitychange', onVisibility)
})
onUnmounted(() => {
  disposed = true
  stopLive()
  document.removeEventListener('visibilitychange', onVisibility)
})

// A-Scan 基础解析走共享 util；保留 d 别名兼容下方图表 option 里的 s.d 引用
function computeStats(w: WaveCompareWave | null) {
  const s = analyzeAscan(w)
  return s ? { ...s, d: s.waveData } : null
}

const curStats = computed(() => computeStats(cur.value))

const ascanOption = computed(() => {
  const s = curStats.value
  if (!s) return {}
  const data = s.d.map((v, i) => [s.x0 + i * s.clk, v])
  // 参考波形（虚线叠加）
  let refData: number[][] = []
  let xmin = s.xmin
  let xmax = s.xmax
  const rw = refWave.value
  if (rw && rw.waveData.length) {
    const rclk = rw.clockInt || 1
    const rx0 = rw.echoStart || 0
    refData = rw.waveData.map((v, i) => [rx0 + i * rclk, v])
    xmin = Math.min(xmin, rx0)
    xmax = Math.max(xmax, rx0 + (rw.waveData.length - 1) * rclk)
  }
  const lines: Record<string, unknown>[] = []
  if (s.thr != null && s.thr > 0) {
    lines.push({ yAxis: s.thr, lineStyle: { color: '#f59e0b', type: 'dashed', opacity: 0.6 }, label: { formatter: '阈值', color: '#f59e0b', position: 'insideStartTop', fontSize: 10 } })
    lines.push({ yAxis: -s.thr, lineStyle: { color: '#f59e0b', type: 'dashed', opacity: 0.6 }, label: { show: false } })
  }
  lines.push({ xAxis: s.detNs, lineStyle: { color: '#f43f5e', width: 1.4 }, label: { formatter: '检出', color: '#f43f5e', position: 'insideEndTop', fontSize: 10 } })
  const mp: Record<string, unknown>[] = [
    { coord: [s.x0 + s.peakIdx * s.clk, s.d[s.peakIdx]], symbol: 'pin', symbolSize: 36, itemStyle: { color: 'rgba(244,63,94,0.85)' }, label: { formatter: '峰', color: '#fff', fontSize: 10 } },
  ]
  if (s.zeroNs != null) mp.push({ coord: [s.zeroNs, 0], symbol: 'circle', symbolSize: 9, itemStyle: { color: '#a78bfa', borderColor: '#fff', borderWidth: 1 }, label: { show: false } })
  const series: Record<string, unknown>[] = []
  if (refData.length) {
    series.push({
      name: '参考波形', type: 'line', symbol: 'none', data: refData, sampling: 'lttb', z: 2,
      lineStyle: { color: 'rgba(148,163,184,0.85)', width: 1.2, type: 'dashed' }, silent: true,
    })
  }
  series.push({
    name: '当前波形', type: 'line', symbol: 'none', data, sampling: 'lttb', z: 3,
    lineStyle: { color: '#22d3ee', width: 1.5 },
    markLine: { symbol: 'none', data: lines, animation: false },
    markPoint: { data: mp },
  })
  return {
    grid: { left: 52, right: 18, top: 18, bottom: 34 },
    tooltip: { trigger: 'axis', backgroundColor: '#0d1320', borderColor: 'rgba(56,189,248,0.3)', textStyle: { color: '#e8eef8' } },
    legend: refData.length ? { data: ['当前波形', '参考波形'], textStyle: { color: '#61748f', fontSize: 11 }, right: 10, top: 2 } : undefined,
    xAxis: { type: 'value', min: xmin, max: xmax, name: '时间 ns', nameTextStyle: { color: '#61748f', fontSize: 11 }, axisLabel: { color: '#61748f', fontSize: 11 }, axisLine: { lineStyle: { color: 'rgba(148,163,184,0.2)' } }, splitLine: { show: false } },
    yAxis: { type: 'value', scale: true, name: '幅值 (mV)', nameTextStyle: { color: '#61748f', fontSize: 11 }, axisLabel: { color: '#61748f', fontSize: 11 }, splitLine: { lineStyle: { color: 'rgba(148,163,184,0.08)' } } },
    series,
    animationDuration: 200,
  }
})

function healthColor(h: number) { return h >= 80 ? '#10b981' : h >= 50 ? '#f59e0b' : '#f43f5e' }
function simColor(s: number) { return s >= 0.95 ? '#10b981' : s >= 0.85 ? '#f59e0b' : '#f43f5e' }
function attenColor(a: number) { return a >= 0.85 ? '#10b981' : a >= 0.7 ? '#f59e0b' : '#f43f5e' }
function fmt(iso: string) { return new Date(iso).toLocaleString('zh-CN', { hour12: false }) }
function fmtClock(ms: number | null) { return ms == null ? '—' : new Date(ms).toLocaleTimeString('zh-CN', { hour12: false }) }
</script>

<template>
  <div class="diag rise">
    <!-- 加载失败横幅：把“请求失败”与“暂无波形”明确区分，并提供重试 -->
    <div v-if="error" class="diag-error">
      <el-icon><WarningFilled /></el-icon>
      <span class="diag-error-msg">加载失败：{{ error }}</span>
      <el-button size="small" type="danger" plain @click="retry">重试</el-button>
    </div>

    <!-- A-Scan + 对比 -->
    <div class="panel panel--glow">
      <div class="panel-hd">
        <span class="panel-title">
          <i class="hd-mark" />实时回波波形 A-Scan
          <span v-if="live" class="live-dot" />
          <span v-if="cur" class="hd-sub">· CH{{ activeChannelNo ?? '?' }} · {{ cur.pointCount }} 点</span>
        </span>
        <div class="ascan-hd">
          <span v-if="cur" class="mono ascan-meta">SNR {{ cur.snrDb != null ? Number(cur.snrDb).toFixed(1) : '—' }}dB · 阈值 {{ cur.threshold ?? '—' }}</span>
          <span v-if="live" class="ascan-meta dim">{{ fmtClock(lastUpdate) }} 刷新</span>
          <el-button size="small" :loading="settingRef" :disabled="!cur" @click="setAsReference">
            <el-icon><Aim /></el-icon> {{ hasRef ? '更新参考' : '设为参考' }}
          </el-button>
          <el-switch v-model="live" @change="onLiveChange" inline-prompt active-text="实时" inactive-text="暂停" style="--el-switch-on-color: #f43f5e" />
        </div>
      </div>

      <!-- 跟随暂停提示（手动切到某通道时） -->
      <div v-if="live && !follow" class="follow-bar">
        <span><el-icon><View /></el-icon> 已暂停跟随，停在 CH{{ activeChannelNo ?? '?' }}</span>
        <span class="follow-resume" @click="resumeFollow"><el-icon><VideoPlay /></el-icon> 跟随最新</span>
      </div>

      <!-- 信号质量诊断指标（§8.6） -->
      <div v-if="metrics && hasRef" class="metrics">
        <div class="metric">
          <span class="m-k">健康度</span>
          <b class="m-v" :style="`color:${healthColor(metrics.healthScore)}`">{{ metrics.healthScore }}<i>/100</i></b>
          <div class="m-bar"><span :style="`width:${metrics.healthScore}%;background:${healthColor(metrics.healthScore)}`" /></div>
        </div>
        <div class="metric">
          <span class="m-k">波形相似度</span>
          <b class="m-v" :style="`color:${simColor(metrics.similarity)}`">{{ (metrics.similarity * 100).toFixed(1) }}<i>%</i></b>
          <span class="m-i">归一化互相关 · vs 基线</span>
        </div>
        <div class="metric">
          <span class="m-k">幅度衰减</span>
          <b class="m-v" :style="`color:${attenColor(metrics.attenuationRatio)}`">{{ (metrics.attenuationRatio * 100).toFixed(0) }}<i>%</i></b>
          <span class="m-i">峰峰值 / 基线</span>
        </div>
        <div class="metric">
          <span class="m-k">波形平移 ΔTOF</span>
          <b class="m-v">{{ (metrics.bestLagNs >= 0 ? '+' : '') + metrics.bestLagNs.toFixed(1) }}<i>ns</i></b>
          <span class="m-i">相对参考最佳对齐</span>
        </div>
      </div>
      <div v-else-if="cur && !hasRef" class="metrics-empty">
        <el-icon><InfoFilled /></el-icon>
        该通道尚未设置参考波形。波形稳定（已拧到目标、耦合良好）时点“设为参考”，之后即可实时对比相似度 / 衰减 / 健康度，预警探头老化与耦合劣化。
      </div>

      <EChart v-if="cur" :option="ascanOption" height="300px" />
      <div v-else class="ascan-empty" :class="{ 'ascan-empty--bad': waveFileError }" v-loading="loadingCmp">
        <el-icon><WarningFilled v-if="waveFileError" /><TrendCharts v-else /></el-icon>
        <span>{{ ascanEmptyText }}</span>
      </div>

      <div v-if="curStats" class="ascan-legend">
        <span class="lg"><i class="lg-line" style="background:#22d3ee" />当前波形</span>
        <span v-if="hasRef" class="lg"><i class="lg-line lg-dash" style="background:#94a3b8" />参考波形</span>
        <span class="lg"><i class="lg-dot" style="background:#f43f5e" />检出点 {{ curStats.detNs.toFixed(0) }}ns</span>
        <span v-if="curStats.zeroNs != null" class="lg"><i class="lg-dot" style="background:#a78bfa" />零交叉 {{ curStats.zeroNs.toFixed(1) }}ns</span>
        <span v-if="curStats.thr != null" class="lg"><i class="lg-line" style="background:#f59e0b" />阈值带 ±{{ curStats.thr }}</span>
      </div>
      <div v-if="metrics && hasRef" class="diag-note">
        参考口径：经验阈值（相似度&lt;0.85、健康度&lt;50 提示耦合/探头劣化）<b>仅作诊断展示，未驱动告警</b>。
      </div>

      <!-- 调参：看着上方波形直调，全通道直发设备（审计留痕）。2026-08-19 取消审批环节 -->
      <div v-if="activeChannelRow" class="tune">
        <div class="tune-hd" @click="tune.open = !tune.open">
          <span class="tune-title"><el-icon><Operation /></el-icon> 调试调参 · CH{{ activeChannelRow.channelNo }}
            <span v-if="activeCalibrated" class="tune-tag tune-tag--locked">已标定 · 下发后核对读数</span>
            <span v-else class="tune-tag">未标定 · 可直发</span>
          </span>
          <span class="dim">{{ tune.open ? '收起 ▲' : '展开 ▼' }}</span>
        </div>
        <template v-if="tune.open">
          <div class="tune-body">
            <div class="tune-fields">
              <label class="tune-f"><span>增益（1~47 档）</span>
                <el-input-number v-model="tune.gain" :min="1" :max="47" :step="1" size="small" controls-position="right" style="width: 110px" /></label>
              <label class="tune-f"><span>回波窗（ns）</span>
                <el-input-number v-model="tune.echoWinNs" :min="0" :max="200000" :step="100" size="small" controls-position="right" style="width: 130px" /></label>
              <label class="tune-f"><span>回波宽度（µs）</span>
                <el-input-number v-model="tune.echoWidthUs" :min="1" :max="1000" :step="1" size="small" controls-position="right" style="width: 110px" /></label>
              <label class="tune-f"><span>探头频率（MHz）</span>
                <el-input-number v-model="tune.probeFreqMhz" :min="0.5" :max="10" :step="0.5" size="small" controls-position="right" style="width: 110px" /></label>
              <el-button type="primary" size="small" :loading="tune.sending" @click="sendTune">下发设备</el-button>
            </div>
            <div class="tune-hint">
              留空 = 不改，只下发填了的参数。下发即写设备（审计留痕），上方波形 1.5 秒内反映效果——
              增益调到回波峰清晰高于阈值带、不削顶为宜。<b>已标定通道</b>下发后核对实时读数（增益/回波窗会平移声时检出，
              漂移明显需重新置零/标定）；未标定通道锁到真实回波后执行<b>重标定</b>设基线，再进标定流程。
            </div>
          </div>
        </template>
      </div>

      <!-- 相关法声时诊断：旁路证据，不改在产计算 -->
      <div v-if="activeChannelId != null" class="tune">
        <div class="tune-hd" @click="xd.open = !xd.open">
          <span class="tune-title"><el-icon><DataAnalysis /></el-icon> 相关法声时 · 稳定性对比
            <span class="tune-tag">旁路诊断</span>
          </span>
          <span class="dim">{{ xd.open ? '收起 ▲' : '展开 ▼' }}</span>
        </div>
        <template v-if="xd.open">
          <div class="tune-body">
            <div class="tune-fields">
              <label class="tune-f"><span>回看窗口（分钟）</span>
                <el-input-number v-model="xd.minutes" :min="5" :max="1440" :step="30" size="small" controls-position="right" style="width: 120px" /></label>
              <el-button type="primary" size="small" :loading="xd.running" :disabled="!hasRef" @click="runXcorr">运行诊断</el-button>
              <span v-if="!hasRef" class="dim">需要先"设为参考"——相关法以参考波形为对齐基准</span>
            </div>
            <div v-if="xd.error" class="xd-err">{{ xd.error }}</div>
            <div v-else-if="xd.result" class="xd-result">
              <div class="xd-stats">
                <span>样本 <b class="mono">{{ xd.result.usableCount }}/{{ xd.result.waveCount }}</b></span>
                <span>阈值法波动 <b class="mono">{{ xd.result.deviceEchoStddevNs != null ? '±' + xd.result.deviceEchoStddevNs + ' ns' : '—' }}</b></span>
                <span>相关法波动 <b class="mono" style="color: var(--ok)">±{{ xd.result.xcorrLagStddevNs }} ns</b></span>
                <span v-if="xd.result.improvementRatio != null">稳定性比 <b class="mono" :style="xd.result.improvementRatio >= 1.5 ? 'color: var(--ok)' : ''">{{ xd.result.improvementRatio }}×</b></span>
              </div>
              <div class="tune-hint">{{ xd.result.verdict }}<template v-if="xd.result.lowSimilarityCount || xd.result.corruptCount">
                （剔除：低相似 {{ xd.result.lowSimilarityCount }} 条 / 损坏 {{ xd.result.corruptCount }} 条）</template></div>
            </div>
            <div v-else class="tune-hint">
              原理：设备阈值法的过阈时刻会随回波幅度漂移（耦合稍变就像声时变了）；相关法对齐整段波形形态，
              对幅度免疫。此诊断只做对比取证，<b>不改变任何在产计算</b>——数据说话后再决定是否切换跟踪算法。
            </div>
          </div>
        </template>
      </div>
    </div>

    <!-- 波形特征（全局，点选切换通道） -->
    <div class="panel panel--glow">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark" />波形特征 · 信号质量 <span v-if="live" class="live-dot" /></span>
        <span class="hd-sub">点选某条切换到该通道做对比</span>
      </div>
      <div class="tbl">
        <div class="tr th"><span>时间</span><span>通道/螺栓</span><span>SNR dB</span><span>峰峰值</span><span>噪声 RMS</span><span>阈值</span><span>点数</span></div>
        <div v-for="(f, i) in features" :key="f.id" class="tr" :class="{ active: activeChannelId === f.channelId }" @click="selectChannel(f)">
          <span class="mono dim">{{ fmt(f.ts) }}<span v-if="i === 0" class="latest-tag">最新</span></span>
          <!-- 内部 boltId 不上屏（#42 这类编号用户对不上号）；有螺栓时用"已绑"点亮即可，名称在详情页 -->
          <span class="mono">{{ f.channelNo != null ? 'CH' + f.channelNo : '—' }}<i v-if="f.boltId" class="bound-dot" title="已绑螺栓" /></span>
          <span class="mono" :style="f.snrDb != null && Number(f.snrDb) < 12 ? 'color:var(--warn)' : ''">{{ f.snrDb != null ? Number(f.snrDb).toFixed(1) : '—' }}</span>
          <span class="mono">{{ f.peakToPeak ?? '—' }}</span>
          <span class="mono">{{ f.noiseRms != null ? Number(f.noiseRms).toFixed(1) : '—' }}</span>
          <span class="mono">{{ f.threshold ?? '—' }}</span>
          <span class="mono dim">{{ f.pointCount }}</span>
        </div>
        <div v-if="!loading && !error && !features.length" class="tbl-empty">
          <span class="led led--off" /> 暂无波形特征。带波形采集启用后，这里展示 SNR / 峰峰值 / 噪声等信号质量指标。
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.diag { display: flex; flex-direction: column; gap: 16px; }
.diag-error { display: flex; align-items: center; gap: 12px; padding: 12px 16px; border-radius: 9px; background: rgba(244, 63, 94, 0.08); border: 1px solid var(--crit); color: var(--text-1); font-size: 13px; }
.diag-error .el-icon { color: var(--crit); font-size: 18px; }
.diag-error-msg { flex: 1; min-width: 0; }
.panel-hd { display: flex; align-items: center; justify-content: space-between; padding: 16px 20px 6px; }
.panel-title { display: inline-flex; align-items: center; }
.panel-title .hd-sub { margin-left: 6px; }
.ascan-hd { display: flex; align-items: center; gap: 12px; }
.ascan-meta { font-size: 12px; color: var(--text-3); }
.ascan-meta.dim { opacity: 0.7; }
.live-dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; background: #f43f5e; margin-left: 8px; animation: livepulse 1.4s ease-in-out infinite; }
@keyframes livepulse { 0%, 100% { opacity: 1; box-shadow: 0 0 0 0 rgba(244, 63, 94, 0.5); } 50% { opacity: 0.5; box-shadow: 0 0 0 5px rgba(244, 63, 94, 0); } }

.follow-bar { display: flex; align-items: center; justify-content: space-between; margin: 6px 18px 0; padding: 8px 14px; border: 1px solid rgba(245, 158, 11, 0.3); background: rgba(245, 158, 11, 0.07); border-radius: 10px; font-size: 12.5px; color: var(--warn); }
.follow-bar span { display: inline-flex; align-items: center; gap: 6px; }
.follow-resume { cursor: pointer; color: var(--accent); padding: 2px 10px; border: 1px solid var(--panel-border-strong); border-radius: 8px; }
.follow-resume:hover { background: rgba(34, 211, 238, 0.08); }

.metrics { display: grid; grid-template-columns: repeat(4, 1fr); gap: 1px; background: var(--hairline); margin: 8px 18px 4px; border: 1px solid var(--hairline); border-radius: 10px; overflow: hidden; }
.metric { display: flex; flex-direction: column; gap: 6px; padding: 13px 16px; background: var(--panel-bg, #0d1320); }
.m-k { font-size: 11px; color: var(--text-3); letter-spacing: 0.04em; }
.m-v { font-size: 26px; font-weight: 800; line-height: 1; }
.m-v i { font-size: 12px; font-weight: 600; color: var(--text-3); font-style: normal; margin-left: 3px; }
.m-i { font-size: 11px; color: var(--text-3); }
.m-bar { height: 4px; border-radius: 3px; background: rgba(148, 163, 184, 0.15); overflow: hidden; margin-top: 2px; }
.m-bar span { display: block; height: 100%; border-radius: 3px; transition: width 0.3s; }
@media (max-width: 900px) { .metrics { grid-template-columns: repeat(2, 1fr); } }
.metrics-empty { display: flex; align-items: center; gap: 8px; margin: 8px 18px 4px; padding: 12px 16px; border: 1px solid rgba(34, 211, 238, 0.22); background: rgba(34, 211, 238, 0.05); border-radius: 10px; font-size: 12.5px; color: var(--text-2); line-height: 1.6; }
.metrics-empty .el-icon { color: var(--accent); flex-shrink: 0; }

.ascan-empty { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 12px; height: 300px; color: var(--text-3); text-align: center; padding: 0 40px; }
.ascan-empty .el-icon { font-size: 34px; color: var(--text-3); opacity: 0.5; }
/* 文件损坏与"还没采到波形"用不同的观感，别让人一眼扫过去当成正常空态 */
.ascan-empty--bad { color: var(--text-1); }
.ascan-empty--bad .el-icon { color: var(--crit); opacity: 1; }
.ascan-legend { display: flex; flex-wrap: wrap; gap: 16px; padding: 4px 20px 6px; }
.ascan-legend .lg { display: inline-flex; align-items: center; gap: 6px; font-size: 11px; color: var(--text-3); }
.lg-line { width: 14px; height: 2px; border-radius: 2px; }
.lg-line.lg-dash { background: repeating-linear-gradient(90deg, #94a3b8 0 4px, transparent 4px 7px) !important; }
.lg-dot { width: 9px; height: 9px; border-radius: 50%; }
.diag-note { font-size: 11px; color: var(--text-3); padding: 2px 20px 16px; line-height: 1.6; }
.diag-note b { color: var(--text-2); }

.tbl { padding: 4px 12px 16px; overflow-x: auto; }
.tr { display: grid; grid-template-columns: 200px 140px 90px 90px 100px 80px 70px; align-items: center; padding: 10px 8px; border-radius: 8px; font-size: 12.5px; cursor: pointer; min-width: 780px; }
.tr:not(.th) { box-shadow: inset 0 -1px 0 var(--hairline); }
.tr:not(.th):hover { background: rgba(56, 189, 248, 0.05); }
.tr:not(.th).active { background: rgba(56, 189, 248, 0.09); }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; text-transform: uppercase; cursor: default; }
.tr.th:hover { background: none; }
.dim { color: var(--text-3); }
.latest-tag { display: inline-block; margin-left: 8px; font-size: 10px; color: #10b981; border: 1px solid rgba(16, 185, 129, 0.4); border-radius: 8px; padding: 0 6px; }
.bound-dot { display: inline-block; width: 6px; height: 6px; border-radius: 50%; background: var(--accent); margin-left: 6px; vertical-align: middle; }
.tbl-empty { display: flex; align-items: center; gap: 8px; justify-content: center; padding: 30px; color: var(--text-3); text-align: center; }

/* 调试调参面板 */
.tune { margin: 0 18px 14px; border: 1px solid var(--hairline); border-radius: 10px; background: rgba(148, 163, 184, 0.04); }
.tune-hd { display: flex; align-items: center; justify-content: space-between; padding: 9px 14px; cursor: pointer; user-select: none; }
.tune-title { display: inline-flex; align-items: center; gap: 7px; font-size: 12.5px; font-weight: 600; color: var(--text-1); }
.tune-tag { font-size: 10px; padding: 1px 7px; border-radius: 8px; border: 1px solid rgba(16, 185, 129, 0.4); color: #10b981; font-weight: 400; }
.tune-tag--locked { border-color: rgba(245, 158, 11, 0.45); color: var(--warn); }
.tune-locked { padding: 4px 14px 12px; font-size: 12px; color: var(--text-2); line-height: 1.7; }
.tune-body { padding: 2px 14px 12px; }
.tune-fields { display: flex; flex-wrap: wrap; align-items: flex-end; gap: 14px; }
.tune-f { display: flex; flex-direction: column; gap: 4px; }
.tune-f > span { font-size: 11px; color: var(--text-3); }
.tune-hint { margin-top: 8px; font-size: 11px; color: var(--text-3); line-height: 1.6; }
.tune-hint b { color: var(--text-2); }
.xd-err { margin-top: 8px; font-size: 12px; color: var(--warn); line-height: 1.6; }
.xd-result { margin-top: 8px; }
.xd-stats { display: flex; flex-wrap: wrap; gap: 18px; font-size: 12.5px; color: var(--text-2); }
.xd-stats b { font-size: 14px; }
</style>
