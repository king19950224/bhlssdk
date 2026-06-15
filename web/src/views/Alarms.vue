<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { alarmApi, type AlarmItem, type AlarmSummaryRow } from '../api'

const items = ref<AlarmItem[]>([])
const summary = ref<AlarmSummaryRow[]>([])
const includeCleared = ref(false)
const loading = ref(false)
let timer: number | undefined

const SEV: Record<string, { label: string; cls: string; color: string }> = {
  critical: { label: '严重', cls: 'led--crit', color: '#f43f5e' },
  crit: { label: '严重', cls: 'led--crit', color: '#f43f5e' },
  warn: { label: '预警', cls: 'led--warn', color: '#f59e0b' },
  warning: { label: '预警', cls: 'led--warn', color: '#f59e0b' },
  info: { label: '提示', cls: 'led--ok', color: '#38bdf8' },
}
const TYPE_LABEL: Record<string, string> = {
  preload_low: '预紧力偏低',
  preload_loss: '预紧力松弛',
  preload_high: '预紧力超高',
  signal_lost: '信号丢失',
  no_data: '数据中断',
}

const openTotal = computed(() => summary.value.reduce((a, r) => a + r.open, 0))
const openCrit = computed(() => summary.value.filter((r) => r.severity === 'critical' || r.severity === 'crit').reduce((a, r) => a + r.open, 0))
const openWarn = computed(() => summary.value.filter((r) => r.severity === 'warn' || r.severity === 'warning').reduce((a, r) => a + r.open, 0))

async function load() {
  loading.value = true
  try {
    items.value = (await alarmApi.list(`?includeCleared=${includeCleared.value}&limit=200`)).items
    summary.value = (await alarmApi.summary()).items
  } finally {
    loading.value = false
  }
}
onMounted(() => {
  load()
  timer = window.setInterval(load, 10000)
})
onUnmounted(() => timer && clearInterval(timer))

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
      <div class="sum-toggle">
        <el-switch v-model="includeCleared" @change="load" />
        <span>含已闭合</span>
      </div>
    </div>

    <div class="panel panel--glow">
      <div class="tbl">
        <div class="tr th">
          <span>级别</span><span>类型</span><span>螺栓</span><span>信息</span><span>触发</span><span>状态</span><span>操作</span>
        </div>
        <div v-for="a in items" :key="a.id" class="tr" :class="{ cleared: !a.isOpen }">
          <span class="sev"><span class="led" :class="SEV[a.severity]?.cls || 'led--off'" />{{ SEV[a.severity]?.label || a.severity }}</span>
          <span>{{ TYPE_LABEL[a.type] || a.type }}</span>
          <span class="mono">{{ a.boltCode }}</span>
          <span class="msg">{{ a.message || '—' }}</span>
          <span class="mono dim">{{ ago(a.triggeredAt) }}</span>
          <span>
            <span v-if="!a.isOpen" class="st st-cleared">已闭合</span>
            <span v-else-if="a.isAcked" class="st st-acked">已确认</span>
            <span v-else class="st st-open">待处置</span>
          </span>
          <span class="ops" v-if="a.isOpen">
            <el-button v-if="!a.isAcked" size="small" text @click="ack(a)">确认</el-button>
            <el-button size="small" type="primary" text @click="clear(a)">闭合</el-button>
          </span>
          <span v-else class="ops dim">—</span>
        </div>
        <div v-if="!loading && !items.length" class="tbl-empty"><span class="led led--ok" /> 暂无告警</div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.alarms { display: flex; flex-direction: column; gap: 16px; }
.sum-row { display: flex; gap: 14px; align-items: center; }
.sum { padding: 14px 22px; display: flex; flex-direction: column; gap: 6px; min-width: 120px; }
.sum b { font-size: 28px; font-weight: 700; }
.sum.alert { border-color: var(--crit); }
.sum-toggle { display: flex; align-items: center; gap: 8px; margin-left: auto; color: var(--text-3); font-size: 13px; }

.tbl { padding: 6px 18px 16px; }
.tr { display: grid; grid-template-columns: 90px 110px 110px 1fr 80px 90px 130px; align-items: center; padding: 11px 6px; border-bottom: 1px solid var(--hairline); font-size: 13px; }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; }
.tr.cleared { opacity: 0.5; }
.sev { display: flex; align-items: center; gap: 8px; }
.msg { color: var(--text-2); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.dim { color: var(--text-3); }
.st { font-size: 11px; padding: 2px 9px; border-radius: 10px; }
.st-open { color: var(--crit); border: 1px solid var(--crit); }
.st-acked { color: var(--warn); border: 1px solid var(--warn); }
.st-cleared { color: var(--text-3); border: 1px solid var(--hairline); }
.ops { display: flex; gap: 2px; }
.tbl-empty { display: flex; align-items: center; gap: 8px; justify-content: center; padding: 30px; color: var(--text-3); }
</style>
