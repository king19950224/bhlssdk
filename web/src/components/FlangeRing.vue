<script setup lang="ts">
import { computed } from 'vue'
import type { GroupBolt } from '../api'

const props = defineProps<{ bolts: GroupBolt[] }>()
const emit = defineEmits<{ (e: 'select', boltId: number): void }>()

const STATUS: Record<string, { label: string; color: string }> = {
  normal: { label: '正常', color: '#10b981' },
  warn: { label: '预警', color: '#f59e0b' },
  loose: { label: '松动', color: '#f43f5e' },
  uncalibrated: { label: '未标定', color: '#5b6b82' },
  nodata: { label: '无数据', color: '#3b4659' },
}

const SIZE = 440
const C = SIZE / 2
const R = 170 // 螺栓中心所在半径
const DOT = 17

function boltNo(b: GroupBolt, i: number) {
  const m = b.code.match(/(\d+)\s*$/)
  return m ? String(Number(m[1])) : String(i + 1)
}

const nodes = computed(() =>
  props.bolts.map((b, i) => {
    const n = props.bolts.length
    const ang = -Math.PI / 2 + (i * 2 * Math.PI) / Math.max(n, 1)
    return {
      bolt: b,
      no: boltNo(b, i),
      x: C + R * Math.cos(ang),
      y: C + R * Math.sin(ang),
      color: STATUS[b.status]?.color ?? '#5b6b82',
      alert: b.status === 'loose' || b.status === 'warn',
    }
  }),
)

const counts = computed(() => {
  const c: Record<string, number> = { normal: 0, warn: 0, loose: 0, uncalibrated: 0, nodata: 0 }
  for (const b of props.bolts) c[b.status] = (c[b.status] ?? 0) + 1
  return c
})
const outOfRange = computed(() =>
  props.bolts.filter((b) => b.status === 'loose' || b.status === 'warn').map((b) => boltNo(b, props.bolts.indexOf(b))),
)
</script>

<template>
  <div class="flange">
    <svg :viewBox="`0 0 ${SIZE} ${SIZE}`" class="ring-svg">
      <!-- 法兰外圈 / 内孔 -->
      <circle :cx="C" :cy="C" :r="R + 32" fill="none" stroke="rgba(148,163,184,0.16)" stroke-width="2" />
      <circle :cx="C" :cy="C" :r="R - 32" fill="none" stroke="rgba(148,163,184,0.12)" stroke-width="1.5" stroke-dasharray="4 6" />
      <circle :cx="C" :cy="C" :r="R" fill="none" stroke="rgba(56,189,248,0.14)" stroke-width="1" />

      <!-- 螺栓节点 -->
      <g v-for="nd in nodes" :key="nd.bolt.id" class="node" :class="{ alert: nd.alert }" @click="emit('select', nd.bolt.id)">
        <circle v-if="nd.alert" :cx="nd.x" :cy="nd.y" :r="DOT + 6" :fill="nd.color" opacity="0.18" class="halo" />
        <circle :cx="nd.x" :cy="nd.y" :r="DOT" :fill="nd.color" :opacity="nd.bolt.status === 'uncalibrated' || nd.bolt.status === 'nodata' ? 0.55 : 0.95" />
        <text :x="nd.x" :y="nd.y + 4" text-anchor="middle" class="node-no">{{ nd.no }}</text>
        <title>{{ nd.bolt.code }} · {{ STATUS[nd.bolt.status]?.label }}{{ nd.bolt.preloadRatio != null ? ' · ' + (Number(nd.bolt.preloadRatio) * 100).toFixed(0) + '%' : '' }}</title>
      </g>

      <!-- 中心汇总 -->
      <text :x="C" :y="C - 8" text-anchor="middle" class="center-num">{{ bolts.length }}</text>
      <text :x="C" :y="C + 14" text-anchor="middle" class="center-cap">螺栓</text>
      <text v-if="counts.loose + counts.warn > 0" :x="C" :y="C + 40" text-anchor="middle" class="center-alert">
        {{ counts.loose }} 松动 · {{ counts.warn }} 预警
      </text>
      <text v-else :x="C" :y="C + 40" text-anchor="middle" class="center-ok">全部正常</text>
    </svg>

    <div class="ring-legend">
      <span v-for="k in ['normal', 'warn', 'loose', 'uncalibrated']" :key="k" class="lg">
        <i :style="`background:${STATUS[k].color}`" /> {{ STATUS[k].label }} {{ counts[k] }}
      </span>
    </div>
    <div v-if="outOfRange.length" class="ring-warn">超限螺栓:{{ outOfRange.join('、') }}</div>
  </div>
</template>

<style scoped>
.flange { display: flex; flex-direction: column; align-items: center; gap: 14px; }
.ring-svg { width: 100%; max-width: 460px; height: auto; }
.node { cursor: pointer; }
.node-no { font-family: var(--font-mono); font-size: 13px; font-weight: 600; fill: #04121a; }
.node:hover circle { stroke: #fff; stroke-width: 2; }
.node.alert .node-no { fill: #fff; }
.halo { animation: halo 1.8s ease-out infinite; transform-origin: center; transform-box: fill-box; }
@keyframes halo { 0% { transform: scale(0.7); opacity: 0.4; } 100% { transform: scale(1.5); opacity: 0; } }
.center-num { font-family: var(--font-mono); font-size: 40px; font-weight: 700; fill: var(--text-1); }
.center-cap { font-size: 13px; fill: var(--text-3); }
.center-alert { font-size: 13px; fill: var(--crit); font-weight: 600; }
.center-ok { font-size: 13px; fill: var(--ok); }
.ring-legend { display: flex; gap: 18px; flex-wrap: wrap; justify-content: center; font-size: 12px; color: var(--text-2); }
.ring-legend .lg { display: flex; align-items: center; gap: 7px; }
.ring-legend i { width: 10px; height: 10px; border-radius: 50%; display: inline-block; }
.ring-warn { font-size: 13px; color: var(--crit); font-weight: 500; }
</style>
