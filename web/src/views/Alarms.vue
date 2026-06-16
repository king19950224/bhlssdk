<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { alarmApi, type AlarmItem, type AlarmSummaryRow } from '../api'
import { onRealtime } from '../api/realtime'

const router = useRouter()
const items = ref<AlarmItem[]>([])
const summary = ref<AlarmSummaryRow[]>([])
const includeCleared = ref(false)
const loading = ref(false)
let timer: number | undefined
let unsub: (() => void) | undefined

const filterSeverity = ref('')
const filterType = ref('')
const sel = ref<Record<number, boolean>>({})

const SEV: Record<string, { label: string; cls: string; color: string; bucket: 'crit' | 'warn' | 'info' }> = {
  critical: { label: '严重', cls: 'led--crit', color: '#f43f5e', bucket: 'crit' },
  crit: { label: '严重', cls: 'led--crit', color: '#f43f5e', bucket: 'crit' },
  warn: { label: '预警', cls: 'led--warn', color: '#f59e0b', bucket: 'warn' },
  warning: { label: '预警', cls: 'led--warn', color: '#f59e0b', bucket: 'warn' },
  info: { label: '提示', cls: 'led--ok', color: '#38bdf8', bucket: 'info' },
}
const TYPE_LABEL: Record<string, string> = {
  preload_low: '预紧力偏低', preload_loss: '预紧力松弛', preload_high: '预紧力超高',
  signal_lost: '信号丢失', no_data: '数据中断',
}

const openTotal = computed(() => summary.value.reduce((a, r) => a + r.open, 0))
const openCrit = computed(() => summary.value.filter((r) => SEV[r.severity]?.bucket === 'crit').reduce((a, r) => a + r.open, 0))
const openWarn = computed(() => summary.value.filter((r) => SEV[r.severity]?.bucket === 'warn').reduce((a, r) => a + r.open, 0))

const typeOptions = computed(() => [...new Set(items.value.map((a) => a.type))])
const filtered = computed(() => items.value.filter((a) =>
  (!filterSeverity.value || SEV[a.severity]?.bucket === filterSeverity.value) &&
  (!filterType.value || a.type === filterType.value)))

// severity × type 矩阵（来自 summary 的 open 计数）
const matrix = computed(() => {
  const types = [...new Set(summary.value.map((r) => r.type))]
  return types.map((t) => {
    const cell = (b: string) => summary.value.filter((r) => r.type === t && SEV[r.severity]?.bucket === b).reduce((a, r) => a + r.open, 0)
    return { type: t, crit: cell('crit'), warn: cell('warn'), info: cell('info') }
  }).filter((r) => r.crit + r.warn + r.info > 0)
})

const openSelected = computed(() => filtered.value.filter((a) => a.isOpen && sel.value[a.id]))

async function load() {
  loading.value = true
  try {
    items.value = (await alarmApi.list(`?includeCleared=${includeCleared.value}&limit=200`)).items
    summary.value = (await alarmApi.summary()).items
    // 清掉已不在列表里的选择
    const ids = new Set(items.value.map((a) => a.id))
    for (const k of Object.keys(sel.value)) if (!ids.has(Number(k))) delete sel.value[Number(k)]
  } finally {
    loading.value = false
  }
}
onMounted(() => {
  load()
  timer = window.setInterval(load, 30000)
  unsub = onRealtime('OnAlarm', () => load())
})
onUnmounted(() => { if (timer) clearInterval(timer); unsub?.() })

async function ack(a: AlarmItem) {
  try {
    const { value } = await ElMessageBox.prompt('确认该告警(标记已读,不闭合)。可填备注:', '确认告警', {
      confirmButtonText: '确认', cancelButtonText: '取消', inputType: 'textarea', inputValue: '',
    })
    await alarmApi.ack(a.id, value || undefined)
    ElMessage.success('已确认')
    load()
  } catch { /* 取消 */ }
}
async function clear(a: AlarmItem) {
  try {
    const { value } = await ElMessageBox.prompt('闭合该告警。请填写闭合原因(将记入审计):', '闭合告警', {
      confirmButtonText: '闭合', cancelButtonText: '取消', inputType: 'textarea',
      inputValidator: (v) => (v && v.trim() ? true : '请填写闭合原因'),
    })
    await alarmApi.clear(a.id, value)
    ElMessage.success('已闭合')
    load()
  } catch { /* 取消 */ }
}
async function bulkAck() {
  const targets = openSelected.value.filter((a) => !a.isAcked)
  if (!targets.length) return ElMessage.warning('未选择待确认告警')
  try {
    await ElMessageBox.confirm(`批量确认 ${targets.length} 条告警？`, '批量确认', { type: 'warning' })
  } catch { return }
  for (const a of targets) {
    try { await alarmApi.ack(a.id, '批量确认') } catch { /* 单条失败不阻断 */ }
  }
  ElMessage.success(`已确认 ${targets.length} 条`)
  sel.value = {}
  load()
}

