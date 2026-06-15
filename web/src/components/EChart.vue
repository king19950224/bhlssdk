<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import * as echarts from 'echarts'

const props = defineProps<{ option: Record<string, unknown>; height?: string }>()
const el = ref<HTMLDivElement>()
const chart = shallowRef<echarts.ECharts>()

function resize() {
  chart.value?.resize()
}
onMounted(() => {
  if (!el.value) return
  chart.value = echarts.init(el.value, undefined, { renderer: 'canvas' })
  chart.value.setOption(props.option as echarts.EChartsCoreOption)
  window.addEventListener('resize', resize)
})
onBeforeUnmount(() => {
  window.removeEventListener('resize', resize)
  chart.value?.dispose()
})
watch(
  () => props.option,
  (o) => chart.value?.setOption(o as echarts.EChartsCoreOption, true),
  { deep: true },
)
</script>

<template>
  <div ref="el" :style="{ width: '100%', height: height || '260px' }" />
</template>
