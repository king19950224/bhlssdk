<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { auditApi, type AuditEntry } from '../api'
import { getAuthToken } from '../api/client'

const items = ref<AuditEntry[]>([])
const actions = ref<{ action: string; count: number }[]>([])
const action = ref<string>('')
const range = ref<[Date, Date] | null>(null)
const offset = ref(0)
const limit = 100
const hasMore = ref(false)
const loading = ref(false)

const ACTION_LABEL: Record<string, string> = {
  load_calibrate_submit: '提交正式标定', load_calibrate_countersign: '标定会签',
  quick_calibrate: '快速标定', torque_calibrate: '扭矩标定', apply_torque_fit: '应用扭矩拟合',
  config_change_approve: '参数审批', config_change_reject: '参数驳回', config_change_execute: '参数执行', config_change_verify: '参数验证',
  alarm_ack: '告警确认', alarm_clear: '告警闭合',
  sync_channel_config: '同步通道配置', move_bolt_channel: '螺栓换通道', update_bolt_spec: '更新螺栓规格', update_target_preload: '更新目标预紧力',
}
const TYPE_LABEL: Record<string, string> = { channel: '通道', alarm: '告警', bolt: '螺栓', calibration: '标定' }

function qs() {
  const p = new URLSearchParams()
  if (action.value) p.set('action', action.value)
  if (range.value) {
    p.set('from', range.value[0].toISOString())
    p.set('to', range.value[1].toISOString())
  }
  p.set('limit', String(limit))
  p.set('offset', String(offset.value))
  return '?' + p.toString()
}
async function load() {
  loading.value = true
  try {
    const d = await auditApi.query(qs())
    items.value = d.items
    hasMore.value = d.hasMore
  } finally {
    loading.value = false
  }
}
function apply() {
  offset.value = 0
  load()
}
function nextPage() {
  offset.value += limit
  load()
}
function prevPage() {
  offset.value = Math.max(0, offset.value - limit)
  load()
}
async function exportCsv() {
  try {
    const res = await fetch(`/api/audit-log/export.csv${qs().replace(/&?offset=\d+/, '')}`, {
      headers: { Authorization: `Bearer ${getAuthToken()}` },
    })
    if (!res.ok) throw new Error(String(res.status))
    const blob = await res.blob()
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `audit-log-${Date.now()}.csv`
    a.click()
    URL.revokeObjectURL(url)
    ElMessage.success('已导出 CSV')
  } catch {
    ElMessage.error('导出失败')
  }
}
function fmt(iso: string) {
  return new Date(iso).toLocaleString('zh-CN', { hour12: false })
}
onMounted(async () => {
  actions.value = (await auditApi.actions()).items
  load()
})
</script>

<template>
  <div class="audit rise">
    <div class="filters panel">
      <el-select v-model="action" placeholder="全部动作" clearable size="default" class="f-action">
        <el-option v-for="a in actions" :key="a.action" :label="`${ACTION_LABEL[a.action] || a.action} (${a.count})`" :value="a.action" />
      </el-select>
      <el-date-picker v-model="range" type="datetimerange" range-separator="至" start-placeholder="开始" end-placeholder="结束" size="default" />
      <el-button type="primary" @click="apply"><el-icon><Search /></el-icon> 查询</el-button>
      <el-button @click="exportCsv"><el-icon><Download /></el-icon> 导出 CSV</el-button>
    </div>

    <div class="panel panel--glow">
      <div class="tbl">
        <div class="tr th"><span>时间</span><span>操作人</span><span>动作</span><span>对象</span><span>说明</span></div>
        <div v-for="e in items" :key="e.id" class="tr">
          <span class="mono dim">{{ fmt(e.at) }}</span>
          <span>{{ e.username || ('用户' + e.userId) }}</span>
          <span><span class="act">{{ ACTION_LABEL[e.action] || e.action }}</span></span>
          <span class="mono dim">{{ e.targetType ? (TYPE_LABEL[e.targetType] || e.targetType) + '#' + e.targetId : '—' }}</span>
          <span class="note">{{ e.note || '—' }}</span>
        </div>
        <div v-if="!loading && !items.length" class="tbl-empty">无审计记录</div>
      </div>
      <div class="pager">
        <el-button size="small" :disabled="offset === 0" @click="prevPage">上一页</el-button>
        <span class="pg-info mono">第 {{ Math.floor(offset / limit) + 1 }} 页</span>
        <el-button size="small" :disabled="!hasMore" @click="nextPage">下一页</el-button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.audit { display: flex; flex-direction: column; gap: 16px; }
.filters { display: flex; gap: 12px; align-items: center; padding: 16px 20px; flex-wrap: wrap; }
.f-action { width: 200px; }
.tbl { padding: 6px 18px 8px; }
.tr { display: grid; grid-template-columns: 180px 120px 140px 130px 1fr; align-items: center; padding: 10px 6px; border-bottom: 1px solid var(--hairline); font-size: 13px; }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; }
.dim { color: var(--text-3); }
.act { font-size: 12px; padding: 2px 9px; border-radius: 9px; background: rgba(56, 189, 248, 0.1); color: var(--accent-2); border: 1px solid var(--panel-border); }
.note { color: var(--text-2); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.tbl-empty { text-align: center; padding: 30px; color: var(--text-3); }
.pager { display: flex; align-items: center; justify-content: center; gap: 16px; padding: 14px; border-top: 1px solid var(--hairline); }
.pg-info { font-size: 12px; color: var(--text-3); }
</style>