// shelve 对话框（可选时长 1..168h + 原因）
const shelve = reactive({ visible: false, alarm: null as AlarmItem | null, hours: 24, reason: '', saving: false })
function openShelve(a: AlarmItem) {
  shelve.alarm = a; shelve.hours = 24; shelve.reason = ''; shelve.visible = true
}
async function confirmShelve() {
  if (!shelve.alarm) return
  if (shelve.hours < 1 || shelve.hours > 168) return ElMessage.warning('时长需在 1..168 小时')
  if (!shelve.reason || shelve.reason.trim().length < 5) return ElMessage.warning('原因至少 5 字')
  shelve.saving = true
  try {
    await alarmApi.shelve({ boltId: shelve.alarm.boltId, alarmType: shelve.alarm.type, hours: shelve.hours, reason: shelve.reason })
    ElMessage.success(`已抑制 ${shelve.hours} 小时`)
    shelve.visible = false
    load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '抑制失败')
  } finally {
    shelve.saving = false
  }
}

function openBolt(a: AlarmItem) {
  if (a.boltId) router.push({ name: 'boltDetail', params: { boltId: a.boltId } })
}
function ago(iso: string | null) {
  if (!iso) return '—'
  const s = Math.floor((Date.now() - new Date(iso).getTime()) / 1000)
  if (s < 60) return `${s}秒前`
  if (s < 3600) return `${Math.floor(s / 60)}分前`
  if (s < 86400) return `${Math.floor(s / 3600)}时前`
  return `${Math.floor(s / 86400)}天前`
}
</script>

<template>
  <div class="alarms rise">
    <div class="sum-row">
      <div class="sum panel" :class="{ alert: openTotal > 0 }">
        <span class="panel-title">未闭合</span>
        <b class="metric" :style="openTotal > 0 ? 'color:var(--crit)' : ''">{{ openTotal }}</b>
      </div>
      <div class="sum panel"><span class="panel-title">严重</span><b class="metric" style="color:var(--crit)">{{ openCrit }}</b></div>
      <div class="sum panel"><span class="panel-title">预警</span><b class="metric" style="color:var(--warn)">{{ openWarn }}</b></div>

      <!-- severity × type 矩阵 -->
      <div class="matrix panel" v-if="matrix.length">
        <span class="panel-title">类型 × 级别（未闭合）</span>
        <div class="mx">
          <div v-for="m in matrix" :key="m.type" class="mx-row">
            <span class="mx-type">{{ TYPE_LABEL[m.type] || m.type }}</span>
            <span class="mx-c crit" :class="{ z: !m.crit }">{{ m.crit }}</span>
            <span class="mx-c warn" :class="{ z: !m.warn }">{{ m.warn }}</span>
            <span class="mx-c info" :class="{ z: !m.info }">{{ m.info }}</span>
          </div>
        </div>
      </div>
    </div>

    <div class="toolbar">
      <el-select v-model="filterSeverity" size="small" style="width: 120px">
        <el-option label="全部级别" value="" /><el-option label="严重" value="crit" /><el-option label="预警" value="warn" /><el-option label="提示" value="info" />
      </el-select>
      <el-select v-model="filterType" size="small" style="width: 150px">
        <el-option label="全部类型" value="" />
        <el-option v-for="t in typeOptions" :key="t" :label="TYPE_LABEL[t] || t" :value="t" />
      </el-select>
      <el-button size="small" :disabled="!openSelected.length" @click="bulkAck">批量确认 ({{ openSelected.length }})</el-button>
      <div class="tb-right">
        <el-switch v-model="includeCleared" @change="load" /><span>含已闭合</span>
      </div>
    </div>

    <div class="panel panel--glow">
      <div class="tbl">
        <div class="tr th">
          <span></span><span>级别</span><span>类型</span><span>螺栓</span><span>信息</span><span>触发</span><span>状态</span><span>操作</span>
        </div>
        <div v-for="a in filtered" :key="a.id" class="tr" :class="{ cleared: !a.isOpen }">
          <span><el-checkbox v-if="a.isOpen" v-model="sel[a.id]" /></span>
          <span class="sev"><span class="led" :class="SEV[a.severity]?.cls || 'led--off'" />{{ SEV[a.severity]?.label || a.severity }}</span>
          <span>{{ TYPE_LABEL[a.type] || a.type }}</span>
          <span class="mono blink" @click="openBolt(a)">{{ a.boltCode }}</span>
          <span class="msg">{{ a.message || '—' }}</span>
          <span class="mono dim">{{ ago(a.triggeredAt) }}</span>
          <span>
            <span v-if="!a.isOpen" class="st st-cleared">已闭合</span>
            <span v-else-if="a.isAcked" class="st st-acked">已确认</span>
            <span v-else class="st st-open">待处置</span>
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

    <el-dialog v-model="shelve.visible" title="抑制告警（维护时段）" width="460px">
      <p class="dlg-sub" v-if="shelve.alarm">抑制【{{ shelve.alarm.boltCode }} · {{ TYPE_LABEL[shelve.alarm.type] || shelve.alarm.type }}】在指定时长内不再触发。</p>
      <el-form label-width="90px" label-position="left">
        <el-form-item label="时长(小时)"><el-input-number v-model="shelve.hours" :min="1" :max="168" :step="1" controls-position="right" style="width:100%" /></el-form-item>
        <el-form-item label="原因"><el-input v-model="shelve.reason" type="textarea" :rows="3" placeholder="≥5 字，记入审计" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="shelve.visible = false">取消</el-button>
        <el-button type="primary" :loading="shelve.saving" @click="confirmShelve">确认抑制</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.alarms { display: flex; flex-direction: column; gap: 16px; }
