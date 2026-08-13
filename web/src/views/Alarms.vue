<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { alarmApi, type AlarmItem, type AlarmSummaryRow, type AlarmShelving } from '../api'
import { onRealtime } from '../api/realtime'
import { useNow } from '../utils/useNow'
import { alarmTypeLabel } from '../utils/labels'
import AlarmRulesPanel from '../components/AlarmRulesPanel.vue'
import NotificationPanel from '../components/NotificationPanel.vue'
import { useAuth } from '../stores/auth'

// 告警中心（工业告警管理口径，参照 ISA-18.2 生命周期）：
// 触发(未确认) → 确认(已知悉,仍开启) → 闭合(恢复/处置完成)。
// 界面优先级：未确认 > 已确认未闭合 > 已闭合；抑制(shelve)必须可见可管理。
const router = useRouter()
const items = ref<AlarmItem[]>([])
const summary = ref<AlarmSummaryRow[]>([])
const shelvings = ref<AlarmShelving[]>([])
const loading = ref(false)
let timer: number | undefined
let unsub: (() => void) | undefined

// 页级切换：告警处置（默认） / 阈值规则（reviewer 才能改） / 出站通知（配置 Admin、发送记录 reviewer+）
const auth = useAuth()
const pageTab = ref<'dispose' | 'rules' | 'notify'>('dispose')
// 状态段选：未闭合(默认) / 未确认 / 已闭合 / 全部
const view = ref<'open' | 'unacked' | 'cleared' | 'all'>('open')
const filterSeverity = ref('')
const filterType = ref('')
const search = ref('')
const sel = ref<Record<number, boolean>>({})

const SEV: Record<string, { label: string; cls: string; color: string; bucket: 'crit' | 'warn' | 'info'; rank: number }> = {
  critical: { label: '严重', cls: 'led--crit', color: '#f43f5e', bucket: 'crit', rank: 0 },
  crit: { label: '严重', cls: 'led--crit', color: '#f43f5e', bucket: 'crit', rank: 0 },
  warn: { label: '预警', cls: 'led--warn', color: '#f59e0b', bucket: 'warn', rank: 1 },
  warning: { label: '预警', cls: 'led--warn', color: '#f59e0b', bucket: 'warn', rank: 1 },
  info: { label: '提示', cls: 'led--off', color: '#38bdf8', bucket: 'info', rank: 2 },
}
// ---- 摘要（summary 接口口径：仅未闭合） ----
const openTotal = computed(() => summary.value.reduce((a, r) => a + r.open, 0))
const openUnacked = computed(() => summary.value.reduce((a, r) => a + r.openUnacked, 0))
const openCrit = computed(() => summary.value.filter((r) => SEV[r.severity]?.bucket === 'crit').reduce((a, r) => a + r.open, 0))
const openWarn = computed(() => summary.value.filter((r) => SEV[r.severity]?.bucket === 'warn').reduce((a, r) => a + r.open, 0))

// severity × type 矩阵（未闭合计数）
const matrix = computed(() => {
  const types = [...new Set(summary.value.map((r) => r.type))]
  return types.map((t) => {
    const cell = (b: string) => summary.value.filter((r) => r.type === t && SEV[r.severity]?.bucket === b).reduce((a, r) => a + r.open, 0)
    return { type: t, crit: cell('crit'), warn: cell('warn'), info: cell('info') }
  }).filter((r) => r.crit + r.warn + r.info > 0)
})

const typeOptions = computed(() => [...new Set(items.value.map((a) => a.type))])

const filtered = computed(() => {
  const q = search.value.trim().toLowerCase()
  const base = items.value.filter((a) => {
    if (view.value === 'open' && !a.isOpen) return false
    if (view.value === 'unacked' && (!a.isOpen || a.isAcked)) return false
    if (view.value === 'cleared' && a.isOpen) return false
    if (filterSeverity.value && SEV[a.severity]?.bucket !== filterSeverity.value) return false
    if (filterType.value && a.type !== filterType.value) return false
    if (q && !`${a.boltCode} ${a.message ?? ''} ${alarmTypeLabel(a.type)}`.toLowerCase().includes(q)) return false
    return true
  })
  // 处置优先级排序：未确认在前 → 级别（严重>预警>提示）→ 时间新在前
  return [...base].sort((x, y) => {
    const ux = x.isOpen && !x.isAcked ? 0 : 1
    const uy = y.isOpen && !y.isAcked ? 0 : 1
    if (ux !== uy) return ux - uy
    const rx = SEV[x.severity]?.rank ?? 9
    const ry = SEV[y.severity]?.rank ?? 9
    if (rx !== ry) return rx - ry
    return new Date(y.triggeredAt).getTime() - new Date(x.triggeredAt).getTime()
  })
})

