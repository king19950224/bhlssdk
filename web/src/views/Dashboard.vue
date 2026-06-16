<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import EChart from '../components/EChart.vue'
import { dashboardApi, runtimeApi, type PreloadHealth, type LooseBolt, type ResidualDist, type ApproachingBolt } from '../api'

const router = useRouter()

const health = ref<PreloadHealth>({ total: 0, normal: 0, warn: 0, loose: 0, uncalibrated: 0, nodata: 0 })
const byGroup = ref<{ turbine: string; group: string; health: PreloadHealth }[]>([])
const topLoose = ref<LooseBolt[]>([])
const residualDist = ref<ResidualDist>({ b100: 0, b90: 0, b80: 0, b70: 0, b70d: 0 })
const approaching = ref<ApproachingBolt[]>([])
const openAlarms = ref(0)
const alarmsBySeverity = ref<{ severity: string; count: number }[]>([])
const loaded = ref(false)

const SEV_LABEL: Record<string, string> = { critical: '严重', crit: '严重', warn: '预警', warning: '预警', info: '提示' }
const alarmBreakdown = computed(() =>
  alarmsBySeverity.value.filter((s) => s.count > 0).map((s) => `${SEV_LABEL[s.severity] || s.severity} ${s.count}`).join(' · '))

const sys = ref({ online: 0, total: 0, walPending: 0, rate: 0 })
let lastTotal = 0
let lastTs = 0
let timer: number | undefined

const STATUS: Record<string, { label: string; color: string; cls: string }> = {
  normal: { label: '正常', color: '#10b981', cls: 'led--ok' },
  warn: { label: '预警', color: '#f59e0b', cls: 'led--warn' },
  loose: { label: '松动', color: '#f43f5e', cls: 'led--crit' },
  uncalibrated: { label: '未标定', color: '#61748f', cls: 'led--off' },
  nodata: { label: '无数据', color: '#3b4659', cls: 'led--off' },
}
const ORDER = ['normal', 'warn', 'loose', 'uncalibrated', 'nodata'] as const

const calibrated = computed(() => health.value.total - health.value.uncalibrated)
const calibratedPct = computed(() => (health.value.total ? Math.round((calibrated.value / health.value.total) * 100) : 0))
const needAttention = computed(() => health.value.warn + health.value.loose)

async function refresh() {
  try {
    const d = await dashboardApi.preloadHealth()
    health.value = d.global
    byGroup.value = d.byGroup
    topLoose.value = d.topLoose
    residualDist.value = d.residualDist
    approaching.value = d.approachingLimit
    openAlarms.value = d.openAlarms
    alarmsBySeverity.value = d.alarmsBySeverity ?? []

    const rt = await runtimeApi.devices()
    const now = Date.now()
    const total = rt.items.reduce((a, x) => a + (x.channelResultCount ?? 0), 0)
    sys.value = {
      online: rt.items.filter((x) => x.state === 'Connected').length,
      total: rt.count,
      walPending: rt.wal?.pending ?? 0,
      rate: lastTs ? Math.max(0, Math.round(((total - lastTotal) / ((now - lastTs) / 1000)) * 10) / 10) : 0,
    }
    lastTotal = total
    lastTs = now
  } catch {
    /* 尽力刷新 */
  } finally {
    loaded.value = true
  }
}
onMounted(() => {
  refresh()
  timer = window.setInterval(refresh, 5000)
})
onUnmounted(() => timer && clearInterval(timer))

const donutOption = computed(() => {
  const data = ORDER.filter((k) => health.value[k] > 0).map((k) => ({
    name: STATUS[k].label,
    value: health.value[k],
    itemStyle: { color: STATUS[k].color },
  }))
  return {
    tooltip: { trigger: 'item', backgroundColor: '#0d1320', borderColor: 'rgba(56,189,248,0.3)', textStyle: { color: '#e8eef8' } },
    series: [
      {
        type: 'pie',
        radius: ['64%', '86%'],
        center: ['50%', '50%'],
        label: { show: false },
        labelLine: { show: false },
        data: data.length ? data : [{ name: '无数据', value: 1, itemStyle: { color: '#1b2333' } }],
      },
    ],
  }
})

