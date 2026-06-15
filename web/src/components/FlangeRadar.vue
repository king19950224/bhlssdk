<script setup lang="ts">
import { computed } from 'vue'
import type { GroupBolt } from '../api'
import { pct } from '../utils/preload'

const props = defineProps<{ bolts: GroupBolt[] }>()
const emit = defineEmits<{ (e: 'select', boltId: number): void }>()

const STATUS: Record<string, { label: string; color: string }> = {
  normal: { label: '正常', color: '#10b981' },
  warn: { label: '预警', color: '#f59e0b' },
  loose: { label: '松动', color: '#f43f5e' },
  uncalibrated: { label: '未标定', color: '#5b6b82' },
  nodata: { label: '无数据', color: '#3b4659' },
}

const SIZE = 460
const C = SIZE / 2
const OUTER = 182
const INNER = 48
const R_LO = 0.6 // 残余率域下界
const R_HI = 1.1 // 残余率域上界

function radiusOf(res: number | null) {
  if (res == null) return OUTER
  const c = Math.max(R_LO, Math.min(R_HI, res))
  return INNER + ((c - R_LO) / (R_HI - R_LO)) * (OUTER - INNER)
}
function angleOf(i: number, n: number) {
  return -90 + (i * 360) / Math.max(n, 1)
}
function pol(angDeg: number, r: number): [number, number] {
  const a = (angDeg * Math.PI) / 180
  return [C + r * Math.cos(a), C + r * Math.sin(a)]
}
function boltNo(b: GroupBolt, i: number) {
  const m = b.code.match(/(\d+)\s*$/)
  return m ? String(Number(m[1])) : String(i + 1)
}

const limitRatio = computed(() => {
  const rs = props.bolts
    .map((b) => (b.minPreloadN != null && b.targetPreloadN ? Number(b.minPreloadN) / Number(b.targetPreloadN) : null))
    .filter((x): x is number => x != null)
  return rs.length ? rs.reduce((a, b) => a + b, 0) / rs.length : 0.8
})

const nodes = computed(() =>
  props.bolts.map((b, i) => {
    const res = b.preloadRatio != null ? Number(b.preloadRatio) : null
    const ang = angleOf(i, props.bolts.length)
    const r = radiusOf(res)
    const [x, y] = pol(ang, r)
    const [sx, sy] = pol(ang, OUTER)
    return {
      b, no: boltNo(b, i), res, ang, x, y, sx, sy,
      color: STATUS[b.status]?.color ?? '#5b6b82',
      alert: b.status === 'loose' || b.status === 'warn',
      hasData: res != null,
    }
  }),
)
const profilePts = computed(() => nodes.value.filter((n) => n.hasData).map((n) => `${n.x.toFixed(1)},${n.y.toFixed(1)}`).join(' '))
const targetR = radiusOf(1.0)
const limitR = computed(() => radiusOf(limitRatio.value))
const warnR = computed(() => radiusOf(limitRatio.value * 1.1))
const gridRings = [0.7, 0.8, 0.9, 1.0]

const counts = computed(() => {
  const c: Record<string, number> = { normal: 0, warn: 0, loose: 0, uncalibrated: 0, nodata: 0 }
  for (const b of props.bolts) c[b.status] = (c[b.status] ?? 0) + 1
  return c
})
// 扫描扇形（40°）路径，整体旋转
const sweepPath = (() => {
  const [x0, y0] = pol(-90, OUTER)
  const [x1, y1] = pol(-50, OUTER)
  return `M ${C} ${C} L ${x0.toFixed(1)} ${y0.toFixed(1)} A ${OUTER} ${OUTER} 0 0 1 ${x1.toFixed(1)} ${y1.toFixed(1)} Z`
})()
const labelPos = (res: number) => pol(-90, radiusOf(res))
</script>

