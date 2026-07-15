<script setup lang="ts">
import { onMounted, onUnmounted, ref, computed } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { deviceApi, runtimeApi, boltApi, type ChannelLive } from '../api'

const router = useRouter()
const deviceId = ref<number | null>(null)
const rows = ref<ChannelLive[]>([])
const loading = ref(true)
let timer: number | undefined

// 每通道近期回波/峰峰值历史,用于检测"敲击"引起的跳动
const hist = new Map<number, { echo: number[]; p2p: number[] }>()
const moveScore = ref<Record<number, number>>({})
const labelEdit = ref<Record<number, string>>({})
const savingPos = ref<number | null>(null)

const ECHO_JUMP_NS = 15 // 回波跳动阈值(ns)
const P2P_JUMP = 0.3 // 峰峰值相对跳动阈值
const PROBE_P2P = 150 // 判定"接了探头"的峰峰值下限(噪声底约 30~40)

async function poll() {
  if (deviceId.value == null) return
  try {
    const d = await deviceApi.channelsLive(deviceId.value)
    rows.value = d.items
    // 输入框用现有位号预填（仅首次,不覆盖正在编辑的内容）,避免"没输入就点标注"把已有位号清空
    for (const it of d.items) {
      if (labelEdit.value[it.channelNo] === undefined && it.positionLabel)
        labelEdit.value[it.channelNo] = it.positionLabel
    }
    const score: Record<number, number> = {}
    for (const it of d.items) {
      const h = hist.get(it.channelNo) ?? { echo: [], p2p: [] }
      if (it.echoNs != null) { h.echo.push(Number(it.echoNs)); if (h.echo.length > 6) h.echo.shift() }
      if (it.peakToPeak != null) { h.p2p.push(it.peakToPeak); if (h.p2p.length > 6) h.p2p.shift() }
      hist.set(it.channelNo, h)
      // 跳动评分(仅对真探头通道有意义,噪声通道随机抖动不计):
      // 回波近期极差(ns)优先;无回波的探头用峰峰值相对变化折算
      const probe = (it.peakToPeak ?? 0) >= PROBE_P2P || it.echoNs != null
      let s = 0
      if (probe) {
        if (h.echo.length >= 2) s = Math.max(...h.echo) - Math.min(...h.echo)
        if (s < ECHO_JUMP_NS && h.p2p.length >= 2) {
          const mn = Math.min(...h.p2p), mx = Math.max(...h.p2p)
          if (mn > 0 && (mx - mn) / mn >= P2P_JUMP) s = ECHO_JUMP_NS + 5
        }
      }
      score[it.channelNo] = s
    }
    moveScore.value = score
  } catch {
    /* 尽力轮询 */
  } finally {
    loading.value = false
  }
}

const route = useRoute()
onMounted(async () => {
  try {
    const rt = await runtimeApi.devices()
    // 优先取路由参数（从设备页跳转时保持同一设备），否则主设备/第一个设备；无设备则留空
    const fromQuery = Number(route.query.deviceId)
    deviceId.value =
      (Number.isFinite(fromQuery) && rt.items.some((x) => x.deviceId === fromQuery) ? fromQuery : null)
      ?? rt.items.find((x) => x.isPrimary)?.deviceId
      ?? rt.items[0]?.deviceId
      ?? null
  } catch {
    deviceId.value = null
  }
  if (deviceId.value == null) {
    loading.value = false
    return
  }
  await poll()
  timer = window.setInterval(() => { if (!document.hidden) poll() }, 1000)
})
onUnmounted(() => timer && clearInterval(timer))

// 按信号强度(峰峰值)排序:真探头通道恒在上,噪声通道沉底(不被随机抖动顶上来)
const sorted = computed(() =>
  [...rows.value].sort((a, b) => (b.echoNs != null ? 1 : 0) - (a.echoNs != null ? 1 : 0) || (b.peakToPeak ?? 0) - (a.peakToPeak ?? 0)),
)
const topMover = computed(() => {
  let best = -1, bestS = ECHO_JUMP_NS
  for (const [ch, s] of Object.entries(moveScore.value)) if (s > bestS) { bestS = s; best = Number(ch) }
  return best
})
function isProbe(it: ChannelLive) {
  return (it.peakToPeak ?? 0) >= PROBE_P2P || it.echoNs != null
}
function sigPct(p2p: number | null) {
  return Math.min(100, Math.round(((p2p ?? 0) / 1500) * 100))
}

async function savePosition(it: ChannelLive) {
  if (it.boltId == null) return ElMessage.warning('该通道未绑定螺栓,无法标位置')
  const label = (labelEdit.value[it.channelNo] ?? '').trim()
  // 清空已有位号必须是明确动作，不能因为没输入就静默清掉
  if (!label && it.positionLabel) {
    try {
      await ElMessageBox.confirm(`输入为空，将清空 CH${it.channelNo} 的现有位号「${it.positionLabel}」，确认？`, '清空位号', { type: 'warning', confirmButtonText: '清空', cancelButtonText: '取消' })
    } catch { return }
  }
  savingPos.value = it.channelNo
  try {
    await boltApi.updateSettings(it.boltId, { positionLabel: label })
    ElMessage.success(`通道 ${it.channelNo} 位置已标为「${label || '(清空)'}」`)
    await poll()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '保存失败')
  } finally {
    savingPos.value = null
  }
}
</script>

