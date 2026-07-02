<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import EChart from '../components/EChart.vue'
import { dashboardApi, type PreloadHealth, type LooseBolt, type ResidualDist, type ApproachingBolt, type DashboardTrendPoint } from '../api'
import { useRuntime } from '../stores/runtime'
import { onRealtime } from '../api/realtime'
import { pctParts } from '../utils/preload'

const router = useRouter()
const runtime = useRuntime()

const health = ref<PreloadHealth>({ total: 0, normal: 0, warn: 0, loose: 0, uncalibrated: 0, nodata: 0, high: 0 })
const byGroup = ref<{ turbine: string; group: string; health: PreloadHealth }[]>([])
const topLoose = ref<LooseBolt[]>([])
const residualDist = ref<ResidualDist>({ b100: 0, b90: 0, b80: 0, b70: 0, b70d: 0 })
const approaching = ref<ApproachingBolt[]>([])
const openAlarms = ref(0)
const alarmsBySeverity = ref<{ severity: string; count: number }[]>([])
const trend = ref<DashboardTrendPoint[]>([])
const loaded = ref(false)
const firstLoad = ref(true)
const error = ref<string | null>(null)

const SEV_LABEL: Record<string, string> = { critical: '严重', crit: '严重', warn: '预警', warning: '预警', info: '提示' }
const alarmBreakdown = computed(() =>
  alarmsBySeverity.value.filter((s) => s.count > 0).map((s) => `${SEV_LABEL[s.severity] || s.severity} ${s.count}`).join(' · '))

// 设备遥测由共享 runtime store 提供（与顶栏共用一次轮询）
const sys = computed(() => ({ online: runtime.online, total: runtime.total, walPending: runtime.walPending, rate: runtime.rate }))
let timer: number | undefined
let debounce: number | undefined
let disposed = false
const unsubs: (() => void)[] = []

const STATUS: Record<string, { label: string; color: string; cls: string }> = {
  normal: { label: '正常', color: '#10b981', cls: 'led--ok' },
  warn: { label: '预警', color: '#f59e0b', cls: 'led--warn' },
  loose: { label: '松动', color: '#f43f5e', cls: 'led--crit' },
  high: { label: '偏高', color: '#a78bfa', cls: 'led--high' },
  uncalibrated: { label: '未标定/过期', color: '#61748f', cls: 'led--off' },
  nodata: { label: '无近期数据', color: '#3b4659', cls: 'led--off' },
}
const ORDER = ['normal', 'warn', 'loose', 'high', 'uncalibrated', 'nodata'] as const

// ── 核心口径：把"在测（已标定且有近期数据）"与"待接入（未标定/无数据）"分开 ──
// 修掉旧总览"健康率 25%(=normal/total)"被大量未标定螺栓拖低、与"平均保持率 103.7%"自相矛盾的问题。
const measured = computed(() => health.value.normal + health.value.warn + health.value.loose + health.value.high)
const pendingSetup = computed(() => health.value.uncalibrated + health.value.nodata)
// 超张力/偏高：与资产页、告警 TriggerHighRatio 口径统一——保持率 > 110% 的在测螺栓（health.high）。
const overTension = computed(() => health.value.high)
// 在测健康率 = 正常 / 在测（比 normal/total 有意义，不被未标定拖低）
const measuredHealthPct = computed(() => (measured.value ? Math.round((health.value.normal / measured.value) * 100) : null))

// 单一安全结论（首屏一句话说清安不安全 + 主色）
const verdict = computed<{ level: 'ok' | 'warn' | 'crit' | 'off'; text: string }>(() => {
  const h = health.value
  if (h.loose > 0) return { level: 'crit', text: `${h.loose} 颗预紧力松动，需立即停机巡检` }
  if (openAlarms.value > 0) return { level: 'crit', text: `${openAlarms.value} 条未闭合告警待处置` }
  if (h.warn > 0) return { level: 'warn', text: `${h.warn} 颗预警，预紧力接近下限` }
  if (h.high > 0) return { level: 'warn', text: `${h.high} 颗预紧力偏高（超目标 110%），关注超张力风险` }
  if (approaching.value.length > 0) return { level: 'warn', text: `${approaching.value.length} 颗预紧力持续下降，关注到限` }
  if (measured.value > 0) return { level: 'ok', text: `在测 ${measured.value} 颗预紧力正常` }
  return { level: 'off', text: '暂无在测螺栓，请先完成标定接入' }
})
const VERDICT_COLOR: Record<string, string> = { ok: 'var(--ok)', warn: 'var(--warn)', crit: 'var(--crit)', off: 'var(--text-3)' }
const verdictColor = computed(() => VERDICT_COLOR[verdict.value.level])

