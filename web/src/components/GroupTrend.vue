<script setup lang="ts">
// 监测组预紧力趋势图：组里每颗螺栓的 24h/7d 曲线叠着画，按状态上色，好看出哪颗在往下掉。
import { ref, computed, onMounted, watch } from 'vue'
import EChart from './EChart.vue'
import { boltApi, type GroupBolt } from '../api'

const props = defineProps<{ bolts: GroupBolt[] }>()

const range = ref<'24h' | '7d'>('24h')
const loading = ref(false)
const seriesData = ref<{ bolt: GroupBolt; pts: [number, number | null][] }[]>([])
// 趋势查询失败的螺栓编号。缺一条线看着像没在采，其实是查询挂了。
const failedBolts = ref<string[]>([])

const STATUS_COLOR: Record<string, string> = {
  normal: 'rgba(56,189,248,0.7)',
  warn: '#f59e0b',
  loose: '#f43f5e',
  high: '#a78bfa',
  uncalibrated: 'rgba(97,116,143,0.5)',
  nodata: 'rgba(97,116,143,0.5)',
}

const limitRatio = computed(() => {
  const rs = props.bolts
    .map((b) => (b.minPreloadN != null && b.targetPreloadN ? Number(b.minPreloadN) / Number(b.targetPreloadN) : null))
    .filter((x): x is number => x != null)
  return rs.length ? rs.reduce((a, b) => a + b, 0) / rs.length : 0.8
})
// 各螺栓下限比不一致时,这条参考线只是平均值——标注上说清楚,别让人当成每颗的精确下限
const limitIsUniform = computed(() => {
  const rs = props.bolts
    .map((b) => (b.minPreloadN != null && b.targetPreloadN ? Number(b.minPreloadN) / Number(b.targetPreloadN) : null))
    .filter((x): x is number => x != null)
  return rs.length <= 1 || Math.max(...rs) - Math.min(...rs) < 0.005
})

// 显式 bucket + maxPoints，不用 auto：auto 档位按"服务器 now − 浏览器 since"的跨度选，
// 服务器时钟慢于浏览器时 24h 请求会翻档到 1min 桶（1440 桶超默认 500）被静默截成尾段（历史消失）。
// 组对比是多颗叠画，用比单颗略粗的桶即可（24h→5min 288 桶 / 7d→1hour 168 桶），也顺便省流量。
const RANGE_QUERY: Record<'24h' | '7d', { hours: number; bucket: string; maxPoints: number }> = {
  '24h': { hours: 24, bucket: '5min', maxPoints: 400 },
  '7d': { hours: 168, bucket: '1hour', maxPoints: 400 },
}
let loadSeq = 0
async function load() {
  if (!props.bolts.length) { seriesData.value = []; failedBolts.value = []; return }
  const seq = ++loadSeq // 竞态令牌：bolts/range 快速切换时，只保留最后一次发起的结果
  loading.value = true
  const rq = RANGE_QUERY[range.value]
  const since = new Date(Date.now() - rq.hours * 3600 * 1000).toISOString()
  const failed: string[] = []
  try {
    // 单颗失败不连坐：其余照画，失败的记下编号在图下说明
    const results = await Promise.all(
      props.bolts.map((b) =>
        boltApi
          .preloadTrend(b.id, `?bucket=${rq.bucket}&maxPoints=${rq.maxPoints}&since=${since}`)
          .then((r) => ({ bolt: b, items: r.items }))
          .catch(() => {
            failed.push(b.code)
            return { bolt: b, items: [] as { ts: string; avgRatio: number | null }[] }
          }),
      ),
    )
    if (seq !== loadSeq) return // 已有更新的加载在途，丢弃这次陈旧结果
    failedBolts.value = failed
    seriesData.value = results.map(({ bolt, items }) => ({
      bolt,
      pts: items.map((p) => [new Date(p.ts).getTime(), p.avgRatio == null ? null : Number(p.avgRatio) * 100] as [number, number | null]),
    }))
  } finally {
    if (seq === loadSeq) loading.value = false
  }
}
onMounted(load)
// 按"螺栓 id 集合"而非数组引用触发重拉：父页(AssetMonitor)每 30s 轮询都会换新数组引用,
// 按引用 watch 会让本组件每 30s 全量重发 N 颗 × N 个 trend 请求(一组 30 颗=每分钟 60 个重聚合查询)。
// 成员没变就不重拉;状态字段变化只影响线色,由 option 计算属性自然生效,无需重查。
watch(() => props.bolts.map((b) => b.id).join(','), () => void load())
watch(range, load)