const distBuckets = computed(() => {
  const r = residualDist.value
  const items = [
    { key: 'b100', label: '≥100%', count: r.b100, color: '#10b981' },
    { key: 'b90', label: '90–100%', count: r.b90, color: '#22d3ee' },
    { key: 'b80', label: '80–90%', count: r.b80, color: '#f59e0b' },
    { key: 'b70', label: '70–80%', count: r.b70, color: '#fb923c' },
    { key: 'b70d', label: '<70%', count: r.b70d, color: '#f43f5e' },
  ]
  return { items, total: items.reduce((a, x) => a + x.count, 0) }
})
function pct(part: number, total: number) {
  return total ? (part / total) * 100 : 0
}
function ratioPctStr(r: number | null) {
  return r == null ? '—' : (r * 100).toFixed(0) + '%'
}
function daysColor(d: number | null) {
  if (d == null) return 'var(--text-2)'
  if (d < 7) return 'var(--crit)'
  if (d < 30) return 'var(--warn)'
  return 'var(--ok)'
}
function fmtDays(d: number | null) {
  if (d == null) return '—'
  return d < 1 ? '<1 天' : Math.round(d) + ' 天'
}
function openBolt(b: { boltId: number }) {
  router.push({ name: 'boltDetail', params: { boltId: b.boltId } })
}
</script>

