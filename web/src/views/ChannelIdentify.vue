<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  acceptanceApi, assetApi, boltApi, channelApi, deviceApi, runtimeApi,
  type AcceptanceSessionView, type ChannelLive, type WiringRowBody,
} from '../api'
import { getAuthToken } from '../api/client'
import { onRealtime, startRealtime } from '../api/realtime'
import { useAuth } from '../stores/auth'
import BoltCreateDialog from '../components/BoltCreateDialog.vue'

// 点位与验收：检测即螺栓——通道确认在位即自动建档并显示（有几颗显示几颗）；
// 点位表导入 / 接入验收是正式交付的可选补强（批量位号、声时指纹），不再是建档必经之路。
const router = useRouter()
const route = useRoute()
const auth = useAuth()

const deviceId = ref<number | null>(null)
const rows = ref<ChannelLive[]>([])
const loading = ref(true)
const labelEdit = ref<Record<number, string>>({})
const savingPos = ref<number | null>(null)
let pollTimer: number | undefined

// 设备默认监测组：自动建档归属；未设置时在位通道只采集不建档，横幅引导去设置
const defaultGroupId = ref<number | null>(null)
const defaultGroupCode = ref<string | null>(null)
const settingsLoaded = ref(false)

// SignalR 实时：OnMeasurement 直刷声时（REST 只兜底补峰峰值/SNR/档案），OnChannelPresence 秒级翻在位灯
const liveAt = reactive<Record<number, number>>({})   // 通道最近一次实时回波的本地时刻（点亮动画用）
const nowTick = ref(Date.now())
let tickTimer: number | undefined
const offRealtime: (() => void)[] = []

async function poll() {
  if (deviceId.value == null) return
  try {
    const d = await deviceApi.channelsLive(deviceId.value)
    rows.value = d.items
    // 输入框用现有位号预填（仅首次，不覆盖正在编辑的内容）
    for (const it of d.items) {
      if (labelEdit.value[it.channelNo] === undefined && it.positionLabel)
        labelEdit.value[it.channelNo] = it.positionLabel
    }
  } catch {
    /* 尽力轮询 */
  } finally {
    loading.value = false
  }
}

async function loadSettings() {
  if (deviceId.value == null) return
  try {
    const s = await deviceApi.getSettings(deviceId.value)
    defaultGroupId.value = s.defaultGroupId
    defaultGroupCode.value = s.defaultGroupCode
  } catch {
    defaultGroupId.value = null
    defaultGroupCode.value = null
  } finally {
    settingsLoaded.value = true
  }
}

onMounted(async () => {
  try {
    const rt = await runtimeApi.devices()
    const fromQuery = Number(route.query.deviceId)
    deviceId.value =
      (Number.isFinite(fromQuery) && rt.items.some((x) => x.deviceId === fromQuery) ? fromQuery : null)
      ?? rt.items.find((x) => x.isPrimary)?.deviceId
      ?? rt.items[0]?.deviceId
      ?? null
  } catch {
    deviceId.value = null
  }
  if (deviceId.value == null) {
    loading.value = false
    return
  }
  await Promise.all([poll(), loadSession(), loadSettings()])
  // 声时走 SignalR 秒级直刷；REST 10s 一轮兜底补峰峰值/SNR/绑定与验收状态
  pollTimer = window.setInterval(() => { if (!document.hidden) poll() }, 10_000)
  tickTimer = window.setInterval(() => { nowTick.value = Date.now(); expireSessionLocally() }, 1000)

  startRealtime()
  offRealtime.push(onRealtime('OnMeasurement', (m) => {
    if (m.deviceId !== deviceId.value) return
    const row = rows.value.find((r) => r.channelNo === m.channel)
    if (!row) return
    if (m.echoTimeNs != null) {
      row.echoNs = m.echoTimeNs
      row.lastTs = m.ts
      liveAt[m.channel] = Date.now()
    }
    if (m.spTempC != null) row.tempC = m.spTempC
  }))
  offRealtime.push(onRealtime('OnChannelPresence', (p) => {
    if (p.deviceId !== deviceId.value) return
    const row = rows.value.find((r) => r.channelNo === p.channelNo)
    if (row) {
      row.hardwareState = p.present ? 'healthy' : 'no_data'
      if (p.present) {
        row.lastPresentAt = p.ts
        // 自动建档后事件带新螺栓编号——就地补齐，不必等下一轮 REST
        if (p.boltId != null) {
          row.boltId = p.boltId
          row.boltCode = p.boltCode
        }
      }
    }
    // 在位确认：立刻 REST 刷新一次，让自动建档的螺栓（含峰峰值/验收态）秒级上屏
    if (p.present) void poll()
    // 验收会话中的插拔事件是现场确认的关键反馈，弹提示；平时只翻状态灯不打扰
    if (!p.initial && session.value?.active) {
      if (p.present) ElMessage.success(`CH${p.channelNo} 信号出现——与当前接的电缆核对无误后，点该通道「确认验收」`)
      else ElMessage.warning(`CH${p.channelNo} 信号静默——刚断开的电缆对应 CH${p.channelNo}`)
    }
  }))
})
onUnmounted(() => {
  if (pollTimer) clearInterval(pollTimer)
  if (tickTimer) clearInterval(tickTimer)
  offRealtime.forEach((off) => off())
})

