<script setup lang="ts">
import { onMounted, onUnmounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { configChangeApi, type ConfigChange } from '../api'
import { useAuth } from '../stores/auth'

const auth = useAuth()
const items = ref<ConfigChange[]>([])
const statusFilter = ref<'pending' | 'all'>('pending')
const loading = ref(false)
let timer: number | undefined

const detail = reactive({ visible: false, item: null as ConfigChange | null })
function openDetail(c: ConfigChange) {
  detail.item = c
  detail.visible = true
}
function paramPairs(c: ConfigChange | null): [string, unknown][] {
  if (!c?.requested) return []
  try {
    const o = JSON.parse(c.requested) as Record<string, unknown>
    return Object.entries(o).filter(([, v]) => v != null)
  } catch {
    return []
  }
}

const ST: Record<string, { label: string; color: string }> = {
  requested: { label: '待审批', color: '#f59e0b' },
  approved: { label: '待执行', color: '#38bdf8' },
  executed: { label: '待验证', color: '#818cf8' },
  verified: { label: '已验证', color: '#10b981' },
  rejected: { label: '已驳回', color: '#61748f' },
  failed: { label: '执行失败', color: '#f43f5e' },
}

async function load() {
  loading.value = true
  try {
    items.value = (await configChangeApi.list(statusFilter.value)).items
  } finally {
    loading.value = false
  }
}
onMounted(() => {
  load()
  timer = window.setInterval(load, 8000)
})
onUnmounted(() => timer && clearInterval(timer))

async function act(fn: () => Promise<unknown>, ok: string) {
  try {
    await fn()
    ElMessage.success(ok)
    load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '操作失败')
  }
}
async function approve(c: ConfigChange) {
  await ElMessageBox.confirm(`审批通过变更 #${c.id}?(须与提交人不同)`, '审批', { type: 'warning', confirmButtonText: '审批通过', cancelButtonText: '取消' })
  act(() => configChangeApi.approve(c.id), '已审批')
}
async function reject(c: ConfigChange) {
  const { value } = await ElMessageBox.prompt(`驳回变更 #${c.id},请填写原因:`, '驳回', {
    inputType: 'textarea', confirmButtonText: '驳回', cancelButtonText: '取消',
    inputValidator: (v) => (v && v.trim() ? true : '请填写驳回原因'),
  })
  act(() => configChangeApi.reject(c.id, value), '已驳回')
}
async function execute(c: ConfigChange) {
  await ElMessageBox.confirm(`执行下发变更 #${c.id} 到设备?`, '执行', { type: 'warning', confirmButtonText: '执行下发', cancelButtonText: '取消' })
  act(() => configChangeApi.execute(c.id), '已下发')
}
async function verify(c: ConfigChange) {
  act(() => configChangeApi.verify(c.id), '已发起读回验证')
}
function params(c: ConfigChange) {
  if (!c.requested) return '—'
  try {
    const o = JSON.parse(c.requested)
    return Object.entries(o).filter(([, v]) => v != null).map(([k, v]) => `${k}=${v}`).join(', ') || '—'
  } catch {
    return c.requested
  }
}
function fmt(iso: string | null) {
  return iso ? new Date(iso).toLocaleString('zh-CN', { hour12: false }) : '—'
}
</script>

