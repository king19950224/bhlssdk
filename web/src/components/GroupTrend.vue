<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import EChart from './EChart.vue'
import { boltApi, type GroupBolt } from '../api'

const props = defineProps<{ bolts: GroupBolt[] }>()

const range = ref<'24h' | '7d'>('24h')
const loading = ref(false)
const seriesData = ref<{ bolt: GroupBolt; pts: [number, number | null][] }[]>([])

const STATUS_COLOR: Record<string, string> = {
  normal: 'rgba(56,189,248,0.7)',
  warn: '#f59e0b',
  loose: '#f43f5e',
  uncalibrated: 'rgba(97,116,143,0.5)',
  nodata: 'rgba(97,116,143,0.5)',
}

const limitRatio = computed(() => {
  const rs = props.bolts
    .map((b) => (b.minPreloadN != null && b.targetPreloadN ? Number(b.minPreloadN) / Number(b.targetPreloadN) : null))
    .filter((x): x is number => x != null)
  return rs.length ? rs.reduce((a, b) => a + b, 0) / rs.length : 0.8
})

async function load() {
  if (!props.bolts.length) return
  loading.value = true
  const hours = range.value === '24h' ? 24 : 168
  const since = new Date(Date.now() - hours * 3600 * 1000).toISOString()
  try {
    const results = await Promise.all(
      props.bolts.map((b) =>
        boltApi
          .preloadTrend(b.id, `?bucket=auto&since=${since}`)
          .then((r) => ({ bolt: b, items: r.items }))
          .catch(() => ({ bolt: b, items: [] as { ts: string; avgRatio: number | null }[] })),
      ),
    )
    seriesData.value = results.map(({ bolt, items }) => ({
      bolt,
      pts: items.map((p) => [new Date(p.ts).getTime(), p.avgRatio == null ? null : Number(p.avgRatio) * 100] as [number, number | null]),
    }))
  } finally {
    loading.value = false
  }
}
onMounted(load)
watch(() => props.bolts, load)
watch(range, load)

const hasData = computed(() => seriesData.value.some((s) => s.pts.some((p) => p[1] != null)))

const option = computed(() => {
  const order: Record<string, number> = { loose: 3, warn: 2, normal: 1, uncalibrated: 0, nodata: 0 }
  const sorted = [...seriesData.value].sort((a, b) => (order[a.bolt.status] ?? 0) - (order[b.bolt.status] ?? 0))
  const series: Record<string, unknown>[] = sorted.map((s) => {
    const st = s.bolt.status
    const alert = st === 'loose' || st === 'warn'
    const col = STATUS_COLOR[st] ?? 'rgba(148,163,184,0.5)'
    return {
      name: s.bolt.code, type: 'line', smooth: true, symbol: 'none', connectNulls: true, data: s.pts,
      color: col,
      itemStyle: { color: col }, // 图例圆点取此色，与线色（按状态）一致
      lineStyle: { color: col, width: alert ? 2.6 : 1.3 },
      emphasis: { focus: 'series' }, z: alert ? 6 : 2,
    }
  })
  const markLines = [
    { yAxis: 100, lineStyle: { color: '#22d3ee', type: 'dashed' }, label: { formatter: '目标 100%', color: '#22d3ee', position: 'insideEndTop', fontSize: 10 } },
    { yAxis: limitRatio.value * 100, lineStyle: { color: '#f43f5e', type: 'dashed' }, label: { formatter: '下限 ' + (limitRatio.value * 100).toFixed(0) + '%', color: '#f43f5e', position: 'insideEndBottom', fontSize: 10 } },
  ]
  if (series.length) (series[series.length - 1] as Record<string, unknown>).markLine = { symbol: 'none', data: markLines, silent: true }
  return {
    grid: { left: 48, right: 18, top: 14, bottom: 50 },
    tooltip: { trigger: 'axis', backgroundColor: '#0d1320', borderColor: 'rgba(56,189,248,0.3)', textStyle: { color: '#e8eef8' }, valueFormatter: (v: number) => (v == null ? '—' : v.toFixed(1) + '%') },
    legend: { type: 'scroll', bottom: 0, textStyle: { color: '#9fb0c8', fontSize: 11 }, inactiveColor: '#3b4659', pageIconColor: '#61748f', pageTextStyle: { color: '#9fb0c8' } },
    xAxis: { type: 'time', axisLine: { lineStyle: { color: 'rgba(148,163,184,0.2)' } }, axisLabel: { color: '#61748f', fontSize: 11 } },
    yAxis: { type: 'value', scale: true, name: '残余率 %', nameTextStyle: { color: '#61748f', fontSize: 11 }, axisLabel: { color: '#61748f', fontSize: 11, formatter: '{value}%' }, splitLine: { lineStyle: { color: 'rgba(148,163,184,0.08)' } } },
    series,
  }
})
</script>

<template>
  <div class="gtrend">
    <div class="gt-bar">
      <span class="gt-title">同部位残余预紧力率对比 <i>全圈齐降 = 系统性松弛；单根掉队 = 个别松动</i></span>
      <div class="range-tabs">
        <span v-for="k in (['24h', '7d'] as const)" :key="k" class="range-tab" :class="{ active: range === k }" @click="range = k">{{ k }}</span>
      </div>
    </div>
    <EChart v-if="hasData" :option="option" height="370px" />
    <div v-else-if="!loading" class="gt-empty">该部位暂无可对比的趋势数据</div>
  </div>
</template>

<style scoped>
.gtrend { width: 100%; }
.gt-bar { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 2px 4px 12px; flex-wrap: wrap; }
.gt-title { font-size: 13px; color: var(--text-2); font-weight: 600; }
.gt-title i { font-size: 11px; color: var(--text-3); font-weight: 400; font-style: normal; margin-left: 8px; }
.range-tabs { display: flex; gap: 4px; }
.range-tab { font-size: 12px; padding: 4px 12px; border-radius: 8px; cursor: pointer; color: var(--text-3); border: 1px solid transparent; }
.range-tab:hover { color: var(--text-1); }
.range-tab.active { color: var(--accent); border-color: var(--panel-border-strong); background: rgba(34, 211, 238, 0.08); }
.gt-empty { text-align: center; color: var(--text-3); padding: 40px; font-size: 13px; }
</style>
