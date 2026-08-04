<script setup lang="ts">
// 大屏看板（/board）：给值班室墙上那块屏用的，全屏没有导航，只放健康分布、告警和松动 TOP。
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import EChart from '../components/EChart.vue'
import { dashboardApi, alarmApi, type PreloadHealthCounts, type LooseBolt, type ApproachingBolt, type AlarmItem } from '../api'
import { useRuntime } from '../stores/runtime'
import { onRealtime, startRealtime, realtimeState } from '../api/realtime'
import { pctParts } from '../utils/preload'
import { alarmTypeLabel } from '../utils/labels'

const router = useRouter()
const runtime = useRuntime()

const health = ref<PreloadHealthCounts>({ total: 0, normal: 0, warn: 0, loose: 0, uncalibrated: 0, nodata: 0, high: 0 })
const byGroup = ref<{ turbine: string; group: string; health: PreloadHealthCounts }[]>([])
const topLoose = ref<LooseBolt[]>([])
const approaching = ref<ApproachingBolt[]>([])
const openAlarms = ref(0)
const recentAlarms = ref<AlarmItem[]>([])
// 设备遥测改由共享 runtime store 提供（统一一处轮询，复用速率差分/可见性暂停/实时刷新）
const sys = computed(() => ({ online: runtime.online, total: runtime.total, rate: runtime.rate }))
const now = ref(new Date())
let timer: number | undefined, clock: number | undefined
let unsub: (() => void) | undefined

const STATUS: Record<string, { label: string; color: string; cls: string }> = {
  normal: { label: '正常', color: '#10b981', cls: 'led--ok' },
  warn: { label: '预警', color: '#f59e0b', cls: 'led--warn' },
  loose: { label: '松动', color: '#f43f5e', cls: 'led--crit' },
  high: { label: '偏高', color: '#a78bfa', cls: 'led--high' },
  uncalibrated: { label: '未标定/过期', color: '#61748f', cls: 'led--off' },
  nodata: { label: '无近期数据', color: '#3b4659', cls: 'led--off' },
}
const ORDER = ['normal', 'warn', 'loose', 'high', 'uncalibrated', 'nodata'] as const
const SEV: Record<string, string> = { critical: '#f43f5e', crit: '#f43f5e', warn: '#f59e0b', warning: '#f59e0b', info: '#38bdf8' }
// 各状态整数百分比（最大余数法，合计恰为 100%）
const healthPct = computed<Record<string, number>>(() => {
  const parts = pctParts(ORDER.map((k) => health.value[k]), health.value.total)
  return Object.fromEntries(ORDER.map((k, i) => [k, parts[i]]))
})

/*
 * 数据新鲜度。大屏的失败方式和别的页面不一样：它不会变空，只会安静地停在最后一屏数字上，
 * 而旁边时钟还在走秒、空态还挂着"无未闭合告警"的绿灯——从值班室那头看过去，
 * 一块死屏和一切正常长得一模一样。顶栏那盏灯跟的是 SignalR，不是喂这些数字的 5s 轮询，
 * 接口 500 而长连接尚在时它仍然是绿的，指望不上。
 * 所以单独盯一个"上次成功刷新"的时刻：偶发一两次失败不惊动人，连续 STALE_AFTER_MS
 * 拿不到数据就必须在屏上说出来。
 */
const STALE_AFTER_MS = 30000
const lastOkAt = ref(Date.now())
const staleMs = computed(() => now.value.getTime() - lastOkAt.value)
const stale = computed(() => staleMs.value >= STALE_AFTER_MS)
const staleText = computed(() => {
  const s = Math.round(staleMs.value / 1000)
  return s < 120 ? `${s} 秒` : `${Math.floor(s / 60)} 分钟`
})