<template>
  <div class="dash rise">
    <!-- KPI -->
    <div class="kpi-row">
      <div class="kpi panel">
        <div class="kpi-top"><span class="panel-title">监测螺栓</span><el-icon class="kpi-ic"><Histogram /></el-icon></div>
        <div class="kpi-val metric">{{ health.total }}</div>
        <div class="kpi-foot">已标定 {{ calibrated }} · {{ calibratedPct }}%</div>
      </div>
      <div class="kpi panel">
        <div class="kpi-top"><span class="panel-title">正常</span><span class="led led--ok" /></div>
        <div class="kpi-val metric" style="color: var(--ok)">{{ health.normal }}</div>
        <div class="kpi-foot">预紧力在安全区间</div>
      </div>
      <div class="kpi panel" :class="{ 'kpi--alert': health.loose > 0 }">
        <div class="kpi-top"><span class="panel-title">需关注</span><span class="led" :class="health.loose > 0 ? 'led--crit' : 'led--warn'" /></div>
        <div class="kpi-val metric" :style="needAttention > 0 ? `color:${health.loose > 0 ? 'var(--crit)' : 'var(--warn)'}` : ''">{{ needAttention }}</div>
        <div class="kpi-foot">预警 {{ health.warn }} · 松动 {{ health.loose }}</div>
      </div>
      <div class="kpi panel kpi--click" :class="{ 'kpi--alert': openAlarms > 0 }" @click="router.push({ name: 'alarms' })" title="查看告警中心">
        <div class="kpi-top"><span class="panel-title">未闭合告警</span><el-icon class="kpi-ic"><BellFilled /></el-icon></div>
        <div class="kpi-val metric" :style="openAlarms > 0 ? 'color:var(--crit)' : ''">{{ openAlarms }}</div>
        <div class="kpi-foot">{{ openAlarms > 0 ? (alarmBreakdown || '需运维处置') : '运行正常' }}</div>
      </div>
    </div>

    <!-- 主区：健康分布 + 松动 Top -->
    <div class="main-row">
      <div class="panel panel--glow health">
        <div class="panel-hd"><span class="panel-title">螺栓预紧力健康分布</span></div>
        <div class="health-body">
          <div class="donut-wrap">
            <EChart :option="donutOption" height="220px" />
            <div class="donut-center">
              <b class="metric">{{ health.total }}</b>
              <span>监测螺栓</span>
            </div>
          </div>
          <div class="legend">
            <div v-for="k in ORDER" :key="k" class="legend-item">
              <span class="led" :class="STATUS[k].cls" />
              <span class="lg-label">{{ STATUS[k].label }}</span>
              <span class="lg-val mono">{{ health[k] }}</span>
              <span class="lg-pct mono">{{ pct(health[k], health.total).toFixed(0) }}%</span>
            </div>
          </div>
        </div>
      </div>

      <div class="panel panel--glow loose">
        <div class="panel-hd">
          <span class="panel-title">松动 / 预警 Top</span>
          <router-link :to="{ name: 'assets' }" class="hd-link">资产监测 →</router-link>
        </div>
        <ul class="loose-list">
          <li v-for="b in topLoose" :key="b.boltId" @click="openBolt(b)">
            <span class="led" :class="STATUS[b.status]?.cls || 'led--off'" />
            <div class="lb-main">
              <div class="lb-code">{{ b.boltCode }}</div>
              <div class="lb-loc">{{ b.turbine }} · {{ b.group }}</div>
            </div>
            <div class="lb-right">
              <span class="lb-ratio metric" :style="`color:${STATUS[b.status]?.color}`">{{ ratioPctStr(b.preloadRatio) }}</span>
              <span class="lb-tag">{{ STATUS[b.status]?.label }}</span>
            </div>
          </li>
          <li v-if="loaded && !topLoose.length" class="loose-empty">
            <span class="led led--ok" /> 无松动 / 预警,预紧力健康
          </li>
        </ul>
      </div>
    </div>

    <!-- 残余率分布 + 即将到限 -->
    <div class="dist-row">
      <div class="panel panel--glow">
        <div class="panel-hd">
          <span class="panel-title">残余预紧力率分布</span>
          <span class="hd-sub mono">{{ distBuckets.total }} 颗在测</span>
        </div>
        <div class="dist-body">
          <div v-for="b in distBuckets.items" :key="b.key" class="dist-item">
            <span class="dist-label">{{ b.label }}</span>
            <div class="dist-track">
              <div class="dist-fill" :style="`width:${distBuckets.total ? (b.count / distBuckets.total) * 100 : 0}%;background:${b.color}`" />
            </div>
            <span class="dist-count mono" :style="`color:${b.color}`">{{ b.count }}</span>
          </div>
          <div v-if="loaded && !distBuckets.total" class="dist-empty">暂无在测螺栓</div>
        </div>
      </div>

      <div class="panel panel--glow">
        <div class="panel-hd">
          <span class="panel-title">即将到限</span>
          <span class="hd-sub">近 7 天回归 · 预计触及下限</span>
        </div>
        <ul class="appr-list">
          <li v-for="b in approaching" :key="b.boltId" @click="openBolt(b)">
            <div class="appr-main">
              <div class="appr-code">{{ b.boltCode }}</div>
              <div class="appr-loc">{{ b.turbine }} · {{ b.group }}</div>
            </div>
            <div class="appr-rate mono">
              {{ b.ratePerDay != null ? Math.round(b.ratePerDay) + ' N/天' : '—' }}
              <span v-if="b.r2 != null" class="appr-r2">R² {{ b.r2.toFixed(2) }}</span>
            </div>
            <div class="appr-days" :style="`color:${daysColor(b.daysToLimit)}`">
              <b>{{ fmtDays(b.daysToLimit) }}</b>
              <span>到下限</span>
            </div>
          </li>
          <li v-if="loaded && !approaching.length" class="appr-empty"><span class="led led--ok" /> 无下降趋势螺栓,预紧力稳定</li>
        </ul>
      </div>
    </div>

    <!-- 按部位健康 -->
    <div class="panel panel--glow bygroup">
      <div class="panel-hd"><span class="panel-title">按部位健康</span></div>
      <div class="bg-list">
        <div v-for="(g, i) in byGroup" :key="i" class="bg-row">
          <div class="bg-name">
            <span class="bg-turbine">{{ g.turbine }}</span>
            <span class="bg-group">{{ g.group }}</span>
          </div>
          <div class="bg-bar">
            <span
              v-for="k in ORDER"
              :key="k"
              v-show="g.health[k] > 0"
              class="bg-seg"
              :style="`width:${pct(g.health[k], g.health.total)}%;background:${STATUS[k].color}`"
              :title="`${STATUS[k].label} ${g.health[k]}`"
            />
          </div>
          <div class="bg-total mono">{{ g.health.total }} 颗</div>
        </div>
        <div v-if="loaded && !byGroup.length" class="bg-empty">暂无部位数据</div>
      </div>
    </div>

    <!-- 系统状态（次要） -->
    <div class="sys-row">
      <span class="panel-title">系统状态</span>
      <div class="sys-chip"><span class="led" :class="sys.online > 0 ? 'led--ok' : 'led--off'" /> 设备在线 <b class="mono">{{ sys.online }}/{{ sys.total }}</b></div>
      <div class="sys-chip">采集速率 <b class="mono glow-accent">{{ sys.rate }}</b> 条/秒</div>
      <div class="sys-chip" :class="{ warn: sys.walPending > 0 }">WAL 兜底 <b class="mono">{{ sys.walPending > 0 ? sys.walPending + ' 待重放' : '正常' }}</b></div>
    </div>
  </div>
