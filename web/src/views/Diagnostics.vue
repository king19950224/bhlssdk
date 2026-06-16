<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import EChart from '../components/EChart.vue'
import { waveApi, type WaveFeature, type WaveCompareResult, type WaveCompareWave } from '../api'

const features = ref<WaveFeature[]>([])
const cmp = ref<WaveCompareResult | null>(null)
const activeChannelId = ref<number | null>(null)
const loading = ref(false)
const loadingCmp = ref(false)
const settingRef = ref(false)

// 实时显示：勾选后每 1.5s 轮询，自动跟随最新波形所在通道刷新对比诊断
const live = ref(true)
const follow = ref(true)
const lastUpdate = ref<number | null>(null)
let liveTimer: number | undefined
let ticking = false

const cur = computed(() => cmp.value?.current ?? null)
const refWave = computed(() => cmp.value?.reference ?? null)
const metrics = computed(() => cmp.value?.metrics ?? null)
const hasRef = computed(() => !!cmp.value?.hasReference)

async function loadFeatures() {
  features.value = (await waveApi.features(60)).items
}

async function loadCompare(channelId: number) {
  loadingCmp.value = true
  try {
    cmp.value = await waveApi.compare(channelId)
    activeChannelId.value = channelId
    lastUpdate.value = Date.now()
  } catch {
    cmp.value = null
  } finally {
    loadingCmp.value = false
  }
}

// 点选某通道波形：若点的不是最新一条则暂停跟随，停在该通道
async function selectChannel(f: WaveFeature) {
  follow.value = features.value.length > 0 && f.id === features.value[0].id
  await loadCompare(f.channelId)
}

async function tick() {
  if (ticking) return
  ticking = true
  try {
    await loadFeatures()
    const latest = features.value[0]
    if (follow.value && latest) {
      await loadCompare(latest.channelId)
    } else {
      lastUpdate.value = Date.now()
    }
  } catch {
    /* 轮询失败下次再试 */
  } finally {
    ticking = false
  }
}

function startLive() {
  stopLive()
  if (!live.value) return
  liveTimer = window.setInterval(tick, 1500)
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
    if (features.value.length) await loadCompare(features.value[0].channelId)
  } finally {
    loading.value = false
  }
  startLive()
})
onUnmounted(stopLive)