// ---- 展示 ----
// 数量口径（对账链，唯一事实源是 channels.hardware_state + last_present_at）：
//   48 通道 → 在位 → 螺栓（在测 + 失联 + 待接入）；在位且未建档 = 有信号未建档（缺默认组时出现）。
//   失联 = 建档且曾在位、当前无信号（红框）；待接入 = 建档但从未在位（点位表预导入，中性灰）。
const sorted = computed(() => [...rows.value].sort((a, b) => a.channelNo - b.channelNo))
const presentCount = computed(() => rows.value.filter(hasSignal).length)
const boundCount = computed(() => rows.value.filter((r) => r.boltId != null).length)
const monitoringCount = computed(() => rows.value.filter((r) => r.boltId != null && hasSignal(r)).length)
const lostCount = computed(() => rows.value.filter((r) => isLost(r)).length)
const pendingCount = computed(() => rows.value.filter((r) => isPending(r)).length)
const needsSpecCount = computed(() => rows.value.filter((r) => needsSpec(r)).length)
const unboundSignalCount = computed(() => rows.value.filter((r) => r.boltId == null && hasSignal(r)).length)
const acceptedCount = computed(() => rows.value.filter((r) => r.acceptedAt != null).length)
const needDefaultGroup = computed(() =>
  settingsLoaded.value && defaultGroupId.value == null && unboundSignalCount.value > 0)

const HW_STATE: Record<string, { label: string; color: string }> = {
  healthy: { label: '在位', color: '#10b981' },
  noisy: { label: '噪声', color: '#f59e0b' },
  suspect: { label: '可疑', color: '#fb923c' },
  no_data: { label: '无信号', color: '#61748f' },
  unknown: { label: '未知', color: '#61748f' },
}
function hasSignal(it: ChannelLive) {
  return it.hardwareState === 'healthy' || it.hardwareState === 'noisy' || it.hardwareState === 'suspect'
}
/** 失联：建档 + 曾在位确认 + 当前无信号 */
function isLost(it: ChannelLive) {
  return it.boltId != null && !hasSignal(it) && it.lastPresentAt != null
}
/** 待接入：建档 + 从未在位（点位表预导入等） */
function isPending(it: ChannelLive) {
  return it.boltId != null && !hasSignal(it) && it.lastPresentAt == null
}
/** 待补规格：已建档但无目标预紧力（自动建档缺省态，与「未标定」分开） */
function needsSpec(it: ChannelLive) {
  return it.boltId != null && it.targetPreloadN == null
}
function sigPct(p2p: number | null) {
  return Math.min(100, Math.round(((p2p ?? 0) / 1500) * 100))
}
function isLive(ch: number) {
  return nowTick.value - (liveAt[ch] ?? 0) < 2500
}
function fmtDay(ts: string | null) {
  return ts ? ts.slice(0, 10) : ''
}

// ---- 验收会话 ----
const session = ref<AcceptanceSessionView | null>(null)
const sessionBusy = ref(false)
const sessionRemaining = computed(() => {
  if (!session.value?.active || !session.value.expiresAt) return ''
  const s = Math.max(0, Math.floor((new Date(session.value.expiresAt).getTime() - nowTick.value) / 1000))
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
})
function expireSessionLocally() {
  if (session.value?.active && session.value.expiresAt && new Date(session.value.expiresAt).getTime() <= Date.now())
    session.value = null
}
function inSession(ch: number) {
  return !!session.value?.active && (session.value.channels ?? []).includes(ch)
}
async function loadSession() {
  if (deviceId.value == null) return
  try {
    const s = await acceptanceApi.session(deviceId.value)
    session.value = s.active ? s : null
  } catch { session.value = null }
}
async function startSession() {
  if (deviceId.value == null) return
  try {
    await ElMessageBox.confirm(
      '验收会话把设备轮询临时缩小到【尚未验收】的通道集，插拔确认从数十秒提速到秒级。' +
      '期间其余监测通道暂停刷新；最长 30 分钟自动结束并恢复全通道轮询。',
      '开启验收会话', { type: 'info', confirmButtonText: '开启', cancelButtonText: '取消' })
  } catch { return }
  sessionBusy.value = true
  try {
    const s = await acceptanceApi.startSession(deviceId.value, {})
    session.value = s
    ElMessage.success(`验收会话已开启：${s.channels?.length ?? 0} 个通道，单通道刷新约 ${s.estimatedRefreshSeconds ?? '-'}s`)
  } catch (e) {
    ElMessage.error((e as Error)?.message || '开启失败')
  } finally {
    sessionBusy.value = false
  }
}
async function endSession() {
  if (deviceId.value == null) return
  sessionBusy.value = true
  try {
    const r = await acceptanceApi.endSession(deviceId.value)
    session.value = null
    ElMessage.success(r.note || '验收会话已结束')
  } catch (e) {
    ElMessage.error((e as Error)?.message || '结束失败')
  } finally {
    sessionBusy.value = false
  }
}

