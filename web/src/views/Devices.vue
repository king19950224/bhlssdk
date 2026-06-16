<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  runtimeApi, deviceApi, channelApi, probeScanApi,
  type RuntimeDevice, type ChannelHealth, type ChannelRow, type UnboundSummaryRow, type ProbeScanView,
} from '../api'
import { useAuth } from '../stores/auth'
import EmptyState from '../components/EmptyState.vue'

const router = useRouter()
const auth = useAuth()

const devices = ref<RuntimeDevice[]>([])
const selected = ref<RuntimeDevice | null>(null)
const tab = ref<'health' | 'scan' | 'bind'>('health')
const loading = ref(false)
let timer: number | undefined

// 通道健康
const channels = ref<ChannelHealth[]>([])
const busyMode = ref('')

// probe-scan
const scan = ref<ProbeScanView | null>(null)
const scanning = ref(false)
let scanTimer: number | undefined

// 通道与绑定
const chanRows = ref<ChannelRow[]>([])
const unbound = ref<UnboundSummaryRow[]>([])

async function loadDevices() {
  const rt = await runtimeApi.devices()
  devices.value = rt.items
  if (!selected.value && rt.items.length) selectDevice(rt.items[0])
}
async function selectDevice(d: RuntimeDevice) {
  selected.value = d
  await Promise.all([loadHealth(), loadScan(), loadBinding()])
}
async function loadHealth() {
  if (!selected.value) return
  loading.value = true
  try { channels.value = (await deviceApi.channelHealth(selected.value.deviceId)).items }
  finally { loading.value = false }
}
async function loadScan() {
  if (!selected.value) return
  try {
    scan.value = await probeScanApi.latest(selected.value.deviceId)
    scanning.value = scan.value?.status === 'running' || scan.value?.status === 'pending'
  } catch { scan.value = null; scanning.value = false }
}
async function loadBinding() {
  if (!selected.value) return
  try {
    const did = selected.value.deviceId
    const [list, ub] = await Promise.all([channelApi.list(), channelApi.unboundSummary()])
    chanRows.value = list.items.filter((c) => c.deviceId === did)
    unbound.value = ub.items.filter((u) => u.deviceId === did)
  } catch { /* ignore */ }
}

onMounted(() => {
  loadDevices()
  timer = window.setInterval(() => { if (selected.value && tab.value === 'health') loadHealth() }, 8000)
})
onUnmounted(() => {
  if (timer) clearInterval(timer)
  if (scanTimer) clearInterval(scanTimer)
})

const MODES: { mode: string; label: string; type: '' | 'primary' | 'warning' | 'danger' }[] = [
  { mode: 'MultiMeas', label: '多通道测量', type: 'primary' },
  { mode: 'Standby', label: '待机', type: '' },
  { mode: 'Reboot', label: '重启设备', type: 'danger' },
]
async function setMode(mode: string, label: string) {
  if (!selected.value) return
  try {
    await ElMessageBox.confirm(`确认对【${selected.value.name}】下发工作模式「${label}」？`, '工作模式', { type: 'warning' })
  } catch { return }
  busyMode.value = mode
  try {
    await deviceApi.setMode(selected.value.deviceId, mode)
    ElMessage.success(`已下发 ${label}`)
  } catch (e) {
    ElMessage.error((e as Error)?.message || '下发失败（扫描进行中或设备离线？）')
  } finally {
    busyMode.value = ''
  }
}
async function recalibrate(c: ChannelHealth) {
  if (!selected.value) return
  try {
    await ElMessageBox.confirm(
      `将通道 CH${c.channelNo} 的【当前回波声时】设为零载/参考基线。请确认螺栓处于已知预紧状态。`,
      '重标定基线', { type: 'warning', confirmButtonText: '重标定' })
  } catch { return }
  try {
    const r = await deviceApi.recalibrate(selected.value.deviceId, c.channelNo)
    ElMessage.success(`CH${c.channelNo} 基线已设为 ${Number(r.newRefTimeNs).toFixed(0)} ns`)
    await loadHealth()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '重标定失败（需复核员权限/稳定声时）')
  }
}