// 待处置聚焦：把"要运维做什么"聚成可点入口，紧急的高亮，无事的淡化
const actionTiles = computed(() => [
  { key: 'alarms', label: '未闭合告警', count: openAlarms.value, sub: openAlarms.value > 0 ? (alarmBreakdown.value || '需处置') : '运行正常', to: 'alarms', level: openAlarms.value > 0 ? 'crit' : 'muted' },
  { key: 'loose', label: '松动 / 预警', count: health.value.loose + health.value.warn, sub: `松动 ${health.value.loose} · 预警 ${health.value.warn}`, to: 'assets', level: health.value.loose > 0 ? 'crit' : health.value.warn > 0 ? 'warn' : 'muted' },
  { key: 'appr', label: '即将到限', count: approaching.value.length, sub: '近 7 天下降趋势', to: 'assets', level: approaching.value.some((b) => b.daysToLimit != null && b.daysToLimit < 30) ? 'warn' : 'muted' },
  { key: 'uncal', label: '待标定', count: health.value.uncalibrated, sub: '未标定 / 已过期', to: 'assets', level: health.value.uncalibrated > 0 ? 'info' : 'muted' },
  { key: 'nodata', label: '无数据', count: health.value.nodata, sub: '无近期回波', to: 'devices', level: 'muted' },
])
function goTile(to: string) { router.push({ name: to }) }

async function refresh() {
  try {
    const [d, t] = await Promise.all([dashboardApi.preloadHealth(), dashboardApi.trend(24).catch(() => null)])
    if (disposed) return
    health.value = d.global
    byGroup.value = d.byGroup
    topLoose.value = d.topLoose
    residualDist.value = d.residualDist
    approaching.value = d.approachingLimit
    openAlarms.value = d.openAlarms
    alarmsBySeverity.value = d.alarmsBySeverity ?? []
    if (t) trend.value = t.points
    error.value = null
  } catch (e) {
    if (disposed) return
    error.value = e instanceof Error ? e.message : '加载失败'
  } finally {
    if (!disposed) { loaded.value = true; firstLoad.value = false }
  }
}

// 实时推送去抖刷新：preload-health 是较重的聚合查询，密集推送 5s 合并一次
function scheduleRefresh() {
  if (debounce) return
  debounce = window.setTimeout(() => { debounce = undefined; if (!document.hidden) refresh() }, 5000)
}
function onVisibility() { if (!document.hidden) refresh() }

onMounted(() => {
  runtime.start()
  refresh()
  timer = window.setInterval(() => { if (!document.hidden) refresh() }, 30000)
  document.addEventListener('visibilitychange', onVisibility)
  unsubs.push(onRealtime('OnAlarm', scheduleRefresh))
  unsubs.push(onRealtime('OnMeasurement', scheduleRefresh))
})
onUnmounted(() => {
  disposed = true
  if (timer) clearInterval(timer)
  if (debounce) clearTimeout(debounce)
  document.removeEventListener('visibilitychange', onVisibility)
  unsubs.forEach((u) => u())
  runtime.stop()
})