// ---- 确认验收 ----
const acceptOpen = ref(false)
const acceptTarget = ref<ChannelLive | null>(null)
const acceptSubmitting = ref(false)
const acceptForm = reactive({ method: 'sequential', cableLabel: '', positionLabel: '', note: '' })
const METHODS = [
  { value: 'sequential', label: '顺序接入', hint: '新装：按点位表逐根接线，通道点亮后确认' },
  { value: 'loopcheck', label: '拔插确认', hint: '存量：断开电缆接头观察哪个通道静默，回插确认' },
  { value: 'manual', label: '人工登记', hint: '无信号验证（如备用通道按图纸登记），不留指纹' },
]
function openAccept(it: ChannelLive) {
  acceptTarget.value = it
  acceptForm.method = hasSignal(it) ? (session.value?.active ? 'sequential' : 'loopcheck') : 'manual'
  acceptForm.cableLabel = ''
  acceptForm.positionLabel = it.positionLabel || it.channelName || ''
  acceptForm.note = ''
  acceptOpen.value = true
}
async function submitAccept() {
  const it = acceptTarget.value
  if (deviceId.value == null || !it) return
  acceptSubmitting.value = true
  try {
    const r = await acceptanceApi.accept(deviceId.value, it.channelNo, {
      method: acceptForm.method,
      cableLabel: acceptForm.cableLabel.trim() || undefined,
      positionLabel: acceptForm.positionLabel.trim() || undefined,
      note: acceptForm.note.trim() || undefined,
    })
    ElMessage.success(
      `CH${it.channelNo} 验收通过` + (r.tofNs != null ? `，声时指纹 ${Number(r.tofNs).toFixed(0)}ns 已存档` : ''))
    if (r.warning) ElMessage.warning(r.warning)
    acceptOpen.value = false
    await poll()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '验收失败')
  } finally {
    acceptSubmitting.value = false
  }
}

// ---- 点位表导入 / 导出 ----
const importOpen = ref(false)
const importSubmitting = ref(false)
const csvText = ref('')
const csvFileName = ref('')
const importCreateBolts = ref(true)
const importGroupId = ref<number | null>(null)
const groupOptions = ref<{ id: number; label: string }[]>([])

const HEADER_ALIASES: Record<string, keyof WiringRowBody> = {
  channel_no: 'channelNo', 通道: 'channelNo', 通道号: 'channelNo',
  position_label: 'positionLabel', 位置: 'positionLabel', 位号: 'positionLabel',
  bolt_code: 'boltCode', 编号: 'boltCode', 螺栓编号: 'boltCode',
  nominal_diam: 'nominalDiam', 规格: 'nominalDiam',
  grade: 'grade', 等级: 'grade',
  target_preload_n: 'targetPreloadN', 目标预紧力: 'targetPreloadN',
  min_preload_n: 'minPreloadN', 报警下限: 'minPreloadN',
}

/** 单行 CSV 字段切分（支持引号包裹与 "" 转义；点位表不含跨行字段）。 */
function splitCsvLine(line: string): string[] {
  const out: string[] = []
  let cur = ''
  let quoted = false
  for (let i = 0; i < line.length; i++) {
    const c = line[i]
    if (quoted) {
      if (c === '"' && line[i + 1] === '"') { cur += '"'; i++ }
      else if (c === '"') quoted = false
      else cur += c
    } else if (c === '"') quoted = true
    else if (c === ',') { out.push(cur); cur = '' }
    else cur += c
  }
  out.push(cur)
  return out.map((s) => s.trim())
}