<template>
  <div class="cc rise">
    <div class="cc-head">
      <div class="seg">
        <span class="seg-tab" :class="{ active: statusFilter === 'pending' }" @click="statusFilter = 'pending'; load()">待办队列</span>
        <span class="seg-tab" :class="{ active: statusFilter === 'all' }" @click="statusFilter = 'all'; load()">全部</span>
      </div>
      <div class="hint" v-if="!auth.isReviewer"><el-icon><InfoFilled /></el-icon> 审批/驳回需复核员权限</div>
    </div>

    <div class="panel panel--glow">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark hd-mark--warn" />变更队列</span>
        <span class="hd-sub mono">{{ items.length }} 条{{ statusFilter === 'pending' ? '待办' : '' }}</span>
      </div>
      <div class="tbl">
        <div class="tr th"><span>#</span><span>设备/通道</span><span>变更参数</span><span>状态</span><span>提交</span><span>审批</span><span>操作</span></div>
        <div v-for="c in items" :key="c.id" class="tr">
          <span class="mono">{{ c.id }}</span>
          <span class="mono">dev{{ c.deviceId }} · CH{{ c.channelNo }}</span>
          <span class="params mono">{{ params(c) }}</span>
          <span><span class="st" :style="`color:${ST[c.status]?.color};border-color:${ST[c.status]?.color}`">{{ ST[c.status]?.label || c.status }}</span></span>
          <span class="who">用户{{ c.requestedBy ?? '—' }}<br /><span class="dim mono">{{ fmt(c.requestedAt) }}</span></span>
          <span class="who">{{ c.approvedBy ? '用户' + c.approvedBy : '—' }}</span>
          <span class="ops">
            <el-button size="small" text @click="openDetail(c)">详情</el-button>
            <template v-if="c.status === 'requested'">
              <el-button size="small" type="primary" text :disabled="!auth.isReviewer" @click="approve(c)">审批</el-button>
              <el-button size="small" type="danger" text :disabled="!auth.isReviewer" @click="reject(c)">驳回</el-button>
            </template>
            <el-button v-else-if="c.status === 'approved'" size="small" type="primary" text @click="execute(c)">执行</el-button>
            <el-button v-else-if="c.status === 'executed'" size="small" text @click="verify(c)">验证</el-button>
          </span>
        </div>
        <div v-if="!loading && !items.length" class="tbl-empty"><span class="led led--ok" /> {{ statusFilter === 'pending' ? '无待办变更' : '无变更记录' }}</div>
      </div>
    </div>

    <div class="flow-note panel">
      <span class="panel-title"><i class="hd-mark" />变更流程</span>
      <div class="flow">
        <span class="fl">提交</span><el-icon><ArrowRight /></el-icon>
        <span class="fl">审批<i>异人复核</i></span><el-icon><ArrowRight /></el-icon>
        <span class="fl">执行<i>下发设备</i></span><el-icon><ArrowRight /></el-icon>
        <span class="fl">验证<i>读回比对</i></span>
      </div>
    </div>

    <el-dialog v-model="detail.visible" :title="detail.item ? `变更请求 #${detail.item.id}` : '变更详情'" width="540px">
      <div v-if="detail.item" class="dd">
        <div class="dd-row"><span class="dd-k">设备 / 通道</span><b class="mono">dev{{ detail.item.deviceId }} · CH{{ detail.item.channelNo }}</b></div>
        <div class="dd-row"><span class="dd-k">状态</span><span class="st" :style="`color:${ST[detail.item.status]?.color};border-color:${ST[detail.item.status]?.color}`">{{ ST[detail.item.status]?.label || detail.item.status }}</span></div>
        <div class="dd-sec">变更参数</div>
        <div v-if="paramPairs(detail.item).length" class="dd-params">
          <div v-for="[k, v] in paramPairs(detail.item)" :key="k" class="dd-param"><span class="mono dd-pk">{{ k }}</span><b class="mono">{{ v }}</b></div>
        </div>
        <div v-else class="dd-empty">无参数</div>
        <div class="dd-sec">流程时间线</div>
        <div class="dd-row"><span class="dd-k">提交</span><span>用户{{ detail.item.requestedBy ?? '—' }} · <span class="mono dim">{{ fmt(detail.item.requestedAt) }}</span></span></div>
        <div class="dd-row"><span class="dd-k">审批</span><span>{{ detail.item.approvedBy ? '用户' + detail.item.approvedBy : '—' }} · <span class="mono dim">{{ fmt(detail.item.approvedAt) }}</span></span></div>
        <div class="dd-row"><span class="dd-k">执行</span><span>{{ detail.item.executedBy ? '用户' + detail.item.executedBy : '—' }} · <span class="mono dim">{{ fmt(detail.item.executedAt) }}</span></span></div>
        <div class="dd-row"><span class="dd-k">验证</span><span class="mono dim">{{ fmt(detail.item.verifiedAt) }}</span></div>
        <div v-if="detail.item.failureReason" class="dd-row dd-fail"><span class="dd-k">失败原因</span><span>{{ detail.item.failureReason }}</span></div>
        <div v-if="detail.item.note" class="dd-row"><span class="dd-k">备注</span><span>{{ detail.item.note }}</span></div>
      </div>
      <template #footer><el-button @click="detail.visible = false">关闭</el-button></template>
    </el-dialog>
  </div>
</template>

<style scoped>
.cc { display: flex; flex-direction: column; gap: 16px; }
.cc-head { display: flex; align-items: center; gap: 16px; }
.seg { display: flex; gap: 4px; background: var(--panel); border: 1px solid var(--panel-border); border-radius: 10px; padding: 4px; }
.seg-tab { font-size: 13px; padding: 6px 16px; border-radius: 7px; cursor: pointer; color: var(--text-3); }
.seg-tab.active { color: var(--accent); background: rgba(34, 211, 238, 0.1); font-weight: 600; }
.hint { display: flex; align-items: center; gap: 6px; color: var(--text-3); font-size: 12px; margin-left: auto; }

.panel-hd { display: flex; align-items: center; justify-content: space-between; padding: 13px 18px 9px; }
.tbl { padding: 4px 12px 14px; }
.tr { display: grid; grid-template-columns: 44px 128px minmax(0, 1.4fr) 80px 128px 80px 120px; align-items: center; padding: 10px 8px; border-radius: 8px; font-size: 13px; }
.tr:not(.th) { box-shadow: inset 0 -1px 0 var(--hairline); }
.tr:not(.th):hover { background: rgba(56, 189, 248, 0.05); }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; text-transform: uppercase; }
.params { color: var(--text-2); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.st { font-size: 11px; padding: 2px 9px; border: 1px solid; border-radius: 10px; }
.who { font-size: 12px; }
.dim { color: var(--text-3); font-size: 11px; }
.ops { display: flex; gap: 2px; }
.tbl-empty { display: flex; align-items: center; gap: 8px; justify-content: center; padding: 30px; color: var(--text-3); }

.dd { display: flex; flex-direction: column; gap: 10px; }
.dd-row { display: flex; gap: 14px; align-items: baseline; font-size: 13px; }
.dd-k { width: 80px; color: var(--text-3); flex-shrink: 0; }
.dd-sec { font-size: 12px; color: var(--accent-2); letter-spacing: 0.06em; margin: 8px 0 2px; padding-bottom: 5px; border-bottom: 1px solid var(--hairline); }
.dd-params { display: flex; flex-direction: column; gap: 6px; }
.dd-param { display: flex; justify-content: space-between; padding: 4px 0; }
.dd-pk { color: var(--text-3); }
.dd-empty { color: var(--text-3); font-size: 13px; }
.dd-fail { color: var(--crit); }

.flow-note { padding: 16px 22px; }
.flow { display: flex; align-items: center; gap: 14px; margin-top: 12px; color: var(--text-3); }
.fl { display: flex; flex-direction: column; font-size: 14px; color: var(--text-1); }
.fl i { font-size: 11px; color: var(--text-3); font-style: normal; }
</style>