async function startScan() {
  if (!selected.value) return
  try {
    const r = await probeScanApi.start(selected.value.deviceId, {})
    ElMessage.success(`已发起通道自动检测（约 ${r.estimatedSeconds}s）`)
    scanning.value = true
    pollScan()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '发起扫描失败')
  }
}
function pollScan() {
  if (scanTimer) clearInterval(scanTimer)
  scanTimer = window.setInterval(async () => {
    await loadScan()
    if (!scanning.value && scanTimer) { clearInterval(scanTimer); scanTimer = undefined }
  }, 2000)
}
async function abortScan() {
  if (!selected.value || !scan.value) return
  try {
    await probeScanApi.abort(selected.value.deviceId, scan.value.scanRunId)
    ElMessage.info('已请求中止扫描')
    await loadScan()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '中止失败')
  }
}

async function unbind(c: ChannelRow) {
  try {
    const { value } = await ElMessageBox.prompt(
      `解绑通道 CH${c.channelNo}（螺栓 ${c.boltCode}）。该螺栓将被停用，标定基线清除。请填原因：`,
      '解绑通道', { confirmButtonText: '解绑', cancelButtonText: '取消', inputType: 'textarea',
        inputValidator: (v) => (v && v.trim() ? true : '请填原因') })
    await channelApi.unbind(c.id, value)
    ElMessage.success('已解绑')
    await loadBinding()
  } catch { /* 取消 */ }
}

const VERDICT: Record<string, { label: string; color: string }> = {
  good: { label: '良好', color: '#10b981' }, ok: { label: '可用', color: '#22d3ee' },
  weak: { label: '弱', color: '#f59e0b' }, noisy: { label: '噪声', color: '#fb923c' },
  no_signal: { label: '无信号', color: '#61748f' }, inconclusive: { label: '不确定', color: '#61748f' },
}
function fmt(n: number) { return n >= 1000 ? (n / 1000).toFixed(1) + 'k' : String(n) }
</script>