const openSelected = computed(() => filtered.value.filter((a) => a.isOpen && sel.value[a.id]))

const loadError = ref<string | null>(null)
// 抑制列表单独一个失败态：它容错退成空数组，但"空"和"没查到"得分开。
const shelvingError = ref<string | null>(null)
// 列表拉取上限（用足后端 1..1000 的最大值）；返回条数达到上限即视为截断，横幅提示——
// 否则摘要卡（全量聚合）说"未闭合 1200"而列表只有 1000 条，两处数字对不上还无解释
const LIST_LIMIT = 1000
const listTruncated = ref(false)
async function load() {
  loading.value = true
  shelvingError.value = null
  try {
    const withCleared = view.value === 'cleared' || view.value === 'all'
    const [list, sum, shv] = await Promise.all([
      alarmApi.list(`?includeCleared=${withCleared}&limit=${LIST_LIMIT}`),
      alarmApi.summary(),
      // 抑制列表单独容错：它挂了不该把告警列表一起拖没（Promise.all 一拒全拒）。
      // 但失败得记在 shelvingError 上，不然静默退成空数组，"生效抑制"显示 0、下方面板消失，
      // 看着就像没有告警被静默。
      alarmApi.shelvings().catch((e) => {
        shelvingError.value = e instanceof Error ? e.message : '抑制规则加载失败'
        return { count: 0, items: [] as AlarmShelving[] }
      }),
    ])
    items.value = list.items
    listTruncated.value = list.items.length >= LIST_LIMIT
    summary.value = sum.items
    shelvings.value = shv.items
    loadError.value = null
    const ids = new Set(items.value.map((a) => a.id))
    for (const k of Object.keys(sel.value)) if (!ids.has(Number(k))) delete sel.value[Number(k)]
  } catch (e) {
    // 拉取失败必须让用户知道列表是旧数据，不能静默装"无告警"
    loadError.value = e instanceof Error ? e.message : '告警列表刷新失败'
  } finally {
    loading.value = false
  }
}
function setView(v: typeof view.value) {
  view.value = v
  load()
}
// OnAlarm 推送去抖：告警风暴（同周期多螺栓越限）时每条推送都全量刷新会打爆后端（3 个请求/条），
// 合并 1s 内的推送为一次刷新
let alarmDebounce: number | undefined
onMounted(() => {
  load()
  timer = window.setInterval(() => { if (!document.hidden) load() }, 30000)
  unsub = onRealtime('OnAlarm', () => {
    if (alarmDebounce) return
    alarmDebounce = window.setTimeout(() => { alarmDebounce = undefined; load() }, 1000)
  })
})
onUnmounted(() => {
  if (timer) clearInterval(timer)
  if (alarmDebounce) clearTimeout(alarmDebounce)
  unsub?.()
})

