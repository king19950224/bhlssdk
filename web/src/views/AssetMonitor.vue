<script setup lang="ts">
import { onMounted, ref, computed } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import HealthBar from '../components/HealthBar.vue'
import FlangeRadar from '../components/FlangeRadar.vue'
import GroupTrend from '../components/GroupTrend.vue'
import PreloadScale from '../components/PreloadScale.vue'
import { computePreloadHealth, pct } from '../utils/preload'
import { assetApi, reportApi, type ProjectNode, type TurbineNode, type GroupNode, type GroupBolt } from '../api'
import { useAuth } from '../stores/auth'

const router = useRouter()
const auth = useAuth()
const reporting = ref(false)

async function genReport() {
  if (!sel.value.group) return
  reporting.value = true
  try {
    const r = await reportApi.generateFlange(sel.value.group.id)
    ElMessage.success(`验收报告已生成（${r.overall}）`)
    router.push({ name: 'reportDetail', params: { reportId: r.reportId } })
  } catch (e) {
    ElMessage.error((e as Error)?.message || '生成失败')
  } finally {
    reporting.value = false
  }
}

const STATUS: Record<string, { label: string; color: string; cls: string }> = {
  normal: { label: '正常', color: '#10b981', cls: 'led--ok' },
  warn: { label: '预警', color: '#f59e0b', cls: 'led--warn' },
  loose: { label: '松动', color: '#f43f5e', cls: 'led--crit' },
  uncalibrated: { label: '未标定', color: '#61748f', cls: 'led--off' },
  nodata: { label: '无数据', color: '#3b4659', cls: 'led--off' },
}

const projects = ref<ProjectNode[]>([])
const projectId = ref<number | null>(null)
const turbines = ref<TurbineNode[]>([])
const groups = ref<GroupNode[]>([])
const bolts = ref<GroupBolt[]>([])

const level = ref<'turbines' | 'groups' | 'bolts'>('turbines')
const sel = ref<{ turbine?: TurbineNode; group?: GroupNode }>({})
const loading = ref(false)
const boltView = ref<'radar' | 'grid' | 'trend'>('radar')

const num = (v: number | null) => (v == null ? null : Number(v))
// 预计算每螺栓的"变化程度"指标（残余率/损失/安全裕度/松弛）
const boltCells = computed(() =>
  bolts.value.map((b) => ({
    b,
    h: computePreloadHealth({ state: b.boltState, lastTs: b.lastTs, current: b.preloadN, target: b.targetPreloadN, min: b.minPreloadN }),
  })),
)
const marginStyle = (m: number | null) => (m == null ? '' : `color:${m < 0.1 ? '#f43f5e' : m < 0.3 ? '#f59e0b' : '#10b981'}`)

const projectName = () => projects.value.find((p) => p.id === projectId.value)?.name ?? '风场'

async function loadTurbines() {
  if (projectId.value == null) return
  loading.value = true
  try {
    turbines.value = (await assetApi.turbines(projectId.value)).items
  } finally {
    loading.value = false
  }
}
async function openTurbine(t: TurbineNode) {
  sel.value = { turbine: t }
  loading.value = true
  try {
    groups.value = (await assetApi.groups(t.id)).items
    level.value = 'groups'
  } finally {
    loading.value = false
  }
}
async function openGroup(g: GroupNode) {
  sel.value = { ...sel.value, group: g }
  loading.value = true
  try {
    bolts.value = (await assetApi.groupBolts(g.id)).items
    level.value = 'bolts'
  } finally {
    loading.value = false
  }
}
function backTo(l: 'turbines' | 'groups') {
  level.value = l
  if (l === 'turbines') sel.value = {}
  else sel.value = { turbine: sel.value.turbine }
}

onMounted(async () => {
  projects.value = (await assetApi.projects()).items
  projectId.value = projects.value[0]?.id ?? null
  await loadTurbines()
})

function ratioStr(r: number | null) {
  return r == null ? '—' : (r * 100).toFixed(0) + '%'
}
function onProjectChange() {
  level.value = 'turbines'
  sel.value = {}
  loadTurbines()
}
</script>