<template>
  <div class="dev rise">
    <div class="dev-cards">
      <div v-for="d in devices" :key="d.deviceId" class="dc panel" :class="{ active: selected?.deviceId === d.deviceId }" @click="selectDevice(d)">
        <div class="dc-hd">
          <span class="led" :class="d.state === 'Connected' ? 'led--ok' : 'led--crit'" />
          <span class="dc-name">{{ d.name }}</span>
          <el-tag v-if="d.isPrimary" size="small" effect="dark" class="ptag">主</el-tag>
        </div>
        <div class="dc-state mono">{{ d.state }}</div>
        <div class="dc-metrics">
          <div><b class="mono">{{ d.sessionChannels?.length ?? 0 }}</b><span>通道</span></div>
          <div><b class="mono">{{ fmt(d.channelResultCount ?? 0) }}</b><span>测量</span></div>
          <div><b class="mono" :style="d.writeFailureCount ? 'color:var(--warn)' : ''">{{ d.writeFailureCount ?? 0 }}</b><span>写失败</span></div>
        </div>
      </div>
      <EmptyState v-if="!devices.length" empty empty-text="暂无设备运行时" />
    </div>

    <div class="panel panel--glow" v-if="selected">
      <div class="panel-hd">
        <span class="panel-title">{{ selected.name }} · 调试控制台</span>
        <router-link :to="{ name: 'identify' }" class="hd-link">敲击识别 →</router-link>
      </div>
      <el-tabs v-model="tab" class="dev-tabs">
        <!-- 通道健康 -->
        <el-tab-pane label="通道健康" name="health">
          <div class="modebar">
            <span class="mb-k">工作模式：</span>
            <el-button v-for="m in MODES" :key="m.mode" size="small" :type="m.type" :loading="busyMode === m.mode"
              :disabled="!auth.isReviewer && m.mode === 'Reboot'" @click="setMode(m.mode, m.label)">{{ m.label }}</el-button>
            <span class="mb-hint">扫描进行中不可切换</span>
          </div>
          <div class="tbl">
            <div class="tr th"><span>CH</span><span>螺栓</span><span>链路</span><span>测量</span><span>EchoTime</span><span>温度</span><span>SNR</span><span>峰峰</span><span>操作</span></div>
            <div v-for="c in channels" :key="c.channelId" class="tr">
              <span class="mono">{{ c.channelNo }}</span>
              <span class="mono">{{ c.bolt?.code || '未绑定' }}</span>
              <span class="hw"><span class="led" :class="c.measurement.healthy ? 'led--ok' : (c.measurement.total > 0 ? 'led--warn' : 'led--off')" />{{ c.hardwareState }}</span>
              <span class="mono">{{ c.measurement.total }} <i class="dim">/ {{ c.measurement.echoRatePct }}%</i></span>
              <span class="mono">{{ c.measurement.avgEchoNs != null ? Number(c.measurement.avgEchoNs).toFixed(0) : '—' }}</span>
              <span class="mono">{{ c.measurement.avgTempC != null ? Number(c.measurement.avgTempC).toFixed(1) + '°C' : '—' }}</span>
              <span class="mono">{{ c.latestWave.snrDb != null ? Number(c.latestWave.snrDb).toFixed(1) : '—' }}</span>
              <span class="mono">{{ c.latestWave.peakToPeak ?? '—' }}</span>
              <span><el-button v-if="auth.isReviewer && c.bolt" size="small" text type="primary" @click="recalibrate(c)">重标定</el-button></span>
            </div>
            <EmptyState v-if="!loading && !channels.length" empty empty-text="该设备暂无通道数据" />
          </div>
        </el-tab-pane>

        <!-- 通道自动检测 -->
        <el-tab-pane label="通道自动检测" name="scan">
          <div class="scan-bar">
            <el-button type="primary" :disabled="scanning" @click="startScan">发起自动检测</el-button>
            <el-button v-if="scanning" type="danger" @click="abortScan">中止</el-button>
            <span v-if="scan" class="scan-st">
              状态 <b :class="scanning ? 'st-run' : ''">{{ scan.status }}</b>
              <template v-if="scan.results?.length"> · 已判定 {{ scan.results.length }} 通道</template>
            </span>
            <span class="mb-hint">扫描期间设备业务监测会短暂中断</span>
          </div>
          <div v-if="scan?.results?.length" class="tbl">
            <div class="tr th scan"><span>CH</span><span>判定</span><span>SNR</span><span>声时σ</span><span>峰峰</span><span>样本</span><span>说明</span></div>
            <div v-for="rrow in scan.results" :key="rrow.channelNo" class="tr scan">
              <span class="mono">{{ rrow.channelNo }}</span>
              <span><i class="vd" :style="`color:${VERDICT[rrow.verdict]?.color || 'var(--text-2)'}`">{{ VERDICT[rrow.verdict]?.label || rrow.verdict }}</i></span>
              <span class="mono">{{ rrow.snrDb != null ? Number(rrow.snrDb).toFixed(1) : '—' }}</span>
              <span class="mono">{{ rrow.echoStdNs != null ? Number(rrow.echoStdNs).toFixed(1) : '—' }}</span>
              <span class="mono">{{ rrow.peakToPeak ?? '—' }}</span>
              <span class="mono">{{ rrow.sampleCount }}</span>
              <span class="dim">{{ rrow.reason || '—' }}</span>
            </div>
          </div>
          <EmptyState v-else :empty="!scanning" :loading="scanning" empty-text="尚无扫描结果，点「发起自动检测」逐通道判定有无探头" />
        </el-tab-pane>

        <!-- 通道与绑定 -->
        <el-tab-pane label="通道与绑定" name="bind">
          <div v-if="unbound.length" class="unbound-note">
            <el-icon><WarningFilled /></el-icon>
            {{ unbound.length }} 个通道有未绑定数据堆积（等待绑定螺栓）：{{ unbound.map(u => 'CH' + u.channelNo + '(' + u.count + ')').join('、') }}
          </div>
          <div class="tbl">
            <div class="tr th binds"><span>CH</span><span>状态</span><span>螺栓</span><span>K_L</span><span>参考声时</span><span>操作</span></div>
            <div v-for="c in chanRows" :key="c.id" class="tr binds">
              <span class="mono">{{ c.channelNo }}</span>
              <span class="mono dim">{{ c.state }}</span>
              <span class="mono">{{ c.boltCode || '未绑定' }}</span>
              <span class="mono">{{ c.kL != null ? Number(c.kL).toExponential(2) : '—' }}</span>
              <span class="mono">{{ c.refTimeNs != null ? Number(c.refTimeNs).toFixed(0) + 'ns' : '—' }}</span>
              <span>
                <el-button v-if="c.boltId" size="small" text @click="router.push({ name: 'boltDetail', params: { boltId: c.boltId } })">详情</el-button>
                <el-button v-if="auth.isReviewer && c.boltId" size="small" text type="danger" @click="unbind(c)">解绑</el-button>
              </span>
            </div>
            <EmptyState v-if="!chanRows.length" empty empty-text="该设备暂无通道" />
          </div>
        </el-tab-pane>
      </el-tabs>
    </div>
  </div>
