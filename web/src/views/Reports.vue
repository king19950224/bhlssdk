<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { reportApi, channelApi, type ReportItem, type ChannelRow } from '../api'
import { getAuthToken } from '../api/client'
import { useAuth } from '../stores/auth'

const router = useRouter()
const auth = useAuth()

const items = ref<ReportItem[]>([])
const loading = ref(false)
const typeFilter = ref('')

// 生成扭矩阶梯报告（选择通道 → 后端按该通道已采集的扭矩阶梯点生成 HTML）
const genVisible = ref(false)
const channels = ref<ChannelRow[]>([])
const genChannelId = ref<number | null>(null)
const generating = ref(false)
async function openGen() {
  genChannelId.value = null
  genVisible.value = true
  try { channels.value = (await channelApi.list()).items.filter((c) => c.boltId != null) } catch { channels.value = [] }
}
async function doGenerate() {
  if (genChannelId.value == null) { ElMessage.warning('请选择通道'); return }
  generating.value = true
  try {
    await reportApi.generateTorqueStep(genChannelId.value)
    ElMessage.success('扭矩阶梯报告已生成')
    genVisible.value = false
    await load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '生成失败（该通道需先在标定中心采集扭矩阶梯点）')
  } finally {
    generating.value = false
  }
}

const TYPE_LABEL: Record<string, string> = {
  torque_step: '扭矩阶梯标定报告',
  flange_acceptance: '法兰验收报告',
  calibration: '标定报告',
  inspection: '巡检报告',
}
const TYPE_OPTIONS = [
  { value: '', label: '全部类型' },
  { value: 'flange_acceptance', label: '法兰验收报告' },
  { value: 'torque_step', label: '扭矩阶梯标定报告' },
]
// 与后端 reports.status 一致：generated（已生成，待签）/ signed（已签发，锁定）
const STATUS: Record<string, { label: string; color: string }> = {
  generated: { label: '已生成', color: '#f59e0b' },
  signed: { label: '已签发', color: '#10b981' },
  archived: { label: '已归档', color: '#61748f' },
}

async function load() {
  loading.value = true
  try {
    items.value = (await reportApi.list(typeFilter.value)).items
  } finally {
    loading.value = false
  }
}
onMounted(load)

async function download(r: ReportItem) {
  try {
    const res = await fetch(`/api/reports/${r.id}/download`, { headers: { Authorization: `Bearer ${getAuthToken()}` } })
    if (!res.ok) throw new Error()
    const blob = await res.blob()
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    // 后端报告是 HTML（非 PDF）；按 fileUrl 实际扩展名命名，避免误导
    const ext = r.fileUrl?.split('.').pop()?.toLowerCase() || 'html'
    a.download = `${r.title || 'report'}.${ext}`
    a.click()
    URL.revokeObjectURL(url)
  } catch {
    ElMessage.error('下载失败(报告文件可能未生成)')
  }
}
function fmt(iso: string) {
  return new Date(iso).toLocaleString('zh-CN', { hour12: false })
}
</script>

<template>
  <div class="rep rise">
    <div class="panel panel--glow">
      <div class="panel-hd">
        <span class="panel-title">报告</span>
        <div class="hd-right">
          <el-button v-if="auth.isReviewer" size="small" type="primary" @click="openGen">
            <el-icon><Document /></el-icon> 生成扭矩阶梯报告
          </el-button>
          <el-select v-model="typeFilter" size="small" style="width: 180px" @change="load">
            <el-option v-for="o in TYPE_OPTIONS" :key="o.value" :label="o.label" :value="o.value" />
          </el-select>
        </div>
      </div>
      <div class="tbl">
        <div class="tr th"><span>标题</span><span>类型</span><span>对象</span><span>状态</span><span>生成时间</span><span>操作</span></div>
        <div v-for="r in items" :key="r.id" class="tr">
          <span class="title link" @click="router.push({ name: 'reportDetail', params: { reportId: r.id } })">{{ r.title }}</span>
          <span><span class="type-tag">{{ TYPE_LABEL[r.reportType] || r.reportType }}</span></span>
          <span class="mono dim">{{ r.subjectType ? r.subjectType + '#' + r.subjectId : '—' }}</span>
          <span><span class="st" :style="`color:${STATUS[r.status]?.color || '#61748f'}`">{{ STATUS[r.status]?.label || r.status }}</span></span>
          <span class="mono dim">{{ fmt(r.generatedAt) }}</span>
          <span>
            <el-button size="small" text type="primary" :disabled="!r.fileUrl" @click="download(r)">
              <el-icon><Download /></el-icon> 下载
            </el-button>
          </span>
        </div>
        <div v-if="!loading && !items.length" class="tbl-empty">
          <el-icon><Document /></el-icon> 暂无报告。完成扭矩阶梯标定等流程后,可生成可追溯报告。
        </div>
      </div>
    </div>

    <el-dialog v-model="genVisible" title="生成扭矩阶梯报告" width="460px">
      <p class="dlg-sub">选择已采集扭矩阶梯实验点的通道，生成可追溯的 HTML 报告（法兰验收报告在「资产监测」按部位生成）。</p>
      <el-select v-model="genChannelId" placeholder="选择通道" filterable style="width: 100%">
        <el-option v-for="c in channels" :key="c.id" :label="`CH${c.channelNo} · ${c.boltCode}（dev${c.deviceId}）`" :value="c.id" />
      </el-select>
      <template #footer>
        <el-button @click="genVisible = false">取消</el-button>
        <el-button type="primary" :loading="generating" @click="doGenerate">生成</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.rep { display: flex; flex-direction: column; gap: 16px; }
.panel-hd { padding: 16px 20px 6px; display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.hd-right { display: flex; align-items: center; gap: 10px; }
.dlg-sub { font-size: 13px; color: var(--text-2); margin: 0 0 14px; line-height: 1.6; }
.tbl { padding: 4px 18px 16px; }
.tr { display: grid; grid-template-columns: 1fr 180px 120px 100px 180px 110px; align-items: center; padding: 11px 6px; border-bottom: 1px solid var(--hairline); font-size: 13px; }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; }
.title { font-weight: 600; }
.title.link { cursor: pointer; }
.title.link:hover { color: var(--accent); }
.type-tag { font-size: 11px; padding: 2px 9px; border-radius: 9px; background: rgba(56, 189, 248, 0.1); color: var(--accent-2); border: 1px solid var(--panel-border); }
.st { font-size: 12px; font-weight: 600; }
.dim { color: var(--text-3); }
.tbl-empty { display: flex; align-items: center; gap: 10px; justify-content: center; padding: 36px; color: var(--text-3); }
.tbl-empty .el-icon { font-size: 22px; opacity: 0.5; }
</style>