// ── 趋势主图（唯一趋势可视化，去掉 KPI 里重复的 sparkline） ──
const hasTrend = computed(() => trend.value.length > 1)
const trendOption = computed<Record<string, unknown>>(() => {
  const pts = trend.value
  const avg = pts.map((p) => [new Date(p.ts).getTime(), p.avgRatio == null ? null : +(p.avgRatio * 100).toFixed(1)])
  const min = pts.map((p) => [new Date(p.ts).getTime(), p.minRatio == null ? null : +(p.minRatio * 100).toFixed(1)])
  const nums: number[] = []
  for (const p of pts) { if (p.avgRatio != null) nums.push(p.avgRatio * 100); if (p.minRatio != null) nums.push(p.minRatio * 100) }
  const lo = nums.length ? Math.min(...nums) : 70
  const yMin = Math.max(0, Math.floor((Math.min(lo, 90) - 4) / 5) * 5)
  return {
    grid: { left: 44, right: 16, top: 30, bottom: 26 },
    legend: { data: ['平均保持率', '最低保持率'], top: 2, right: 6, icon: 'roundRect', itemWidth: 14, itemHeight: 4, textStyle: { color: '#9fb2cc', fontSize: 11 }, inactiveColor: '#3b4659' },
    tooltip: {
      trigger: 'axis', backgroundColor: '#0d1320', borderColor: 'rgba(56,189,248,0.25)', borderWidth: 1,
      textStyle: { color: '#e8eef8', fontSize: 12 },
      axisPointer: { type: 'line', lineStyle: { color: 'rgba(148,163,184,0.3)' } },
      valueFormatter: (v: number) => (v == null ? '—' : v.toFixed(1) + ' %'),
    },
    xAxis: {
      type: 'time',
      axisLine: { lineStyle: { color: 'rgba(148,163,184,0.18)' } },
      axisLabel: { color: '#61748f', fontSize: 11, hideOverlap: true },
      axisTick: { show: false }, splitLine: { show: false },
    },
    yAxis: {
      type: 'value', min: yMin, max: 105, interval: 5,
      name: '保持率 %', nameTextStyle: { color: '#61748f', fontSize: 11, padding: [0, 0, 0, 18] },
      axisLabel: { color: '#61748f', fontSize: 11, formatter: '{value}' },
      splitLine: { lineStyle: { color: 'rgba(148,163,184,0.06)' } },
    },
    series: [
      {
        name: '平均保持率', type: 'line', data: avg, smooth: true, symbol: 'none', connectNulls: true,
        lineStyle: { color: '#22d3ee', width: 2 },
        areaStyle: { color: { type: 'linear', x: 0, y: 0, x2: 0, y2: 1, colorStops: [{ offset: 0, color: 'rgba(34,211,238,0.22)' }, { offset: 1, color: 'rgba(34,211,238,0.01)' }] } },
        z: 3,
      },
      {
        name: '最低保持率', type: 'line', data: min, smooth: true, symbol: 'none', connectNulls: true,
        lineStyle: { color: '#f59e0b', width: 1.5 }, z: 2,
        markLine: {
          silent: true, symbol: 'none',
          data: [
            { yAxis: 100, lineStyle: { color: '#10b981', type: 'dashed', width: 1 }, label: { formatter: '目标 100%', color: '#10b981', fontSize: 10, position: 'insideStartTop' } },
            { yAxis: 90, lineStyle: { color: 'rgba(245,158,11,0.7)', type: 'dashed', width: 1 }, label: { formatter: '预警 90%', color: '#f59e0b', fontSize: 10, position: 'insideStartBottom' } },
          ],
        },
      },
    ],
    animation: false,
  }
})

// ── 明细区（可折叠）：健康分布 donut + 保持率分布 + 排行 + 按部位 ──
const healthColor = computed(() => {
  const p = measuredHealthPct.value
  return p == null ? 'var(--text-2)' : p >= 90 ? 'var(--ok)' : p >= 70 ? 'var(--warn)' : 'var(--crit)'
})
const healthPct = computed<Record<string, number>>(() => {
  const parts = pctParts(ORDER.map((k) => health.value[k]), health.value.total)
  return Object.fromEntries(ORDER.map((k, i) => [k, parts[i]]))
})
const donutOption = computed(() => {
  const data = ORDER.filter((k) => health.value[k] > 0).map((k) => ({ name: STATUS[k].label, value: health.value[k], itemStyle: { color: STATUS[k].color } }))
  return {
    tooltip: { trigger: 'item', backgroundColor: '#0d1320', borderColor: 'rgba(56,189,248,0.25)', textStyle: { color: '#e8eef8' } },
    series: [{ type: 'pie', radius: ['66%', '88%'], center: ['50%', '50%'], label: { show: false }, labelLine: { show: false }, data: data.length ? data : [{ name: '无数据', value: 1, itemStyle: { color: '#1b2333' } }] }],
  }
})