</template>

<style scoped>
.dash { display: flex; flex-direction: column; gap: 18px; }

.kpi-row { display: grid; grid-template-columns: repeat(4, 1fr); gap: 16px; }
.kpi { padding: 18px 20px; }
.kpi-top { display: flex; align-items: center; justify-content: space-between; }
.kpi-ic { font-size: 18px; color: var(--accent); opacity: 0.8; }
.kpi-val { font-size: 38px; font-weight: 700; line-height: 1.1; margin: 12px 0 8px; }
.kpi-foot { font-size: 12px; color: var(--text-3); }
.kpi--alert { border-color: var(--crit); box-shadow: 0 0 26px -10px var(--crit-glow); }
.kpi--click { cursor: pointer; transition: border-color 0.18s; }
.kpi--click:hover { border-color: var(--accent); }

.main-row { display: grid; grid-template-columns: 1.5fr 1fr; gap: 16px; }
.panel-hd { display: flex; align-items: center; justify-content: space-between; padding: 16px 20px 8px; }
.hd-link { font-size: 12px; }

.health-body { display: flex; align-items: center; gap: 10px; padding: 0 20px 20px; }
.donut-wrap { position: relative; width: 240px; flex-shrink: 0; }
.donut-center { position: absolute; inset: 0; display: grid; place-items: center; pointer-events: none; }
.donut-center b { font-size: 34px; line-height: 1; }
.donut-center span { font-size: 11px; color: var(--text-3); }
.legend { flex: 1; display: flex; flex-direction: column; gap: 4px; }
.legend-item { display: flex; align-items: center; gap: 10px; padding: 8px 6px; border-bottom: 1px solid var(--hairline); }
.lg-label { flex: 1; font-size: 13px; color: var(--text-2); }
.lg-val { font-size: 15px; font-weight: 600; color: var(--text-1); min-width: 36px; text-align: right; }
.lg-pct { font-size: 12px; color: var(--text-3); min-width: 42px; text-align: right; }

