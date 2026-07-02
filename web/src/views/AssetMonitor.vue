<script setup lang="ts">
import { onMounted, onUnmounted, ref, computed } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import HealthBar from '../components/HealthBar.vue'
import FlangeRadar from '../components/FlangeRadar.vue'
import GroupTrend from '../components/GroupTrend.vue'
import PreloadScale from '../components/PreloadScale.vue'
import { computePreloadHealth, pct, STATUS_META } from '../utils/preload'
import { useNow } from '../utils/useNow'
import { assetApi, reportApi, type ProjectNode, type TurbineNode, type GroupNode, type GroupBolt } from '../api'
import { onRealtime } from '../api/realtime'
import { useAuth } from '../stores/auth'

const router = useRouter()
const auth = useAuth()
const reporting = ref(false)
const search = ref('')
const boltSort = ref<'pos' | 'worst'>('pos')

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

// 状态元数据统一来自 utils/preload（含 high 偏高/超张力），不再本页各写一份
const STATUS: Record<string, { label: string; color: string; cls: string }> = STATUS_META

const projects = ref<ProjectNode[]>([])
const projectId = ref<number | null>(null)
const turbines = ref<TurbineNode[]>([])
const groups = ref<GroupNode[]>([])
const bolts = ref<GroupBolt[]>([])

const level = ref<'turbines' | 'groups' | 'bolts'>('turbines')
const sel = ref<{ turbine?: TurbineNode; group?: GroupNode }>({})
const loading = ref(false)
const boltView = ref<'radar' | 'grid' | 'trend'>('radar')

const num = (v: number | null) => (v == null || !Number.isFinite(v) ? null : Number(v))
// 让 stale/nodata 判定随时间刷新（否则设备离线后状态灯会一直停在最后一次加载时的判定）
const now = useNow(30000)
// 预计算每螺栓的"变化程度"指标（保持率/损失/安全裕度/松弛）
const boltCells = computed(() =>
  bolts.value.map((b) => ({
    b,
    h: computePreloadHealth({ state: b.boltState, lastTs: b.lastTs, current: b.preloadN, target: b.targetPreloadN, min: b.minPreloadN }, now.value),
  })),
)
const marginStyle = (m: number | null) => (m == null ? '' : `color:${m < 0.1 ? '#f43f5e' : m < 0.3 ? '#f59e0b' : '#10b981'}`)

const projectName = () => projects.value.find((p) => p.id === projectId.value)?.name ?? '风场'

// 搜索过滤（风机/部位按名称或编号）
function matchQ(...parts: (string | null | undefined)[]) {
  const q = search.value.trim().toLowerCase()
  return !q || parts.filter(Boolean).join(' ').toLowerCase().includes(q)
}
const filteredTurbines = computed(() => turbines.value.filter((t) => matchQ(t.name, t.code, t.model)))
const filteredGroups = computed(() => groups.value.filter((g) => matchQ(g.name, g.code, g.structureType)))
// 螺栓排序：worst 把保持率最低（最危险）排前，便于一眼抓问题
const sortedBoltCells = computed(() => {
  const cells = boltCells.value.filter((c) => matchQ(c.b.code, c.b.positionLabel))
  if (boltSort.value !== 'worst') return cells
  return [...cells].sort((a, b) => (a.b.preloadRatio ?? 99) - (b.b.preloadRatio ?? 99))
})

// 部位级工程摘要：状态计数 + 目标/下限参考值（工程视图先给统计与基准，再看单根）
const STATUS_SUMMARY = ['loose', 'warn', 'high', 'normal', 'uncalibrated', 'nodata'] as const
const boltSummary = computed(() => {
  const c: Record<string, number> = { total: bolts.value.length, normal: 0, warn: 0, loose: 0, high: 0, uncalibrated: 0, nodata: 0 }
  for (const b of bolts.value) c[b.status] = (c[b.status] ?? 0) + 1
  return c
})
const groupTargetN = computed(() => num(sel.value.group?.targetPreloadN ?? null))
const kn = (n: number | null) => (n == null ? '—' : (n / 1000).toFixed(0) + ' kN')

// 请求序号：快速切换风场/风机/部位时，后发先至的旧响应不得覆盖当前视图（与 GroupTrend 同一防护）。
let loadSeq = 0