// ---- 处置动作（均要求留痕） ----
// 注意把“用户取消弹窗”和“API 调用失败”分开：原实现同一个 try/catch 全吞，
// 网络错误/已被他人处置(404)会被当成"取消"静默忽略，操作者误以为已确认/已闭合。
async function ack(a: AlarmItem) {
  let note: string | undefined
  try {
    const { value } = await ElMessageBox.prompt('确认表示"已知悉、处置中"，告警保持开启。可填备注（记入档案）:', `确认告警 · ${a.boltCode}`, {
      confirmButtonText: '确认', cancelButtonText: '取消', inputType: 'textarea', inputValue: '',
    })
    note = value || undefined
  } catch { return /* 用户取消 */ }
  try {
    await alarmApi.ack(a.id, note)
    ElMessage.success('已确认')
  } catch (e) {
    ElMessage.error((e as Error)?.message || '确认失败（告警可能已被他人处置）')
  }
  load()
}
async function clear(a: AlarmItem) {
  let reason: string
  try {
    const { value } = await ElMessageBox.prompt('闭合表示"已恢复/处置完成"。请填写闭合原因（记入审计）。若测点仍越限，闭合后会立即重新触发。', `闭合告警 · ${a.boltCode}`, {
      confirmButtonText: '闭合', cancelButtonText: '取消', inputType: 'textarea',
      inputValidator: (v) => {
        if (!v || !v.trim()) return '请填写闭合原因'
        if (v.trim().length > 500) return `闭合原因最多 500 字（当前 ${v.trim().length}）`
        return true
      },
    })
    reason = value
  } catch { return /* 用户取消 */ }
  try {
    await alarmApi.clear(a.id, reason)
    ElMessage.success('已闭合')
  } catch (e) {
    ElMessage.error((e as Error)?.message || '闭合失败（告警可能已被他人闭合）')
  }
  load()
}
const bulkAcking = ref(false)
async function bulkAck() {
  if (bulkAcking.value) return
  const targets = openSelected.value.filter((a) => !a.isAcked)
  if (!targets.length) return ElMessage.warning('未选择待确认告警')
  try {
    await ElMessageBox.confirm(`批量确认 ${targets.length} 条告警？（备注统一记"批量确认"）`, '批量确认', { type: 'warning' })
  } catch { return }
  bulkAcking.value = true
  try {
    let ok = 0
    for (const a of targets) {
      try { await alarmApi.ack(a.id, '批量确认'); ok++ } catch { /* 单条失败不阻断 */ }
    }
    // 如实报告成功/失败数，不把部分失败包装成全部成功
    if (ok === targets.length) ElMessage.success(`已确认 ${ok} 条`)
    else ElMessage.warning(`已确认 ${ok} 条，失败 ${targets.length - ok} 条（可能已被他人处置，列表已刷新）`)
    sel.value = {}
    load()
  } finally {
    bulkAcking.value = false
  }
}

// ---- 抑制（shelve）：创建 + 生效列表 + 取消 ----
// scope: type=仅该类型；bolt=该螺栓全部类型（换探头等整颗维护，后端 alarmType=null 通配）
const shelve = reactive({ visible: false, alarm: null as AlarmItem | null, hours: 24, reason: '', scope: 'type' as 'type' | 'bolt', saving: false })
function openShelve(a: AlarmItem) {
  shelve.alarm = a; shelve.hours = 24; shelve.reason = ''; shelve.scope = 'type'; shelve.visible = true
}
async function confirmShelve() {
  if (!shelve.alarm) return
  if (shelve.hours < 1 || shelve.hours > 168) return ElMessage.warning('时长需在 1..168 小时')
  if (!shelve.reason || shelve.reason.trim().length < 5) return ElMessage.warning('原因至少 5 字')
  shelve.saving = true
  try {
    await alarmApi.shelve({
      boltId: shelve.alarm.boltId,
      alarmType: shelve.scope === 'type' ? shelve.alarm.type : undefined,
      hours: shelve.hours, reason: shelve.reason,
    })
    ElMessage.success(`已抑制 ${shelve.hours} 小时`)
    shelve.visible = false
    load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '抑制失败')
  } finally {
    shelve.saving = false
  }
}
async function cancelShelving(s: AlarmShelving) {
  try {
    await ElMessageBox.confirm(`取消对【${s.boltCode ?? '全部螺栓'} · ${s.alarmType ? alarmTypeLabel(s.alarmType) : '全部类型'}】的抑制？告警将恢复触发。`, '取消抑制', { type: 'warning' })
  } catch { return }
  try {
    await alarmApi.cancelShelve(s.id)
    ElMessage.success('已取消抑制')
    load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '取消失败')
  }
}

function openBolt(a: AlarmItem) {
  if (a.boltId) router.push({ name: 'boltDetail', params: { boltId: a.boltId } })
}

