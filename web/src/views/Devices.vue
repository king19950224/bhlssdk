<script setup lang="ts">
// 页面:设备管理——连接状态、通道在位矩阵与绑定情况、探头热插拔提示、默认监测组设置。
import { onMounted, onUnmounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox, ElNotification } from 'element-plus'
import {
  runtimeApi, deviceApi, channelApi, probeScanApi,
  type RuntimeDevice, type ChannelHealth, type ChannelRow, type UnboundSummaryRow, type ProbeScanView,
  type DeviceSettings,
} from '../api'
import { onRealtime, startRealtime, type OnChannelPresence } from '../api/realtime'
import { useAuth } from '../stores/auth'
import EmptyState from '../components/EmptyState.vue'
import BoltCreateDialog from '../components/BoltCreateDialog.vue'
import CalibrationWizard from '../components/CalibrationWizard.vue'

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

const devicesLoaded = ref(false)
async function loadDevices() {
  try {
    const rt = await runtimeApi.devices()
    devices.value = rt.items
    if (!selected.value && rt.items.length) selectDevice(rt.items[0])
  } catch (e) {
    ElMessage.error((e as Error)?.message || '设备运行时加载失败')
  } finally {
    devicesLoaded.value = true
  }
}
async function selectDevice(d: RuntimeDevice) {
  selected.value = d
  await Promise.all([loadHealth(), loadScan(), loadBinding(), loadSettings()])
}
async function loadHealth() {
  const did = selected.value?.deviceId
  if (did == null) return
  loading.value = true
  try {
    const items = (await deviceApi.channelHealth(did)).items
    // 快速切换设备时，仅当结果仍属当前选中设备才写入，避免把 A 的健康显示在 B 下。
    if (selected.value?.deviceId === did) channels.value = items
  }
  finally { if (selected.value?.deviceId === did) loading.value = false }
}
async function loadScan() {
  const did = selected.value?.deviceId
  if (did == null) return
  try {
    const v = await probeScanApi.latest(did)
    if (selected.value?.deviceId !== did) return
    scan.value = v
    scanning.value = scan.value?.status === 'running' || scan.value?.status === 'pending'
    // 页面刷新/切换设备时若有扫描在跑,自动恢复轮询(否则进度卡住不动)
    if (scanning.value && !scanTimer) pollScan()
  } catch { if (selected.value?.deviceId === did) { scan.value = null; scanning.value = false } }
}
async function loadBinding() {
  const did = selected.value?.deviceId
  if (did == null) return
  try {
    // deviceId 过滤下推到后端（原先拉全库再客户端过滤，通道多时白传一堆）
    const [list, ub] = await Promise.all([channelApi.list({ deviceId: did }), channelApi.unboundSummary()])
    if (selected.value?.deviceId !== did) return
    chanRows.value = list.items
    unbound.value = ub.items.filter((u) => u.deviceId === did)
  } catch { /* ignore */ }
}

// 默认监测组（检测即螺栓：探头在位自动建档的归属组）
const settings = ref<DeviceSettings | null>(null)
const groupDlgOpen = ref(false)
const groupDlgSel = ref<number | null>(null)
const savingGroup = ref(false)
async function loadSettings() {
  const did = selected.value?.deviceId
  if (did == null) return
  try {
    const s = await deviceApi.getSettings(did)
    if (selected.value?.deviceId === did) settings.value = s
  } catch { /* ignore */ }
}
function openGroupDlg() {
  groupDlgSel.value = settings.value?.defaultGroupId ?? null
  groupDlgOpen.value = true
}
async function saveDefaultGroup() {
  if (!selected.value) return
  savingGroup.value = true
  try {
    const r = await deviceApi.patchSettings(selected.value.deviceId, groupDlgSel.value)
    if (settings.value) settings.value = { ...settings.value, defaultGroupId: r.defaultGroupId, defaultGroupCode: r.defaultGroupCode }
    if (r.enrolledBolts.length) {
      ElNotification({
        title: `已自动建档 ${r.enrolledBolts.length} 颗在位螺栓`,
        message: r.enrolledBolts.map((b) => `CH${b.channelNo} → ${b.code}`).join('、'),
        type: 'success', duration: 8000,
      })
      await Promise.all([loadHealth(), loadBinding()])
    } else {
      ElMessage.success(r.defaultGroupId ? `默认监测组已设为 ${r.defaultGroupCode}` : '已清除默认监测组（自动建档暂停）')
    }
    groupDlgOpen.value = false
  } catch (e) {
    ElMessage.error((e as Error)?.message || '设置失败（需复核员权限）')
  } finally {
    savingGroup.value = false
  }
}

