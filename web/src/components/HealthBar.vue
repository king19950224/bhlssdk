<script setup lang="ts">
import { computed } from 'vue'
import type { PreloadHealth } from '../api'

const props = defineProps<{ health: PreloadHealth }>()

const ORDER = ['normal', 'warn', 'loose', 'uncalibrated', 'nodata'] as const
const COLOR: Record<string, string> = {
  normal: '#10b981',
  warn: '#f59e0b',
  loose: '#f43f5e',
  uncalibrated: '#61748f',
  nodata: '#3b4659',
}
const segs = computed(() => {
  const total = props.health.total || 0
  return ORDER.filter((k) => props.health[k] > 0).map((k) => ({
    key: k,
    width: total ? (props.health[k] / total) * 100 : 0,
    color: COLOR[k],
  }))
})
</script>

<template>
  <div class="hb">
    <span v-for="s in segs" :key="s.key" class="hb-seg" :style="`width:${s.width}%;background:${s.color}`" />
  </div>
</template>

<style scoped>
.hb { display: flex; height: 14px; border-radius: 7px; overflow: hidden; background: rgba(148, 163, 184, 0.1); }
.hb-seg { height: 100%; transition: width 0.4s ease; }
</style>
