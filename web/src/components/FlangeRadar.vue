<script setup lang="ts">
import { computed, ref } from 'vue'
import type { GroupBolt } from '../api'
import { pct, STATUS_META } from '../utils/preload'

// 法兰螺栓布置图（俯视 bolt circle）：
// - 螺栓按【现场位号】(position_label "N号"）顺时针均匀落在螺栓圆上，1 号位于 12 点方向；
// - 总孔位数取 组 expected_bolt_count / 最大位号 / 监测数 的最大值，未监测位号画成空心孔——
//   与真实法兰一一对应，回答"现场哪一颗"；
// - 点色 = 状态（正常/预警/松动/偏高/未标定/无数据），外侧小字 = 保持率%；
// - 不再用"半径=保持率"的雷达半径编码（视觉不均匀且无位置语义）。
// - 位号冲突（多颗螺栓解析到同一孔位）不再静默覆盖：孔位画警示环 + ×N 徽标，
//   tooltip 列出全部冲突螺栓，图下方警示条给出处理指引（现场多接/接错的高发场景）。
// - 监测数超过配置孔位时圆盘会自适应重排，但同时给出警示条提示核对组孔位数。
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

// 现场位号：position_label 优先，其次 code 尾数，最后数组序。
// 解析规则："5号"/"第5号" 形式优先（避免 "M36-5号" 先吃到 36 落错孔位），
// 否则取【最后一段】数字（"法兰12-3" → 3）；解析出 0 或非法值按未解析处理（圆盘只画 1..maxSeq，seq=0 会凭空消失）。
function seqOf(b: GroupBolt, i: number): number {
  const pick = (m: RegExpMatchArray | null | undefined) => {
    const n = m ? Number(m[1]) : NaN
    return Number.isFinite(n) && n >= 1 ? n : null
  }
  const label = b.positionLabel ?? ''
  return pick(label.match(/(\d+)\s*号/))
    ?? pick(label.match(/(\d+)(?!.*\d)/))
    ?? pick(b.code.match(/(\d+)\s*$/))
    ?? i + 1
}

interface Hole {
  seq: number
  x: number
  y: number
  bolt: GroupBolt | null
  /// 同位号的其余螺栓（位号冲突时 >0；bolt 为第一颗）
  others: GroupBolt[]
  res: number | null
  color: string
  alert: boolean
  tip: string
  lx: number
  ly: number
  anchor: 'start' | 'middle' | 'end'
}

function tipOf(list: GroupBolt[], seq: number): string {
  const b = list[0]
  const base = `${b.positionLabel || seq + '号'} · ${b.code} · ${STATUS[b.status]?.label ?? b.status}${
    b.preloadRatio != null ? ' · 保持 ' + pct(Number(b.preloadRatio)) : ''
  } · CH${b.channelNo}`
  if (list.length <= 1) return base
  return `位号冲突：${list.length} 颗螺栓同标 ${seq} 号位 —— ${list
    .map((x) => `${x.code}(CH${x.channelNo})`)
    .join('、')}。点击可逐颗查看，请到「点位与验收」重标位号`
}

const holes = computed<Hole[]>(() => {
  const seqs = props.bolts.map((b, i) => ({ b, seq: seqOf(b, i) }))
  const maxSeq = Math.max(props.totalHoles ?? 0, props.bolts.length, ...seqs.map((s) => s.seq), 1)
  // 位号冲突（多接/接错/重复标号）保留全部螺栓，不再静默覆盖
  const bySeq = new Map<number, GroupBolt[]>()
  for (const s of seqs) {
    const arr = bySeq.get(s.seq)
    if (arr) arr.push(s.b)
    else bySeq.set(s.seq, [s.b])
  }
  return Array.from({ length: maxSeq }, (_, k) => {
    const seq = k + 1
    const ang = -90 + (k * 360) / maxSeq // 1 号 12 点起，顺时针
    const [x, y] = pol(ang, R_BOLT)
    const [lx, ly] = pol(ang, R_LABEL)
    const cos = Math.cos((ang * Math.PI) / 180)
    const anchor: Hole['anchor'] = cos > 0.35 ? 'start' : cos < -0.35 ? 'end' : 'middle'
    const list = bySeq.get(seq) ?? []
    const bolt = list[0] ?? null
    const res = bolt?.preloadRatio != null ? Number(bolt.preloadRatio) : null
    return {
      seq, x, y, bolt, others: list.slice(1), res,
      color: bolt ? STATUS[bolt.status]?.color ?? '#5b6b82' : 'transparent',
      alert: bolt ? bolt.status === 'loose' || bolt.status === 'warn' || bolt.status === 'high' : false,
      tip: bolt ? tipOf(list, seq) : `${seq}号 · 未监测孔位`,
      lx, ly, anchor,
    }
  })
})

