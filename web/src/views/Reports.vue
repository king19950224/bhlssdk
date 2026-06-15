<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { reportApi, type ReportItem } from '../api'
import { getAuthToken } from '../api/client'

const router = useRouter()

const items = ref<ReportItem[]>([])
const loading = ref(false)

const TYPE_LABEL: Record<string, string> = {
  torque_step: '扭矩阶梯标定报告',
  calibration: '标定报告',
  inspection: '巡检报告',
}
const STATUS: Record<string, { label: string; color: string }> = {
  final: { label: '已定稿', color: '#10b981' },
  draft: { label: '草稿', color: '#f59e0b' },
  archived: { label: '已归档', color: '#61748f' },
}

async function load() {
  loading.value = true
  try {
    items.value = (await reportApi.list()).items
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
    a.download = `${r.title || 'report'}.pdf`
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
      <div class="panel-hd"><span class="panel-title">报告</span></div>
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
  </div>
</template>

<style scoped>
.rep { display: flex; flex-direction: column; gap: 16px; }
.panel-hd { padding: 16px 20px 6px; }
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
