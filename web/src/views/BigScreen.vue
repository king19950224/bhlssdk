<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import EChart from '../components/EChart.vue'
import { dashboardApi, runtimeApi, alarmApi, type PreloadHealth, type LooseBolt, type ApproachingBolt, type AlarmItem } from '../api'
import { onRealtime } from '../api/realtime'

const router = useRouter()

const health = ref<PreloadHealth>({ total: 0, normal: 0, warn: 0, loose: 0, uncalibrated: 0, nodata: 0 })
const byGroup = ref<{ turbine: string; group: string; health: PreloadHealth }[]>([])
const topLoose = ref<LooseBolt[]>([])
const approaching = ref<ApproachingBolt[]>([])
const openAlarms = ref(0)
const recentAlarms = ref<AlarmItem[]>([])
const sys = ref({ online: 0, total: 0, rate: 0 })
const now = ref(new Date())
let lastTotal = 0, lastTs = 0
let timer: number | undefined, clock: number | undefined
let unsub: (() => void) | undefined

const STATUS: Record<string, { label: string; color: string; cls: string }> = {
  normal: { label: '正常', color: '#10b981', cls: 'led--ok' },
  warn: { label: '预警', color: '#f59e0b', cls: 'led--warn' },
  loose: { label: '松动', color: '#f43f5e', cls: 'led--crit' },
  uncalibrated: { label: '未标定', color: '#61748f', cls: 'led--off' },
  nodata: { label: '无数据', color: '#3b4659', cls: 'led--off' },
}
const ORDER = ['normal', 'warn', 'loose', 'uncalibrated', 'nodata'] as const
const SEV: Record<string, string> = { critical: '#f43f5e', crit: '#f43f5e', warn: '#f59e0b', warning: '#f59e0b', info: '#38bdf8' }

async function refresh() {
  try {
    const d = await dashboardApi.preloadHealth()
    health.value = d.global
    byGroup.value = d.byGroup
    topLoose.value = d.topLoose
    approaching.value = d.approachingLimit
    openAlarms.value = d.openAlarms
    const rt = await runtimeApi.devices()
    const total = rt.items.reduce((a, x) => a + (x.channelResultCount ?? 0), 0)
    const t = Date.now()
    sys.value = {
      online: rt.items.filter((x) => x.state === 'Connected').length,
      total: rt.count,
      rate: lastTs ? Math.max(0, Math.round(((total - lastTotal) / ((t - lastTs) / 1000)) * 10) / 10) : 0,
    }
    lastTotal = total; lastTs = t
    recentAlarms.value = (await alarmApi.list('?includeCleared=false&limit=10')).items
  } catch { /* 尽力刷新 */ }
}
onMounted(() => {
  refresh()
  timer = window.setInterval(refresh, 5000)
  clock = window.setInterval(() => { now.value = new Date() }, 1000)
  unsub = onRealtime('OnAlarm', () => refresh())
})
onUnmounted(() => {
  if (timer) clearInterval(timer)
  if (clock) clearInterval(clock)
  unsub?.()
})

const donutOption = computed(() => {
  const data = ORDER.filter((k) => health.value[k] > 0).map((k) => ({ name: STATUS[k].label, value: health.value[k], itemStyle: { color: STATUS[k].color } }))
  return {
    series: [{
      type: 'pie', radius: ['62%', '88%'], center: ['50%', '50%'], label: { show: false }, labelLine: { show: false },
      data: data.length ? data : [{ name: '无数据', value: 1, itemStyle: { color: '#1b2333' } }],
    }],
  }
})
function pct(part: number, total: number) { return total ? (part / total) * 100 : 0 }
function ratioStr(r: number | null) { return r == null ? '—' : (r * 100).toFixed(0) + '%' }
function fmtClock(d: Date) { return d.toLocaleTimeString('zh-CN', { hour12: false }) }
function fmtDate(d: Date) { return d.toLocaleDateString('zh-CN', { year: 'numeric', month: '2-digit', day: '2-digit', weekday: 'short' }) }
</script>

