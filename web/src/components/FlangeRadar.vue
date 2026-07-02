<script setup lang="ts">
import { computed } from 'vue'
import type { GroupBolt } from '../api'
import { pct, STATUS_META } from '../utils/preload'

// 法兰螺栓布置图（俯视 bolt circle）：
// - 螺栓按【现场位号】(position_label "N号"）顺时针均匀落在螺栓圆上，1 号位于 12 点方向；
// - 总孔位数取 组 expected_bolt_count / 最大位号 / 监测数 的最大值，未监测位号画成空心孔——
//   与真实法兰一一对应，回答"现场哪一颗"；
// - 点色 = 状态（正常/预警/松动/偏高/未标定/无数据），外侧小字 = 保持率%；
// - 不再用"半径=保持率"的雷达半径编码（视觉不均匀且无位置语义）。
const props = defineProps<{ bolts: GroupBolt[]; totalHoles?: number | null }>()
const emit = defineEmits<{ (e: 'select', boltId: number): void }>()

const STATUS: Record<string, { label: string; color: string; cls: string }> = STATUS_META

// 画布比法兰大一圈：给外圈保持率标签留边距，避免左右边缘的 "149%" 被 viewBox 裁成 "9%"
const SIZE = 532
const C = SIZE / 2
const R_OUT = 196   // 法兰外缘
const R_IN = 118    // 法兰内缘
const R_BOLT = 157  // 螺栓圆
const R_LABEL = R_OUT + 22 // 保持率标签半径

function pol(angDeg: number, r: number): [number, number] {
  const a = (angDeg * Math.PI) / 180
  return [C + r * Math.cos(a), C + r * Math.sin(a)]
}

// 现场位号：position_label 数字（"5号"→5）优先，其次 code 尾数，最后数组序。
function seqOf(b: GroupBolt, i: number): number {
  const fromPos = b.positionLabel?.match(/(\d+)/)
  if (fromPos) return Number(fromPos[1])
  const fromCode = b.code.match(/(\d+)\s*$/)
  if (fromCode) return Number(fromCode[1])
  return i + 1
}

interface Hole {
  seq: number
  x: number
  y: number
  bolt: GroupBolt | null
  res: number | null
  color: string
  alert: boolean
  lx: number
  ly: number
  anchor: 'start' | 'middle' | 'end'
}

const holes = computed<Hole[]>(() => {
  const seqs = props.bolts.map((b, i) => ({ b, seq: seqOf(b, i) }))
  const maxSeq = Math.max(props.totalHoles ?? 0, props.bolts.length, ...seqs.map((s) => s.seq), 1)
  const bySeq = new Map(seqs.map((s) => [s.seq, s.b]))
  return Array.from({ length: maxSeq }, (_, k) => {
    const seq = k + 1
    const ang = -90 + (k * 360) / maxSeq // 1 号 12 点起，顺时针
    const [x, y] = pol(ang, R_BOLT)
    const [lx, ly] = pol(ang, R_LABEL)
    const cos = Math.cos((ang * Math.PI) / 180)
    const anchor: Hole['anchor'] = cos > 0.35 ? 'start' : cos < -0.35 ? 'end' : 'middle'
    const bolt = bySeq.get(seq) ?? null
    const res = bolt?.preloadRatio != null ? Number(bolt.preloadRatio) : null
    return {
      seq, x, y, bolt, res,
      color: bolt ? STATUS[bolt.status]?.color ?? '#5b6b82' : 'transparent',
      alert: bolt ? bolt.status === 'loose' || bolt.status === 'warn' || bolt.status === 'high' : false,
      lx, ly, anchor,
    }
  })
})

// 孔位多时外圈不标保持率（挤成一团反而不专业），悬停/网格视图看数值
const showPct = computed(() => holes.value.length <= 16)

const counts = computed(() => {
  const c: Record<string, number> = { normal: 0, warn: 0, loose: 0, high: 0, uncalibrated: 0, nodata: 0 }
  for (const b of props.bolts) c[b.status] = (c[b.status] ?? 0) + 1
  return c
})
const unmonitored = computed(() => holes.value.filter((h) => !h.bolt).length)

// 方位刻度（12 点起顺时针 0/90/180/270）
const ticks = [-90, 0, 90, 180].map((a) => {
  const [x1, y1] = pol(a, R_OUT + 3)
  const [x2, y2] = pol(a, R_OUT + 10)
  return { x1, y1, x2, y2 }
})
</script>

