<script setup lang="ts">
// 审计日志（复核员以上可看）：全库操作流水，按动作和时间翻。出了事靠这个追是谁动的。
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { auditApi, type AuditEntry } from '../api'
import { downloadFile } from '../utils/download'

const items = ref<AuditEntry[]>([])
const actions = ref<{ action: string; label: string; count: number }[]>([])
const action = ref<string>('')
const range = ref<[Date, Date] | null>(null)
const offset = ref(0)
const limit = 100
const hasMore = ref(false)
const loading = ref(false)

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
// 查询失败别显示成"无审计记录"。这一页是出事后追责的入口，"查不到"和"没发生过"是两回事。
const loadError = ref<string | null>(null)
async function load() {
  loading.value = true
  try {
    const d = await auditApi.query(qs())
    items.value = d.items
    hasMore.value = d.hasMore
    loadError.value = null
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : '审计日志查询失败'
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
    await downloadFile(`/api/audit-log/export.csv${q ? '?' + q : ''}`, `审计日志-${Date.now()}.csv`)
    ElMessage.success('已导出 CSV')
  } catch {
    ElMessage.error('导出失败')
  }
}
function fmt(iso: string) {
  return new Date(iso).toLocaleString('zh-CN', { hour12: false })
}
onMounted(async () => {
  // actions() 只是筛选下拉的候选项，它失败不该拦住主查询。
  // 不接这个 catch，load() 就不会执行，页面 loading=false、loadError=null、items 为空，
  // 显示出来就是"无审计记录"——正是开头那条注释要避免的。
  try {
    actions.value = (await auditApi.actions()).items
  } catch {
    actions.value = []
  }
  load()
})
</script>

<template>
  <div class="audit rise">
    <div class="filters panel">
      <el-select v-model="action" placeholder="全部动作" clearable size="default" class="f-action">
        <el-option v-for="a in actions" :key="a.action" :label="`${a.label}（${a.count}）`" :value="a.action" />
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
      <div v-if="loadError" class="load-error">
        <el-icon><WarningFilled /></el-icon> 审计日志查询失败（下方可能是旧数据，不代表这段时间无人操作）：{{ loadError }}
        <el-button size="small" text type="primary" @click="load">重试</el-button>
      </div>
      <div class="tbl" v-loading="loading">
        <div class="tr th"><span>时间</span><span>操作人</span><span>动作</span><span>对象</span><span>说明</span></div>
        <div v-for="e in items" :key="e.id" class="tr">
          <span class="mono dim">{{ fmt(e.at) }}</span>
          <!-- user_id 可为空（system_degraded 等由采集链路写入的系统事件）：显示"系统"而不是字面量"用户null" -->
          <span>{{ e.username || (e.userId != null ? '用户' + e.userId : '系统') }}</span>
          <span><span class="act">{{ e.actionLabel }}</span></span>
          <span class="mono dim">{{ e.targetTypeLabel ? e.targetTypeLabel + '#' + e.targetId : '—' }}</span>
          <span class="note">{{ e.note || '—' }}</span>
        </div>
        <div v-if="!loading && !loadError && !items.length" class="tbl-empty">无审计记录</div>
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