// 探头热插拔：实时在位事件 → 通道灯即时翻转 + 新探头提示（识别、采集、建档全自动）
let offPresence: (() => void) | undefined
function onPresence(ev: OnChannelPresence) {
  if (selected.value?.deviceId !== ev.deviceId) return
  // 通道灯就地翻转（healthy/no_data 与后端落库词表一致），列表其余字段等下一轮轮询补齐
  const row = channels.value.find((c) => c.channelNo === ev.channelNo)
  if (row) row.hardwareState = ev.present ? 'healthy' : 'no_data'
  else if (ev.present) loadHealth() // 新出现的通道（自动发现）：拉全量补行

  if (ev.initial) return // 启动确认：只刷状态，不打扰
  if (ev.present) {
    loadBinding()
    if (ev.boltCode) {
      // 在位事件带螺栓编号：老螺栓恢复或刚自动建档的新螺栓，都已在采集
      ElMessage.success(`通道 CH${ev.channelNo} 探头在位（${ev.boltCode}），采集中`)
    } else {
      ElNotification({
        title: `通道 CH${ev.channelNo} 检测到新探头`,
        message: '已开始采集（未建档）。设置设备默认监测组后新探头会自动建档，也可到「通道与绑定」人工建档。',
        type: 'success', duration: 8000,
        onClick: () => { tab.value = 'bind'; loadBinding() },
      })
    }
  } else {
    ElMessage.warning(`通道 CH${ev.channelNo} 探头断开${ev.boltCode ? `（${ev.boltCode}）` : ''}`)
  }
}