// A-Scan 解析：绝对时间轴(echoStart + i·clockInt)、峰值、检出点(过阈)、零交叉
function computeStats(w: WaveCompareWave | null) {
  if (!w || !w.waveData.length) return null
  const clk = w.clockInt || 1
  const x0 = w.echoStart || 0
  const d = w.waveData
  const n = d.length
  const xmin = x0
  const xmax = x0 + (n - 1) * clk
  const t = (i: number) => x0 + i * clk
  let peakIdx = 0
  let peakAbs = -1
  for (let i = 0; i < n; i++) {
    const a = Math.abs(d[i])
    if (a > peakAbs) { peakAbs = a; peakIdx = i }
  }
  const thr = w.threshold != null ? Math.abs(w.threshold) : null
  let detIdx = -1
  if (thr != null && thr > 0) for (let i = 0; i < n; i++) { if (Math.abs(d[i]) >= thr) { detIdx = i; break } }
  if (detIdx < 0) detIdx = peakIdx
  const detNs = t(detIdx)
  let zeroNs: number | null = null
  for (let i = detIdx; i < n - 1; i++) {
    const a = d[i]
    const b = d[i + 1]
    if ((a <= 0 && b > 0) || (a >= 0 && b < 0)) { zeroNs = t(i) + (a === b ? 0 : a / (a - b)) * clk; break }
  }
  return { d, clk, x0, n, xmin, xmax, peakIdx, detIdx, detNs, zeroNs, thr }
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
    yAxis: { type: 'value', scale: true, name: '幅值', nameTextStyle: { color: '#61748f', fontSize: 11 }, axisLabel: { color: '#61748f', fontSize: 11 }, splitLine: { lineStyle: { color: 'rgba(148,163,184,0.08)' } } },
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
    <!-- A-Scan + 对比 -->
    <div class="panel panel--glow">
      <div class="panel-hd">
        <span class="panel-title">
          实时回波波形 A-Scan
          <span v-if="live" class="live-dot" />
          <span v-if="cur" class="hd-sub">· CH{{ activeChannelId }} · {{ cur.pointCount }} 点</span>
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
        <span><el-icon><View /></el-icon> 已暂停跟随，停在 CH{{ activeChannelId }}</span>
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
        该通道尚未设置参考波形。波形稳定（已拧到目标、耦合良好）时点「设为参考」，之后即可实时对比相似度 / 衰减 / 健康度，预警探头老化与耦合劣化。
      </div>

      <EChart v-if="cur" :option="ascanOption" height="300px" />
      <div v-else class="ascan-empty" v-loading="loadingCmp">
        <el-icon><TrendCharts /></el-icon>
        <span>{{ loading ? '加载中…' : '暂无波形数据。模拟器或真机产生带波形的测量后,可在此实时回放并对比 A-Scan。' }}</span>
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
    </div>

    <!-- 波形特征（全局，点选切换通道） -->
    <div class="panel panel--glow">
      <div class="panel-hd">
        <span class="panel-title">波形特征 · 信号质量 <span v-if="live" class="live-dot" /></span>
        <span class="hd-sub">点选某条切换到该通道做对比</span>
      </div>
      <div class="tbl">
        <div class="tr th"><span>时间</span><span>通道/螺栓</span><span>SNR dB</span><span>峰峰值</span><span>噪声 RMS</span><span>阈值</span><span>点数</span></div>
        <div v-for="(f, i) in features" :key="f.id" class="tr" :class="{ active: activeChannelId === f.channelId }" @click="selectChannel(f)">
          <span class="mono dim">{{ fmt(f.ts) }}<span v-if="i === 0" class="latest-tag">最新</span></span>
          <span class="mono">CH{{ f.channelId }}{{ f.boltId ? ' · #' + f.boltId : '' }}</span>
          <span class="mono" :style="f.snrDb != null && Number(f.snrDb) < 12 ? 'color:var(--warn)' : ''">{{ f.snrDb != null ? Number(f.snrDb).toFixed(1) : '—' }}</span>
          <span class="mono">{{ f.peakToPeak ?? '—' }}</span>
          <span class="mono">{{ f.noiseRms != null ? Number(f.noiseRms).toFixed(1) : '—' }}</span>
          <span class="mono">{{ f.threshold ?? '—' }}</span>
          <span class="mono dim">{{ f.pointCount }}</span>
        </div>
        <div v-if="!loading && !features.length" class="tbl-empty">
          <span class="led led--off" /> 暂无波形特征。带波形采集启用后,这里展示 SNR / 峰峰值 / 噪声等信号质量指标。
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.diag { display: flex; flex-direction: column; gap: 16px; }
.panel-hd { display: flex; align-items: center; justify-content: space-between; padding: 16px 20px 6px; }
.panel-title { display: inline-flex; align-items: center; }
.hd-sub { font-size: 12px; color: var(--text-3); margin-left: 8px; }
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
.ascan-legend { display: flex; flex-wrap: wrap; gap: 16px; padding: 4px 20px 6px; }
.ascan-legend .lg { display: inline-flex; align-items: center; gap: 6px; font-size: 11px; color: var(--text-3); }
.lg-line { width: 14px; height: 2px; border-radius: 2px; }
.lg-line.lg-dash { background: repeating-linear-gradient(90deg, #94a3b8 0 4px, transparent 4px 7px) !important; }
.lg-dot { width: 9px; height: 9px; border-radius: 50%; }
.diag-note { font-size: 11px; color: var(--text-3); padding: 2px 20px 16px; line-height: 1.6; }
.diag-note b { color: var(--text-2); }

.tbl { padding: 4px 18px 16px; }
.tr { display: grid; grid-template-columns: 200px 140px 90px 90px 100px 80px 70px; align-items: center; padding: 10px 6px; border-bottom: 1px solid var(--hairline); font-size: 12.5px; cursor: pointer; }
.tr:not(.th):hover, .tr.active { background: rgba(56, 189, 248, 0.07); }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; cursor: default; }
.tr.th:hover { background: none; }
.dim { color: var(--text-3); }
.latest-tag { display: inline-block; margin-left: 8px; font-size: 10px; color: #10b981; border: 1px solid rgba(16, 185, 129, 0.4); border-radius: 8px; padding: 0 6px; }
.tbl-empty { display: flex; align-items: center; gap: 8px; justify-content: center; padding: 30px; color: var(--text-3); text-align: center; }
</style>