const distBuckets = computed(() => {
  const r = residualDist.value
  const items = [
    { key: 'b100', label: '≥100% 超张力', count: r.b100, color: '#a78bfa' },
    { key: 'b90', label: '90–100%', count: r.b90, color: '#10b981' },
    { key: 'b80', label: '80–90%', count: r.b80, color: '#f59e0b' },
    { key: 'b70', label: '70–80%', count: r.b70, color: '#fb923c' },
    { key: 'b70d', label: '<70%', count: r.b70d, color: '#f43f5e' },
  ]
  return { items, total: items.reduce((a, x) => a + x.count, 0) }
})
function pct(part: number, total: number) { return total ? (part / total) * 100 : 0 }
function ratioPctStr(r: number | null) { return r == null ? '—' : (r * 100).toFixed(0) + '%' }
function daysColor(d: number | null) { return d == null ? 'var(--text-2)' : d < 7 ? 'var(--crit)' : d < 30 ? 'var(--warn)' : 'var(--ok)' }
function fmtDays(d: number | null) { return d == null ? '—' : d < 1 ? '<1 天' : Math.round(d) + ' 天' }
function openBolt(b: { boltId: number }) { router.push({ name: 'boltDetail', params: { boltId: b.boltId } }) }
</script>

<template>
  <div class="dash rise" v-loading="firstLoad" element-loading-text="加载中…">
    <div v-if="error" class="dash-error">
      <el-icon><WarningFilled /></el-icon>
      <span class="dash-error-msg">加载失败：{{ error }}</span>
      <el-button size="small" type="danger" plain @click="refresh">重试</el-button>
    </div>

    <!-- ① 单一安全结论条：一句话 + 主色 + 在测/待接入构成 -->
    <div class="verdict panel" :class="`verdict--${verdict.level}`">
      <div class="vd-left">
        <span class="vd-dot" :style="`background:${verdictColor}`" />
        <div class="vd-text">
          <div class="vd-headline" :style="`color:${verdictColor}`">{{ verdict.text }}</div>
          <div class="vd-sub">共 {{ health.total }} 颗 · 在测 {{ measured }} · 待接入 {{ pendingSetup }}（未标定 {{ health.uncalibrated }} / 无数据 {{ health.nodata }}）</div>
        </div>
      </div>
      <div class="vd-chips">
        <div class="vd-chip">
          <b class="metric" :style="`color:${healthColor}`">{{ measuredHealthPct == null ? '—' : measuredHealthPct }}<i v-if="measuredHealthPct != null">%</i></b>
          <span>在测健康率</span>
        </div>
        <div v-if="overTension > 0" class="vd-chip">
          <b class="metric" style="color:#a78bfa">{{ overTension }}</b>
          <span>偏高(&gt;110%)</span>
        </div>
      </div>
    </div>

    <!-- ② 待处置聚焦：可点入口，紧急高亮、无事淡化 -->
    <div class="actions">
      <div
        v-for="a in actionTiles" :key="a.key"
        class="act panel" :class="[`act--${a.level}`, { 'act--zero': a.count === 0 }]"
        role="button" tabindex="0"
        @click="goTile(a.to)" @keydown.enter="goTile(a.to)" @keydown.space.prevent="goTile(a.to)"
      >
        <div class="act-hd"><span class="act-label">{{ a.label }}</span><el-icon class="act-go"><ArrowRightBold /></el-icon></div>
        <div class="act-val metric">{{ a.count }}</div>
        <div class="act-sub">{{ a.sub }}</div>
      </div>
    </div>

    <!-- ③ 趋势主图（唯一趋势可视化） -->
    <div class="panel panel--glow trend">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark" />预紧力保持率趋势</span>
        <span class="hd-sub">近 24 小时 · 平均 / 最低</span>
      </div>
      <div class="trend-body">
        <EChart v-if="hasTrend" :option="trendOption" height="260px" />
        <div v-else-if="loaded" class="trend-empty">暂无足够时序数据</div>
      </div>
    </div>

    <!-- ④ 明细（默认折叠，需要时展开）：健康分布 / 保持率分布 / 排行 / 按部位 -->
    <details class="more panel" :open="false">
      <summary class="more-summary">
        <span class="panel-title"><i class="hd-mark" />健康明细</span>
        <span class="more-hint">健康分布 · 保持率分布 · 松动排行 · 即将到限 · 按部位（点击展开）</span>
      </summary>

      <div class="more-body">
        <!-- 健康分布 + 保持率分布 -->
        <div class="more-row">
          <div class="mb-card">
            <div class="mb-title">螺栓预紧力健康分布</div>
            <div class="health-body">
              <div class="donut-wrap">
                <EChart :option="donutOption" height="180px" />
                <div class="donut-center">
                  <b class="metric" :style="`color:${healthColor}`">{{ measuredHealthPct == null ? '—' : measuredHealthPct }}<i v-if="measuredHealthPct != null">%</i></b>
                  <span>在测健康率</span>
                </div>
              </div>
              <div class="legend">
                <div v-for="k in ORDER" :key="k" class="legend-item">
                  <span class="led" :class="STATUS[k].cls" />
                  <span class="lg-label">{{ STATUS[k].label }}</span>
                  <span class="lg-val mono">{{ health[k] }}</span>
                  <span class="lg-bar"><i :style="`width:${healthPct[k]}%;background:${STATUS[k].color}`" /></span>
                  <span class="lg-pct mono">{{ healthPct[k] }}%</span>
                </div>
              </div>
            </div>
          </div>

          <div class="mb-card">
            <div class="mb-title">保持率分布 <span class="mb-sub mono">{{ distBuckets.total }} 颗在测</span></div>
            <div class="dist-body">
              <div v-for="b in distBuckets.items" :key="b.key" class="dist-item">
                <span class="dist-label">{{ b.label }}</span>
                <div class="dist-track"><div class="dist-fill" :style="`width:${distBuckets.total ? (b.count / distBuckets.total) * 100 : 0}%;background:${b.color}`" /></div>
                <span class="dist-count mono" :style="`color:${b.color}`">{{ b.count }}</span>
              </div>
              <div v-if="loaded && !distBuckets.total" class="dist-empty">暂无在测螺栓</div>
            </div>
          </div>
        </div>

        <!-- 松动排行 + 即将到限 -->
        <div class="more-row">
          <div class="mb-card">
            <div class="mb-title">松动 / 预警排行 <router-link :to="{ name: 'assets' }" class="hd-link">资产监测 →</router-link></div>
            <ul class="data-list">
              <li v-for="b in topLoose" :key="b.boltId" role="button" tabindex="0" @click="openBolt(b)" @keydown.enter="openBolt(b)" @keydown.space.prevent="openBolt(b)">
                <span class="led" :class="STATUS[b.status]?.cls || 'led--off'" />
                <div class="li-main"><div class="li-code">{{ b.boltCode }}</div><div class="li-loc">{{ b.turbine }} · {{ b.group }}</div></div>
                <div class="li-right"><span class="li-metric metric" :style="`color:${STATUS[b.status]?.color}`">{{ ratioPctStr(b.preloadRatio) }}</span><span class="li-tag">{{ STATUS[b.status]?.label }}</span></div>
              </li>
              <li v-if="loaded && !topLoose.length" class="list-empty"><span class="led led--ok" /> 无松动 / 预警</li>
            </ul>
          </div>

          <div class="mb-card">
            <div class="mb-title">即将到限 <span class="mb-sub">近 7 天回归</span></div>
            <ul class="data-list">
              <li v-for="b in approaching" :key="b.boltId" role="button" tabindex="0" @click="openBolt(b)" @keydown.enter="openBolt(b)" @keydown.space.prevent="openBolt(b)">
                <div class="li-main"><div class="li-code">{{ b.boltCode }}</div><div class="li-loc">{{ b.turbine }} · {{ b.group }}</div></div>
                <div class="appr-rate mono">{{ b.ratePerDay != null ? '↓' + Math.abs(Math.round(b.ratePerDay)) + ' N/天' : '—' }}<span v-if="b.r2 != null" class="appr-r2">R² {{ b.r2.toFixed(2) }}</span></div>
                <div class="appr-days" :style="`color:${daysColor(b.daysToLimit)}`"><b>{{ fmtDays(b.daysToLimit) }}</b><span>到下限</span></div>
              </li>
              <li v-if="loaded && !approaching.length" class="list-empty"><span class="led led--ok" /> 无下降趋势螺栓</li>
            </ul>
          </div>
        </div>

        <!-- 按部位健康 -->
        <div class="mb-card">
          <div class="mb-title">按部位健康</div>
          <div class="bg-list">
            <div v-for="g in byGroup" :key="`${g.turbine}-${g.group}`" class="bg-row">
              <div class="bg-name"><span class="bg-turbine">{{ g.turbine }}</span><span class="bg-group">{{ g.group }}</span></div>
              <div class="bg-bar"><span v-for="k in ORDER" :key="k" v-show="g.health[k] > 0" class="bg-seg" :style="`width:${pct(g.health[k], g.health.total)}%;background:${STATUS[k].color}`" :title="`${STATUS[k].label} ${g.health[k]}`" /></div>
              <div class="bg-total mono">{{ g.health.total }} 颗</div>
            </div>
            <div v-if="loaded && !byGroup.length" class="bg-empty">暂无部位数据</div>
          </div>
        </div>
      </div>
    </details>

    <!-- ⑤ 系统状态（下沉，紧凑一条） -->
    <div class="panel sys-row">
      <span class="panel-title"><i class="hd-mark hd-mark--ok" />系统状态</span>
      <div class="sys-chips">
        <div class="sys-chip"><span class="led" :class="sys.online > 0 ? 'led--ok' : 'led--off'" /> 设备在线 <b class="mono">{{ sys.online }}/{{ sys.total }}</b></div>
        <div class="sys-chip"><el-icon class="sys-ic"><Odometer /></el-icon> 采集速率 <b class="mono glow-accent">{{ sys.rate }}</b> 条/秒</div>
        <div class="sys-chip" :class="{ warn: sys.walPending > 0 }"><el-icon class="sys-ic"><RefreshRight /></el-icon> 断网续传 <b class="mono">{{ sys.walPending > 0 ? sys.walPending + ' 条待补传' : '正常' }}</b></div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.dash { display: flex; flex-direction: column; gap: 14px; }