<template>
  <div class="asset rise">
    <!-- 面包屑 + 项目选择 -->
    <div class="crumbs">
      <el-select v-if="projects.length > 1" v-model="projectId" size="small" class="proj-sel" @change="onProjectChange">
        <el-option v-for="p in projects" :key="p.id" :label="p.name" :value="p.id" />
      </el-select>
      <span v-else class="crumb-link" :class="{ active: level === 'turbines' }" @click="backTo('turbines')">{{ projectName() }}</span>
      <template v-if="sel.turbine">
        <el-icon class="crumb-sep"><ArrowRight /></el-icon>
        <span class="crumb-link" :class="{ active: level === 'groups' }" @click="backTo('groups')">{{ sel.turbine.name }}</span>
      </template>
      <template v-if="sel.group && level === 'bolts'">
        <el-icon class="crumb-sep"><ArrowRight /></el-icon>
        <span class="crumb-link active">{{ sel.group.name }}</span>
      </template>
    </div>

    <!-- 风机 -->
    <div v-if="level === 'turbines'" class="grid">
      <div v-for="t in turbines" :key="t.id" class="card panel" @click="openTurbine(t)">
        <div class="card-hd">
          <el-icon class="card-ic"><Cpu /></el-icon>
          <div class="card-titles">
            <div class="card-name">{{ t.name }}</div>
            <div class="card-sub mono">{{ t.code }} · {{ t.model || '—' }}</div>
          </div>
          <span v-if="t.openAlarms > 0" class="alarm-pill">{{ t.openAlarms }}</span>
        </div>
        <HealthBar :health="t.health" />
        <div class="card-foot">
          <span>{{ t.groups }} 个部位 · {{ t.health.total }} 螺栓</span>
          <span class="loose-hint" v-if="t.health.loose > 0"><span class="led led--crit" />{{ t.health.loose }} 松动</span>
        </div>
      </div>
      <div v-if="!loading && !turbines.length" class="empty panel">该项目暂无风机</div>
    </div>

    <!-- 部位 -->
    <div v-else-if="level === 'groups'" class="grid">
      <div v-for="g in groups" :key="g.id" class="card panel" @click="openGroup(g)">
        <div class="card-hd">
          <el-icon class="card-ic"><Connection /></el-icon>
          <div class="card-titles">
            <div class="card-name">{{ g.name }}</div>
            <div class="card-sub mono">{{ g.structureType || '部位' }} · 目标 {{ g.targetPreloadN ? (g.targetPreloadN / 1000).toFixed(0) + 'kN' : '—' }}</div>
          </div>
          <span v-if="g.openAlarms > 0" class="alarm-pill">{{ g.openAlarms }}</span>
        </div>
        <HealthBar :health="g.health" />
        <div class="card-foot">
          <span>{{ g.health.total }} 螺栓</span>
          <span class="loose-hint" v-if="g.health.loose > 0"><span class="led led--crit" />{{ g.health.loose }} 松动</span>
        </div>
      </div>
      <div v-if="!loading && !groups.length" class="empty panel">该风机暂无监测部位</div>
    </div>

    <!-- 螺栓层：法兰圈 / 网格 -->
    <template v-else>
      <div class="bolt-bar">
        <div class="bolt-toggle">
          <span class="bv-tab" :class="{ active: boltView === 'radar' }" @click="boltView = 'radar'"><el-icon><Aim /></el-icon> 法兰雷达</span>
          <span class="bv-tab" :class="{ active: boltView === 'trend' }" @click="boltView = 'trend'"><el-icon><TrendCharts /></el-icon> 趋势对比</span>
          <span class="bv-tab" :class="{ active: boltView === 'grid' }" @click="boltView = 'grid'"><el-icon><Grid /></el-icon> 网格</span>
        </div>
        <el-button v-if="auth.isReviewer && bolts.length" size="small" type="primary" :loading="reporting" @click="genReport">
          <el-icon><Document /></el-icon> 生成验收报告
        </el-button>
      </div>

      <div v-if="boltView === 'radar'" class="panel panel--glow ring-panel">
        <FlangeRadar v-if="bolts.length" :bolts="bolts" @select="(id) => router.push({ name: 'boltDetail', params: { boltId: id } })" />
        <div v-else-if="!loading" class="empty">该部位暂无螺栓</div>
      </div>

      <div v-else-if="boltView === 'trend'" class="panel panel--glow trend-panel">
        <GroupTrend v-if="bolts.length" :bolts="bolts" />
        <div v-else-if="!loading" class="empty">该部位暂无螺栓</div>
      </div>

      <div v-else class="grid grid--bolts">
        <div v-for="{ b, h } in boltCells" :key="b.id" class="bolt panel" :class="`bolt--${b.status}`" @click="router.push({ name: 'boltDetail', params: { boltId: b.id } })">
          <div class="bolt-hd">
            <span class="led" :class="STATUS[b.status]?.cls" />
            <span class="bolt-code">{{ b.code }}</span>
            <span class="bolt-tag" :style="`color:${STATUS[b.status]?.color}`">{{ STATUS[b.status]?.label }}</span>
          </div>
          <div class="bolt-ratio metric" :style="`color:${STATUS[b.status]?.color}`">
            {{ ratioStr(b.preloadRatio) }}<span class="bolt-ratio-cap">残余</span>
          </div>
          <PreloadScale :current="num(b.preloadN)" :target="num(b.targetPreloadN)" :min="num(b.minPreloadN)" :height="6" compact />
          <div class="bolt-margin">
            <span>裕度 <b :style="marginStyle(h.safetyMargin)">{{ pct(h.safetyMargin) }}</b></span>
            <span v-if="h.relax && h.relax.band !== 'good'" :style="`color:${h.relax.color}`">损失 {{ pct(h.lossPct) }}</span>
            <span v-else class="mono">{{ b.preloadN ? (Number(b.preloadN) / 1000).toFixed(1) + 'kN' : '—' }}</span>
          </div>
          <div class="bolt-foot mono">{{ b.nominalDiam }}·{{ b.grade }} · CH{{ b.channelNo }}</div>
        </div>
        <div v-if="!loading && !boltCells.length" class="empty panel">该部位暂无螺栓</div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.asset { display: flex; flex-direction: column; gap: 18px; }