const parsedImport = computed<{ rows: WiringRowBody[]; errors: string[] }>(() => {
  const errors: string[] = []
  const rowsOut: WiringRowBody[] = []
  const lines = csvText.value.replace(/^\uFEFF/, '').split(/\r?\n/)
    .map((l) => l.trim())
    .filter((l) => l && !l.startsWith('#'))
  if (!lines.length) return { rows: rowsOut, errors }

  const header = splitCsvLine(lines[0]).map((h) => h.toLowerCase())
  const colMap: (keyof WiringRowBody | null)[] = header.map((h) => HEADER_ALIASES[h] ?? null)
  if (!colMap.includes('channelNo')) {
    errors.push('缺少 channel_no（通道号）列——表头需含 channel_no,position_label,... （导出的点位表即为此格式）')
    return { rows: rowsOut, errors }
  }
  for (let i = 1; i < lines.length; i++) {
    const cells = splitCsvLine(lines[i])
    const row: Partial<WiringRowBody> = {}
    colMap.forEach((key, ci) => {
      if (!key || cells[ci] === undefined || cells[ci] === '') return
      if (key === 'channelNo' || key === 'targetPreloadN' || key === 'minPreloadN') {
        const n = Number(cells[ci])
        if (Number.isFinite(n)) (row as Record<string, unknown>)[key] = n
        else errors.push(`第 ${i + 1} 行：${key} 不是数字（${cells[ci]}）`)
      } else {
        (row as Record<string, unknown>)[key] = cells[ci]
      }
    })
    if (row.channelNo == null) { errors.push(`第 ${i + 1} 行：缺通道号`); continue }
    rowsOut.push(row as WiringRowBody)
  }
  const seen = new Set<number>()
  for (const r of rowsOut) {
    if (seen.has(r.channelNo)) errors.push(`通道号 ${r.channelNo} 重复`)
    seen.add(r.channelNo)
  }
  return { rows: rowsOut, errors }
})

async function openImport() {
  csvText.value = ''
  csvFileName.value = ''
  importCreateBolts.value = true
  importGroupId.value = null
  importOpen.value = true
  // 组选项：projects → turbines → groups 拉平（与建档对话框同一做法，规模小可接受）
  try {
    const projs = await assetApi.projects()
    const turbineLists = await Promise.all(projs.items.map((p) => assetApi.turbines(p.id)))
    const turbines = turbineLists.flatMap((t) => t.items)
    const groupLists = await Promise.all(turbines.map((t) => assetApi.groups(t.id)))
    groupOptions.value = groupLists.flatMap((g, i) =>
      g.items.map((gg) => ({ id: gg.id, label: `${turbines[i].name} · ${gg.name}` })))
    if (groupOptions.value.length === 1) importGroupId.value = groupOptions.value[0].id
  } catch { groupOptions.value = [] }
}
function onCsvFile(ev: Event) {
  const f = (ev.target as HTMLInputElement).files?.[0]
  if (!f) return
  csvFileName.value = f.name
  const reader = new FileReader()
  reader.onload = () => { csvText.value = String(reader.result ?? '') }
  reader.readAsText(f)
}
async function submitImport() {
  if (deviceId.value == null) return
  const { rows: parsed, errors } = parsedImport.value
  if (errors.length || !parsed.length) return ElMessage.warning('请先修正点位表内容')
  if (importCreateBolts.value && importGroupId.value == null) return ElMessage.warning('批量建档需选择监测组')
  importSubmitting.value = true
  try {
    const r = await acceptanceApi.importWiring(deviceId.value, {
      rows: parsed,
      createBolts: importCreateBolts.value,
      groupId: importCreateBolts.value ? importGroupId.value! : undefined,
      note: csvFileName.value ? `导入文件 ${csvFileName.value}` : undefined,
    })
    ElMessage.success(
      `点位表导入完成：位置更新 ${r.updatedPositions}，建档 ${r.createdBolts.length}` +
      (r.skippedBound.length ? `，已绑定跳过 ${r.skippedBound.length}` : '') +
      (r.backfilled ? `，回填暂存 ${r.backfilled} 条` : ''))
    importOpen.value = false
    await poll()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '导入失败（整批未生效）')
  } finally {
    importSubmitting.value = false
  }
}

async function downloadCsv(url: string, fallback: string) {
  try {
    const res = await fetch(url, { headers: { Authorization: `Bearer ${getAuthToken()}` } })
    if (!res.ok) throw new Error(`下载失败（${res.status}）`)
    const blob = await res.blob()
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = res.headers.get('content-disposition')?.match(/filename="?([^";]+)/)?.[1] ?? fallback
    a.click()
    URL.revokeObjectURL(a.href)
  } catch (e) {
    ElMessage.error((e as Error)?.message || '下载失败')
  }
}

// ---- 位号标注 / 建档 / 删除（沿用原有闭环） ----
async function savePosition(it: ChannelLive) {
  if (it.boltId == null) return ElMessage.warning('该通道未绑定螺栓,无法标位置')
  const label = (labelEdit.value[it.channelNo] ?? '').trim()
  if (!label && it.positionLabel) {
    try {
      await ElMessageBox.confirm(`输入为空，将清空 CH${it.channelNo} 的现有位号「${it.positionLabel}」，确认？`, '清空位号', { type: 'warning', confirmButtonText: '清空', cancelButtonText: '取消' })
    } catch { return }
  }
  savingPos.value = it.channelNo
  try {
    await boltApi.updateSettings(it.boltId, { positionLabel: label })
    ElMessage.success(`通道 ${it.channelNo} 位置已标为「${label || '(清空)'}」`)
    await poll()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '保存失败')
  } finally {
    savingPos.value = null
  }
}

