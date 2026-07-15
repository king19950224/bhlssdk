<script setup lang="ts">
import { computed } from 'vue'
import { fmtN } from '../utils/preload'

const props = defineProps<{
  current: number | null
  target: number | null
  min: number | null
  height?: number
  compact?: boolean
}>()

// 量程：下限略下方 → 目标略上方；当前值超出目标区间时量程随之扩展，
// 避免超张力螺栓的指针被钳在最右端、看起来像"刚好达标"
const range = computed(() => {
  const { min, target, current } = props
  const lo = min != null ? min * 0.85 : 0
  let hi = target != null ? target * 1.08 : current != null ? current * 1.1 : 1
  if (current != null && current > hi) hi = current * 1.04
  return { lo, hi, span: Math.max(hi - lo, 1) }
})
const posOf = (v: number | null) =>
  v == null ? null : Math.max(0, Math.min(100, ((v - range.value.lo) / range.value.span) * 100))

const dangerEnd = computed(() => posOf(props.min) ?? 0)
const warnEnd = computed(() => (props.min != null ? posOf(props.min * 1.1) ?? dangerEnd.value : dangerEnd.value))
const curPos = computed(() => posOf(props.current))
const minPos = computed(() => posOf(props.min))
const tgtPos = computed(() => posOf(props.target))
// 超过目标 110%（偏高告警口径）时,指针改用偏高紫色示警
const overTension = computed(() => props.current != null && props.target != null && props.target > 0 && props.current > props.target * 1.1)
const h = computed(() => props.height ?? 12)
</script>

<template>
  <div class="pscale" :class="{ compact }">
    <div class="track" :style="{ height: h + 'px' }">
      <div class="zone danger" :style="{ width: dangerEnd + '%' }" />
      <div class="zone warn" :style="{ left: dangerEnd + '%', width: warnEnd - dangerEnd + '%' }" />
      <div class="zone safe" :style="{ left: warnEnd + '%' }" />
      <div v-if="minPos != null" class="tick tick-min" :style="{ left: minPos + '%' }" />
      <div v-if="tgtPos != null" class="tick tick-tgt" :style="{ left: tgtPos + '%' }" />
      <div v-if="curPos != null" class="needle" :class="{ over: overTension }" :style="{ left: curPos + '%' }">
        <span class="needle-dot" />
      </div>
    </div>
    <div v-if="!compact" class="axis">
      <span v-if="min != null" class="ax ax-min" :style="{ left: minPos + '%' }">下限 {{ fmtN(min) }}</span>
      <span v-if="target != null" class="ax ax-tgt" :style="{ left: tgtPos + '%' }">目标 {{ fmtN(target) }}</span>
    </div>
  </div>
</template>

<style scoped>
.pscale { width: 100%; }
.track { position: relative; width: 100%; border-radius: 999px; overflow: hidden; background: rgba(148, 163, 184, 0.1); }
.zone { position: absolute; top: 0; bottom: 0; right: 0; }
.zone.danger { left: 0; background: rgba(244, 63, 94, 0.55); }
.zone.warn { background: rgba(245, 158, 11, 0.5); }
.zone.safe { background: linear-gradient(90deg, rgba(16, 185, 129, 0.4), rgba(16, 185, 129, 0.6)); }
.tick { position: absolute; top: -2px; bottom: -2px; width: 2px; transform: translateX(-1px); }
.tick-min { background: rgba(244, 63, 94, 0.9); }
.tick-tgt { background: rgba(34, 211, 238, 0.95); }
.needle { position: absolute; top: -4px; bottom: -4px; width: 2px; background: #fff; transform: translateX(-1px); box-shadow: 0 0 8px rgba(255, 255, 255, 0.7); }
.needle-dot { position: absolute; top: -5px; left: 50%; width: 11px; height: 11px; border-radius: 50%; background: #fff; transform: translateX(-50%); box-shadow: 0 0 8px rgba(255, 255, 255, 0.6); }
.needle.over, .needle.over .needle-dot { background: #a78bfa; box-shadow: 0 0 8px rgba(167, 139, 250, 0.8); }
.compact .needle-dot { width: 8px; height: 8px; top: -3px; }
.axis { position: relative; height: 16px; margin-top: 9px; font-size: 10.5px; font-family: var(--font-mono); }
.ax { position: absolute; transform: translateX(-50%); white-space: nowrap; top: 0; }
.ax-min { color: var(--crit); }
.ax-tgt { color: var(--accent); }
</style>