.loose { display: flex; flex-direction: column; }
.loose-list { list-style: none; margin: 0; padding: 4px 14px 16px; flex: 1; }
.loose-list li { display: flex; align-items: center; gap: 11px; padding: 10px 6px; border-bottom: 1px solid var(--hairline); cursor: pointer; border-radius: 8px; transition: background 0.15s; }
.loose-list li:hover { background: rgba(56, 189, 248, 0.06); }
.lb-main { flex: 1; min-width: 0; }
.lb-code { font-size: 13px; font-weight: 600; }
.lb-loc { font-size: 11px; color: var(--text-3); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.lb-right { display: flex; flex-direction: column; align-items: flex-end; gap: 2px; }
.lb-ratio { font-size: 16px; font-weight: 700; }
.lb-tag { font-size: 10px; color: var(--text-3); }
.loose-empty { color: var(--text-3); font-size: 13px; gap: 8px; cursor: default; }
.loose-empty:hover { background: none !important; }

.dist-row { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
.hd-sub { font-size: 11.5px; color: var(--text-3); }
.dist-body { padding: 10px 20px 20px; display: flex; flex-direction: column; gap: 13px; }
.dist-item { display: flex; align-items: center; gap: 14px; }
.dist-label { width: 64px; font-size: 12px; color: var(--text-2); font-family: var(--font-mono); flex-shrink: 0; }
.dist-track { flex: 1; height: 14px; border-radius: 7px; background: rgba(148, 163, 184, 0.08); overflow: hidden; }
.dist-fill { height: 100%; border-radius: 7px; min-width: 2px; transition: width 0.5s ease; }
.dist-count { width: 28px; text-align: right; font-size: 14px; font-weight: 700; flex-shrink: 0; }
.dist-empty { color: var(--text-3); font-size: 13px; padding: 12px 0; text-align: center; }

.appr-list { list-style: none; margin: 0; padding: 4px 14px 16px; }
.appr-list li { display: flex; align-items: center; gap: 14px; padding: 11px 6px; border-bottom: 1px solid var(--hairline); cursor: pointer; border-radius: 8px; transition: background 0.15s; }
.appr-list li:hover { background: rgba(56, 189, 248, 0.06); }
.appr-main { flex: 1; min-width: 0; }
.appr-code { font-size: 13px; font-weight: 600; }
.appr-loc { font-size: 11px; color: var(--text-3); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.appr-rate { font-size: 12px; color: var(--crit); white-space: nowrap; display: flex; flex-direction: column; align-items: flex-end; gap: 1px; }
.appr-r2 { font-size: 10px; color: var(--text-3); }
.appr-days { display: flex; flex-direction: column; align-items: flex-end; line-height: 1.2; min-width: 56px; }
.appr-days b { font-size: 17px; font-weight: 700; }
.appr-days span { font-size: 10px; color: var(--text-3); }
.appr-empty { color: var(--text-3); font-size: 13px; gap: 8px; cursor: default !important; }
.appr-empty:hover { background: none !important; }

.bygroup { padding-bottom: 18px; }
.bg-list { padding: 4px 20px; display: flex; flex-direction: column; gap: 14px; }
.bg-row { display: flex; align-items: center; gap: 16px; }
.bg-name { width: 200px; flex-shrink: 0; display: flex; flex-direction: column; gap: 2px; }
.bg-turbine { font-size: 13px; font-weight: 600; }
.bg-group { font-size: 11px; color: var(--text-3); }
.bg-bar { flex: 1; height: 16px; border-radius: 8px; overflow: hidden; display: flex; background: rgba(148, 163, 184, 0.08); }
.bg-seg { height: 100%; transition: width 0.4s ease; }
.bg-total { width: 60px; text-align: right; font-size: 12px; color: var(--text-2); flex-shrink: 0; }
.bg-empty { color: var(--text-3); font-size: 13px; padding: 10px 0; }

.sys-row { display: flex; align-items: center; gap: 18px; padding: 4px 4px; }
.sys-chip { display: flex; align-items: center; gap: 8px; font-size: 13px; color: var(--text-3); }
.sys-chip b { color: var(--text-1); }
.sys-chip.warn b { color: var(--warn); }

@media (max-width: 1100px) {
  .kpi-row { grid-template-columns: repeat(2, 1fr); }
  .main-row { grid-template-columns: 1fr; }
}
</style>