<template>
  <div class="flange">
    <svg :viewBox="`0 0 ${SIZE} ${SIZE}`" class="flange-svg">
      <!-- 法兰环带（外缘/内缘） -->
      <circle :cx="C" :cy="C" :r="R_OUT" fill="rgba(148,163,184,0.035)" stroke="rgba(148,163,184,0.28)" stroke-width="1.5" />
      <circle :cx="C" :cy="C" :r="R_IN" fill="var(--bg-elev-1, #0d1320)" stroke="rgba(148,163,184,0.2)" stroke-width="1" />
      <!-- 螺栓圆（点位所在的分度圆） -->
      <circle :cx="C" :cy="C" :r="R_BOLT" fill="none" stroke="rgba(148,163,184,0.14)" stroke-width="1" stroke-dasharray="3 6" />
      <!-- 方位刻度 -->
      <line v-for="(t, i) in ticks" :key="'t' + i" :x1="t.x1" :y1="t.y1" :x2="t.x2" :y2="t.y2" stroke="rgba(148,163,184,0.3)" stroke-width="1" />

      <!-- 孔位：未监测 = 空心孔；监测 = 状态色实心点 + 位号 -->
      <g v-for="h in holes" :key="h.seq">
        <template v-if="h.bolt">
          <g class="node" :class="{ alert: h.alert }" @click="emit('select', h.bolt!.id)">
            <circle v-if="h.alert" :cx="h.x" :cy="h.y" r="17" :fill="h.color" opacity="0.18" />
            <circle :cx="h.x" :cy="h.y" r="12" :fill="h.color" stroke="rgba(4,18,26,0.55)" stroke-width="1" />
            <text :x="h.x" :y="h.y + 3.5" text-anchor="middle" class="node-no">{{ h.seq }}</text>
            <title>{{ h.bolt.positionLabel || h.seq + '号' }} · {{ h.bolt.code }} · {{ STATUS[h.bolt.status]?.label }}{{ h.res != null ? ' · 保持 ' + pct(h.res) : '' }} · CH{{ h.bolt.channelNo }}</title>
          </g>
          <text v-if="showPct && h.res != null" :x="h.lx" :y="h.ly + 3.5" :text-anchor="h.anchor" class="pct-lb" :fill="h.color">{{ pct(h.res) }}</text>
        </template>
        <template v-else>
          <circle :cx="h.x" :cy="h.y" r="8" fill="none" stroke="rgba(148,163,184,0.3)" stroke-width="1.2" />
          <text :x="h.x" :y="h.y + 3" text-anchor="middle" class="hole-no">{{ h.seq }}</text>
          <title>{{ h.seq }}号 · 未监测孔位</title>
        </template>
      </g>

      <!-- 中心统计 -->
      <text :x="C" :y="C - 8" text-anchor="middle" class="c-num">{{ bolts.length }}<tspan class="c-den">/{{ holes.length }}</tspan></text>
      <text :x="C" :y="C + 12" text-anchor="middle" class="c-cap">监测 / 孔位</text>
      <text v-if="counts.loose + counts.warn + counts.high > 0" :x="C" :y="C + 34" text-anchor="middle" class="c-alert">{{ counts.loose }} 松动 · {{ counts.warn }} 预警 · {{ counts.high }} 偏高</text>
      <text v-else :x="C" :y="C + 34" text-anchor="middle" class="c-ok">预紧力均衡</text>
    </svg>

    <div class="flange-legend">
      <span v-for="k in ['normal', 'warn', 'loose', 'high']" :key="k" class="lg"><i :style="`background:${STATUS[k].color}`" /> {{ STATUS[k].label }} {{ counts[k] }}</span>
      <span v-if="counts.uncalibrated" class="lg"><i :style="`background:${STATUS.uncalibrated.color}`" /> 未标定 {{ counts.uncalibrated }}</span>
      <span v-if="counts.nodata" class="lg"><i :style="`background:${STATUS.nodata.color}`" /> 无数据 {{ counts.nodata }}</span>
      <span v-if="unmonitored" class="lg"><i class="hole-c" /> 未监测孔位 {{ unmonitored }}</span>
    </div>
    <div class="flange-hint">按现场位号顺时针排布（1 号位于 12 点方向）· 点内数字 = 位号 · 外圈 = 保持率 · 点击节点查看详情</div>
  </div>
</template>

<style scoped>
.flange { display: flex; flex-direction: column; align-items: center; gap: 12px; }
.flange-svg { width: 100%; max-width: 480px; height: auto; }
.node { cursor: pointer; }
.node-no { font-family: var(--font-mono); font-size: 11px; font-weight: 600; fill: #04121a; }
.node:hover circle { stroke: #fff; stroke-width: 2; }
.node.alert .node-no { fill: #fff; }
.hole-no { font-family: var(--font-mono); font-size: 9px; fill: rgba(148, 163, 184, 0.45); }
.pct-lb { font-family: var(--font-mono); font-size: 10.5px; font-weight: 600; }
.c-num { font-family: var(--font-mono); font-size: 36px; font-weight: 700; fill: var(--text-1); }
.c-den { font-size: 16px; fill: var(--text-3); font-weight: 500; }
.c-cap { font-size: 11px; fill: var(--text-3); letter-spacing: 0.1em; }
.c-alert { font-size: 12px; fill: var(--crit); font-weight: 600; }
.c-ok { font-size: 12px; fill: var(--ok); }
.flange-legend { display: flex; gap: 16px; flex-wrap: wrap; justify-content: center; font-size: 12px; color: var(--text-2); }
.flange-legend .lg { display: flex; align-items: center; gap: 6px; }
.flange-legend i { width: 10px; height: 10px; border-radius: 50%; display: inline-block; }
.flange-legend .hole-c { background: none; border: 1.2px solid rgba(148, 163, 184, 0.45); }
.flange-hint { font-size: 11.5px; color: var(--text-3); }
</style>
