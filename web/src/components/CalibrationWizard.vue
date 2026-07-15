<script setup lang="ts">
// 引导式标定向导:把"设基线 → 拧紧 → 扭矩反推 K_L"的计量流程翻译成现场工人的语言。
// 设计原则:
// 1. 客户只回答"螺栓现在什么状态、用了多大扭矩"——K_L/量纲/温补全部系统代劳;
// 2. 不可行的事情不提供(已拧紧且不能松的螺栓,物理上无法现场标出绝对值,只给趋势监测,不给假数字);
// 3. 每一步实时显示声时稳定性,采样中给进度,完成页直接给"当前预紧力 XX kN(目标的 XX%)"。
import { computed, onUnmounted, reactive, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { boltApi, deviceApi, calibrationApi, type BoltListItem, type BoltChannelConfig } from '../api'

const props = withDefaults(defineProps<{
  modelValue: boolean
  boltId: number | null
  channelId: number | null
  boltCode?: string
}>(), { boltCode: '' })

const emit = defineEmits<{ 'update:modelValue': [boolean]; done: []; expert: [] }>()

const visible = computed({
  get: () => props.modelValue,
  set: (v) => emit('update:modelValue', v),
})

// ---- 向导状态机 ----
type Step =
  | 'scene'            // 选场景
  | 'baseline-confirm' // A/C:确认螺栓处于基准状态
  | 'baseline-sampling'// 采集稳定声时并写基线
  | 'tighten'          // A:去现场拧紧
  | 'torque-input'     // A:输入安装扭矩
  | 'calibrating'      // A:采样计算中
  | 'done'             // A:完成(有绝对值)
  | 'done-rel'         // C:完成(趋势监测)
const step = ref<Step>('scene')
const scene = ref<'fresh' | 'inservice' | null>(null)

const bolt = ref<BoltListItem | null>(null)
const chCfg = ref<BoltChannelConfig | null>(null)
const busy = ref(false)
const errText = ref('')

// ---- 实时声时观测(基线/采样步共用) ----
const liveEcho = ref<number | null>(null)
const liveTemp = ref<number | null>(null)
const recentEchos = ref<number[]>([])
let pollTimer: number | undefined

const echoSpreadNs = computed(() => {
  if (recentEchos.value.length < 4) return null
  return Math.max(...recentEchos.value) - Math.min(...recentEchos.value)
})
// 稳定判据:最近样本极差 < 5ns(与后端 probe-scan 的 echoStd 良好线同量级)
const echoStable = computed(() => echoSpreadNs.value != null && echoSpreadNs.value < 5)

async function pollLive() {
  if (!chCfg.value) return
  try {
    const r = await deviceApi.channelsLive(chCfg.value.deviceId)
    const it = r.items.find((x) => x.channelNo === chCfg.value!.channelNo)
    if (it?.echoNs != null) {
      liveEcho.value = Number(it.echoNs)
      liveTemp.value = it.tempC != null ? Number(it.tempC) : null
      recentEchos.value.push(Number(it.echoNs))
      if (recentEchos.value.length > 10) recentEchos.value.shift()
    }
  } catch { /* 观测尽力 */ }
}
function startPoll() {
  stopPoll()
  recentEchos.value = []
  pollTimer = window.setInterval(pollLive, 1200)
  void pollLive()
}
function stopPoll() {
  if (pollTimer) { clearInterval(pollTimer); pollTimer = undefined }
}
onUnmounted(stopPoll)

// ---- 打开时加载上下文 ----
watch(() => props.modelValue, async (open) => {
  stopPoll()
  if (!open || props.boltId == null) return
  step.value = 'scene'
  scene.value = null
  errText.value = ''
  torque.torqueNm = null
  try {
    bolt.value = await boltApi.get(props.boltId)
    chCfg.value = await boltApi.channelConfig(props.boltId)
    if (bolt.value.installTorqueNm != null) torque.torqueNm = Number(bolt.value.installTorqueNm)
  } catch (e) {
    errText.value = (e as Error)?.message || '加载螺栓信息失败'
  }
})

// ---- 场景选择 ----
function chooseScene(s: 'fresh' | 'inservice') {
  scene.value = s
  step.value = 'baseline-confirm'
  startPoll()
}

// ---- 设基线(A 第 1 步 / C 唯一步) ----
const baselineResult = ref<{ refNs: number; refTemp: number } | null>(null)
async function setBaseline() {
  if (!chCfg.value) return
  busy.value = true
  errText.value = ''
  try {
    const r = await deviceApi.recalibrate(chCfg.value.deviceId, chCfg.value.channelNo)
    baselineResult.value = { refNs: Number(r.newRefTimeNs), refTemp: Number(r.newRefTempC) }
    chCfg.value = await boltApi.channelConfig(props.boltId!)
    if (scene.value === 'fresh') {
      step.value = 'tighten'
      stopPoll()
    } else {
      step.value = 'done-rel'
      stopPoll()
      emit('done')
    }
  } catch (e) {
    errText.value = (e as Error)?.message || '记录基准失败(设备需在线且声时稳定)'
  } finally {
    busy.value = false
  }
}

// 已设过基线的用户从"拧紧完成"直接续走
function resumeAfterTighten() {
  scene.value = 'fresh'
  step.value = 'torque-input'
}

// ---- 扭矩输入与标定(A 第 2 步) ----
const torque = reactive({ torqueNm: null as number | null })
const SAMPLE_TARGET = 20
const sampleCount = ref(0)
let sampleTimer: number | undefined

async function startCalibrate() {
  if (torque.torqueNm == null || torque.torqueNm <= 0) return ElMessage.warning('请输入拧紧时使用的扭矩(N·m)')
  step.value = 'calibrating'
  errText.value = ''
  sampleCount.value = 0
  startPoll()
  // 等窗口内攒够样本再提交(后端要求近 60s ≥20 条有效声时)
  sampleTimer = window.setInterval(async () => {
    try {
      const ms = await boltApi.measurements(props.boltId!, 30)
      const cutoff = Date.now() - 60_000
      sampleCount.value = ms.items.filter((m) => m.echoTimeNs != null && new Date(m.ts).getTime() > cutoff).length
      if (sampleCount.value >= SAMPLE_TARGET) {
        if (sampleTimer) { clearInterval(sampleTimer); sampleTimer = undefined }
        await submitTorque()
      }
    } catch { /* 下轮再试 */ }
  }, 2000)
}

async function submitTorque() {
  if (props.channelId == null) return
  busy.value = true
  try {
    await calibrationApi.torqueCalibrate(props.channelId, {
      torqueNm: torque.torqueNm!,
      note: `引导标定:安装扭矩 ${torque.torqueNm} N·m(向导流程:零载基线+扭矩反推)`,
    })
    bolt.value = await boltApi.get(props.boltId!)
    step.value = 'done'
    stopPoll()
    emit('done')
  } catch (e) {
    errText.value = (e as Error)?.message || '标定计算失败'
    step.value = 'torque-input'
  } finally {
    busy.value = false
  }
}
onUnmounted(() => { if (sampleTimer) clearInterval(sampleTimer) })

function close() {
  stopPoll()
  if (sampleTimer) { clearInterval(sampleTimer); sampleTimer = undefined }
  visible.value = false
}

const doneRatioPct = computed(() => {
  const r = bolt.value?.lastRatio
  return r != null ? (Number(r) * 100).toFixed(1) + '%' : null
})
const doneKn = computed(() => {
  const n = bolt.value?.lastPreloadN
  return n != null ? (Number(n) / 1000).toFixed(1) : null
})
</script>

<template>
  <el-dialog v-model="visible" :title="`标定向导 · ${boltCode || '螺栓'}`" width="620px" top="7vh" :close-on-click-modal="false" @close="close">
    <div v-if="errText" class="wz-err"><el-icon><WarningFilled /></el-icon>{{ errText }}</div>

    <!-- 场景选择 -->
    <template v-if="step === 'scene'">
      <p class="wz-lead">标定的作用:告诉系统"这颗螺栓拧到多紧时声音走多快",之后系统才能把超声读数换算成预紧力。请选择这颗螺栓的实际情况:</p>
      <div class="scene-cards">
        <div class="scene-card" @click="chooseScene('fresh')">
          <div class="sc-head"><el-icon><CircleCheckFilled /></el-icon> 新安装,或允许松开重新拧紧<span class="sc-tag">推荐</span></div>
          <div class="sc-body">两步完成:先在<b>未拧紧</b>状态记录基准,再拧紧到位并输入扭矩。标定后即可显示预紧力数值。</div>
          <div class="sc-req">需要:力矩扳手 · 全程约 3~5 分钟(不含拧紧时间)</div>
        </div>
        <div class="scene-card" @click="chooseScene('inservice')">
          <div class="sc-head"><el-icon><View /></el-icon> 已拧紧在役,不能松开</div>
          <div class="sc-body">把<b>当前状态记录为基准</b>,系统持续监测它相对现在是变松还是变紧(趋势与告警可用,<b>不显示绝对吨位</b>)。待检修窗口可升级为完整标定。</div>
          <div class="sc-req">需要:无工具 · 1 分钟</div>
        </div>
      </div>
      <div class="wz-foot-links">
        <span class="link" @click="resumeAfterTighten">已做过第一步基准,直接输入扭矩 →</span>
        <span class="link dim" @click="emit('expert'); close()">专家模式(载荷标定/手动参数)</span>
      </div>
    </template>

    <!-- 基准确认 + 实时稳定性 -->
    <template v-else-if="step === 'baseline-confirm' || step === 'baseline-sampling'">
      <div class="wz-stepline">{{ scene === 'fresh' ? '第 1 步 / 共 3 步:记录未拧紧基准' : '记录当前状态为基准' }}</div>
      <p class="wz-lead" v-if="scene === 'fresh'">
        请确认螺栓处于<b>未拧紧</b>状态(刚穿入、螺母仅手拧贴合,不受力)。确认后点下方按钮,系统会把当前的超声读数记录为"零载基准"。
      </p>
      <p class="wz-lead" v-else>
        无需任何操作,系统将把<b>此刻</b>的超声读数记录为比较基准。之后页面上会显示这颗螺栓相对现在的松紧变化。
      </p>
      <div class="live-box">
        <div class="lb-row">
          <span class="lb-k">当前超声读数</span>
          <b class="mono">{{ liveEcho != null ? liveEcho.toFixed(0) + ' ns' : '等待设备数据…' }}</b>
        </div>
        <div class="lb-row">
          <span class="lb-k">温度</span>
          <b class="mono">{{ liveTemp != null ? liveTemp.toFixed(1) + ' °C' : '—' }}</b>
        </div>
        <div class="lb-row">
          <span class="lb-k">信号稳定性</span>
          <b v-if="echoSpreadNs == null" class="dim">评估中…</b>
          <b v-else-if="echoStable" style="color: var(--ok)">稳定(波动 {{ echoSpreadNs.toFixed(1) }} ns)✓</b>
          <b v-else style="color: var(--warn)">波动较大({{ echoSpreadNs.toFixed(1) }} ns),请检查探头耦合后再试</b>
        </div>
      </div>
      <div class="wz-actions">
        <el-button @click="step = 'scene'; stopPoll()">上一步</el-button>
        <el-button type="primary" :loading="busy" :disabled="liveEcho == null" @click="setBaseline">
          {{ scene === 'fresh' ? '确认未拧紧,记录基准' : '记录当前为基准' }}
        </el-button>
      </div>
    </template>

    <!-- A:去拧紧 -->
    <template v-else-if="step === 'tighten'">
      <div class="wz-stepline">第 2 步 / 共 3 步:拧紧螺栓</div>
      <div class="wz-ok-note"><el-icon><CircleCheckFilled /></el-icon> 基准已记录({{ baselineResult ? baselineResult.refNs.toFixed(0) + ' ns' : '' }})</div>
      <p class="wz-lead">
        现在请按安装工艺,用力矩扳手把螺栓<b>拧紧到规定扭矩</b>。<br />
        拧紧期间可以关闭本窗口——回来后在向导首页点「已做过第一步基准,直接输入扭矩」继续。
      </p>
      <div class="wz-actions">
        <el-button @click="close">先去拧紧,稍后继续</el-button>
        <el-button type="primary" @click="step = 'torque-input'">已拧紧完成,下一步</el-button>
      </div>
    </template>

    <!-- A:输入扭矩 -->
    <template v-else-if="step === 'torque-input'">
      <div class="wz-stepline">第 3 步 / 共 3 步:输入安装扭矩</div>
      <p class="wz-lead">输入刚才拧紧时力矩扳手设定的扭矩值,系统将自动完成换算与标定:</p>
      <div class="torque-row">
        <el-input-number v-model="torque.torqueNm" :min="1" :step="10" :precision="0" size="large" style="width: 220px" />
        <span class="torque-unit">N·m</span>
        <span v-if="bolt?.installTorqueNm" class="dim">(档案记录的安装扭矩:{{ Number(bolt.installTorqueNm).toFixed(0) }} N·m)</span>
      </div>
      <p class="wz-hint">提交后系统会连续采集约 1 分钟的测量数据取平均,期间请勿再扳动螺栓。</p>
      <div class="wz-actions">
        <el-button @click="step = 'scene'">返回</el-button>
        <el-button type="primary" :disabled="torque.torqueNm == null" @click="startCalibrate">开始标定</el-button>
      </div>
    </template>

    <!-- A:采样计算中 -->
    <template v-else-if="step === 'calibrating'">
      <div class="wz-stepline">正在标定,请勿扳动螺栓…</div>
      <div class="prog-box">
        <el-progress :percentage="Math.min(100, Math.round((sampleCount / SAMPLE_TARGET) * 100))" :stroke-width="14" striped striped-flow />
        <p class="dim">已采集 {{ sampleCount }}/{{ SAMPLE_TARGET }} 个稳定测量样本
          <template v-if="liveEcho != null"> · 当前读数 {{ liveEcho.toFixed(0) }} ns</template>
        </p>
      </div>
    </template>

    <!-- A:完成 -->
    <template v-else-if="step === 'done'">
      <div class="done-box">
        <el-icon class="done-ic"><CircleCheckFilled /></el-icon>
        <h3>标定完成</h3>
        <div class="done-kpi" v-if="doneKn">
          当前预紧力 <b>{{ doneKn }} kN</b>
          <template v-if="doneRatioPct">(为目标的 <b>{{ doneRatioPct }}</b>)</template>
        </div>
        <div class="done-kpi dim" v-else>预紧力将在下一次测量后显示(几秒内)</div>
        <p class="done-note">
          本次为<b>扭矩法现场标定</b>(可信等级:临时),已可用于日常监测与告警。<br />
          正式验收如需计量级数据,请在实验室用载荷标定替换(专家模式)。
        </p>
        <el-button type="primary" @click="close">完成</el-button>
      </div>
    </template>

    <!-- C:完成(趋势) -->
    <template v-else-if="step === 'done-rel'">
      <div class="done-box">
        <el-icon class="done-ic"><CircleCheckFilled /></el-icon>
        <h3>基准已记录,趋势监测已开启</h3>
        <p class="done-note">
          螺栓详情页将显示它相对当前状态的<b>松紧变化</b>(偏松/偏紧/基线附近)。<br />
          由于未做载荷标定,暂不显示绝对预紧力数值;待检修窗口允许松开复拧时,重新运行本向导选第一项即可升级。
        </p>
        <el-button type="primary" @click="close">完成</el-button>
      </div>
    </template>
  </el-dialog>
</template>

<style scoped>
.wz-lead { font-size: 13.5px; line-height: 1.8; color: var(--text-2); margin: 0 0 14px; }
.wz-lead b { color: var(--text-1); }
.wz-stepline { font-size: 12px; color: var(--accent); letter-spacing: 0.08em; margin-bottom: 10px; font-weight: 600; }
.wz-err { display: flex; align-items: center; gap: 8px; color: var(--crit); border: 1px solid var(--crit); border-radius: 8px; padding: 8px 12px; font-size: 12.5px; margin-bottom: 12px; }
.wz-ok-note { display: flex; align-items: center; gap: 8px; color: var(--ok); font-size: 13px; margin-bottom: 10px; }

.scene-cards { display: flex; flex-direction: column; gap: 12px; }
.scene-card { border: 1px solid var(--panel-border); border-radius: 12px; padding: 14px 16px; cursor: pointer; transition: border-color 0.15s, background 0.15s; }
.scene-card:hover { border-color: var(--accent); background: rgba(34, 211, 238, 0.05); }
.sc-head { display: flex; align-items: center; gap: 8px; font-size: 14.5px; font-weight: 600; }
.sc-tag { font-size: 10.5px; color: var(--ok); border: 1px solid var(--ok); border-radius: 8px; padding: 1px 8px; margin-left: 4px; }
.sc-body { font-size: 12.5px; color: var(--text-2); line-height: 1.7; margin-top: 6px; }
.sc-req { font-size: 11.5px; color: var(--text-3); margin-top: 6px; }

.wz-foot-links { display: flex; justify-content: space-between; margin-top: 14px; font-size: 12.5px; }
.link { color: var(--accent); cursor: pointer; }
.link:hover { text-decoration: underline; }
.link.dim { color: var(--text-3); }

.live-box { border: 1px solid var(--panel-border); border-radius: 10px; padding: 12px 16px; display: flex; flex-direction: column; gap: 8px; margin-bottom: 14px; }
.lb-row { display: flex; justify-content: space-between; font-size: 13px; }
.lb-k { color: var(--text-3); }

.torque-row { display: flex; align-items: center; gap: 10px; margin: 6px 0 4px; }
.torque-unit { font-size: 15px; font-weight: 600; }
.wz-hint { font-size: 12px; color: var(--text-3); margin: 10px 0 0; }
.wz-actions { display: flex; justify-content: flex-end; gap: 10px; margin-top: 18px; }

.prog-box { padding: 18px 6px 8px; }
.prog-box p { margin-top: 12px; font-size: 12.5px; text-align: center; }

.done-box { text-align: center; padding: 10px 8px 4px; }
.done-ic { font-size: 44px; color: var(--ok); }
.done-box h3 { margin: 10px 0 12px; }
.done-kpi { font-size: 15px; margin-bottom: 12px; }
.done-kpi b { font-size: 20px; color: var(--accent); }
.done-note { font-size: 12.5px; color: var(--text-3); line-height: 1.8; margin-bottom: 16px; }
.dim { color: var(--text-3); }
.mono { font-family: var(--font-mono); }
</style>
