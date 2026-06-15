<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { calibrationApi, boltApi, type CalibMethod, type BoltListItem } from '../api'

const router = useRouter()
const methods = ref<CalibMethod[]>([])
const bolts = ref<BoltListItem[]>([])
const loading = ref(false)

const TRUST: Record<string, { label: string; color: string }> = {
  formal: { label: '正式', color: '#10b981' },
  engineering_estimate: { label: '工程估算', color: '#f59e0b' },
  temporary: { label: '临时', color: '#61748f' },
  unknown: { label: '未知', color: '#61748f' },
}
const METHOD_LABEL: Record<string, string> = {
  load_calibrated: '正式载荷标定', gb_single_wave: 'GB 单波法', gb_bi_wave: 'GB 双波法',
  torque_fit: '扭矩拟合', torque_estimate: '扭矩估算', quick: '快速标定',
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
          <span class="trust" :style="`color:${TRUST[m.trustLevel]?.color};border-color:${TRUST[m.trustLevel]?.color}`">{{ TRUST[m.trustLevel]?.label }}</span>
        </div>
        <div class="m-stats">
          <div><b class="metric">{{ m.active }}</b><span>生效</span></div>
          <div><b class="metric dim">{{ m.total }}</b><span>累计</span></div>
          <div><b class="metric" :style="m.expired > 0 ? 'color:var(--warn)' : ''">{{ m.expired }}</b><span>过期</span></div>
          <div><b class="metric">{{ m.avgR2 != null ? Number(m.avgR2).toFixed(3) : '—' }}</b><span>平均 R²</span></div>
        </div>
      </div>
      <div v-if="!loading && !methods.length" class="empty panel">暂无标定记录</div>
    </div>

    <div class="trust-note">
      <span><span class="dot" style="background:#10b981" /> 正式:可用于正式报告与告警阈值依据</span>
      <span><span class="dot" style="background:#f59e0b" /> 工程估算:可用于试运行/趋势,报告须标明非载荷标定</span>
      <span><span class="dot" style="background:#61748f" /> 临时:仅调试演示,不用于安全结论</span>
    </div>

    <!-- 螺栓标定花名册 -->
    <div class="panel panel--glow">
      <div class="panel-hd"><span class="panel-title">螺栓标定状态</span></div>
      <div class="tbl">
        <div class="tr th"><span>螺栓</span><span>规格</span><span>状态</span><span>K_L</span><span>参考声时</span><span>参考温度</span><span></span></div>
        <div v-for="b in bolts" :key="b.id" class="tr" @click="router.push({ name: 'boltDetail', params: { boltId: b.id } })">
          <span class="mono">{{ b.code }}</span>
          <span>{{ b.nominalDiam }}·{{ b.grade }}</span>
          <span class="bs"><span class="led" :class="BSTATE[b.state]?.cls || 'led--off'" />{{ BSTATE[b.state]?.label || b.state }}</span>
          <span class="mono">{{ b.kL != null ? Number(b.kL).toExponential(2) : '—' }}</span>
          <span class="mono">{{ b.refTimeNs != null ? Number(b.refTimeNs).toFixed(0) + 'ns' : '—' }}</span>
          <span class="mono">{{ b.refTempC != null ? Number(b.refTempC).toFixed(1) + '°C' : '—' }}</span>
          <span class="go"><el-icon><ArrowRight /></el-icon></span>
        </div>
        <div v-if="!loading && !bolts.length" class="tbl-empty">暂无螺栓</div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.cal { display: flex; flex-direction: column; gap: 16px; }
.methods { display: grid; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); gap: 16px; }
.m-card { padding: 18px 20px; }
.m-top { display: flex; align-items: center; justify-content: space-between; margin-bottom: 16px; }
.m-name { font-size: 15px; font-weight: 600; }
.trust { font-size: 11px; padding: 2px 9px; border: 1px solid; border-radius: 10px; }
.m-stats { display: flex; justify-content: space-between; }
.m-stats > div { display: flex; flex-direction: column; gap: 3px; }
.m-stats b { font-size: 20px; font-weight: 700; }
.m-stats b.dim { color: var(--text-3); }
.m-stats span { font-size: 11px; color: var(--text-3); }

.trust-note { display: flex; gap: 24px; flex-wrap: wrap; font-size: 12px; color: var(--text-3); padding: 0 4px; }
.trust-note span { display: flex; align-items: center; gap: 7px; }
.dot { width: 8px; height: 8px; border-radius: 50%; display: inline-block; }

.panel-hd { padding: 16px 20px 4px; }
.tbl { padding: 4px 18px 16px; }
.tr { display: grid; grid-template-columns: 120px 100px 110px 1fr 110px 100px 30px; align-items: center; padding: 11px 6px; border-bottom: 1px solid var(--hairline); font-size: 13px; cursor: pointer; }
.tr:not(.th):hover { background: rgba(56, 189, 248, 0.05); }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; cursor: default; }
.tr.th:hover { background: none; }
.bs { display: flex; align-items: center; gap: 8px; }
.go { color: var(--text-3); }
.empty, .tbl-empty { padding: 30px; text-align: center; color: var(--text-3); }
.empty { grid-column: 1 / -1; }
</style>