<template>
  <div class="bs-screen">
    <header class="bs-hd">
      <div class="bs-brand">
        <svg viewBox="0 0 32 32" width="30" height="30"><polygon points="16,3 27,9 27,23 16,29 5,23 5,9" fill="none" stroke="var(--accent)" stroke-width="1.6" /><circle cx="16" cy="16" r="4.5" fill="var(--accent)" /></svg>
        <span>螺栓预紧力在线监测 · 态势大屏</span>
      </div>
      <div class="bs-clock">
        <b class="mono">{{ fmtClock(now) }}</b>
        <span>{{ fmtDate(now) }}</span>
      </div>
      <div class="bs-sys">
        <span><span class="led" :class="sys.online > 0 ? 'led--ok' : 'led--off'" /> 设备在线 <b class="mono">{{ sys.online }}/{{ sys.total }}</b></span>
        <span>采集速率 <b class="mono glow-accent">{{ sys.rate }}</b> 条/秒</span>
        <el-button size="small" text class="bs-exit" @click="router.push({ name: 'dashboard' })">退出大屏</el-button>
      </div>
    </header>

    <div class="bs-grid">
      <!-- 左：健康分布 + KPI -->
      <section class="bs-card">
        <h3>预紧力健康分布</h3>
        <div class="donut-wrap">
          <EChart :option="donutOption" height="260px" />
          <div class="donut-center"><b class="mono">{{ health.total }}</b><span>监测螺栓</span></div>
        </div>
        <div class="legend">
          <div v-for="k in ORDER" :key="k" class="lg"><span class="led" :class="STATUS[k].cls" /><span class="lg-l">{{ STATUS[k].label }}</span><b class="mono">{{ health[k] }}</b><span class="lg-p mono">{{ pct(health[k], health.total).toFixed(0) }}%</span></div>
        </div>
      </section>

      <!-- 中：按部位健康 -->
      <section class="bs-card">
        <h3>按部位健康</h3>
        <div class="bg-list">
          <div v-for="(g, i) in byGroup" :key="i" class="bg-row">
            <div class="bg-name"><b>{{ g.turbine }}</b><span>{{ g.group }}</span></div>
            <div class="bg-bar">
              <span v-for="k in ORDER" :key="k" v-show="g.health[k] > 0" class="bg-seg" :style="`width:${pct(g.health[k], g.health.total)}%;background:${STATUS[k].color}`" />
            </div>
            <span class="bg-total mono">{{ g.health.total }}</span>
          </div>
          <div v-if="!byGroup.length" class="bs-empty">暂无部位数据</div>
        </div>
      </section>

      <!-- 右：告警 + 松动 + 到限 -->
      <section class="bs-card bs-alarms" :class="{ alert: openAlarms > 0 }">
        <h3>未闭合告警 <b class="cnt" :style="openAlarms > 0 ? 'color:var(--crit)' : ''">{{ openAlarms }}</b></h3>
        <ul class="al-list">
          <li v-for="a in recentAlarms" :key="a.id">
            <span class="led" :style="`background:${SEV[a.severity] || '#61748f'}`" />
            <span class="al-code mono">{{ a.boltCode }}</span>
            <span class="al-msg">{{ a.message || a.type }}</span>
          </li>
          <li v-if="!recentAlarms.length" class="bs-empty"><span class="led led--ok" /> 无未闭合告警</li>
        </ul>
      </section>

      <!-- 底：松动 Top + 即将到限 -->
      <section class="bs-card bs-wide">
        <h3>松动 / 预警 Top</h3>
        <div class="row-list">
          <div v-for="b in topLoose.slice(0, 6)" :key="b.boltId" class="rl">
            <span class="led" :class="STATUS[b.status]?.cls || 'led--off'" />
            <span class="rl-code">{{ b.boltCode }}</span>
            <span class="rl-loc">{{ b.turbine }} · {{ b.group }}</span>
            <span class="rl-v metric" :style="`color:${STATUS[b.status]?.color}`">{{ ratioStr(b.preloadRatio) }}</span>
          </div>
          <div v-if="!topLoose.length" class="bs-empty"><span class="led led--ok" /> 无松动 / 预警</div>
        </div>
      </section>
      <section class="bs-card bs-wide">
        <h3>即将到限（近 7 天回归）</h3>
        <div class="row-list">
          <div v-for="b in approaching.slice(0, 6)" :key="b.boltId" class="rl">
            <span class="rl-code">{{ b.boltCode }}</span>
            <span class="rl-loc">{{ b.turbine }} · {{ b.group }}</span>
            <span class="rl-rate mono">{{ b.ratePerDay != null ? Math.round(b.ratePerDay) + ' N/天' : '—' }}</span>
            <span class="rl-days mono" :style="b.daysToLimit != null && b.daysToLimit < 30 ? 'color:var(--crit)' : 'color:var(--warn)'">{{ b.daysToLimit != null ? Math.max(0, Math.round(b.daysToLimit)) + ' 天' : '—' }}</span>
          </div>
          <div v-if="!approaching.length" class="bs-empty"><span class="led led--ok" /> 无下降趋势螺栓</div>
        </div>
      </section>
    </div>
  </div>