onMounted(() => {
  loadDevices()
  startRealtime()
  offPresence = onRealtime('OnChannelPresence', onPresence)
  timer = window.setInterval(() => { if (selected.value && tab.value === 'health') loadHealth() }, 8000)
})
onUnmounted(() => {
  if (timer) clearInterval(timer)
  if (scanTimer) clearInterval(scanTimer)
  offPresence?.()
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

// 通道目录重建（reviewer+）：设置设备实际物理通道数（如 48），复活被删通道、补插缺失行，
// 全通道轮询/探头扫描范围随之扩大——解决"只显示 8 个通道/新口永远识别不到"的死角
const rebuildingCatalog = ref(false)
async function editChannelCatalog() {
  if (!selected.value) return
  const current = selected.value.options?.channelCount || 40
  let input: string
  try {
    const r = await ElMessageBox.prompt(
      `设置【${selected.value.name}】的物理通道数（1..48）。确认后：目录中缺失/被删除的通道会补齐恢复，` +
      '设备将轮询全部通道（热插拔识别范围 1..N），新插探头即可被发现。已绑定的螺栓不受影响。',
      '通道目录', {
        confirmButtonText: '重建目录', cancelButtonText: '取消',
        inputValue: String(current),
        inputPattern: /^([1-9]|[1-3][0-9]|4[0-8])$/, inputErrorMessage: '通道数须为 1..48 的整数',
      })
    input = r.value
  } catch { return }
  rebuildingCatalog.value = true
  try {
    const r = await deviceApi.rebuildChannelCatalog(selected.value.deviceId, Number(input))
    const parts = [`通道数 ${r.previousChannelCount}→${r.channelCount}`]
    if (r.resurrectedChannels.length) parts.push(`恢复 ${r.resurrectedChannels.length} 个被删通道`)
    if (r.createdChannels.length) parts.push(`新建 ${r.createdChannels.length} 个通道`)
    ElMessage.success(`通道目录已重建：${parts.join('，')}`)
    if (r.warning) ElMessage.warning(r.warning)
    await Promise.all([loadDevices(), loadHealth(), loadBinding()])
  } catch (e) {
    ElMessage.error((e as Error)?.message || '通道目录重建失败（需复核员权限）')
  } finally {
    rebuildingCatalog.value = false
  }
}

// 建档并绑定（新接入螺栓的闭环）：预选该行通道打开对话框
const createOpen = ref(false)
const createChannelId = ref<number | null>(null)
function openCreate(channelId: number | null) {
  createChannelId.value = channelId
  createOpen.value = true
}

// 建档完成 → 无缝接标定向导(接入闭环最后一步),复核员权限才弹
const wizardOpen = ref(false)
const wizardSel = ref<{ boltId: number; channelId: number; code: string } | null>(null)
async function onBoltCreated(payload?: { boltId: number; channelId: number; code: string }) {
  await loadBinding()
  if (!payload || !auth.isReviewer) return
  try {
    await ElMessageBox.confirm(
      `螺栓 ${payload.code} 已建档绑定。现在就完成标定吗？(标定后才能显示预紧力)`,
      '继续标定', { type: 'info', confirmButtonText: '开始标定', cancelButtonText: '稍后再说' })
  } catch { return }
  wizardSel.value = payload
  wizardOpen.value = true
}

// 通道自动检测判定（probe-scan verdict）→ 中文 + 颜色
const VERDICT: Record<string, { label: string; color: string }> = {
  active_good: { label: '良好', color: '#10b981' },
  active_weak: { label: '弱信号', color: '#f59e0b' },
  active_no_echo: { label: '无回波', color: '#fb923c' },
  not_reporting: { label: '未上报', color: '#61748f' },
  inconclusive: { label: '不确定', color: '#61748f' },
}
// 通道链路硬件状态（channels.hardware_state）→ 中文 + 颜色
const HW_STATE: Record<string, { label: string; color: string }> = {
  healthy: { label: '健康', color: '#10b981' },
  noisy: { label: '噪声', color: '#f59e0b' },
  suspect: { label: '可疑', color: '#fb923c' },
  no_data: { label: '无数据', color: '#61748f' },
  unknown: { label: '未知', color: '#61748f' },
}
// 设备/扫描/通道状态枚举的中文映射（后端返回英文枚举，UI 统一汉化，未知值回退原值）
const DEV_STATE: Record<string, string> = { Connected: '在线', Connecting: '连接中', Reconnecting: '重连中', Disconnected: '离线', Faulted: '故障' }
const SCAN_STATUS: Record<string, string> = { pending: '排队中', running: '检测中', completed: '已完成', failed: '失败', aborted: '已中止' }
const CH_STATE: Record<string, string> = { manual: '手动', auto_discovered: '自动发现', auto: '自动' }
// 测量计数用中文"万"做紧凑显示——原先的 1.2k 缩写易被误读成 kN
function fmt(n: number) { return n >= 10000 ? (n / 10000).toFixed(1) + '万' : String(Math.round(n)) }
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
        <div class="dc-state mono">{{ DEV_STATE[d.state] || d.state }}</div>
        <div class="dc-metrics">
          <div><b class="metric">{{ d.sessionChannels?.length ?? 0 }}</b><span>通道</span></div>
          <div><b class="metric">{{ fmt(d.channelResultCount ?? 0) }}</b><span>测量</span></div>
          <div><b class="metric" :style="d.writeFailureCount ? 'color:var(--warn)' : ''">{{ d.writeFailureCount ?? 0 }}</b><span>异常</span></div>
        </div>
      </div>
      <EmptyState v-if="!devices.length" :loading="!devicesLoaded" :empty="devicesLoaded" empty-text="暂无设备运行时" />
    </div>

    <div class="panel panel--glow" v-if="selected">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark" />{{ selected.name }} · 设备控制台</span>
        <span class="hd-tools">
          <el-tooltip content="设备物理通道数（全通道轮询/识别范围 1..N）。点击重建通道目录" placement="bottom">
            <el-button size="small" text type="primary" :loading="rebuildingCatalog" :disabled="!auth.isReviewer" @click="editChannelCatalog">
              通道数 {{ selected.options?.channelCount || '—' }}
            </el-button>
          </el-tooltip>
          <el-tooltip content="探头在位自动建档的归属监测组。未设置时新探头只采集不建档" placement="bottom">
            <el-button size="small" text :type="settings?.defaultGroupId ? 'primary' : 'warning'" :disabled="!auth.isReviewer" @click="openGroupDlg">
              默认组 {{ settings?.defaultGroupCode || '未设置' }}
            </el-button>
          </el-tooltip>
          <router-link :to="{ name: 'identify', query: { deviceId: selected.deviceId } }" class="hd-link">点位与验收 →</router-link>
        </span>
      </div>
      <div v-if="selected.state !== 'Connected'" class="offline-note">
        <el-icon><WarningFilled /></el-icon>
        设备当前{{ DEV_STATE[selected.state] || selected.state }},以下为最后一次已知快照;模式下发/自动检测/重标定需设备在线后执行。
      </div>
      <el-tabs v-model="tab" class="dev-tabs">
        <!-- 通道健康 -->
        <el-tab-pane label="通道健康" name="health">
          <div class="modebar">
            <span class="mb-k">工作模式：</span>
            <!-- set-mode 整组后端要求 reviewer+：所有工作模式按钮对 operator 一律禁用（不止 Reboot），与后端一致 -->
            <el-button v-for="m in MODES" :key="m.mode" size="small" :type="m.type" :loading="busyMode === m.mode"
              :disabled="!auth.isReviewer || scanning" @click="setMode(m.mode, m.label)">{{ m.label }}</el-button>
            <span class="mb-hint" v-if="scanning">通道自动检测进行中，暂不可切换</span>
            <span class="mb-hint" v-else-if="!auth.isReviewer">需复核员权限</span>
          </div>
          <div class="tbl">
            <div class="tr th"><span>CH</span><span>螺栓</span><span>链路</span><span>测量</span><span>回波声时</span><span>温度</span><span>SNR</span><span>峰峰</span><span>操作</span></div>
            <div v-for="c in channels" :key="c.channelId" class="tr">
              <span class="mono">{{ c.channelNo }}</span>
              <span class="mono">{{ c.bolt?.code || '未绑定' }}</span>
              <!-- 灯与文字同源（都按 hardwareState），避免出现"绿灯 + 噪声"的自相矛盾显示 -->
              <span class="hw"><span class="led" :style="`background:${HW_STATE[c.hardwareState]?.color || '#61748f'};box-shadow:0 0 8px ${HW_STATE[c.hardwareState]?.color || '#61748f'}`" />{{ HW_STATE[c.hardwareState]?.label || c.hardwareState }}</span>
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
              状态 <b :class="scanning ? 'st-run' : ''">{{ SCAN_STATUS[scan.status] || scan.status }}</b>
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
            <span class="un-text">{{ unbound.length }} 个通道有未绑定数据堆积（等待绑定螺栓）：{{ unbound.map(u => 'CH' + u.channelNo + '(' + u.count + ')').join('、') }}</span>
            <el-button size="small" type="primary" @click="openCreate(unbound[0]?.channelId ?? null)">建档绑定</el-button>
          </div>
          <div class="tbl">
            <div class="tr th binds"><span>CH</span><span>状态</span><span>螺栓</span><span>K_L</span><span>参考声时</span><span>操作</span></div>
            <div v-for="c in chanRows" :key="c.id" class="tr binds">
              <span class="mono">{{ c.channelNo }}</span>
              <span class="mono dim">{{ CH_STATE[c.state] || c.state }}</span>
              <span class="mono">{{ c.boltCode || '未绑定' }}</span>
              <span class="mono">{{ c.kL != null ? Number(c.kL).toExponential(2) : '—' }}</span>
              <span class="mono">{{ c.refTimeNs != null ? Number(c.refTimeNs).toFixed(0) + 'ns' : '—' }}</span>
              <span>
                <el-button v-if="c.boltId" size="small" text @click="router.push({ name: 'boltDetail', params: { boltId: c.boltId } })">详情</el-button>
                <el-button v-if="!c.boltId" size="small" text type="primary" @click="openCreate(c.id)">建档绑定</el-button>
                <el-button v-if="auth.isReviewer && c.boltId" size="small" text type="danger" @click="unbind(c)">解绑</el-button>
              </span>
            </div>
            <EmptyState v-if="!chanRows.length" empty empty-text="该设备暂无通道" />
          </div>
        </el-tab-pane>
      </el-tabs>
    </div>

    <el-dialog v-model="groupDlgOpen" title="默认监测组（自动建档）" width="460px">
      <p class="dlg-note">
        探头插入确认在位后，系统自动把螺栓建档到此组：编号「通道X-Y号螺栓」（每 10 口一束、束内 1~10）、缺省规格 M20/8.8（可在螺栓详情后补位置、规格与目标值）。
        保存后对当前已在位但未建档的通道立即补建档，无需重新插拔。
      </p>
      <el-select v-model="groupDlgSel" clearable placeholder="选择监测组（清空 = 暂停自动建档）" style="width: 100%">
        <el-option v-for="g in settings?.availableGroups || []" :key="g.id" :value="g.id" :label="`${g.code} · ${g.name}`" />
      </el-select>
      <template #footer>
        <el-button @click="groupDlgOpen = false">取消</el-button>
        <el-button type="primary" :loading="savingGroup" @click="saveDefaultGroup">保存</el-button>
      </template>
    </el-dialog>

    <BoltCreateDialog v-model="createOpen" :channel-id="createChannelId" @done="onBoltCreated" />
    <CalibrationWizard
      v-model="wizardOpen"
      :bolt-id="wizardSel?.boltId ?? null"
      :channel-id="wizardSel?.channelId ?? null"
      :bolt-code="wizardSel?.code ?? ''"
      @done="loadBinding(); loadHealth()"
    />
  </div>
</template>

<style scoped>
.dev { display: flex; flex-direction: column; gap: 16px; }
.dev-cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 14px; }
.dc { padding: 16px 18px; cursor: pointer; transition: all 0.18s; }
.dc:hover { border-color: var(--panel-border-strong); }
.dc.active { border-color: var(--accent); background: rgba(34, 211, 238, 0.06); }
.dc-hd { display: flex; align-items: center; gap: 9px; }
.dc-name { font-weight: 600; font-size: 14px; flex: 1; }
.ptag { background: rgba(34, 211, 238, 0.16) !important; border-color: var(--panel-border-strong) !important; color: var(--accent) !important; }
.dc-state { font-size: 11px; color: var(--text-3); margin: 4px 0 14px; letter-spacing: 0.08em; }
.dc-metrics { display: flex; justify-content: space-between; }
.dc-metrics > div { display: flex; flex-direction: column; gap: 3px; }
.dc-metrics b { font-size: 17px; }
.dc-metrics span { font-size: 11px; color: var(--text-3); }

.panel-hd { display: flex; align-items: center; justify-content: space-between; padding: 16px 20px 0; }
.hd-tools { display: flex; align-items: center; gap: 14px; }
.offline-note { display: flex; align-items: center; gap: 8px; margin: 10px 20px 0; padding: 8px 12px; border: 1px solid var(--warn); border-radius: 8px; color: var(--warn); font-size: 12.5px; }
.hd-link { font-size: 12px; }
.dev-tabs { padding: 0 18px 12px; }
.modebar { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; padding: 6px 0 14px; }
.mb-k { font-size: 12px; color: var(--text-3); }
.mb-hint { font-size: 11px; color: var(--text-3); }
.scan-bar { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; padding: 6px 0 14px; }
.scan-st { font-size: 13px; color: var(--text-2); }
.scan-st .st-run { color: var(--accent); }
.unbound-note { display: flex; align-items: center; gap: 8px; font-size: 12.5px; color: var(--warn); padding: 4px 0 12px; flex-wrap: wrap; }
.unbound-note .un-text { flex: 1; min-width: 200px; }
.dlg-note { font-size: 12.5px; color: var(--text-2); line-height: 1.7; margin: 0 0 14px; }

.tbl { padding: 4px 4px 8px; overflow-x: auto; }
.tr { display: grid; grid-template-columns: 50px 110px 130px 110px 100px 80px 70px 70px 90px; align-items: center; padding: 10px 10px; border-radius: 8px; font-size: 12.5px; min-width: 820px; }
.tr:not(.th) { box-shadow: inset 0 -1px 0 var(--hairline); }
.tr:not(.th):hover { background: rgba(56, 189, 248, 0.05); }
.tr.th.scan, .tr.scan { grid-template-columns: 60px 90px 80px 90px 80px 70px 1fr; }
.tr.th.binds, .tr.binds { grid-template-columns: 60px 130px 130px 120px 120px 130px; }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; text-transform: uppercase; }
.hw { display: flex; align-items: center; gap: 8px; }
.vd { font-style: normal; font-weight: 600; }
.dim { color: var(--text-3); }
</style>