let disposed = false
// inFlight 防重入：5s 轮询 + OnAlarm 推送可能并发触发 refresh，慢响应后到会覆盖新数据造成大屏"闪一下又回退"
let inFlight = false
async function refresh() {
  if (document.hidden || inFlight) return
  inFlight = true
  try {
    const d = await dashboardApi.preloadHealth()
    if (disposed) return
    health.value = d.global
    byGroup.value = d.byGroup
    topLoose.value = d.topLoose
    approaching.value = d.approachingLimit
    openAlarms.value = d.openAlarms
    const alarms = (await alarmApi.list('?includeCleared=false&limit=10')).items
    if (!disposed) recentAlarms.value = alarms
    lastOkAt.value = Date.now()
  } catch { /* 单次失败不打断画面；连续失败由陈旧横幅兜底 */ } finally { inFlight = false }
}
// 隐藏期间 refresh 直接跳过，不刷新时间戳；重新可见时立刻补一次，
// 免得刚切回来就先看到一条其实已经不成立的陈旧告警。
function onVisibilityChange() { if (!document.hidden) refresh() }
// OnAlarm 去抖：告警风暴时合并 1s 内的推送为一次刷新（与总览页同思路）
let alarmDebounce: number | undefined
onMounted(() => {
  runtime.start()
  // 大屏是独立全屏路由,不经过 AppShell：需自行建立实时连接,告警才能秒级上屏
  startRealtime()
  refresh()
  timer = window.setInterval(refresh, 5000)
  clock = window.setInterval(() => { now.value = new Date() }, 1000)
  unsub = onRealtime('OnAlarm', () => {
    if (alarmDebounce) return
    alarmDebounce = window.setTimeout(() => { alarmDebounce = undefined; refresh() }, 1000)
  })
  document.addEventListener('visibilitychange', onVisibilityChange)
})
onUnmounted(() => {
  disposed = true
  if (timer) clearInterval(timer)
  if (clock) clearInterval(clock)
  if (alarmDebounce) clearTimeout(alarmDebounce)
  unsub?.()
  document.removeEventListener('visibilitychange', onVisibilityChange)
  runtime.stop()
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
        <span><span class="led" :class="realtimeState === 'connected' ? 'led--ok' : realtimeState === 'connecting' ? 'led--warn' : 'led--off'" /> {{ realtimeState === 'connected' ? '实时已连' : realtimeState === 'connecting' ? '实时连接中' : '实时断开' }}</span>
        <el-button size="small" text class="bs-exit" @click="router.push({ name: 'dashboard' })">退出大屏</el-button>
      </div>
    </header>

    <!-- 数据停更横幅：做得比卡片标题还显眼是刻意的，它要在几米外一眼可见 -->
    <div v-if="stale" class="bs-stale">
      <span class="led led--crit" />
      数据已停止刷新 {{ staleText }} · 以下为最后一次成功获取的画面，不代表当前状态
    </div>

    <div class="bs-grid" :class="{ 'bs-grid--stale': stale }">
      <!-- 左：健康分布 + KPI -->
      <section class="bs-card">
        <h3>预紧力健康分布 <span class="h3-sub">全部监测螺栓（含待接入）</span></h3>
        <div class="donut-wrap">
          <EChart :option="donutOption" height="260px" />
          <div class="donut-center"><b class="mono">{{ health.total }}</b><span>监测螺栓</span></div>
        </div>
        <div class="legend">
          <div v-for="k in ORDER" :key="k" class="lg"><span class="led" :class="STATUS[k].cls" /><span class="lg-l">{{ STATUS[k].label }}</span><b class="mono">{{ health[k] }}</b><span class="lg-p mono">{{ healthPct[k] }}%</span></div>
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
            <!-- message 为空时退到类型中文名，不能把 preload_low 这种英文码打上大屏 -->
            <span class="al-msg">{{ a.message || alarmTypeLabel(a.type) }}</span>
          </li>
          <li v-if="!recentAlarms.length" class="bs-empty"><span class="led led--ok" /> 无未闭合告警</li>
        </ul>
      </section>

      <!-- 底：松动 Top + 即将到限 -->
      <section class="bs-card bs-wide">
        <h3>松动 / 预警排行</h3>
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
            <span class="rl-rate mono">{{ b.ratePerDay != null ? '↓' + Math.abs(Math.round(b.ratePerDay)) + ' N/天' : '—' }}</span>
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

/*
 * 停更横幅。尺寸和呼吸动画都是按"几米外扫一眼"定的，不是按坐在屏前看定的；
 * 同时把下方内容压暗一档，让"这屏数字不是现在的"这件事不用读字也能看出来。
 */
.bs-stale {
  display: flex; align-items: center; gap: 12px;
  padding: 10px 18px; border-radius: 12px;
  border: 1px solid var(--crit); background: rgba(244,63,94,0.12); color: var(--crit);
  font-size: 18px; font-weight: 600; letter-spacing: 0.02em;
  animation: bs-stale-pulse 2s ease-in-out infinite;
}
@keyframes bs-stale-pulse {
  0%, 100% { box-shadow: 0 0 0 0 rgba(244,63,94,0); }
  50% { box-shadow: 0 0 26px -6px var(--crit); }
}
.bs-grid--stale { opacity: 0.55; }
@media (prefers-reduced-motion: reduce) { .bs-stale { animation: none; } }

.bs-grid { flex: 1; display: grid; grid-template-columns: 1fr 1.3fr 1fr; grid-template-rows: 1.4fr 1fr; gap: 16px; min-height: 0; transition: opacity 0.4s ease; }
.bs-card { background: rgba(13,19,32,0.7); border: 1px solid var(--panel-border); border-radius: 14px; padding: 16px 20px; display: flex; flex-direction: column; min-height: 0; backdrop-filter: blur(6px); }
.bs-card h3 { margin: 0 0 12px; font-size: 15px; font-weight: 600; color: var(--text-2); letter-spacing: 0.04em; }
.h3-sub { font-size: 11px; color: var(--text-3); font-weight: 400; margin-left: 8px; }
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
/* 非全屏/矮窗口：放弃"两行铺满视口"，按内容自适应并整页滚动,避免固定高图表挤爆卡片 */
@media (max-height: 820px) { .bs-grid { grid-template-rows: auto auto; } .bs-card { min-height: auto; } }
</style>