async function loadTurbines() {
  if (projectId.value == null) return
  const seq = ++loadSeq
  loading.value = true
  try {
    const items = (await assetApi.turbines(projectId.value)).items
    if (seq !== loadSeq) return
    turbines.value = items
  } finally {
    if (seq === loadSeq) loading.value = false
  }
}
async function openTurbine(t: TurbineNode) {
  sel.value = { turbine: t }
  const seq = ++loadSeq
  loading.value = true
  try {
    const items = (await assetApi.groups(t.id)).items
    if (seq !== loadSeq) return
    groups.value = items
    level.value = 'groups'
  } finally {
    if (seq === loadSeq) loading.value = false
  }
}
async function openGroup(g: GroupNode) {
  sel.value = { ...sel.value, group: g }
  const seq = ++loadSeq
  loading.value = true
  try {
    const items = (await assetApi.groupBolts(g.id)).items
    if (seq !== loadSeq) return
    bolts.value = items
    level.value = 'bolts'
  } finally {
    if (seq === loadSeq) loading.value = false
  }
}
function backTo(l: 'turbines' | 'groups') {
  level.value = l
  if (l === 'turbines') sel.value = {}
  else sel.value = { turbine: sel.value.turbine }
}

// 实时刷新：告警变化即时刷新当前层；测量仅在螺栓层做去抖刷新（避免高频抖动）
async function refreshCurrent() {
  const seq = ++loadSeq
  try {
    if (level.value === 'turbines') { await loadTurbines(); return }
    if (level.value === 'groups' && sel.value.turbine) {
      const items = (await assetApi.groups(sel.value.turbine.id)).items
      if (seq === loadSeq) groups.value = items
    } else if (level.value === 'bolts' && sel.value.group) {
      const items = (await assetApi.groupBolts(sel.value.group.id)).items
      if (seq === loadSeq) bolts.value = items
    }
  } catch { /* 尽力刷新 */ }
}
let debounceTimer: number | undefined
function scheduleRefresh() {
  if (debounceTimer) clearTimeout(debounceTimer)
  debounceTimer = window.setTimeout(refreshCurrent, 2500)
}
let unsubAlarm: (() => void) | undefined
let unsubMeas: (() => void) | undefined

onMounted(async () => {
  projects.value = (await assetApi.projects()).items
  projectId.value = projects.value[0]?.id ?? null
  await loadTurbines()
  unsubAlarm = onRealtime('OnAlarm', scheduleRefresh)
  unsubMeas = onRealtime('OnMeasurement', () => { if (level.value === 'bolts') scheduleRefresh() })
})
onUnmounted(() => {
  if (debounceTimer) clearTimeout(debounceTimer)
  unsubAlarm?.()
  unsubMeas?.()
})