.dash-error { display: flex; align-items: center; gap: 12px; padding: 12px 16px; border-radius: var(--radius-sm); background: rgba(244, 63, 94, 0.08); border: 1px solid var(--crit); color: var(--text-1); font-size: 13px; }
.dash-error .el-icon { color: var(--crit); font-size: 18px; }
.dash-error-msg { flex: 1; min-width: 0; }

.panel-hd { display: flex; align-items: center; justify-content: space-between; padding: 13px 18px 9px; }
.panel-title { display: inline-flex; align-items: center; font-size: 12px; font-weight: 600; letter-spacing: 0.1em; text-transform: uppercase; color: var(--text-2); }
.hd-mark { width: 3px; height: 12px; border-radius: 2px; margin-right: 9px; background: var(--accent); }
.hd-mark--ok { background: var(--ok); }
.hd-link { font-size: 12px; }
.hd-sub { font-size: 11.5px; color: var(--text-3); }

/* ① 结论条 */
.verdict { display: flex; align-items: center; justify-content: space-between; gap: 18px; padding: 16px 20px; border-left: 3px solid var(--text-3); }
.verdict--ok { border-left-color: var(--ok); }
.verdict--warn { border-left-color: var(--warn); }
.verdict--crit { border-left-color: var(--crit); background: rgba(244, 63, 94, 0.05); }
.vd-left { display: flex; align-items: center; gap: 14px; min-width: 0; }
.vd-dot { width: 12px; height: 12px; border-radius: 50%; flex-shrink: 0; box-shadow: 0 0 12px currentColor; }
.vd-headline { font-size: 20px; font-weight: 700; line-height: 1.25; }
.vd-sub { font-size: 12px; color: var(--text-3); margin-top: 3px; }
.vd-chips { display: flex; align-items: center; gap: 26px; flex-shrink: 0; }
.vd-chip { display: flex; flex-direction: column; align-items: flex-end; gap: 2px; }
.vd-chip b { font-size: 30px; font-weight: 700; line-height: 1; display: inline-flex; align-items: baseline; }
.vd-chip b i { font-size: 15px; font-style: normal; margin-left: 2px; opacity: 0.7; }
.vd-chip span { font-size: 11px; color: var(--text-3); letter-spacing: 0.06em; }