const createOpen = ref(false)
const createChannelId = ref<number | null>(null)
function openCreate(it: ChannelLive) {
  createChannelId.value = it.channelId
  createOpen.value = true
}
async function onBoltCreated() {
  await poll()
}

async function unbind(it: ChannelLive) {
  if (it.boltId == null) return
  const stillPresent = hasSignal(it)
  try {
    const { value } = await ElMessageBox.prompt(
      `删除螺栓 ${it.boltCode}（通道 CH${it.channelNo}）。档案将停用，该通道标定基线清除，历史数据保留可查。` +
      (stillPresent
        ? '探头仍在位：不会立刻自动重建，须拔掉再插（或进程重启也不会顶回）后才会重新建档。'
        : '') +
      '请填原因：',
      '删除螺栓', { confirmButtonText: '删除', cancelButtonText: '取消', inputType: 'textarea',
        inputValidator: (v) => (v && v.trim() ? true : '请填原因') })
    await channelApi.unbind(it.channelId, value)
    ElMessage.success(
      stillPresent
        ? `螺栓 ${it.boltCode} 已删除；探头仍在位，自动建档已抑制至离位后再插入`
        : `螺栓 ${it.boltCode} 已删除，通道 CH${it.channelNo} 回到未绑定`)
    delete labelEdit.value[it.channelNo]
    await poll()
  } catch { /* 取消 */ }
}
</script>