function goAlarms() {
  router.push({ name: 'alarms' })
}

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
      <div class="crumb-right">
        <el-input v-model="search" size="small" placeholder="搜索名称 / 编号" clearable style="width: 190px">
          <template #prefix><el-icon><Search /></el-icon></template>
        </el-input>
        <router-link :to="{ name: 'devices' }" class="crumb-link unbound-link">未绑定通道 →</router-link>
      </div>
    </div>

    <!-- 风机 -->
    <div v-if="level === 'turbines'" class="grid">
      <div v-for="t in filteredTurbines" :key="t.id" class="card panel" @click="openTurbine(t)">
        <div class="card-hd">
          <span class="card-ic"><el-icon><Cpu /></el-icon></span>
          <div class="card-titles">
            <div class="card-name">{{ t.name }}</div>
            <div class="card-sub mono">{{ t.code }} · {{ t.model || '—' }}</div>
          </div>
          <span v-if="t.openAlarms > 0" class="alarm-pill" title="查看告警中心" @click.stop="goAlarms">{{ t.openAlarms }}</span>
        </div>
        <HealthBar :health="t.health" />
        <div class="card-foot">
          <span>{{ t.groups }} 个部位 · {{ t.health.total }} 螺栓</span>
          <span class="loose-hint" v-if="t.health.loose > 0"><span class="led led--crit" />{{ t.health.loose }} 松动</span>
        </div>
      </div>
      <div v-if="!loading && !filteredTurbines.length" class="empty panel">{{ search ? '无匹配风机' : '该项目暂无风机' }}</div>
    </div>

    <!-- 部位 -->
    <div v-else-if="level === 'groups'" class="grid">
      <div v-for="g in filteredGroups" :key="g.id" class="card panel" @click="openGroup(g)">
        <div class="card-hd">
          <span class="card-ic"><el-icon><Connection /></el-icon></span>
          <div class="card-titles">
            <div class="card-name">{{ g.name }}</div>
            <div class="card-sub mono">{{ g.structureType || '部位' }} · 目标 {{ g.targetPreloadN ? (g.targetPreloadN / 1000).toFixed(0) + 'kN' : '—' }}</div>
          </div>
          <span v-if="g.openAlarms > 0" class="alarm-pill" title="查看告警中心" @click.stop="goAlarms">{{ g.openAlarms }}</span>
        </div>
        <HealthBar :health="g.health" />
        <div class="card-foot">
          <span>{{ g.health.total }} 螺栓</span>
          <span class="loose-hint" v-if="g.health.loose > 0"><span class="led led--crit" />{{ g.health.loose }} 松动</span>
        </div>
      </div>
      <div v-if="!loading && !filteredGroups.length" class="empty panel">{{ search ? '无匹配部位' : '该风机暂无监测部位' }}</div>
    </div>

    <!-- 螺栓层：法兰圈 / 网格 -->
    <template v-else>
      <!-- 部位工程摘要：目标/下限基准 + 状态分布计数（密集、可对比，先统计后单根） -->
      <div class="grp-summary panel" v-if="bolts.length">
        <div class="gs-refs">
          <div class="gs-ref"><span class="gs-k">监测</span><b class="mono">{{ boltSummary.total }}</b><span class="gs-u">颗</span></div>
          <div class="gs-ref"><span class="gs-k">组目标</span><b class="mono">{{ kn(groupTargetN) }}</b></div>
        </div>
        <div class="gs-stats">
          <span v-for="k in STATUS_SUMMARY" :key="k" class="gs-stat" :class="{ zero: !boltSummary[k] }">
            <i class="gs-dot" :style="`background:${STATUS[k].color}`" />{{ STATUS[k].label }}
            <b class="mono">{{ boltSummary[k] }}</b>
          </span>
        </div>
      </div>

      <div class="bolt-bar">
        <div class="bolt-toggle">
          <span class="bv-tab" :class="{ active: boltView === 'radar' }" @click="boltView = 'radar'"><el-icon><Aim /></el-icon> 法兰雷达</span>
          <span class="bv-tab" :class="{ active: boltView === 'trend' }" @click="boltView = 'trend'"><el-icon><TrendCharts /></el-icon> 趋势对比</span>
          <span class="bv-tab" :class="{ active: boltView === 'grid' }" @click="boltView = 'grid'"><el-icon><Grid /></el-icon> 网格</span>
        </div>
        <div class="bolt-bar-right">
          <div v-if="boltView === 'grid'" class="sort-toggle">
            <span class="bv-tab" :class="{ active: boltSort === 'pos' }" @click="boltSort = 'pos'">默认</span>
            <span class="bv-tab" :class="{ active: boltSort === 'worst' }" @click="boltSort = 'worst'">最危险优先</span>
          </div>
          <el-button v-if="auth.isReviewer && bolts.length" size="small" type="primary" :loading="reporting" @click="genReport">
            <el-icon><Document /></el-icon> 生成验收报告
          </el-button>
        </div>
      </div>

      <div v-if="boltView === 'radar'" class="panel panel--glow ring-panel">
        <div class="panel-hd">
          <span class="panel-title"><i class="hd-mark" />法兰预紧力雷达</span>
          <span class="hd-sub mono">{{ sel.group?.name }} · {{ bolts.length }} 颗</span>
        </div>
        <div class="ring-body">
          <FlangeRadar v-if="bolts.length" :bolts="bolts" @select="(id) => router.push({ name: 'boltDetail', params: { boltId: id } })" />
          <div v-else-if="!loading" class="empty">该部位暂无螺栓</div>
        </div>
      </div>

      <div v-else-if="boltView === 'trend'" class="panel panel--glow trend-panel">
        <GroupTrend v-if="bolts.length" :bolts="bolts" />
        <div v-else-if="!loading" class="empty">该部位暂无螺栓</div>
      </div>

      <div v-else class="grid grid--bolts">
        <div v-for="{ b, h } in sortedBoltCells" :key="b.id" class="bolt panel" :class="`bolt--${b.status}`" :title="h.reason" @click="router.push({ name: 'boltDetail', params: { boltId: b.id } })">
          <div class="bolt-hd">
            <span class="led" :class="STATUS[b.status]?.cls" />
            <span class="bolt-code">{{ b.code }}</span>
            <span class="bolt-tag" :style="`color:${STATUS[b.status]?.color}`">{{ STATUS[b.status]?.label }}</span>
            <!-- 安全状态正常但松弛显著（损失≥10%，风电常见复拧阈）时加一枚次级提示，调和"绿灯却在掉力"的观感 -->
            <span v-if="b.status === 'normal' && h.relax && h.relax.band === 'significant'" class="bolt-subtag" :style="`color:${h.relax.color}`">松弛</span>
          </div>
          <div class="bolt-ratio metric" :style="`color:${STATUS[b.status]?.color}`">
            {{ ratioStr(b.preloadRatio) }}<span class="bolt-ratio-cap">保持</span>
          </div>
          <PreloadScale :current="num(b.preloadN)" :target="num(b.targetPreloadN)" :min="num(b.minPreloadN)" :height="6" compact />
          <div class="bolt-margin">
            <!-- 偏高/超张力：主指标改显"超目标 +X%"，避免"裕度 240%"这种看着很安全实则过张力的误导 -->
            <span v-if="b.status === 'high'">超目标 <b style="color:#a78bfa">+{{ pct((h.residualRatio ?? 1) - 1) }}</b></span>
            <span v-else>裕度 <b :style="marginStyle(h.safetyMargin)">{{ pct(h.safetyMargin) }}</b></span>
            <span v-if="h.relax && h.relax.band !== 'good' && b.status !== 'high'" :style="`color:${h.relax.color}`">损失 {{ pct(h.lossPct) }}</span>
            <span v-else class="mono">{{ b.preloadN ? (Number(b.preloadN) / 1000).toFixed(1) + 'kN' : '—' }}</span>
          </div>
          <div class="bolt-foot mono">{{ b.nominalDiam }}·{{ b.grade }} · CH{{ b.channelNo }}</div>
        </div>
        <div v-if="!loading && !sortedBoltCells.length" class="empty panel">{{ search ? '无匹配螺栓' : '该部位暂无螺栓' }}</div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.asset { display: flex; flex-direction: column; gap: 14px; }