/* ② 待处置 */
.actions { display: grid; grid-template-columns: repeat(5, 1fr); gap: 12px; }
.act { padding: 13px 16px 12px; display: flex; flex-direction: column; gap: 6px; cursor: pointer; transition: border-color 0.16s, transform 0.16s; min-width: 0; }
.act:hover { transform: translateY(-1px); border-color: var(--panel-border-strong); }
.act:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.act-hd { display: flex; align-items: center; justify-content: space-between; }
.act-label { font-size: 11px; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; color: var(--text-3); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.act-go { font-size: 12px; color: var(--text-3); transition: transform 0.16s; }
.act:hover .act-go { transform: translateX(2px); color: var(--accent); }
.act-val { font-size: 28px; font-weight: 700; line-height: 1; }
.act-sub { font-size: 11px; color: var(--text-3); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.act--crit { border-color: rgba(244, 63, 94, 0.4); }
.act--crit .act-val { color: var(--crit); }
.act--warn { border-color: rgba(245, 158, 11, 0.35); }
.act--warn .act-val { color: var(--warn); }
.act--info .act-val { color: var(--accent); }
.act--zero { opacity: 0.62; }
.act--zero:hover { opacity: 1; }

/* ③ 趋势 */
.trend-body { padding: 2px 12px 12px; }
.trend-empty { color: var(--text-3); font-size: 13px; text-align: center; padding: 80px 0; }

/* ④ 明细折叠区 */
.more { padding: 0; }
.more-summary { display: flex; align-items: center; gap: 14px; padding: 13px 18px; cursor: pointer; list-style: none; user-select: none; }
.more-summary::-webkit-details-marker { display: none; }
.more-summary::after { content: '▸'; margin-left: auto; color: var(--text-3); font-size: 12px; transition: transform 0.16s; }
.more[open] .more-summary::after { transform: rotate(90deg); }
.more-hint { font-size: 11.5px; color: var(--text-3); }
.more-body { padding: 4px 16px 18px; display: flex; flex-direction: column; gap: 14px; }
.more-row { display: grid; grid-template-columns: 1.4fr 1fr; gap: 14px; }
.mb-card { background: rgba(148, 163, 184, 0.03); border: 1px solid var(--hairline); border-radius: var(--radius-sm); padding: 12px 14px; }
.mb-title { font-size: 12px; font-weight: 600; color: var(--text-2); display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-bottom: 8px; }
.mb-sub { font-size: 11px; color: var(--text-3); font-weight: 400; }

.health-body { display: flex; align-items: center; gap: 16px; }
.donut-wrap { position: relative; width: 180px; flex-shrink: 0; }
.donut-center { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; pointer-events: none; }
.donut-center b { font-size: 30px; line-height: 1; display: inline-flex; align-items: baseline; }
.donut-center b i { font-size: 14px; font-style: normal; margin-left: 2px; opacity: 0.65; }
.donut-center span { font-size: 10px; color: var(--text-3); letter-spacing: 0.1em; margin-top: 4px; }
.legend { flex: 1; display: flex; flex-direction: column; }
.legend-item { display: grid; grid-template-columns: 12px minmax(0, 1fr) 30px 58px 40px; align-items: center; gap: 10px; padding: 6px 2px; border-bottom: 1px solid var(--hairline); }
.legend-item:last-child { border-bottom: none; }
.lg-label { font-size: 13px; color: var(--text-2); }
.lg-val { font-size: 14px; font-weight: 600; color: var(--text-1); text-align: right; }
.lg-bar { height: 5px; border-radius: 3px; background: rgba(148, 163, 184, 0.1); overflow: hidden; }
.lg-bar i { display: block; height: 100%; border-radius: 3px; transition: width 0.5s ease; }
.lg-pct { font-size: 12px; color: var(--text-3); text-align: right; }

.dist-body { display: flex; flex-direction: column; gap: 11px; padding-top: 2px; }
.dist-item { display: flex; align-items: center; gap: 12px; }
.dist-label { width: 92px; font-size: 12px; color: var(--text-2); font-family: var(--font-mono); flex-shrink: 0; }
.dist-track { flex: 1; height: 13px; border-radius: 7px; background: rgba(148, 163, 184, 0.07); overflow: hidden; }
.dist-fill { height: 100%; border-radius: 7px; min-width: 3px; transition: width 0.5s ease; }
.dist-count { width: 26px; text-align: right; font-size: 14px; font-weight: 700; flex-shrink: 0; }
.dist-empty { color: var(--text-3); font-size: 13px; padding: 12px 0; text-align: center; }

.data-list { list-style: none; margin: 0; padding: 0; }
.data-list li { display: flex; align-items: center; gap: 12px; padding: 8px 6px; cursor: pointer; border-radius: 8px; transition: background 0.14s; }
.data-list li:not(.list-empty) { box-shadow: inset 0 -1px 0 var(--hairline); }
.data-list li:not(.list-empty):hover { background: rgba(56, 189, 248, 0.06); }
.data-list li:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.li-main { flex: 1; min-width: 0; }
.li-code { font-size: 13px; font-weight: 600; }
.li-loc { font-size: 11px; color: var(--text-3); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.li-right { display: flex; flex-direction: column; align-items: flex-end; gap: 1px; }
.li-metric { font-size: 16px; font-weight: 700; }
.li-tag { font-size: 10px; color: var(--text-3); }
.list-empty { color: var(--text-3); font-size: 13px; gap: 8px; cursor: default !important; }
.list-empty:hover { background: none !important; }
.appr-rate { font-size: 12px; color: var(--crit); white-space: nowrap; display: flex; flex-direction: column; align-items: flex-end; gap: 1px; }
.appr-r2 { font-size: 10px; color: var(--text-3); }
.appr-days { display: flex; flex-direction: column; align-items: flex-end; line-height: 1.2; min-width: 54px; }
.appr-days b { font-size: 16px; font-weight: 700; }
.appr-days span { font-size: 10px; color: var(--text-3); }

.bg-list { display: flex; flex-direction: column; gap: 12px; padding-top: 2px; }
.bg-row { display: flex; align-items: center; gap: 16px; }
.bg-name { width: 200px; flex-shrink: 0; display: flex; flex-direction: column; gap: 2px; }
.bg-turbine { font-size: 13px; font-weight: 600; }
.bg-group { font-size: 11px; color: var(--text-3); }
.bg-bar { flex: 1; height: 15px; border-radius: 7px; overflow: hidden; display: flex; background: rgba(148, 163, 184, 0.07); }
.bg-seg { height: 100%; transition: width 0.4s ease; }
.bg-seg:first-child { border-radius: 7px 0 0 7px; }
.bg-seg:last-child { border-radius: 0 7px 7px 0; }
.bg-total { width: 56px; text-align: right; font-size: 12px; color: var(--text-2); flex-shrink: 0; }
.bg-empty { color: var(--text-3); font-size: 13px; padding: 10px 0; }

/* ⑤ 系统状态 */
.sys-row { display: flex; align-items: center; gap: 22px; padding: 13px 18px; flex-wrap: wrap; }
.sys-chips { display: flex; align-items: center; gap: 22px; flex-wrap: wrap; }
.sys-chip { display: flex; align-items: center; gap: 8px; font-size: 13px; color: var(--text-3); }
.sys-ic { font-size: 15px; color: var(--text-3); }
.sys-chip b { color: var(--text-1); }
.sys-chip.warn b { color: var(--warn); }

@media (max-width: 1200px) { .actions { grid-template-columns: repeat(3, 1fr); } }
@media (max-width: 1100px) { .more-row { grid-template-columns: 1fr; } .verdict { flex-direction: column; align-items: flex-start; gap: 12px; } .vd-chips { align-self: stretch; justify-content: space-between; } }
@media (max-width: 760px) { .actions { grid-template-columns: repeat(2, 1fr); } }
</style>