<template>
  <div class="radar">
    <svg :viewBox="`0 0 ${SIZE} ${SIZE}`" class="radar-svg">
      <defs>
        <radialGradient id="sweepGrad" cx="0%" cy="0%" r="100%">
          <stop offset="0%" stop-color="rgba(34,211,238,0.32)" />
          <stop offset="100%" stop-color="rgba(34,211,238,0)" />
        </radialGradient>
        <radialGradient id="coreGlow">
          <stop offset="0%" stop-color="rgba(56,189,248,0.18)" />
          <stop offset="100%" stop-color="rgba(56,189,248,0)" />
        </radialGradient>
      </defs>

      <circle :cx="C" :cy="C" :r="OUTER" fill="url(#coreGlow)" />

      <!-- 残余率栅格环 -->
      <circle v-for="g in gridRings" :key="'g' + g" :cx="C" :cy="C" :r="radiusOf(g)" fill="none" stroke="rgba(148,163,184,0.09)" stroke-width="1" />

      <!-- 扫描扇形 -->
      <g>
        <path :d="sweepPath" fill="url(#sweepGrad)" opacity="0.9">
          <animateTransform attributeName="transform" type="rotate" :from="`0 ${C} ${C}`" :to="`360 ${C} ${C}`" dur="5s" repeatCount="indefinite" />
        </path>
      </g>

      <!-- 辐条 -->
      <line v-for="(n, i) in nodes" :key="'sp' + i" :x1="C" :y1="C" :x2="n.sx" :y2="n.sy" stroke="rgba(148,163,184,0.07)" stroke-width="1" />

      <!-- 参考环：下限(红) / 预警(琥珀) / 目标(青) -->
      <circle :cx="C" :cy="C" :r="limitR" fill="none" stroke="rgba(244,63,94,0.7)" stroke-width="1.5" stroke-dasharray="3 5" />
      <circle :cx="C" :cy="C" :r="warnR" fill="none" stroke="rgba(245,158,11,0.5)" stroke-width="1" stroke-dasharray="2 6" />
      <circle :cx="C" :cy="C" :r="targetR" fill="none" stroke="rgba(34,211,238,0.7)" stroke-width="1.5" />

      <!-- 预紧力剖面多边形 -->
      <polygon v-if="profilePts" :points="profilePts" fill="rgba(34,211,238,0.1)" stroke="rgba(34,211,238,0.45)" stroke-width="1.5" stroke-linejoin="round" />

      <!-- 螺栓节点 -->
      <g v-for="n in nodes" :key="n.b.id" class="node" :class="{ alert: n.alert }" @click="emit('select', n.b.id)">
        <circle v-if="n.alert" :cx="n.x" :cy="n.y" r="15" :fill="n.color" opacity="0.18" class="halo" />
        <circle :cx="n.x" :cy="n.y" r="11" :fill="n.color" :opacity="n.hasData ? 0.95 : 0.5" stroke="rgba(4,18,26,0.6)" stroke-width="1" />
        <text :x="n.x" :y="n.y + 3.5" text-anchor="middle" class="node-no">{{ n.no }}</text>
        <title>{{ n.b.code }} · {{ STATUS[n.b.status]?.label }}{{ n.res != null ? ' · 残余 ' + pct(n.res) : '' }}</title>
      </g>

      <!-- 参考环标注 -->
      <text :x="labelPos(1.0)[0] + 4" :y="labelPos(1.0)[1] + 3" class="ring-lb" fill="#22d3ee">目标</text>
      <text :x="pol(-90, limitR)[0] + 4" :y="pol(-90, limitR)[1] + 3" class="ring-lb" fill="#f43f5e">下限</text>

      <!-- 中心 -->
      <text :x="C" :y="C - 6" text-anchor="middle" class="c-num">{{ bolts.length }}</text>
      <text :x="C" :y="C + 14" text-anchor="middle" class="c-cap">螺栓</text>
      <text v-if="counts.loose + counts.warn > 0" :x="C" :y="C + 38" text-anchor="middle" class="c-alert">{{ counts.loose }} 松动 · {{ counts.warn }} 预警</text>
      <text v-else :x="C" :y="C + 38" text-anchor="middle" class="c-ok">预紧力均衡</text>
    </svg>

    <div class="radar-legend">
      <span class="lg"><i class="ring-c" style="border-color:#22d3ee" /> 目标环 (100%)</span>
      <span class="lg"><i class="ring-c" style="border-color:#f43f5e;border-style:dashed" /> 下限环</span>
      <span v-for="k in ['normal', 'warn', 'loose']" :key="k" class="lg"><i :style="`background:${STATUS[k].color}`" /> {{ STATUS[k].label }} {{ counts[k] }}</span>
    </div>
    <div class="radar-hint">节点离圆心越近 = 残余预紧力越低；跌破红色下限环即松动</div>
  </div>
</template>

<style scoped>
.radar { display: flex; flex-direction: column; align-items: center; gap: 12px; }
.radar-svg { width: 100%; max-width: 480px; height: auto; }
.node { cursor: pointer; }
.node-no { font-family: var(--font-mono); font-size: 11px; font-weight: 600; fill: #04121a; }
.node:hover circle { stroke: #fff; stroke-width: 2; }
.node.alert .node-no { fill: #fff; }
.halo { animation: halo 1.8s ease-out infinite; transform-origin: center; transform-box: fill-box; }
@keyframes halo { 0% { transform: scale(0.7); opacity: 0.45; } 100% { transform: scale(1.7); opacity: 0; } }
.ring-lb { font-size: 10px; font-family: var(--font-mono); opacity: 0.9; }
.c-num { font-family: var(--font-mono); font-size: 38px; font-weight: 700; fill: var(--text-1); }
.c-cap { font-size: 12px; fill: var(--text-3); }
.c-alert { font-size: 12px; fill: var(--crit); font-weight: 600; }
.c-ok { font-size: 12px; fill: var(--ok); }
.radar-legend { display: flex; gap: 16px; flex-wrap: wrap; justify-content: center; font-size: 12px; color: var(--text-2); }
.radar-legend .lg { display: flex; align-items: center; gap: 6px; }
.radar-legend i { width: 10px; height: 10px; border-radius: 50%; display: inline-block; }
.radar-legend .ring-c { background: none; border: 1.5px solid; border-radius: 50%; }
.radar-hint { font-size: 11.5px; color: var(--text-3); }
</style>
