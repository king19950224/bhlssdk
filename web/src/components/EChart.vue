<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import * as echarts from 'echarts'

const props = defineProps<{ option: Record<string, unknown>; height?: string }>()
const el = ref<HTMLDivElement>()
const chart = shallowRef<echarts.ECharts>()
let ro: ResizeObserver | undefined

function resize() {
  chart.value?.resize()
}
onMounted(() => {
  if (!el.value) return
  chart.value = echarts.init(el.value, undefined, { renderer: 'canvas' })
  chart.value.setOption(props.option as echarts.EChartsCoreOption)
  window.addEventListener('resize', resize)
  // 容器自身尺寸变化(栅格断点切换/面板展开/初始布局晚于 init)也要重算,
  // 只听 window resize 会让图表画在错误的旧尺寸上(大屏环图偏移的根因)
  ro = new ResizeObserver(() => resize())
  ro.observe(el.value)
})
onBeforeUnmount(() => {
  window.removeEventListener('resize', resize)
  ro?.disconnect()
  chart.value?.dispose()
})
// 所有调用方都传 computed(每次重算返回新对象),引用变化即可触发;
// 不用 deep——高频实时页(1s tick)深遍历整条波形/趋势数组会白耗 CPU
watch(
  () => props.option,
  (o) => chart.value?.setOption(o as echarts.EChartsCoreOption, true),
)
</script>

<template>
  <div ref="el" :style="{ width: '100%', height: height || '260px' }" />
</template>