</template>

<style scoped>
.bs-screen { position: fixed; inset: 0; background: radial-gradient(1200px 800px at 70% -10%, rgba(34,211,238,0.08), transparent), #060912; color: var(--text-1); display: flex; flex-direction: column; padding: 18px 22px; gap: 16px; overflow: auto; z-index: 50; }
.bs-hd { display: flex; align-items: center; gap: 24px; }
.bs-brand { display: flex; align-items: center; gap: 12px; font-size: 22px; font-weight: 700; letter-spacing: 0.02em; filter: drop-shadow(0 0 10px var(--accent-glow)); }
.bs-clock { margin-left: auto; text-align: right; display: flex; flex-direction: column; }
.bs-clock b { font-size: 26px; line-height: 1; letter-spacing: 1px; }
.bs-clock span { font-size: 12px; color: var(--text-3); }
.bs-sys { display: flex; align-items: center; gap: 22px; font-size: 14px; color: var(--text-2); }
.bs-sys b { color: var(--text-1); }
.bs-exit { color: var(--text-3); }

.bs-grid { flex: 1; display: grid; grid-template-columns: 1fr 1.3fr 1fr; grid-template-rows: 1.4fr 1fr; gap: 16px; min-height: 0; }
.bs-card { background: rgba(13,19,32,0.7); border: 1px solid var(--panel-border); border-radius: 14px; padding: 16px 20px; display: flex; flex-direction: column; min-height: 0; backdrop-filter: blur(6px); }
.bs-card h3 { margin: 0 0 12px; font-size: 15px; font-weight: 600; color: var(--text-2); letter-spacing: 0.04em; }
.bs-card.bs-wide { }
.bs-alarms.alert { border-color: var(--crit); box-shadow: 0 0 30px -12px var(--crit-glow); }
.cnt { font-size: 18px; margin-left: 8px; }

.donut-wrap { position: relative; }
.donut-center { position: absolute; inset: 0; display: grid; place-items: center; pointer-events: none; }
.donut-center b { font-size: 38px; line-height: 1; }
.donut-center span { font-size: 12px; color: var(--text-3); }
.legend { display: flex; flex-direction: column; gap: 4px; margin-top: 6px; }
.lg { display: flex; align-items: center; gap: 10px; font-size: 14px; padding: 4px 2px; }
.lg-l { flex: 1; color: var(--text-2); }
.lg-p { color: var(--text-3); min-width: 44px; text-align: right; }

.bg-list { flex: 1; overflow: auto; display: flex; flex-direction: column; gap: 12px; }
.bg-row { display: flex; align-items: center; gap: 14px; }
.bg-name { width: 180px; display: flex; flex-direction: column; }
.bg-name b { font-size: 14px; }
.bg-name span { font-size: 11px; color: var(--text-3); }
.bg-bar { flex: 1; height: 18px; border-radius: 9px; overflow: hidden; display: flex; background: rgba(148,163,184,0.08); }
.bg-seg { height: 100%; }
.bg-total { width: 40px; text-align: right; color: var(--text-2); }

.al-list, .row-list { list-style: none; margin: 0; padding: 0; flex: 1; overflow: auto; display: flex; flex-direction: column; gap: 8px; }
.al-list li { display: flex; align-items: center; gap: 10px; font-size: 14px; padding: 4px 0; border-bottom: 1px solid var(--hairline); }
.al-code { font-weight: 600; }
.al-msg { color: var(--text-3); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.rl { display: flex; align-items: center; gap: 12px; font-size: 14px; padding: 5px 0; border-bottom: 1px solid var(--hairline); }
.rl-code { font-weight: 600; min-width: 90px; }
.rl-loc { flex: 1; color: var(--text-3); font-size: 12px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.rl-v { font-size: 20px; font-weight: 700; }
.rl-rate { color: var(--crit); font-size: 13px; }
.rl-days { font-size: 15px; font-weight: 700; min-width: 56px; text-align: right; }
.bs-empty { display: flex; align-items: center; gap: 8px; color: var(--text-3); font-size: 13px; padding: 8px 0; }
@media (max-width: 1200px) { .bs-grid { grid-template-columns: 1fr 1fr; grid-template-rows: auto; } }
</style>