<template>
  <div class="ident rise" v-loading="loading">
    <div class="ident-hd panel">
      <div class="ident-intro">
        <span class="panel-title"><i class="hd-mark" />点位与验收</span>
        <p class="sub">
          <b>检测即螺栓</b>：探头确认在位后自动建档并显示（有几颗显示几颗，零点击）；断线标失联不消失，删除仍需人工。
          自动建档后请补目标预紧力并标定；点位表 / 接入验收为正式交付可选。
        </p>
        <p v-if="rows.length" class="sub stat mono">
          共 {{ rows.length }} 通道 · {{ presentCount }} 路在位 ·
          螺栓 {{ boundCount }} 颗（在测 {{ monitoringCount }}<template v-if="lostCount"> · <span class="stat-lost">失联 {{ lostCount }}</span></template><template v-if="pendingCount"> · 待接入 {{ pendingCount }}</template><template v-if="needsSpecCount"> · <span class="stat-spec">待补规格 {{ needsSpecCount }}</span></template>）<template v-if="unboundSignalCount"> · 有信号未建档 {{ unboundSignalCount }} 路</template> · 已验收 {{ acceptedCount }} 路
        </p>
      </div>
      <div class="hd-tools">
        <template v-if="deviceId != null">
          <el-button v-if="auth.isReviewer && !session?.active" size="small" type="primary" :loading="sessionBusy" @click="startSession">开启验收会话</el-button>
          <el-button v-if="auth.isReviewer" size="small" @click="openImport">导入点位表</el-button>
          <el-button size="small" text @click="downloadCsv(acceptanceApi.wiringExportUrl(deviceId), 'wiring.csv')">导出点位表</el-button>
          <el-button size="small" text @click="downloadCsv(acceptanceApi.reportUrl(deviceId), 'acceptance.csv')">验收报告</el-button>
        </template>
      </div>
    </div>

    <!-- 未设默认监测组：自动建档停摆，引导去设备页设置 -->
    <div v-if="needDefaultGroup" class="nogroup panel">
      <span class="nogroup-badge">自动建档未生效</span>
      <span class="nogroup-text">
        检测到 {{ unboundSignalCount }} 路在位探头，但设备未设置默认监测组——新螺栓无处归属。
        请到「采集设备」页设置默认监测组，设置后当前在位通道会立即补建档。
      </span>
      <el-button size="small" type="warning" @click="router.push({ name: 'devices', query: deviceId != null ? { deviceId } : undefined })">去设置 →</el-button>
    </div>

    <!-- 验收会话横幅：现场操作的两种玩法 + 倒计时 -->
    <div v-if="session?.active" class="session panel">
      <div class="session-main">
        <span class="session-badge">验收会话进行中</span>
        <span class="session-meta mono">
          {{ session.channels?.length ?? 0 }} 个通道 · 单通道刷新约 {{ session.estimatedRefreshSeconds ?? '—' }}s · 剩余 {{ sessionRemaining }}
        </span>
        <el-button size="small" :loading="sessionBusy" @click="endSession">结束会话并恢复轮询</el-button>
      </div>
      <p class="session-hint">
        <b>顺序接入（新装）</b>：按点位表逐根接线，接头插入后对应通道秒级点亮并弹提示，核对电缆标签后点「确认验收」。
        <b>拔插确认（存量）</b>：在设备端断开某根电缆接头，静默的通道即对应该电缆，回插信号恢复后确认。
        结束/超时后自动恢复全通道轮询。
      </p>
    </div>

    <div class="grid">
      <!-- 空通道淡化；失联（曾在位）红框；待接入（从未在位，点位表预导入）中性 -->
      <div
        v-for="it in sorted" :key="it.channelNo"
        class="ch panel"
        :class="{
          weak: !hasSignal(it) && it.boltId == null,
          lost: isLost(it),
          pending: isPending(it),
          insession: inSession(it.channelNo),
        }">
        <div class="ch-hd">
          <span class="ch-no">CH{{ it.channelNo }}</span>
          <span class="hw" :style="`color:${HW_STATE[it.hardwareState]?.color || '#61748f'}`">
            <i class="hw-led" :style="`background:${HW_STATE[it.hardwareState]?.color || '#61748f'}`" />{{ HW_STATE[it.hardwareState]?.label || it.hardwareState }}
          </span>
          <span v-if="inSession(it.channelNo)" class="sess-tag">验收中</span>
          <span v-if="needsSpec(it)" class="spec-tag">待补规格</span>
          <span class="ch-bolt mono">{{ it.boltCode || '未绑螺栓' }}</span>
        </div>

        <div class="ch-echo mono" :class="{ live: isLive(it.channelNo) }">
          <template v-if="it.echoNs != null">{{ Number(it.echoNs).toFixed(0) }}<i>ns</i></template>
          <template v-else><span class="noecho">无声时</span></template>
        </div>

        <div class="ch-sig">
          <div class="sig-bar"><div class="sig-fill" :style="{ width: sigPct(it.peakToPeak) + '%' }" /></div>
          <span class="sig-txt mono">峰峰 {{ it.peakToPeak ?? '—' }} · SNR {{ it.snrDb != null ? Number(it.snrDb).toFixed(0) : '—' }}dB</span>
        </div>

        <div class="ch-accept">
          <span v-if="it.acceptedAt" class="acc-ok">✓ 已验收 {{ fmtDay(it.acceptedAt) }}</span>
          <span v-else class="acc-no">未验收</span>
          <el-button size="small" :type="it.acceptedAt ? 'default' : 'success'" :plain="!!it.acceptedAt" text bg @click="openAccept(it)">
            {{ it.acceptedAt ? '重新验收' : '确认验收' }}
          </el-button>
        </div>

        <!-- 已绑定：标位置 + 详情 + 删除；未绑定：建档绑定（自动建档失败时的人工兜底） -->
        <template v-if="it.boltId != null">
          <div v-if="isLost(it)" class="ch-lostmsg">
            探头失联——检查电缆接头与耦合；若设备有上报但持续算不出声时，需重标定基线
          </div>
          <div v-else-if="isPending(it)" class="ch-pendingmsg">
            待接入——档案已建（点位表预导入），接上探头后自动进入在测
          </div>
          <div class="ch-pos">
            <el-input v-model="labelEdit[it.channelNo]" :placeholder="it.positionLabel || '位置,如 12点'" size="small" />
            <el-button size="small" type="primary" :loading="savingPos === it.channelNo" @click="savePosition(it)">标注</el-button>
          </div>
          <div v-if="it.positionLabel" class="ch-curpos">当前: {{ it.positionLabel }}</div>
          <div class="ch-actions">
            <span
              class="ch-open"
              @click="router.push({ name: 'boltDetail', params: { boltId: it.boltId } })">
              {{ needsSpec(it) ? '补规格 / 标定 →' : '查看实时波形 →' }}
            </span>
            <el-button v-if="auth.isReviewer" size="small" text type="danger" class="ch-del" @click="unbind(it)">删除螺栓</el-button>
          </div>
        </template>
        <template v-else>
          <div class="ch-bind">
            <el-button size="small" type="primary" plain @click="openCreate(it)">建档绑定</el-button>
            <span v-if="hasSignal(it)" class="bind-hint">
              {{ defaultGroupId ? '检测到探头，自动建档中…也可手工建档' : '检测到探头——请先设置设备默认监测组，或手工建档' }}
            </span>
            <span v-else class="bind-hint dim">{{ it.channelName ? `位置：${it.channelName}` : '未检测到探头' }}</span>
          </div>
        </template>
      </div>
      <div v-if="!loading && !rows.length" class="empty">
        {{ deviceId == null
          ? '未找到采集设备运行时,请先到「采集设备」页确认设备已接入'
          : '该设备通道目录为空——请到「采集设备」页用「通道数」设置实际通道数（如 48）重建目录' }}
      </div>
    </div>

    <!-- 确认验收 -->
    <el-dialog v-model="acceptOpen" :title="`确认验收：CH${acceptTarget?.channelNo ?? ''}`" width="480px" destroy-on-close>
      <el-alert type="info" :closable="false" show-icon style="margin-bottom: 14px">
        验收会把当前声时存为该通道的<b>接线指纹</b>并落验收记录（含操作人，可出报告）。
        信号验证方式要求验收时刻有回波；无信号的备用通道请用「人工登记」。
      </el-alert>
      <el-form label-width="90px" label-position="left">
        <el-form-item label="验收方式">
          <el-radio-group v-model="acceptForm.method">
            <el-radio v-for="m in METHODS" :key="m.value" :value="m.value">{{ m.label }}</el-radio>
          </el-radio-group>
          <div class="fld-hint">{{ METHODS.find((m) => m.value === acceptForm.method)?.hint }}</div>
        </el-form-item>
        <el-form-item label="电缆标签">
          <el-input v-model="acceptForm.cableLabel" placeholder="现场线缆吊牌号，如 W-A12（强烈建议填）" maxlength="64" />
        </el-form-item>
        <el-form-item label="物理位置">
          <el-input v-model="acceptForm.positionLabel" placeholder="如「1号机舱后法兰 5号孔」，写入通道位置" maxlength="64" />
        </el-form-item>
        <el-form-item label="备注">
          <el-input v-model="acceptForm.note" type="textarea" :rows="2" placeholder="选填（记入验收记录与审计）" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="acceptOpen = false">取消</el-button>
        <el-button type="primary" :loading="acceptSubmitting" @click="submitAccept">验收通过</el-button>
      </template>
    </el-dialog>

    <!-- 导入点位表 -->
    <el-dialog v-model="importOpen" title="导入点位表（接线表）" width="640px" destroy-on-close>
      <el-alert type="info" :closable="false" show-icon style="margin-bottom: 14px">
        CSV 表头：<code>channel_no,position_label,bolt_code,nominal_diam,grade,target_preload_n,min_preload_n</code>
        （仅前两列必填；「导出点位表」可拿到当前模板）。导入为<b>全或无</b>：任一行无效整批不生效。
        已绑定螺栓的通道只更新位置、不重复建档，可放心重导。
      </el-alert>
      <div class="imp-file">
        <input type="file" accept=".csv,text/csv" @change="onCsvFile" />
        <span v-if="csvFileName" class="mono">{{ csvFileName }}</span>
      </div>
      <el-input v-model="csvText" type="textarea" :rows="8" placeholder="或直接粘贴 CSV 内容…" class="mono" />
      <div class="imp-status">
        <span v-if="parsedImport.rows.length" class="ok">解析出 {{ parsedImport.rows.length }} 行</span>
        <span v-for="(e, i) in parsedImport.errors.slice(0, 5)" :key="i" class="err">{{ e }}</span>
      </div>
      <el-form label-width="110px" label-position="left" style="margin-top: 10px">
        <el-form-item label="批量建档">
          <el-switch v-model="importCreateBolts" />
          <span class="fld-hint inline">为未绑定的通道按行建立螺栓档案（编号缺省 组-CH号）</span>
        </el-form-item>
        <el-form-item v-if="importCreateBolts" label="监测组" required>
          <el-select v-model="importGroupId" placeholder="新建螺栓所属部位（法兰）" style="width: 100%" filterable>
            <el-option v-for="g in groupOptions" :key="g.id" :value="g.id" :label="g.label" />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="importOpen = false">取消</el-button>
        <el-button
          type="primary" :loading="importSubmitting"
          :disabled="!parsedImport.rows.length || !!parsedImport.errors.length"
          @click="submitImport">导入 {{ parsedImport.rows.length || '' }} 行</el-button>
      </template>
    </el-dialog>

    <BoltCreateDialog v-model="createOpen" :channel-id="createChannelId" @done="onBoltCreated" />
  </div>