.sum-row { display: flex; gap: 14px; align-items: stretch; flex-wrap: wrap; }
.sum { padding: 14px 22px; display: flex; flex-direction: column; gap: 6px; min-width: 120px; }
.sum b { font-size: 28px; font-weight: 700; }
.sum.alert { border-color: var(--crit); }
.matrix { padding: 12px 18px; flex: 1; min-width: 280px; }
.mx { display: flex; flex-direction: column; gap: 4px; margin-top: 8px; }
.mx-row { display: grid; grid-template-columns: 1fr 44px 44px 44px; gap: 6px; align-items: center; font-size: 12.5px; }
.mx-type { color: var(--text-2); }
.mx-c { text-align: center; font-family: var(--font-mono); font-weight: 700; border-radius: 6px; padding: 2px 0; }
.mx-c.crit { color: #f43f5e; background: rgba(244,63,94,0.1); }
.mx-c.warn { color: #f59e0b; background: rgba(245,158,11,0.1); }
.mx-c.info { color: #38bdf8; background: rgba(56,189,248,0.1); }
.mx-c.z { color: var(--text-3); background: transparent; }

.toolbar { display: flex; align-items: center; gap: 10px; }
.tb-right { display: flex; align-items: center; gap: 8px; margin-left: auto; color: var(--text-3); font-size: 13px; }

.tbl { padding: 6px 18px 16px; }
.tr { display: grid; grid-template-columns: 34px 90px 110px 110px 1fr 80px 90px 150px; align-items: center; padding: 11px 6px; border-bottom: 1px solid var(--hairline); font-size: 13px; }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; }
.tr.cleared { opacity: 0.5; }
.sev { display: flex; align-items: center; gap: 8px; }
.msg { color: var(--text-2); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.blink { cursor: pointer; color: var(--accent); }
.blink:hover { text-decoration: underline; }
.dim { color: var(--text-3); }
.st { font-size: 11px; padding: 2px 9px; border-radius: 10px; }
.st-open { color: var(--crit); border: 1px solid var(--crit); }
.st-acked { color: var(--warn); border: 1px solid var(--warn); }
.st-cleared { color: var(--text-3); border: 1px solid var(--hairline); }
.ops { display: flex; gap: 2px; }
.tbl-empty { display: flex; align-items: center; gap: 8px; justify-content: center; padding: 30px; color: var(--text-3); }
.dlg-sub { font-size: 13px; color: var(--text-2); margin: 0 0 14px; }
</style>
