<script setup lang="ts">
// 标定管理（复核员以上）：所有标定记录都在这，盯的是可信等级和有效期。正式载荷标定要会签才生效。
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { calibrationApi, boltApi, type CalibMethod, type BoltListItem } from '../api'
import { useAuth } from '../stores/auth'
import CalibrationDialog from '../components/CalibrationDialog.vue'
import CalibrationWizard from '../components/CalibrationWizard.vue'

const router = useRouter()
const auth = useAuth()
const methods = ref<CalibMethod[]>([])
const bolts = ref<BoltListItem[]>([])
const loading = ref(false)

// 默认入口是引导向导(现场人员用语);专家模式(载荷标定/手动参数)从向导内进入或按钮直开
const wizardOpen = ref(false)
const dialogOpen = ref(false)
const sel = ref<{ channelId: number; boltId: number; code: string } | null>(null)
function openCalib(b: BoltListItem) {
  sel.value = { channelId: b.channelId, boltId: b.id, code: b.code }
  wizardOpen.value = true
}
function openExpert(b?: BoltListItem) {
  if (b) sel.value = { channelId: b.channelId, boltId: b.id, code: b.code }
  dialogOpen.value = true
}

const TRUST: Record<string, { label: string; color: string }> = {
  formal: { label: '正式', color: '#10b981' },
  engineering_estimate: { label: '工程估算', color: '#f59e0b' },
  temporary: { label: '临时', color: '#61748f' },
  unknown: { label: '未分级', color: '#61748f' },
}
const METHOD_LABEL: Record<string, string> = {
  load_calibrated: '正式载荷标定', gb_single_wave: 'GB 单波法', gb_bi_wave: 'GB 双波法',
  torque_fit: '扭矩拟合', torque_estimate: '扭矩估算', quick: '快速标定', sim_baseline: '初始基线',
}
// 单点标定（无回归）方法：R² 无意义，避免界面把占位的 0.000 当成"拟合极差"
const SINGLE_POINT = new Set(['quick', 'torque_estimate'])
// 初始基线非载荷标定，单列“参考”可信级（后端 trustLevel 落 unknown，这里前端归一）
function trustView(m: CalibMethod) {
  if (m.method === 'sim_baseline') return { label: '参考', color: '#8b5cf6' }
  return TRUST[m.trustLevel] ?? { label: m.trustLevel || '未分级', color: '#61748f' }
}
function r2View(m: CalibMethod) {
  if (m.avgR2 == null) return '—'
  if (SINGLE_POINT.has(m.method) && Number(m.avgR2) === 0) return '—'
  return Number(m.avgR2).toFixed(3)
}
const BSTATE: Record<string, { label: string; cls: string }> = {
  calibrated: { label: '已标定', cls: 'led--ok' },
  uncalibrated: { label: '未标定', cls: 'led--off' },
  expired: { label: '已过期', cls: 'led--warn' },
  disabled: { label: '停用', cls: 'led--off' },
}

async function load() {
  loading.value = true
  try {
    methods.value = (await calibrationApi.summary()).methods
    bolts.value = (await boltApi.list()).items
  } finally {
    loading.value = false
  }
}
onMounted(load)
</script>