// 位号冲突孔位（多接/接错高发信号）：警示条 + 图例都要显式暴露
const conflictHoles = computed(() => holes.value.filter((h) => h.others.length > 0))
const conflictDesc = computed(() =>
  conflictHoles.value
    .map((h) => `${h.seq}号(${[h.bolt!, ...h.others].map((b) => b.code).join('、')})`)
    .join('；'),
)
// 监测数超过配置孔位：圆盘已按实际数自适应，但配置很可能没跟上现场
const overCapacity = computed(
  () => props.totalHoles != null && props.totalHoles > 0 && props.bolts.length > props.totalHoles,
)

// 孔位多时外圈不标保持率（挤成一团反而不专业），悬停/网格视图看数值
const showPct = computed(() => holes.value.length <= 16)

// 冲突孔位点击：弹出选择列表(原先只能进第一颗,其余螺栓从图上无路可达)
const pick = ref<{ visible: boolean; seq: number; list: GroupBolt[] }>({ visible: false, seq: 0, list: [] })
function onNodeClick(h: Hole) {
  if (!h.bolt) return
  if (h.others.length) {
    pick.value = { visible: true, seq: h.seq, list: [h.bolt, ...h.others] }
    return
  }
  emit('select', h.bolt.id)
}
function pickBolt(id: number) {
  pick.value.visible = false
  emit('select', id)
}

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

      <!-- 孔位：未监测 = 空心孔；监测 = 状态色实心点 + 位号；位号冲突 = 警示环 + ×N 徽标 -->
      <g v-for="h in holes" :key="h.seq">
        <template v-if="h.bolt">
          <g class="node" :class="{ alert: h.alert }" @click="onNodeClick(h)">
            <circle v-if="h.alert" :cx="h.x" :cy="h.y" r="17" :fill="h.color" opacity="0.18" />
            <circle v-if="h.others.length" :cx="h.x" :cy="h.y" r="16.5" fill="none" stroke="#f43f5e" stroke-width="1.6" stroke-dasharray="4 3" class="conflict-ring" />
            <circle :cx="h.x" :cy="h.y" r="12" :fill="h.color" stroke="rgba(4,18,26,0.55)" stroke-width="1" />
            <text :x="h.x" :y="h.y + 3.5" text-anchor="middle" class="node-no">{{ h.seq }}</text>
            <g v-if="h.others.length">
              <circle :cx="h.x + 13" :cy="h.y - 13" r="8" fill="#f43f5e" stroke="rgba(4,18,26,0.55)" stroke-width="1" />
              <text :x="h.x + 13" :y="h.y - 10" text-anchor="middle" class="conflict-no">×{{ h.others.length + 1 }}</text>
            </g>
            <title>{{ h.tip }}</title>
          </g>
          <text v-if="showPct && h.res != null" :x="h.lx" :y="h.ly + 3.5" :text-anchor="h.anchor" class="pct-lb" :fill="h.color">{{ pct(h.res) }}</text>
        </template>
        <template v-else>
          <circle :cx="h.x" :cy="h.y" r="8" fill="none" stroke="rgba(148,163,184,0.3)" stroke-width="1.2" />
          <text :x="h.x" :y="h.y + 3" text-anchor="middle" class="hole-no">{{ h.seq }}</text>
          <title>{{ h.tip }}</title>
        </template>
      </g>

      <!-- 中心统计 -->
      <text :x="C" :y="C - 8" text-anchor="middle" class="c-num">{{ bolts.length }}<tspan class="c-den">/{{ holes.length }}</tspan></text>
      <text :x="C" :y="C + 12" text-anchor="middle" class="c-cap">监测 / 孔位</text>
      <text v-if="counts.loose + counts.warn + counts.high > 0" :x="C" :y="C + 34" text-anchor="middle" class="c-alert">{{ counts.loose }} 松动 · {{ counts.warn }} 预警 · {{ counts.high }} 偏高</text>
      <text v-else :x="C" :y="C + 34" text-anchor="middle" class="c-ok">预紧力均衡</text>
    </svg>

    <div v-if="conflictHoles.length" class="flange-warn">
      位号冲突 {{ conflictHoles.length }} 处：{{ conflictDesc }} —— 点击冲突孔位可逐颗查看，请到「点位与验收」重标位号
    </div>
    <div v-if="overCapacity" class="flange-warn">
      监测 {{ bolts.length }} 颗 &gt; 配置孔位 {{ totalHoles }}，圆盘已按 {{ holes.length }} 孔重排 —— 请核对组孔位数或位号
    </div>

    <div class="flange-legend">
      <span v-for="k in ['normal', 'warn', 'loose', 'high']" :key="k" class="lg"><i :style="`background:${STATUS[k].color}`" /> {{ STATUS[k].label }} {{ counts[k] }}</span>
      <span v-if="counts.uncalibrated" class="lg"><i :style="`background:${STATUS.uncalibrated.color}`" /> 未标定 {{ counts.uncalibrated }}</span>
      <span v-if="counts.nodata" class="lg"><i :style="`background:${STATUS.nodata.color}`" /> 无数据 {{ counts.nodata }}</span>
      <span v-if="unmonitored" class="lg"><i class="hole-c" /> 未监测孔位 {{ unmonitored }}</span>
      <span v-if="conflictHoles.length" class="lg lg-conflict"><i class="conflict-c" /> 位号冲突 {{ conflictHoles.length }}</span>
    </div>
    <div class="flange-hint">按现场位号顺时针排布（1 号位于 12 点方向）· 点内数字 = 位号 · 外圈 = 保持率 · 点击节点查看详情</div>

    <!-- 冲突孔位选择：列出同位号全部螺栓,逐颗可进详情 -->
    <el-dialog v-model="pick.visible" :title="`${pick.seq} 号孔位 · ${pick.list.length} 颗螺栓（位号冲突）`" width="420px">
      <div class="pick-list">
        <div v-for="b in pick.list" :key="b.id" class="pick-row" @click="pickBolt(b.id)">
          <span class="led" :class="STATUS[b.status]?.cls || 'led--off'" />
          <span class="mono pick-code">{{ b.code }}</span>
          <span class="mono dim">CH{{ b.channelNo }}</span>
          <span class="pick-st" :style="`color:${STATUS[b.status]?.color}`">{{ STATUS[b.status]?.label || b.status }}</span>
          <span class="mono dim">{{ b.preloadRatio != null ? pct(Number(b.preloadRatio)) : '—' }}</span>
        </div>
      </div>
      <p class="pick-hint">同一孔位出现多颗螺栓，多为现场位号标注重复，请到「点位与验收」重标位号。</p>
    </el-dialog>
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
.flange-legend .conflict-c { background: none; border: 1.6px dashed #f43f5e; }
.flange-legend .lg-conflict { color: #f43f5e; font-weight: 600; }
.conflict-no { font-family: var(--font-mono); font-size: 8.5px; font-weight: 700; fill: #fff; }
.conflict-ring { animation: conflict-pulse 1.6s ease-in-out infinite; }
@keyframes conflict-pulse { 50% { opacity: 0.35; } }
.flange-warn {
  max-width: 480px; text-align: center; font-size: 12px; line-height: 1.6; color: #f43f5e;
  background: rgba(244, 63, 94, 0.08); border: 1px solid rgba(244, 63, 94, 0.3);
  border-radius: 8px; padding: 6px 14px;
}
.flange-hint { font-size: 11.5px; color: var(--text-3); }
.pick-list { display: flex; flex-direction: column; }
.pick-row { display: flex; align-items: center; gap: 12px; padding: 10px 8px; border-radius: 8px; cursor: pointer; font-size: 13px; }
.pick-row:hover { background: rgba(56, 189, 248, 0.07); }
.pick-code { font-weight: 600; flex: 1; }
.pick-st { font-size: 12px; }
.pick-hint { margin: 10px 0 0; font-size: 12px; color: var(--text-3); line-height: 1.6; }
.dim { color: var(--text-3); }
</style>
