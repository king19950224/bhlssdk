<script setup lang="ts">
// 报警系统 KPI（GB/T 41261-2022）：验收时业主直接对着这一页拉数据核对四项限值。
// 限值与判定都由后端 /api/alarms/kpi 给（value/limit/pass/basis），本页只渲染，不自己算限值。
import { onMounted, onBeforeUnmount, ref } from 'vue'
import { alarmApi, type AlarmKpi } from '../api'

const kpi = ref<AlarmKpi | null>(null)
const loadError = ref('')
const loading = ref(false)
const windowHours = ref(24)
// 拉取失败时保留上一次的数用于对比，但必须把 loadError 显示出来——
// 这一页是拿去验收的，静默显示旧数比不显示更糟
let seq = 0
let timer: number | undefined

async function load() {
  const mine = ++seq
  loading.value = true
  try {
    const d = await alarmApi.kpi(windowHours.value)
    if (mine !== seq) return
    kpi.value = d
    loadError.value = ''
  } catch (e) {
    if (mine !== seq) return
    loadError.value = e instanceof Error ? e.message : 'KPI 加载失败'
  } finally {
    if (mine === seq) loading.value = false
  }
}

function setWindow(h: number) {
  windowHours.value = h
  load()
}

function pct(v: number | null) {
  return v == null ? '—' : (v * 100).toFixed(2) + '%'
}
function num(v: number | null, digits = 2) {
  return v == null ? '—' : Number(v).toFixed(digits)
}
function minutes(v: number | null) {
  if (v == null) return '—'
  if (v < 60) return v.toFixed(1) + ' 分钟'
  return (v / 60).toFixed(1) + ' 小时'
}

onMounted(() => {
  load()
  timer = window.setInterval(load, 60000)
})
onBeforeUnmount(() => {
  if (timer) window.clearInterval(timer)
})
</script>

<template>
  <div class="kpi rise">
    <div v-if="loadError" class="banner-err">
      KPI 拉取失败：{{ loadError }}。下面显示的是上一次成功的数据，不可用于验收核对。
    </div>

    <div class="head panel">
      <div>
        <div class="h-title">报警系统 KPI</div>
        <div class="h-sub">
          依据 GB/T 41261-2022《过程工业报警系统管理》（等同采用 IEC 62682:2014）。
          统计窗口内触发 {{ kpi?.totalTriggered ?? '—' }} 条，启用账号 {{ kpi?.operatorCount ?? '—' }} 个。
        </div>
      </div>
      <div class="win">
        <button v-for="h in [24, 72, 168]" :key="h" class="win-btn" :class="{ on: windowHours === h }" @click="setWindow(h)">
          {{ h === 24 ? '24 小时' : h === 72 ? '3 天' : '7 天' }}
        </button>
      </div>
    </div>

    <div v-if="kpi" class="cards">
      <div class="c panel" :class="kpi.metrics.avgPerHourPerOperator.pass ? 'ok' : 'bad'">
        <div class="c-top"><span class="c-name">平均报警率</span><span class="verdict">{{ kpi.metrics.avgPerHourPerOperator.pass ? '达标' : '超限' }}</span></div>
        <div class="c-val">{{ num(kpi.metrics.avgPerHourPerOperator.value, 3) }}<span class="unit">条/小时/操作员</span></div>
        <div class="c-basis">限值 &lt; {{ kpi.metrics.avgPerHourPerOperator.limit }}　·　{{ kpi.metrics.avgPerHourPerOperator.basis }}</div>
      </div>

      <div class="c panel" :class="kpi.metrics.peakPer10Min.pass ? 'ok' : 'bad'">
        <div class="c-top"><span class="c-name">峰值报警率</span><span class="verdict">{{ kpi.metrics.peakPer10Min.pass ? '达标' : '超限' }}</span></div>
        <div class="c-val">{{ kpi.metrics.peakPer10Min.value ?? '—' }}<span class="unit">条 / 10 分钟</span></div>
        <div class="c-basis">限值 ≤ {{ kpi.metrics.peakPer10Min.limit }}　·　{{ kpi.metrics.peakPer10Min.basis }}</div>
      </div>

      <div class="c panel" :class="kpi.metrics.overdueCount.pass ? 'ok' : 'bad'">
        <div class="c-top"><span class="c-name">超期报警</span><span class="verdict">{{ kpi.metrics.overdueCount.pass ? '达标' : '超限' }}</span></div>
        <div class="c-val">{{ kpi.metrics.overdueCount.value ?? '—' }}<span class="unit">条未处理超 24h</span></div>
        <div class="c-basis">限值 ≤ {{ kpi.metrics.overdueCount.limit }}　·　{{ kpi.metrics.overdueCount.basis }}</div>
      </div>

      <div class="c panel" :class="kpi.metrics.highFrequencyRatio.pass ? 'ok' : 'bad'">
        <div class="c-top"><span class="c-name">高频报警率</span><span class="verdict">{{ kpi.metrics.highFrequencyRatio.pass ? '达标' : '超限' }}</span></div>
        <div class="c-val">{{ pct(kpi.metrics.highFrequencyRatio.value) }}<span class="unit">单条规则占比</span></div>
        <div class="c-basis">限值 &lt; {{ pct(kpi.metrics.highFrequencyRatio.limit) }}　·　{{ kpi.metrics.highFrequencyRatio.basis }}</div>
      </div>
    </div>

    <div v-if="kpi" class="two">
      <div class="panel blk">
        <div class="blk-title">处置时长</div>
        <div class="rows">
          <div class="row"><span>平均确认时间</span><b>{{ minutes(kpi.avgAckMinutes) }}</b></div>
          <div class="row"><span>平均处理时间（触发到闭合）</span><b>{{ minutes(kpi.avgClearMinutes) }}</b></div>
          <div class="row"><span>窗口内平均报警率</span><b>{{ num(kpi.avgPerHour, 3) }} 条/小时</b></div>
        </div>
        <div class="note">国标未对这两项给限值，§9.1 要求作为报警系统健康度持续跟踪，只报数不判定。</div>
      </div>

      <div class="panel blk">
        <div class="blk-title">高频规则 TOP 10</div>
        <table v-if="kpi.topFrequent.length" class="t">
          <thead><tr><th>螺栓</th><th>类型</th><th>次数</th><th>占比</th></tr></thead>
          <tbody>
            <tr v-for="r in kpi.topFrequent" :key="`${r.boltId}-${r.type}`">
              <td>{{ r.boltCode }}</td>
              <td>{{ r.typeLabel }}</td>
              <td class="mono">{{ r.count }}</td>
              <td class="mono">{{ pct(r.ratio) }}</td>
            </tr>
          </tbody>
        </table>
        <div v-else class="note">窗口内没有告警。</div>
        <div class="note">一条规则＝一个（螺栓，告警类型）。占比超 5% 说明该点位在刷屏，应做报警合理化评审而不是直接调高阈值（§9.15.3）。</div>
      </div>
    </div>

    <div v-else-if="!loadError" class="note panel">{{ loading ? '加载中…' : '暂无数据' }}</div>
  </div>
