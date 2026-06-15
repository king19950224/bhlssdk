<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { reportApi, type FlangeSnapshot } from '../api'
import { useAuth } from '../stores/auth'

const route = useRoute()
const router = useRouter()
const auth = useAuth()
const id = Number(route.params.reportId)

const title = ref('')
const generatedAt = ref('')
const status = ref('')
const generatedBy = ref<number | null>(null)
const generatedByName = ref<string | null>(null)
const signedAt = ref<string | null>(null)
const signedByName = ref<string | null>(null)
const snap = ref<FlangeSnapshot | null>(null)
const loading = ref(true)
const signing = ref(false)

const canSign = computed(() => status.value === 'generated' && auth.isReviewer && auth.userId !== generatedBy.value)

const RESULT: Record<string, string> = { 合格: '#10b981', 不合格: '#f43f5e', 未标定: '#61748f', 无数据: '#3b4659' }
const METHOD: Record<string, string> = {
  load_calibrated: '正式载荷标定', gb_single_wave: 'GB单波', gb_bi_wave: 'GB双波',
  torque_fit: '扭矩拟合', torque_estimate: '扭矩估算', quick: '快速标定',
}

async function load() {
  const d = await reportApi.get(id)
  title.value = d.title
  generatedAt.value = d.generatedAt
  status.value = d.status
  generatedBy.value = d.generatedBy
  generatedByName.value = d.generatedByName
  signedAt.value = d.signedAt
  signedByName.value = d.signedByName
  snap.value = d.snapshot
}
onMounted(async () => {
  try {
    await load()
  } finally {
    loading.value = false
  }
})
async function sign() {
  signing.value = true
  try {
    await reportApi.sign(id)
    ElMessage.success('已审核签发,报告已锁定')
    await load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '签发失败')
  } finally {
    signing.value = false
  }
}

function n(v: number | null | undefined) {
  return v == null ? '—' : Number(v).toLocaleString('en-US', { maximumFractionDigits: 0 })
}
function ratio(v: number | null) {
  return v == null ? '—' : (Number(v) * 100).toFixed(1) + '%'
}
function fmt(iso: string | null) {
  return iso ? new Date(iso).toLocaleString('zh-CN', { hour12: false }) : '—'
}
function doPrint() {
  window.print()
}
</script>

<template>
  <div class="rd" v-loading="loading">
    <div class="rd-toolbar no-print">
      <span class="back" @click="router.back()"><el-icon><ArrowLeft /></el-icon> 返回</span>
      <div class="tb-right">
        <span class="status-badge" :class="status === 'signed' ? 'signed' : 'draft'">{{ status === 'signed' ? '已签发 · 已锁定' : '草稿' }}</span>
        <el-button v-if="canSign" :loading="signing" @click="sign"><el-icon><Select /></el-icon> 审核签发</el-button>
        <el-button type="primary" @click="doPrint"><el-icon><Printer /></el-icon> 打印 / 导出 PDF</el-button>
      </div>
    </div>

    <div v-if="snap" class="sheet panel panel--glow">
      <header class="rh">
        <div class="rh-logo">
          <svg viewBox="0 0 32 32" width="30" height="30"><polygon points="16,3 27,9 27,23 16,29 5,23 5,9" fill="none" stroke="var(--accent)" stroke-width="1.6" /><circle cx="16" cy="16" r="4.5" fill="var(--accent)" /></svg>
          <span>螺栓预紧力在线监测系统</span>
        </div>
        <h1>{{ title }}</h1>
        <div class="rh-std">{{ snap.standard }}</div>
      </header>

      <div class="meta-grid">
        <div><span>风场</span><b>{{ snap.project }}</b></div>
        <div><span>风机</span><b>{{ snap.turbine }}</b></div>
        <div><span>监测部位</span><b>{{ snap.flange.name }}（{{ snap.flange.code }}）</b></div>
        <div><span>组目标预紧力</span><b>{{ snap.flange.targetPreloadN ? n(snap.flange.targetPreloadN) + ' N' : '—' }}</b></div>
        <div><span>生成时间</span><b>{{ fmt(generatedAt) }}</b></div>
        <div><span>报告编号</span><b>FR-{{ String(id).padStart(6, '0') }}</b></div>
      </div>

      <div class="verdict" :class="snap.summary.overall === '合格' ? 'ok' : 'no'">
        <div class="v-main">
          <span class="v-label">验收结论</span>
          <span class="v-result">{{ snap.summary.overall }}</span>
        </div>
        <div class="v-stats">
          <div><b>{{ snap.summary.total }}</b><span>螺栓总数</span></div>
          <div><b>{{ snap.summary.calibrated }}</b><span>已标定</span></div>
          <div><b style="color:var(--ok)">{{ snap.summary.passed }}</b><span>合格</span></div>
          <div><b :style="snap.summary.failed > 0 ? 'color:var(--crit)' : ''">{{ snap.summary.failed }}</b><span>不合格</span></div>
        </div>
      </div>

      <table class="rt">
        <thead>
          <tr><th>#</th><th>螺栓编号</th><th>规格</th><th>位置</th><th>目标 N</th><th>下限 N</th><th>当前预紧力 N</th><th>比值</th><th>标定方法</th><th>可信</th><th>R²</th><th>结论</th></tr>
        </thead>
        <tbody>
          <tr v-for="(b, i) in snap.bolts" :key="i">
            <td>{{ i + 1 }}</td>
            <td class="mono">{{ b.code }}</td>
            <td>{{ b.spec }}</td>
            <td>{{ b.position || '—' }}</td>
            <td class="mono">{{ n(b.targetN) }}</td>
            <td class="mono">{{ n(b.minN) }}</td>
            <td class="mono">{{ n(b.preloadN) }}</td>
            <td class="mono">{{ ratio(b.ratio) }}</td>
            <td>{{ b.calibMethod ? (METHOD[b.calibMethod] || b.calibMethod) : '—' }}</td>
            <td>{{ b.trust }}</td>
            <td class="mono">{{ b.rSquared != null ? Number(b.rSquared).toFixed(3) : '—' }}</td>
            <td><span class="res" :style="`color:${RESULT[b.result]}`">{{ b.result }}</span></td>
          </tr>
        </tbody>
      </table>

      <footer class="sign">
        <div class="sig">
          <span class="sig-name">{{ generatedByName || '—' }}</span>
          <span class="sig-cap">编制</span>
        </div>
        <div class="sig">
          <span class="sig-name" :class="{ pending: !signedByName }">{{ signedByName || '（待审核签发）' }}</span>
          <span class="sig-cap">审核签发</span>
        </div>
        <div class="sig-date">
          {{ status === 'signed' && signedAt ? '签发日期：' + new Date(signedAt).toLocaleDateString('zh-CN') : '日期：__________' }}
        </div>
      </footer>
      <div class="rd-note">本报告由螺栓预紧力在线监测系统自动生成，数据快照已归档，可追溯复现。标定可信等级为"临时/工程估算"的螺栓不作为正式安全验收依据。</div>
    </div>

    <div v-else-if="!loading" class="empty panel">报告不存在或快照为空</div>
  </div>