</template>

<style scoped>
.ident { display: flex; flex-direction: column; gap: 16px; }
.ident-hd { display: flex; align-items: flex-start; justify-content: space-between; gap: 24px; flex-wrap: wrap; padding: 16px 20px; }
.ident-intro { display: flex; flex-direction: column; gap: 8px; min-width: 0; }
.sub { margin: 0; font-size: 13px; color: var(--text-3); max-width: 640px; line-height: 1.6; }
.sub b { color: var(--accent); }
.sub.stat { font-size: 12px; color: var(--text-2); }
.stat-lost { color: var(--danger, #f56c6c); font-weight: 700; }
.stat-spec { color: var(--warn, #f59e0b); font-weight: 700; }
.spec-tag { font-size: 10.5px; color: var(--warn, #f59e0b); border: 1px solid var(--warn, #f59e0b); border-radius: 3px; padding: 0 4px; }
.hd-tools { display: flex; align-items: center; gap: 4px; flex-wrap: wrap; }

.nogroup { display: flex; align-items: center; gap: 12px; padding: 12px 20px; border-color: rgba(245, 158, 11, 0.5); flex-wrap: wrap; }
.nogroup-badge { font-size: 12px; font-weight: 700; color: var(--warn, #f59e0b); white-space: nowrap; }
.nogroup-text { flex: 1; min-width: 240px; font-size: 12.5px; color: var(--text-2); line-height: 1.6; }

.session { padding: 12px 20px; border-color: var(--accent); box-shadow: 0 0 0 1px var(--accent); }
.session-main { display: flex; align-items: center; gap: 14px; flex-wrap: wrap; }
.session-badge { font-size: 12px; font-weight: 700; color: var(--accent); animation: blink 1.2s steps(2) infinite; }
@keyframes blink { 50% { opacity: 0.4; } }
.session-meta { font-size: 12px; color: var(--text-2); }
.session-hint { margin: 8px 0 0; font-size: 12px; color: var(--text-3); line-height: 1.7; }
.session-hint b { color: var(--text-2); }

.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 14px; }
.ch { padding: 14px 16px; transition: border-color 0.2s, box-shadow 0.2s, opacity 0.2s; }
.ch.weak { opacity: 0.55; }
.ch.weak:hover { opacity: 0.9; }
.ch.lost { border-color: rgba(245, 108, 108, 0.55); }
.ch.pending { border-color: rgba(148, 163, 184, 0.35); opacity: 0.85; }
.ch.insession { border-color: rgba(64, 158, 255, 0.55); }
.ch-lostmsg { margin-top: 8px; font-size: 11px; line-height: 1.6; color: var(--danger, #f56c6c); }
.ch-pendingmsg { margin-top: 8px; font-size: 11px; line-height: 1.6; color: var(--text-3); }
.ch-hd { display: flex; align-items: center; gap: 8px; }
.ch-no { font-size: 15px; font-weight: 700; }
.hw { display: inline-flex; align-items: center; gap: 4px; font-size: 10.5px; }
.hw-led { width: 7px; height: 7px; border-radius: 50%; display: inline-block; }
.sess-tag { font-size: 10.5px; color: var(--accent); border: 1px solid var(--accent); border-radius: 3px; padding: 0 4px; }
.ch-bolt { font-size: 11px; color: var(--text-3); margin-left: auto; }
.ch-echo { font-size: 26px; font-weight: 700; margin: 8px 0 4px; color: var(--accent); transition: text-shadow 0.3s; }
.ch-echo.live { text-shadow: 0 0 14px var(--accent); }
.ch-echo i { font-size: 12px; color: var(--text-3); font-style: normal; margin-left: 3px; }
.noecho { font-size: 13px; color: var(--text-3); font-weight: 400; }
.ch-sig { display: flex; flex-direction: column; gap: 4px; margin: 6px 0; }
.sig-bar { height: 6px; border-radius: 3px; background: rgba(148, 163, 184, 0.12); overflow: hidden; }
.sig-fill { height: 100%; background: linear-gradient(90deg, var(--accent), var(--ok)); border-radius: 3px; transition: width 0.4s; }
.sig-txt { font-size: 10.5px; color: var(--text-3); }
.ch-accept { display: flex; align-items: center; justify-content: space-between; margin-top: 6px; min-height: 24px; }
.acc-ok { font-size: 11px; color: var(--ok); }
.acc-no { font-size: 11px; color: var(--text-3); }
.ch-pos { display: flex; gap: 6px; margin-top: 8px; }
.ch-curpos { font-size: 11px; color: var(--accent-2); margin-top: 5px; }
.ch-actions { display: flex; align-items: center; justify-content: space-between; margin-top: 8px; min-height: 22px; }
.ch-open { font-size: 11px; color: var(--accent); cursor: pointer; }
.ch-open:hover { text-decoration: underline; }
.ch-del { padding: 0 4px; }
.ch-bind { display: flex; align-items: center; gap: 8px; margin-top: 10px; flex-wrap: wrap; }
.bind-hint { font-size: 11px; color: var(--ok); }
.bind-hint.dim { color: var(--text-3); }
.empty { grid-column: 1 / -1; text-align: center; color: var(--text-3); padding: 40px; }

.fld-hint { font-size: 11.5px; color: var(--text-3); line-height: 1.5; margin-top: 4px; }
.fld-hint.inline { margin: 0 0 0 10px; }
.imp-file { display: flex; align-items: center; gap: 10px; margin-bottom: 8px; font-size: 12px; }
.imp-status { display: flex; flex-direction: column; gap: 3px; margin-top: 8px; font-size: 12px; }
.imp-status .ok { color: var(--ok); }
.imp-status .err { color: var(--danger, #f56c6c); }
</style>