// ---- 时间：绝对 + 相对 + 持续时长（可追溯优先给绝对值） ----
const now = useNow(30000)
function fmtAbs(iso: string | null) {
  if (!iso) return '—'
  const d = new Date(iso)
  const p = (n: number) => String(n).padStart(2, '0')
  return `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`
}
function ago(iso: string | null) {
  if (!iso) return '—'
  const s = Math.floor((now.value - new Date(iso).getTime()) / 1000)
  if (s < 0) return '刚刚'
  if (s < 60) return `${s}秒前`
  if (s < 3600) return `${Math.floor(s / 60)}分前`
  if (s < 86400) return `${Math.floor(s / 3600)}时前`
  return `${Math.floor(s / 86400)}天前`
}
function durationOf(a: AlarmItem) {
  const end = a.clearedAt ? new Date(a.clearedAt).getTime() : now.value
  const s = Math.max(0, Math.floor((end - new Date(a.triggeredAt).getTime()) / 1000))
  if (s < 60) return `${s} 秒`
  if (s < 3600) return `${Math.floor(s / 60)} 分`
  if (s < 86400) return `${Math.floor(s / 3600)} 时 ${Math.floor((s % 3600) / 60)} 分`
  return `${Math.floor(s / 86400)} 天 ${Math.floor((s % 86400) / 3600)} 时`
}
// 开启中的告警：条件最近一次命中距今（≤2 分钟视为"仍在越限"）。null=后端旧数据，无法判定。
function lastHitOf(a: AlarmItem): { label: string; live: boolean } | null {
  if (!a.isOpen || !a.lastActiveAt) return null
  const s = Math.floor((now.value - new Date(a.lastActiveAt).getTime()) / 1000)
  if (s <= 120) return { label: '仍在越限', live: true }
  return { label: `最后命中 ${ago(a.lastActiveAt)}`, live: false }
}
// 触发值 vs 阈值（工程可追溯：告警行直接给"值/限"）
function valueOf(a: AlarmItem) {
  if (a.preloadRatio == null) return null
  return (Number(a.preloadRatio) * 100).toFixed(1) + '%'
}
function limitOf(a: AlarmItem) {
  if (a.type === 'preload_high' && a.thresholdHigh != null) return '>' + (Number(a.thresholdHigh) * 100).toFixed(0) + '%'
  if ((a.type === 'preload_low' || a.type === 'preload_loss') && a.thresholdLow != null) return '<' + (Number(a.thresholdLow) * 100).toFixed(0) + '%'
  return null
}
</script>