</template>

<style scoped>
.rd { display: flex; flex-direction: column; gap: 16px; }
.rd-toolbar { display: flex; align-items: center; justify-content: space-between; }
.back { display: inline-flex; align-items: center; gap: 6px; color: var(--text-3); cursor: pointer; font-size: 13px; }
.back:hover { color: var(--accent); }
.tb-right { display: flex; align-items: center; gap: 12px; }
.status-badge { font-size: 12px; padding: 4px 12px; border-radius: 12px; border: 1px solid; }
.status-badge.draft { color: var(--warn); border-color: var(--warn); }
.status-badge.signed { color: var(--ok); border-color: var(--ok); background: rgba(16, 185, 129, 0.08); }

.sheet { padding: 36px 40px; max-width: 1100px; margin: 0 auto; width: 100%; }
.rh { text-align: center; border-bottom: 2px solid var(--panel-border-strong); padding-bottom: 18px; margin-bottom: 22px; }
.rh-logo { display: inline-flex; align-items: center; gap: 8px; font-weight: 700; font-size: 16px; color: var(--accent); }
.rh h1 { font-size: 24px; margin: 12px 0 6px; }
.rh-std { font-size: 12px; color: var(--text-3); }

.meta-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 14px 30px; margin-bottom: 22px; }
.meta-grid > div { display: flex; flex-direction: column; gap: 3px; border-bottom: 1px solid var(--hairline); padding-bottom: 8px; }
.meta-grid span { font-size: 11px; color: var(--text-3); }
.meta-grid b { font-size: 14px; }

.verdict { display: flex; align-items: center; justify-content: space-between; padding: 18px 24px; border-radius: 12px; border: 1px solid; margin-bottom: 22px; }
.verdict.ok { border-color: rgba(16, 185, 129, 0.4); background: rgba(16, 185, 129, 0.07); }
.verdict.no { border-color: rgba(244, 63, 94, 0.4); background: rgba(244, 63, 94, 0.08); }
.v-main { display: flex; flex-direction: column; gap: 4px; }
.v-label { font-size: 12px; color: var(--text-3); letter-spacing: 0.1em; }
.v-result { font-size: 34px; font-weight: 800; }
.verdict.ok .v-result { color: var(--ok); }
.verdict.no .v-result { color: var(--crit); }
.v-stats { display: flex; gap: 34px; }
.v-stats > div { display: flex; flex-direction: column; align-items: center; gap: 3px; }
.v-stats b { font-size: 24px; font-weight: 700; font-family: var(--font-mono); }
.v-stats span { font-size: 11px; color: var(--text-3); }

.rt { width: 100%; border-collapse: collapse; font-size: 12.5px; margin-bottom: 26px; }
.rt th { text-align: left; padding: 10px 8px; color: var(--text-3); font-weight: 600; border-bottom: 1px solid var(--panel-border-strong); font-size: 11px; }
.rt td { padding: 9px 8px; border-bottom: 1px solid var(--hairline); }
.res { font-weight: 600; }

.sign { display: flex; gap: 50px; align-items: flex-end; margin-top: 30px; flex-wrap: wrap; }
.sig { display: flex; flex-direction: column; gap: 6px; min-width: 130px; }
.sig-name { font-size: 15px; font-weight: 600; border-bottom: 1px solid var(--text-3); padding-bottom: 6px; min-height: 26px; }
.sig-name.pending { color: var(--text-3); font-weight: 400; }
.sig-cap { font-size: 12px; color: var(--text-3); }
.sig-date { margin-left: auto; color: var(--text-2); font-size: 13px; align-self: flex-end; }
.rd-note { margin-top: 18px; font-size: 11px; color: var(--text-3); line-height: 1.6; border-top: 1px solid var(--hairline); padding-top: 12px; }
.empty { padding: 40px; text-align: center; color: var(--text-3); }
</style>