</template>

<style scoped>
.dev { display: flex; flex-direction: column; gap: 16px; }
.dev-cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 14px; }
.dc { padding: 16px 18px; cursor: pointer; transition: all 0.18s; }
.dc:hover { border-color: var(--panel-border-strong); }
.dc.active { border-color: var(--accent); box-shadow: 0 0 22px -10px var(--accent-glow); }
.dc-hd { display: flex; align-items: center; gap: 9px; }
.dc-name { font-weight: 600; font-size: 14px; flex: 1; }
.ptag { background: rgba(34, 211, 238, 0.16) !important; border-color: var(--panel-border-strong) !important; color: var(--accent) !important; }
.dc-state { font-size: 11px; color: var(--text-3); margin: 4px 0 14px; letter-spacing: 0.08em; }
.dc-metrics { display: flex; justify-content: space-between; }
.dc-metrics > div { display: flex; flex-direction: column; gap: 3px; }
.dc-metrics b { font-size: 17px; }
.dc-metrics span { font-size: 11px; color: var(--text-3); }

.panel-hd { display: flex; align-items: center; justify-content: space-between; padding: 16px 20px 0; }
.hd-link { font-size: 12px; }
.dev-tabs { padding: 0 18px 12px; }
.modebar { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; padding: 6px 0 14px; }
.mb-k { font-size: 12px; color: var(--text-3); }
.mb-hint { font-size: 11px; color: var(--text-3); }
.scan-bar { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; padding: 6px 0 14px; }
.scan-st { font-size: 13px; color: var(--text-2); }
.scan-st .st-run { color: var(--accent); }
.unbound-note { display: flex; align-items: center; gap: 8px; font-size: 12.5px; color: var(--warn); padding: 4px 0 12px; }

.tbl { border-top: 1px solid var(--hairline); }
.tr { display: grid; grid-template-columns: 50px 110px 130px 110px 100px 80px 70px 70px 90px; align-items: center; padding: 10px 6px; border-bottom: 1px solid var(--hairline); font-size: 12.5px; }
.tr.th.scan, .tr.scan { grid-template-columns: 60px 90px 80px 90px 80px 70px 1fr; }
.tr.th.binds, .tr.binds { grid-template-columns: 60px 130px 130px 120px 120px 130px; }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; }
.hw { display: flex; align-items: center; gap: 8px; }
.vd { font-style: normal; font-weight: 600; }
.dim { color: var(--text-3); }
</style>