<template>
  <div class="cal rise">
    <!-- 方法/可信等级摘要 -->
    <div class="methods">
      <div v-for="m in methods" :key="m.method" class="m-card panel">
        <div class="m-top">
          <span class="m-name">{{ METHOD_LABEL[m.method] || m.method }}</span>
          <span class="trust" :style="`color:${trustView(m).color};border-color:${trustView(m).color}`">{{ trustView(m).label }}</span>
        </div>
        <div class="m-stats">
          <div class="ms"><b class="metric">{{ m.active }}</b><span class="stat-label">生效</span></div>
          <div class="ms"><b class="metric dim">{{ m.total }}</b><span class="stat-label">累计</span></div>
          <div class="ms"><b class="metric" :style="m.expired > 0 ? 'color:var(--warn)' : ''">{{ m.expired }}</b><span class="stat-label">过期</span></div>
          <div class="ms"><b class="metric">{{ r2View(m) }}</b><span class="stat-label">平均 R²</span></div>
        </div>
      </div>
      <div v-if="!loading && !methods.length" class="empty panel">暂无标定记录</div>
    </div>

    <div class="trust-note">
      <span><span class="dot" style="background:#10b981" /> 正式:可用于正式报告与告警阈值依据</span>
      <span><span class="dot" style="background:#f59e0b" /> 工程估算:可用于试运行/趋势,报告须标明非载荷标定</span>
      <span><span class="dot" style="background:#61748f" /> 临时:仅作临时参考,不用于安全结论</span>
    </div>

    <!-- 螺栓标定花名册 -->
    <div class="panel panel--glow">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark" />螺栓标定状态</span>
        <span class="hd-sub mono">{{ bolts.length }} 颗</span>
      </div>
      <div class="tbl" v-loading="loading">
        <div class="tr th"><span>螺栓</span><span>规格</span><span>状态</span><span>K_L</span><span>参考声时</span><span>参考温度</span><span>操作</span></div>
        <div v-for="b in bolts" :key="b.id" class="tr" @click="router.push({ name: 'boltDetail', params: { boltId: b.id } })">
          <span class="mono">{{ b.code }}</span>
          <span>{{ b.nominalDiam }}·{{ b.grade }}</span>
          <span class="bs"><span class="led" :class="BSTATE[b.state]?.cls || 'led--off'" />{{ BSTATE[b.state]?.label || b.state }}</span>
          <span class="mono">{{ b.kL != null ? Number(b.kL).toExponential(2) : '—' }}</span>
          <span class="mono">{{ b.refTimeNs != null ? Number(b.refTimeNs).toFixed(0) + 'ns' : '—' }}</span>
          <span class="mono">{{ b.refTempC != null ? Number(b.refTempC).toFixed(1) + '°C' : '—' }}</span>
          <span class="ops" @click.stop>
            <el-button v-if="auth.isReviewer" size="small" type="primary" text @click="openCalib(b)">{{ b.calibrated ? '重新标定' : '开始标定' }}</el-button>
            <el-button v-if="auth.isReviewer" size="small" text @click="openExpert(b)">专家模式</el-button>
            <el-icon class="go" @click="router.push({ name: 'boltDetail', params: { boltId: b.id } })"><ArrowRight /></el-icon>
          </span>
        </div>
        <div v-if="!loading && !bolts.length" class="tbl-empty">暂无螺栓</div>
      </div>
    </div>

    <CalibrationWizard
      v-model="wizardOpen"
      :channel-id="sel?.channelId ?? null"
      :bolt-id="sel?.boltId ?? null"
      :bolt-code="sel?.code ?? ''"
      @done="load"
      @expert="dialogOpen = true"
    />
    <CalibrationDialog
      v-model="dialogOpen"
      :channel-id="sel?.channelId ?? null"
      :bolt-id="sel?.boltId ?? null"
      :bolt-code="sel?.code ?? ''"
      @done="load"
    />
  </div>
</template>

<style scoped>
.cal { display: flex; flex-direction: column; gap: 14px; }

/* 方法 / 可信等级摘要卡（向 stat 卡看齐：小写 label + 大号 metric） */
.methods { display: grid; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); gap: 12px; }
.m-card { padding: 15px 16px 14px; transition: border-color 0.16s, transform 0.16s; }
.m-card:hover { border-color: var(--panel-border-strong); transform: translateY(-1px); }
.m-top { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin-bottom: 14px; }
.m-name { font-size: 14px; font-weight: 600; color: var(--text-1); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.trust { font-size: 10.5px; font-weight: 600; letter-spacing: 0.04em; padding: 2px 8px; border: 1px solid; border-radius: 999px; flex-shrink: 0; }
.m-stats { display: flex; justify-content: space-between; gap: 8px; }
.ms { display: flex; flex-direction: column; gap: 5px; min-width: 0; }
.m-stats b { font-size: 20px; font-weight: 700; line-height: 1; }
.m-stats b.dim { color: var(--text-3); }

.trust-note { display: flex; gap: 24px; flex-wrap: wrap; font-size: 12px; color: var(--text-3); padding: 0 4px; }
.trust-note span { display: flex; align-items: center; gap: 7px; }
.dot { width: 8px; height: 8px; border-radius: 50%; display: inline-block; }

/* 标定花名册表格 */
.panel-hd { display: flex; align-items: center; justify-content: space-between; padding: 13px 18px 9px; }
.tbl { padding: 4px 12px 14px; overflow-x: auto; }
.tr { display: grid; grid-template-columns: 120px 100px 110px 1fr 110px 100px 120px; align-items: center; padding: 10px 8px; border-radius: 8px; font-size: 13px; cursor: pointer; min-width: 780px; }
.tr:not(.th) { box-shadow: inset 0 -1px 0 var(--hairline); }
.tr:not(.th):hover { background: rgba(56, 189, 248, 0.05); }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; cursor: default; }
.tr.th:hover { background: none; }
.ops { display: flex; align-items: center; gap: 8px; justify-content: flex-end; }
.bs { display: flex; align-items: center; gap: 8px; }
.go { color: var(--text-3); }
.empty, .tbl-empty { padding: 30px; text-align: center; color: var(--text-3); }
.empty { grid-column: 1 / -1; }
</style>
