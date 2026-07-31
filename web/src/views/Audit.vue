<script setup lang="ts">
// 页面:审计日志(复核员+)——全库操作流水,按动作/时间检索。
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
  load_calibrate_submit: '提交正式标定', load_calibrate_countersign: '标定会签', load_calibrate_reject: '标定驳回',
  quick_calibrate: '快速标定', torque_calibrate: '扭矩标定', apply_torque_fit: '应用扭矩拟合', torque_point_capture: '采集扭矩点',
  config_change_approve: '参数审批', config_change_reject: '参数驳回', config_change_execute: '参数执行', config_change_verify: '参数验证',
  alarm_ack: '告警确认', alarm_clear: '告警闭合', alarm_shelve: '告警抑制', alarm_shelve_cancel: '取消抑制',
  sync_channel_config: '同步通道配置', move_bolt_channel: '螺栓换通道', bolt_move_channel: '螺栓换通道', channel_bind: '绑定通道', channel_unbind: '解绑通道',
  update_bolt_spec: '更新螺栓规格', update_target_preload: '更新目标预紧力', update_group_config: '更新部位配置', create_bolt: '螺栓建档',
  channel_commission: '调试期直发参数', channel_recalibrate: '重标定基线', channel_rename: '通道重命名',
  report_sign: '报告签发', user_create: '创建/重置用户', user_toggle: '启停用户',
}
const TYPE_LABEL: Record<string, string> = { channel: '通道', alarm: '告警', bolt: '螺栓', calibration: '标定', report: '报告', user: '用户', monitor_group: '监测部位' }

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
    // 导出不分页：用当前筛选条件重建 query（不能字符串替换 offset,边界情况会拼坏 query）
    const p = new URLSearchParams()
    if (action.value) p.set('action', action.value)
    if (range.value) {
      p.set('from', range.value[0].toISOString())
      p.set('to', range.value[1].toISOString())
    }
    const q = p.toString()
    const res = await fetch(`/api/audit-log/export.csv${q ? '?' + q : ''}`, {
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
      <el-button type="primary" :loading="loading" @click="apply"><el-icon><Search /></el-icon> 查询</el-button>
      <el-button @click="exportCsv"><el-icon><Download /></el-icon> 导出 CSV</el-button>
    </div>

    <div class="panel panel--glow">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark" />审计日志</span>
        <span class="hd-sub mono">{{ items.length }} 条</span>
      </div>
      <div class="tbl" v-loading="loading">
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
        <span class="pg-info mono">第 {{ Math.floor(offset / limit) + 1 }} 页 · 本页 {{ items.length }} 条{{ hasMore ? ' · 还有更多' : '' }}</span>
        <el-button size="small" :disabled="!hasMore" @click="nextPage">下一页</el-button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.audit { display: flex; flex-direction: column; gap: 14px; }
.filters { display: flex; gap: 12px; align-items: center; padding: 14px 16px; flex-wrap: wrap; }
.f-action { width: 200px; }

.panel-hd { display: flex; align-items: center; justify-content: space-between; padding: 13px 18px 9px; }

.tbl { padding: 4px 10px 12px; overflow-x: auto; }
.tr { display: grid; grid-template-columns: 180px 120px 140px 130px 1fr; align-items: center; padding: 10px 8px; border-radius: 8px; font-size: 13px; min-width: 700px; }
.tr:not(.th) { box-shadow: inset 0 -1px 0 var(--hairline); }
.tr:not(.th):hover { background: rgba(56, 189, 248, 0.05); }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; }
.dim { color: var(--text-3); }
.act { font-size: 12px; padding: 2px 9px; border-radius: 8px; background: rgba(56, 189, 248, 0.1); color: var(--accent-2); border: 1px solid var(--panel-border); }
.note { color: var(--text-2); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.tbl-empty { text-align: center; padding: 30px; color: var(--text-3); }
.pager { display: flex; align-items: center; justify-content: center; gap: 16px; padding: 12px 14px; border-top: 1px solid var(--hairline); }
.pg-info { font-size: 12px; color: var(--text-3); }
</style>