.crumbs { display: flex; align-items: center; gap: 10px; font-size: 15px; }
.proj-sel { width: 180px; }
.crumb-link { color: var(--text-2); cursor: pointer; transition: color 0.15s; }
.crumb-link:hover { color: var(--text-1); }
.crumb-link.active { color: var(--accent); font-weight: 600; }
.crumb-sep { color: var(--text-3); font-size: 13px; }

.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 16px; }
.grid--bolts { grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); }

.card { padding: 18px 20px; cursor: pointer; transition: all 0.18s; }
.card:hover { border-color: var(--panel-border-strong); transform: translateY(-2px); }
.card-hd { display: flex; align-items: center; gap: 12px; margin-bottom: 16px; }
.card-ic { font-size: 22px; color: var(--accent); }
.card-titles { flex: 1; min-width: 0; }
.card-name { font-size: 16px; font-weight: 600; }
.card-sub { font-size: 11px; color: var(--text-3); }
.alarm-pill { background: var(--crit); color: #fff; font-size: 11px; font-weight: 700; padding: 2px 8px; border-radius: 10px; box-shadow: 0 0 10px var(--crit-glow); }
.card-foot { display: flex; justify-content: space-between; align-items: center; margin-top: 14px; font-size: 12px; color: var(--text-3); }
.loose-hint { display: flex; align-items: center; gap: 6px; color: var(--crit); }

.bolt { padding: 16px; cursor: pointer; transition: all 0.18s; border-radius: var(--radius-sm); }
.bolt:hover { transform: translateY(-2px); }
.bolt--loose { border-color: var(--crit); box-shadow: 0 0 22px -10px var(--crit-glow); }
.bolt--warn { border-color: var(--warn); }
.bolt-hd { display: flex; align-items: center; gap: 8px; }
.bolt-code { font-size: 13px; font-weight: 600; flex: 1; }
.bolt-tag { font-size: 11px; }
.bolt-ratio { font-size: 32px; font-weight: 700; margin: 10px 0 10px; line-height: 1; display: flex; align-items: baseline; gap: 6px; }
.bolt-ratio-cap { font-size: 11px; font-weight: 500; color: var(--text-3); }
.bolt-margin { display: flex; justify-content: space-between; align-items: center; font-size: 11.5px; color: var(--text-3); margin-top: 10px; }
.bolt-margin b { font-weight: 700; }
.bolt-foot { margin-top: 10px; font-size: 10.5px; color: var(--text-3); border-top: 1px solid var(--hairline); padding-top: 8px; }

.empty { grid-column: 1 / -1; padding: 40px; text-align: center; color: var(--text-3); }

.bolt-bar { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.bolt-toggle { display: flex; gap: 4px; background: var(--panel); border: 1px solid var(--panel-border); border-radius: 10px; padding: 4px; width: fit-content; }
.bv-tab { display: flex; align-items: center; gap: 6px; font-size: 13px; padding: 6px 16px; border-radius: 7px; cursor: pointer; color: var(--text-3); }
.bv-tab.active { color: #04121a; background: linear-gradient(135deg, var(--accent), var(--accent-2)); font-weight: 600; }
.ring-panel { padding: 26px 20px; display: flex; justify-content: center; }
.trend-panel { padding: 18px 20px; }
</style>