<template>
  <div class="alarms rise">
    <div class="seg page-seg">
      <span class="seg-tab" :class="{ active: pageTab === 'dispose' }" @click="pageTab = 'dispose'">告警处置</span>
      <span class="seg-tab" :class="{ active: pageTab === 'rules' }" @click="pageTab = 'rules'">阈值规则</span>
      <span v-if="auth.isReviewer" class="seg-tab" :class="{ active: pageTab === 'notify' }" @click="pageTab = 'notify'">通知</span>
    </div>
    <AlarmRulesPanel v-if="pageTab === 'rules'" />
    <NotificationPanel v-else-if="pageTab === 'notify'" />
    <template v-else>
    <!-- 摘要：未确认是处置焦点（ISA-18.2），单列突出 -->
    <div class="sum-row">
      <div class="stat panel" :class="{ 'stat--crit': openUnacked > 0 }">
        <div class="stat-hd"><span class="stat-label">未确认</span><span class="led" :class="openUnacked > 0 ? 'led--crit' : 'led--off'" /></div>
        <div class="stat-val metric" :style="openUnacked > 0 ? 'color:var(--crit)' : ''">{{ openUnacked }}</div>
        <div class="stat-foot">需立即知悉处置</div>
      </div>
      <div class="stat panel">
        <div class="stat-hd"><span class="stat-label">未闭合</span><el-icon class="stat-ic"><BellFilled /></el-icon></div>
        <div class="stat-val metric" :style="openTotal > 0 ? 'color:var(--warn)' : ''">{{ openTotal }}</div>
        <div class="stat-foot">严重 {{ openCrit }} · 预警 {{ openWarn }}</div>
      </div>
      <div class="stat panel">
        <div class="stat-hd"><span class="stat-label">生效抑制</span><span class="led" :class="shelvingError || shelvings.length ? 'led--warn' : 'led--off'" /></div>
        <!-- 查不到时显示 — 而不是 0：0 是"确实没有被静默的规则"，这个结论此刻并不成立 -->
        <div class="stat-val metric" :style="shelvingError || shelvings.length ? 'color:var(--warn)' : ''">{{ shelvingError ? '—' : shelvings.length }}</div>
        <div class="stat-foot">{{ shelvingError ? '查询失败，可能有规则正被静默' : '被临时静默的规则' }}</div>
      </div>

      <div class="matrix panel" v-if="matrix.length">
        <span class="panel-title"><i class="hd-mark" />类型 × 级别（未闭合）</span>
        <div class="mx">
          <div v-for="m in matrix" :key="m.type" class="mx-row">
            <span class="mx-type">{{ alarmTypeLabel(m.type) }}</span>
            <span class="mx-c crit" :class="{ z: !m.crit }">{{ m.crit }}</span>
            <span class="mx-c warn" :class="{ z: !m.warn }">{{ m.warn }}</span>
            <span class="mx-c info" :class="{ z: !m.info }">{{ m.info }}</span>
          </div>
        </div>
      </div>
    </div>

    <!-- 工具栏：状态段选 + 筛选 + 搜索 + 批量 -->
    <div class="toolbar">
      <div class="seg">
        <span class="seg-tab" :class="{ active: view === 'open' }" @click="setView('open')">未闭合</span>
        <span class="seg-tab" :class="{ active: view === 'unacked' }" @click="setView('unacked')">未确认</span>
        <span class="seg-tab" :class="{ active: view === 'cleared' }" @click="setView('cleared')">已闭合</span>
        <span class="seg-tab" :class="{ active: view === 'all' }" @click="setView('all')">全部</span>
      </div>
      <el-select v-model="filterSeverity" size="small" style="width: 110px">
        <el-option label="全部级别" value="" /><el-option label="严重" value="crit" /><el-option label="预警" value="warn" /><el-option label="提示" value="info" />
      </el-select>
      <el-select v-model="filterType" size="small" style="width: 140px">
        <el-option label="全部类型" value="" />
        <el-option v-for="t in typeOptions" :key="t" :label="alarmTypeLabel(t)" :value="t" />
      </el-select>
      <el-input v-model="search" size="small" placeholder="搜索螺栓 / 信息" clearable style="width: 180px">
        <template #prefix><el-icon><Search /></el-icon></template>
      </el-input>
      <el-button size="small" :disabled="!openSelected.length" :loading="bulkAcking" @click="bulkAck">批量确认 ({{ openSelected.length }})</el-button>
    </div>

    <!-- 告警列表 -->
    <div class="panel panel--glow">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark hd-mark--crit" />告警列表</span>
        <span class="hd-sub mono">{{ filtered.length }} 条</span>
      </div>
      <div v-if="loadError" class="load-error">
        <el-icon><WarningFilled /></el-icon> 告警列表刷新失败（显示的可能是旧数据）：{{ loadError }}
        <el-button size="small" text type="primary" @click="load">重试</el-button>
      </div>
      <!-- 截断透出：摘要卡是全量聚合，列表被 limit 截断时两处数字会对不上，必须说明而不是让人猜 -->
      <div v-if="listTruncated" class="load-error">
        <el-icon><WarningFilled /></el-icon>
        列表仅显示最近 {{ LIST_LIMIT }} 条，更早的告警未列出（摘要卡计数为全量口径）。可按级别筛选或缩小查看范围。
      </div>
      <div class="tbl" v-loading="loading">
        <div class="tr th">
          <span></span><span>级别</span><span>类型</span><span>螺栓</span><span>触发值 / 限</span><span>信息</span><span>触发时间</span><span>持续</span><span>状态</span><span>操作</span>
        </div>
        <div
          v-for="a in filtered" :key="a.id"
          class="tr" :class="[{ cleared: !a.isOpen }, a.isOpen && !a.isAcked ? `unacked unacked--${SEV[a.severity]?.bucket || 'info'}` : '']"
        >
          <span><el-checkbox v-if="a.isOpen" v-model="sel[a.id]" /></span>
          <span class="sev"><span class="led" :class="SEV[a.severity]?.cls || 'led--off'" />{{ SEV[a.severity]?.label || a.severity }}</span>
          <span>{{ alarmTypeLabel(a.type) }}</span>
          <span class="mono link" @click="openBolt(a)">{{ a.boltCode }}</span>
          <span class="mono val">
            <template v-if="valueOf(a)">{{ valueOf(a) }}<i v-if="limitOf(a)" class="lim">{{ limitOf(a) }}</i></template>
            <template v-else>—</template>
          </span>
          <span class="msg" :title="a.message || ''">{{ a.message || '—' }}</span>
          <span class="mono time"><b>{{ fmtAbs(a.triggeredAt) }}</b><i>{{ ago(a.triggeredAt) }}</i></span>
          <span class="mono time dur">
            <b>{{ durationOf(a) }}</b>
            <i v-if="lastHitOf(a)" :class="{ hot: lastHitOf(a)!.live }">{{ lastHitOf(a)!.label }}</i>
          </span>
          <span>
            <span v-if="!a.isOpen" class="st st-cleared" :title="`闭合原因：${a.clearedReason || '—'}${a.clearedBy ? ' · by ' + a.clearedBy : ''}`">已闭合<i v-if="a.clearedBy"> · {{ a.clearedBy }}</i></span>
            <span v-else-if="a.isAcked" class="st st-acked" :title="`确认备注：${a.ackedNote || '—'}`">已确认<i v-if="a.ackedBy"> · {{ a.ackedBy }}</i></span>
            <span v-else class="st st-open">待确认</span>
          </span>
          <span class="ops" v-if="a.isOpen">
            <el-button v-if="!a.isAcked" size="small" text @click="ack(a)">确认</el-button>
            <el-button size="small" text @click="openShelve(a)">抑制</el-button>
            <el-button size="small" type="primary" text @click="clear(a)">闭合</el-button>
          </span>
          <span v-else class="ops dim">—</span>
        </div>
        <div v-if="!loading && !filtered.length" class="tbl-empty"><span class="led led--ok" /> 无匹配告警</div>
      </div>
    </div>

    <!-- 生效中的抑制：被静默的规则必须可见、可取消。查询失败时面板也要留着并挂错误条，
         整块消失会让值班以为没有任何告警被静默 -->
    <div class="panel" v-if="shelvings.length || shelvingError">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark hd-mark--warn" />生效中的抑制</span>
        <span class="hd-sub">到期自动恢复；取消后立即恢复触发</span>
      </div>
      <div v-if="shelvingError" class="load-error">
        <el-icon><WarningFilled /></el-icon>
        抑制规则查询失败，无法确认当前有哪些告警被静默（可能有规则正在生效）：{{ shelvingError }}
        <el-button size="small" text type="primary" @click="load">重试</el-button>
      </div>
      <div class="tbl">
        <div class="tr th shv">
          <span>对象</span><span>类型</span><span>操作人</span><span>原因</span><span>到期</span><span>操作</span>
        </div>
        <div v-for="s in shelvings" :key="s.id" class="tr shv">
          <span class="mono">{{ s.boltCode ?? '全部螺栓' }}</span>
          <span>{{ s.alarmType ? alarmTypeLabel(s.alarmType) : '全部类型' }}</span>
          <span class="dim">{{ s.shelvedByName ?? '—' }}</span>
          <span class="msg" :title="s.reason">{{ s.reason }}</span>
          <span class="mono time"><b>{{ fmtAbs(s.expiresAt) }}</b><i>设置于 {{ ago(s.shelvedAt) }}</i></span>
          <span><el-button size="small" text type="warning" @click="cancelShelving(s)">取消抑制</el-button></span>
        </div>
      </div>
    </div>

    <el-dialog v-model="shelve.visible" title="抑制告警（维护时段）" width="460px">
      <p class="dlg-sub" v-if="shelve.alarm">抑制【{{ shelve.alarm.boltCode }}】在指定时长内不再触发；生效中的抑制在下方面板可见、可随时取消。</p>
      <el-form label-width="90px" label-position="left">
        <el-form-item label="范围">
          <el-radio-group v-model="shelve.scope">
            <el-radio value="type">仅 {{ shelve.alarm ? alarmTypeLabel(shelve.alarm.type) : '此类型' }}</el-radio>
            <el-radio value="bolt">该螺栓全部类型</el-radio>
          </el-radio-group>
          <div v-if="shelve.scope === 'bolt'" class="scope-hint">整颗维护（如换探头）：同时静默信号丢失与预紧力类告警</div>
        </el-form-item>
        <el-form-item label="时长（小时）"><el-input-number v-model="shelve.hours" :min="1" :max="168" :step="1" controls-position="right" style="width:100%" /></el-form-item>
        <el-form-item label="原因"><el-input v-model="shelve.reason" type="textarea" :rows="3" placeholder="≥5 字，记入档案" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="shelve.visible = false">取消</el-button>
        <el-button type="primary" :loading="shelve.saving" @click="confirmShelve">确认抑制</el-button>
      </template>
    </el-dialog>
    </template>
  </div>