const hasData = computed(() => seriesData.value.some((s) => s.pts.some((p) => p[1] != null)))

const option = computed(() => {
  const order: Record<string, number> = { loose: 3, high: 3, warn: 2, normal: 1, uncalibrated: 0, nodata: 0 }
  const sorted = [...seriesData.value].sort((a, b) => (order[a.bolt.status] ?? 0) - (order[b.bolt.status] ?? 0))
  const series: Record<string, unknown>[] = sorted.map((s) => {
    const st = s.bolt.status
    const alert = st === 'loose' || st === 'warn' || st === 'high'
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
    { yAxis: limitRatio.value * 100, lineStyle: { color: '#f43f5e', type: 'dashed' }, label: { formatter: (limitIsUniform.value ? '下限 ' : '平均下限 ') + (limitRatio.value * 100).toFixed(0) + '%', color: '#f43f5e', position: 'insideEndBottom', fontSize: 10 } },
  ]
  if (series.length) (series[series.length - 1] as Record<string, unknown>).markLine = { symbol: 'none', data: markLines, silent: true }
  return {
    grid: { left: 48, right: 18, top: 14, bottom: 50 },
    tooltip: { trigger: 'axis', backgroundColor: '#0d1320', borderColor: 'rgba(56,189,248,0.3)', textStyle: { color: '#e8eef8' }, valueFormatter: (v: number) => (v == null ? '—' : v.toFixed(1) + '%') },
    legend: { type: 'scroll', bottom: 0, textStyle: { color: '#9fb0c8', fontSize: 11 }, inactiveColor: '#3b4659', pageIconColor: '#61748f', pageTextStyle: { color: '#9fb0c8' } },
    xAxis: { type: 'time', axisLine: { lineStyle: { color: 'rgba(148,163,184,0.2)' } }, axisLabel: { color: '#61748f', fontSize: 11 } },
    yAxis: { type: 'value', scale: true, name: '保持率 %', nameTextStyle: { color: '#61748f', fontSize: 11 }, axisLabel: { color: '#61748f', fontSize: 11, formatter: '{value}%' }, splitLine: { lineStyle: { color: 'rgba(148,163,184,0.08)' } } },
    series,
  }
})
</script>

<template>
  <div class="gtrend">
    <div class="gt-bar">
      <span class="gt-title">同部位预紧力保持率对比 <i>全圈齐降 = 系统性松弛；单根掉队 = 个别松动</i></span>
      <div class="range-tabs">
        <span v-for="k in (['24h', '7d'] as const)" :key="k" class="range-tab" :class="{ active: range === k }" @click="range = k">{{ k === '24h' ? '24 小时' : '7 天' }}</span>
      </div>
    </div>
    <EChart v-if="hasData" :option="option" height="370px" />
    <div v-else-if="loading" class="gt-empty" v-loading="true" style="min-height:120px" />
    <div v-else-if="failedBolts.length" class="gt-empty gt-empty--err">
      趋势加载失败：{{ failedBolts.join('、') }}
    </div>
    <div v-else class="gt-empty">该部位暂无可对比的趋势数据</div>
    <div v-if="hasData && failedBolts.length" class="gt-warn">
      以下螺栓趋势查询失败，图上没有对应曲线：{{ failedBolts.join('、') }}
    </div>
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
.gt-empty--err { color: var(--warn); }
.gt-warn { color: var(--warn); font-size: 12px; padding: 0 4px 8px; }
</style>