</template>

<style scoped>
.kpi { display: flex; flex-direction: column; gap: 14px; }
.banner-err {
  padding: 10px 14px; border-radius: var(--radius-sm);
  border: 1px solid var(--crit); background: rgba(239, 68, 68, 0.1);
  color: var(--crit); font-size: 13px;
}
.head { display: flex; align-items: flex-start; justify-content: space-between; gap: 16px; padding: 16px 18px; }
.h-title { font-size: 16px; font-weight: 600; color: var(--text-1); }
.h-sub { font-size: 12px; color: var(--text-3); margin-top: 5px; line-height: 1.6; }
.win { display: flex; gap: 6px; flex-shrink: 0; }
.win-btn {
  padding: 5px 12px; font-size: 12px; border-radius: 999px; cursor: pointer;
  border: 1px solid var(--panel-border); background: transparent; color: var(--text-2);
}
.win-btn.on { border-color: var(--accent); color: var(--accent); }

.cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(255px, 1fr)); gap: 12px; }
.c { padding: 15px 16px; border-left: 3px solid var(--panel-border); }
/* 达标/超限用颜色 + 文字双标识，色弱也能分辨（§9.24） */
.c.ok { border-left-color: var(--ok); }
.c.bad { border-left-color: var(--crit); }
.c-top { display: flex; align-items: center; justify-content: space-between; margin-bottom: 10px; }
.c-name { font-size: 13px; color: var(--text-2); font-weight: 600; }
.verdict { font-size: 10.5px; font-weight: 700; letter-spacing: 0.06em; padding: 2px 8px; border-radius: 999px; border: 1px solid; }
.c.ok .verdict { color: var(--ok); border-color: var(--ok); }
.c.bad .verdict { color: var(--crit); border-color: var(--crit); }
.c-val { font-size: 26px; font-weight: 700; color: var(--text-1); font-variant-numeric: tabular-nums; }
.unit { font-size: 11.5px; font-weight: 400; color: var(--text-3); margin-left: 7px; }
.c-basis { font-size: 11px; color: var(--text-3); margin-top: 9px; line-height: 1.5; }

.two { display: grid; grid-template-columns: 1fr 1.3fr; gap: 12px; }
@media (max-width: 1100px) { .two { grid-template-columns: 1fr; } }
.blk { padding: 15px 16px; }
.blk-title { font-size: 13px; font-weight: 600; color: var(--text-1); margin-bottom: 12px; }
.rows { display: flex; flex-direction: column; gap: 9px; }
.row { display: flex; align-items: baseline; justify-content: space-between; font-size: 13px; color: var(--text-2); }
.row b { color: var(--text-1); font-variant-numeric: tabular-nums; }
.note { font-size: 11.5px; color: var(--text-3); margin-top: 11px; line-height: 1.6; }
.t { width: 100%; border-collapse: collapse; font-size: 12.5px; }
.t th, .t td { padding: 6px 8px; text-align: left; border-bottom: 1px solid var(--hairline); }
.t th { color: var(--text-3); font-weight: 500; font-size: 11.5px; }
.t td { color: var(--text-2); }
</style>