</template>

<style scoped>
.alarms { display: flex; flex-direction: column; gap: 14px; }
.sum-row { display: flex; gap: 14px; align-items: stretch; flex-wrap: wrap; }
.sum-row .stat { flex: 0 0 160px; }
.matrix { padding: 13px 18px; flex: 1; min-width: 280px; display: flex; flex-direction: column; }
.mx { display: flex; flex-direction: column; gap: 5px; margin-top: 10px; }
.mx-row { display: grid; grid-template-columns: 1fr 44px 44px 44px; gap: 6px; align-items: center; font-size: 12.5px; }
.mx-type { color: var(--text-2); }
.mx-c { text-align: center; font-family: var(--font-mono); font-weight: 700; border-radius: 6px; padding: 2px 0; }
.mx-c.crit { color: #f43f5e; background: rgba(244,63,94,0.1); }
.mx-c.warn { color: #f59e0b; background: rgba(245,158,11,0.1); }
.mx-c.info { color: #38bdf8; background: rgba(56,189,248,0.1); }
.mx-c.z { color: var(--text-3); background: transparent; }

.toolbar { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.seg { display: flex; gap: 4px; background: var(--panel); border: 1px solid var(--panel-border); border-radius: 9px; padding: 3px; }
.page-seg { align-self: flex-start; }
.seg-tab { font-size: 12.5px; padding: 5px 14px; border-radius: 6px; cursor: pointer; color: var(--text-3); }
.seg-tab:hover { color: var(--text-1); }
.seg-tab.active { color: var(--accent); background: rgba(34, 211, 238, 0.1); font-weight: 600; }

.tbl { padding: 4px 12px 14px; overflow-x: auto; }
.tr { display: grid; grid-template-columns: 30px 76px 100px 104px 108px 1fr 148px 96px 130px 150px; align-items: center; padding: 9px 8px; border-radius: 8px; font-size: 13px; gap: 4px; min-width: 1080px; }
.tr:not(.th) { box-shadow: inset 0 -1px 0 var(--hairline); }
.tr:not(.th):hover { background: rgba(56, 189, 248, 0.05); }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; }
.tr.cleared { opacity: 0.5; }
/* 未确认行：左缘状态色细条（处置焦点一眼可辨） */
.tr.unacked { box-shadow: inset 3px 0 0 var(--crit), inset 0 -1px 0 var(--hairline); }
.tr.unacked--warn { box-shadow: inset 3px 0 0 var(--warn), inset 0 -1px 0 var(--hairline); }
.tr.unacked--info { box-shadow: inset 3px 0 0 var(--info), inset 0 -1px 0 var(--hairline); }
.tr.shv { grid-template-columns: 120px 130px 100px 1fr 168px 100px; }
.sev { display: flex; align-items: center; gap: 8px; }
.msg { color: var(--text-2); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.link { cursor: pointer; color: var(--accent); }
.link:hover { text-decoration: underline; }
.dim { color: var(--text-3); }
.val { font-weight: 600; }
.val .lim { font-style: normal; font-weight: 400; color: var(--text-3); margin-left: 6px; font-size: 11px; }
.time { display: flex; flex-direction: column; line-height: 1.25; }
.time b { font-weight: 500; font-size: 12px; }
.time i { font-style: normal; font-size: 10.5px; color: var(--text-3); }
.dur i.hot { color: var(--crit); font-weight: 600; }
.scope-hint { font-size: 11.5px; color: var(--text-3); line-height: 1.4; margin-top: 2px; }
.st { font-size: 11px; padding: 2px 9px; border-radius: 10px; white-space: nowrap; }
.st i { font-style: normal; opacity: 0.85; }
.st-open { color: var(--crit); border: 1px solid var(--crit); }
.st-acked { color: var(--warn); border: 1px solid var(--warn); }
.st-cleared { color: var(--text-3); border: 1px solid var(--hairline); }
.ops { display: flex; gap: 2px; }
.tbl-empty { display: flex; align-items: center; gap: 8px; justify-content: center; padding: 30px; color: var(--text-3); }
.dlg-sub { font-size: 13px; color: var(--text-2); margin: 0 0 14px; }

@media (max-width: 1280px) {
  .tr { grid-template-columns: 30px 70px 92px 96px 96px 1fr 130px 84px 116px 140px; font-size: 12.5px; }
}
</style>