<template>
  <div class="ident rise" v-loading="loading">
    <div class="ident-hd panel">
      <div class="ident-intro">
        <span class="panel-title"><i class="hd-mark" />通道识别</span>
        <p class="sub">依次<b>轻敲或微动每一颗螺栓</b>,信号跳动的通道会自动高亮,即为该螺栓对应的通道,可就地填写现场位号。</p>
      </div>
      <div class="legend">
        <span><i class="dot moving" /> 正在跳动(当前敲击)</span>
        <span><i class="dot probe" /> 有探头(强信号)</span>
        <span><i class="dot weak" /> 弱/无信号</span>
      </div>
    </div>

    <div class="grid">
      <div
        v-for="it in sorted" :key="it.channelNo"
        class="ch panel" :class="{ moving: topMover === it.channelNo, weak: !isProbe(it) }">
        <div class="ch-hd">
          <span class="ch-no">CH{{ it.channelNo }}</span>
          <span v-if="topMover === it.channelNo" class="moving-tag">← 正在动</span>
          <span class="ch-bolt mono">{{ it.boltCode || '未绑螺栓' }}</span>
        </div>

        <div class="ch-echo mono">
          <template v-if="it.echoNs != null">{{ Number(it.echoNs).toFixed(0) }}<i>ns</i></template>
          <template v-else><span class="noecho">无声时</span></template>
        </div>

        <div class="ch-sig">
          <div class="sig-bar"><div class="sig-fill" :style="{ width: sigPct(it.peakToPeak) + '%' }" /></div>
          <span class="sig-txt mono">峰峰 {{ it.peakToPeak ?? '—' }} · SNR {{ it.snrDb != null ? Number(it.snrDb).toFixed(0) : '—' }}dB</span>
        </div>

        <div class="ch-move" :style="`opacity:${Math.min(1, (moveScore[it.channelNo] ?? 0) / 30)}`">
          跳动 {{ (moveScore[it.channelNo] ?? 0).toFixed(0) }}
        </div>

        <div class="ch-pos">
          <el-input v-model="labelEdit[it.channelNo]" :placeholder="it.positionLabel || '位置,如 12点'" size="small" :disabled="it.boltId == null" />
          <el-button size="small" type="primary" :loading="savingPos === it.channelNo" :disabled="it.boltId == null" @click="savePosition(it)">标注</el-button>
        </div>
        <div v-if="it.positionLabel" class="ch-curpos">当前: {{ it.positionLabel }}</div>
        <div class="ch-open" @click="it.boltId && router.push({ name: 'boltDetail', params: { boltId: it.boltId } })">
          <span v-if="it.boltId">查看实时波形 →</span>
        </div>
      </div>
      <div v-if="!loading && !rows.length" class="empty">
        {{ deviceId == null ? '未找到采集设备运行时,请先到「采集设备」页确认设备已接入' : '暂无在出数的通道(设备可能离线或探头未接)' }}
      </div>
    </div>
  </div>
</template>

<style scoped>
.ident { display: flex; flex-direction: column; gap: 16px; }
.ident-hd { display: flex; align-items: center; justify-content: space-between; gap: 24px; flex-wrap: wrap; padding: 16px 20px; }
.ident-intro { display: flex; flex-direction: column; gap: 8px; min-width: 0; }
.sub { margin: 0; font-size: 13px; color: var(--text-3); max-width: 640px; line-height: 1.6; }
.sub b { color: var(--accent); }
.legend { display: flex; flex-direction: column; gap: 6px; font-size: 12px; color: var(--text-3); }
.legend span { display: flex; align-items: center; gap: 7px; }
.dot { width: 10px; height: 10px; border-radius: 50%; display: inline-block; }
.dot.moving { background: var(--warn); }
.dot.probe { background: var(--ok); }
.dot.weak { background: #3b4659; }

.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 14px; }
.ch { padding: 14px 16px; transition: border-color 0.2s, box-shadow 0.2s, transform 0.2s, opacity 0.2s; }
.ch.weak { opacity: 0.5; }
.ch.moving { border-color: var(--warn); box-shadow: 0 0 0 1px var(--warn), 0 10px 28px -14px var(--warn-glow); transform: translateY(-1px); }
.ch-hd { display: flex; align-items: center; gap: 8px; }
.ch-no { font-size: 15px; font-weight: 700; }
.moving-tag { font-size: 11px; color: var(--warn); font-weight: 700; animation: blink 0.8s steps(2) infinite; }
@keyframes blink { 50% { opacity: 0.3; } }
.ch-bolt { font-size: 11px; color: var(--text-3); margin-left: auto; }
.ch-echo { font-size: 26px; font-weight: 700; margin: 8px 0 4px; color: var(--accent); }
.ch-echo i { font-size: 12px; color: var(--text-3); font-style: normal; margin-left: 3px; }
.noecho { font-size: 13px; color: var(--text-3); font-weight: 400; }
.ch-sig { display: flex; flex-direction: column; gap: 4px; margin: 6px 0; }
.sig-bar { height: 6px; border-radius: 3px; background: rgba(148, 163, 184, 0.12); overflow: hidden; }
.sig-fill { height: 100%; background: linear-gradient(90deg, var(--accent), var(--ok)); border-radius: 3px; transition: width 0.4s; }
.sig-txt { font-size: 10.5px; color: var(--text-3); }
.ch-move { font-size: 11px; color: var(--warn); height: 16px; }
.ch-pos { display: flex; gap: 6px; margin-top: 8px; }
.ch-curpos { font-size: 11px; color: var(--accent-2); margin-top: 5px; }
.ch-open { font-size: 11px; color: var(--accent); cursor: pointer; margin-top: 8px; height: 14px; }
.ch-open:hover { text-decoration: underline; }
.empty { grid-column: 1 / -1; text-align: center; color: var(--text-3); padding: 40px; }
</style>