.crumbs { display: flex; align-items: center; gap: 10px; font-size: 14px; }
.proj-sel { width: 180px; }
.crumb-link { color: var(--text-2); cursor: pointer; transition: color 0.15s; }
.crumb-link:hover { color: var(--text-1); }
.crumb-link.active { color: var(--accent); font-weight: 600; }
.crumb-sep { color: var(--text-3); font-size: 13px; }

.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 14px; }
.grid--bolts { grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 12px; }

/* 风机 / 部位实体卡（图标容器 + 健康分布 + 脚注，克制边框 hover） */
.card { padding: 16px 18px; cursor: pointer; transition: border-color 0.16s, transform 0.16s; }
.card:hover { border-color: var(--panel-border-strong); transform: translateY(-1px); }
.card-hd { display: flex; align-items: center; gap: 12px; margin-bottom: 14px; }
.card-ic { width: 36px; height: 36px; border-radius: 9px; display: inline-flex; align-items: center; justify-content: center; flex-shrink: 0; background: rgba(56, 189, 248, 0.08); border: 1px solid var(--panel-border); color: var(--accent); font-size: 18px; }
.card-titles { flex: 1; min-width: 0; }
.card-name { font-size: 15px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.card-sub { font-size: 11px; color: var(--text-3); }
.alarm-pill { background: var(--crit); color: #fff; font-size: 11px; font-weight: 700; padding: 2px 8px; border-radius: 999px; flex-shrink: 0; cursor: pointer; }
.card-foot { display: flex; justify-content: space-between; align-items: center; margin-top: 14px; font-size: 12px; color: var(--text-3); }
.loose-hint { display: flex; align-items: center; gap: 6px; color: var(--crit); }

/* 螺栓网格卡（stat 风：大号保持率 + 状态点 + 脚注，状态色仅用于边/标记） */
.bolt { padding: 14px 14px 13px; cursor: pointer; transition: border-color 0.16s, transform 0.16s; border-radius: var(--radius-sm); }
.bolt:hover { transform: translateY(-2px); }
.bolt:not(.bolt--loose):not(.bolt--warn):not(.bolt--high):hover { border-color: var(--panel-border-strong); }
.bolt--loose { border-color: var(--crit); }
.bolt--warn { border-color: var(--warn); }
.bolt--high { border-color: #a78bfa; }
.bolt-subtag { font-size: 10px; padding: 0 5px; border-radius: 4px; background: rgba(167, 139, 250, 0.12); }
.bolt-hd { display: flex; align-items: center; gap: 8px; }
.bolt-code { font-size: 13px; font-weight: 600; flex: 1; }
.bolt-tag { font-size: 11px; }
.bolt-ratio { font-size: 30px; font-weight: 700; margin: 10px 0; line-height: 1; display: flex; align-items: baseline; gap: 6px; }
.bolt-ratio-cap { font-size: 11px; font-weight: 500; color: var(--text-3); }
.bolt-margin { display: flex; justify-content: space-between; align-items: center; font-size: 11.5px; color: var(--text-3); margin-top: 10px; }
.bolt-margin b { font-weight: 700; }
.bolt-foot { margin-top: 10px; font-size: 10.5px; color: var(--text-3); border-top: 1px solid var(--hairline); padding-top: 8px; }

.empty { grid-column: 1 / -1; padding: 40px; text-align: center; color: var(--text-3); }

.bolt-bar { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.bolt-toggle { display: flex; gap: 4px; background: var(--panel); border: 1px solid var(--panel-border); border-radius: 10px; padding: 4px; width: fit-content; }
.bv-tab { display: flex; align-items: center; gap: 6px; font-size: 13px; padding: 6px 16px; border-radius: 7px; cursor: pointer; color: var(--text-3); transition: color 0.15s, background 0.15s; }
.bv-tab:hover { color: var(--text-1); }
/* 克制强调：选中态用低饱和填充 + 强调色文字，不用高亮霓虹渐变 */
.bv-tab.active { color: var(--accent); background: rgba(34, 211, 238, 0.1); font-weight: 600; }

/* 可视化面板：统一面板头（hd-mark + 标题 + 副标题） */
.panel-hd { display: flex; align-items: center; justify-content: space-between; padding: 13px 18px 9px; }
.ring-panel { display: flex; flex-direction: column; }
.ring-body { display: flex; justify-content: center; padding: 18px 20px 26px; }
.trend-panel { padding: 18px 20px; }

/* 部位工程摘要条：左基准值 + 右状态分布，密集单行 */
.grp-summary { display: flex; align-items: center; justify-content: space-between; gap: 20px; padding: 11px 16px; flex-wrap: wrap; }
.gs-refs { display: flex; align-items: center; gap: 22px; }
.gs-ref { display: flex; align-items: baseline; gap: 6px; }
.gs-ref .gs-k { font-size: 11px; color: var(--text-3); letter-spacing: 0.06em; }
.gs-ref b { font-size: 17px; font-weight: 700; color: var(--text-1); }
.gs-ref .gs-u { font-size: 11px; color: var(--text-3); }
.gs-stats { display: flex; align-items: center; gap: 16px; flex-wrap: wrap; }
.gs-stat { display: inline-flex; align-items: center; gap: 6px; font-size: 12px; color: var(--text-2); }
.gs-stat b { color: var(--text-1); font-size: 13px; }
.gs-stat.zero { opacity: 0.4; }
.gs-dot { width: 8px; height: 8px; border-radius: 2px; display: inline-block; }

.crumb-right { margin-left: auto; display: flex; align-items: center; gap: 14px; }
.unbound-link { font-size: 12px; white-space: nowrap; }
.bolt-bar-right { display: flex; align-items: center; gap: 12px; }
.sort-toggle { display: flex; gap: 4px; background: var(--panel); border: 1px solid var(--panel-border); border-radius: 8px; padding: 3px; }
.sort-toggle .bv-tab { padding: 5px 12px; font-size: 12px; border-radius: 6px; }
</style>
