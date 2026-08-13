<script setup lang="ts">
// 引导式标定向导:把"设基线 → 拧紧 → 扭矩反推 K_L"的计量流程翻译成现场工人的语言。
// 设计原则:
// 1. 客户只回答"螺栓现在什么状态、用了多大扭矩"——K_L/量纲/温补全部系统代劳;
// 2. 不可行的事情不提供(已拧紧且不能松的螺栓,物理上无法现场标出绝对值,只给趋势监测,不给假数字);
// 3. 每一步实时显示声时稳定性,采样中给进度,完成页直接给"当前预紧力 XX kN(目标的 XX%)"。
import { computed, onUnmounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { boltApi, deviceApi, calibrationApi, type BoltListItem, type BoltChannelConfig, type TorqueFitSummary } from '../api'

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
  | 'baseline-confirm' // A/B/C:确认螺栓处于基准状态
  | 'baseline-sampling'// 采集稳定声时并写基线
  | 'tighten'          // A:去现场拧紧
  | 'torque-input'     // A:输入安装扭矩
  | 'calibrating'      // A:采样计算中
  | 'ladder-run'       // B:阶梯逐档（拧到位→等稳→采点循环）
  | 'ladder-fit'       // B:拟合摘要与应用
  | 'done'             // A/B:完成(有绝对值)
  | 'done-rel'         // C:完成(趋势监测)
const step = ref<Step>('scene')
// fresh=单点扭矩（快速开始）；ladder=多档阶梯精标（验收前推荐）；inservice=在役趋势基准
const scene = ref<'fresh' | 'ladder' | 'inservice' | null>(null)

const bolt = ref<BoltListItem | null>(null)
const chCfg = ref<BoltChannelConfig | null>(null)
const busy = ref(false)
const errText = ref('')

// ---- 实时声时观测(基线/采样步共用) ----
const liveEcho = ref<number | null>(null)
const liveTemp = ref<number | null>(null)
const recentEchos = ref<number[]>([])
let pollTimer: number | undefined
let lastSampleTs = ''  // 按测量时间戳去重:轮询(1.2s)可能快于设备单通道刷新(全通道轮询下约 4s 一条)

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
      // 只有拿到【新测量】才计入稳定性样本:设备停报/数据陈旧时重复推同一值会把极差压成 0,
      // 伪造出"稳定"误导现场在坏数据上设基线
      if (it.lastTs && it.lastTs !== lastSampleTs) {
        lastSampleTs = it.lastTs
        recentEchos.value.push(Number(it.echoNs))
        if (recentEchos.value.length > 10) recentEchos.value.shift()
      }
    }
  } catch { /* 观测尽力 */ }
}
function startPoll() {
  stopPoll()
  recentEchos.value = []
  lastSampleTs = ''
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
  stopLadderPoll()
  if (!open || props.boltId == null) return
  step.value = 'scene'
  scene.value = null
  errText.value = ''
  torque.torqueNm = null
  resetLadderRun()
  try {
    bolt.value = await boltApi.get(props.boltId)
    chCfg.value = await boltApi.channelConfig(props.boltId)
    if (bolt.value.installTorqueNm != null) torque.torqueNm = Number(bolt.value.installTorqueNm)
  } catch (e) {
    errText.value = (e as Error)?.message || '加载螺栓信息失败'
  }
})

// ---- 场景选择 ----
function chooseScene(s: 'fresh' | 'ladder' | 'inservice') {
  scene.value = s
  step.value = 'baseline-confirm'
  startPoll()
}

// ---- 设基线(A 第 1 步 / C 唯一步) ----
const baselineResult = ref<{ refNs: number; refTemp: number } | null>(null)
async function setBaseline() {
  if (!chCfg.value) return
  // 信号不稳时基线不可信(探头耦合差/干扰),要求现场明确确认而不是顺手点过
  if (!echoStable.value) {
    try {
      await ElMessageBox.confirm(
        `当前信号波动${echoSpreadNs.value != null ? ` ${echoSpreadNs.value.toFixed(1)} ns` : ''}偏大，基线可能不可靠。建议检查探头耦合后等待显示"稳定"再记录。仍要以当前读数设基线吗？`,
        '信号未达稳定判据', { type: 'warning', confirmButtonText: '仍要记录', cancelButtonText: '再等等' })
    } catch { return }
  }
  busy.value = true
  errText.value = ''
  try {
    // 零载声明（hardening §3）：新装/阶梯场景是在"未拧紧"前提下设基线，后端会核对
    // 当前折算预紧力≈0，带载设零载基线直接被拒——把 08-12 CH3 那种基线错位挡在门外。
    // 在役场景本来就是把带载状态记成相对基准，不声明。
    const r = await deviceApi.recalibrate(chCfg.value.deviceId, chCfg.value.channelNo,
      { expectZeroLoad: scene.value !== 'inservice' })
    if (r.warning) ElMessage.warning({ message: r.warning, duration: 8000 })
    baselineResult.value = { refNs: Number(r.newRefTimeNs), refTemp: Number(r.newRefTempC) }
    chCfg.value = await boltApi.channelConfig(props.boltId!)
    if (scene.value === 'fresh') {
      step.value = 'tighten'
      stopPoll()
    } else if (scene.value === 'ladder') {
      step.value = 'ladder-run'
      resetLadderRun()
      stopPoll()
    } else {
      step.value = 'done-rel'
      stopPoll()
      emit('done')
    }
  } catch (e) {
    errText.value = (e as Error)?.message || '记录基准失败（设备需在线且声时稳定）'
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
// 采样窗口 180s：热插拔全通道轮询下单通道刷新率被稀释(40 通道约 0.25Hz，60s 最多 15 条攒不够 20 条)，
// 窗口放宽到 3 分钟；后端 torque-calibrate 按同一窗口取平均(clamp 5..600s)。螺栓静置，窗口放宽无物理代价。
const SAMPLE_WINDOW_SEC = 180
// 采样兜底超时：窗口期后仍不达标(设备离线/信号异常/上报过慢)，报错返回而不是永远转圈
const SAMPLE_TIMEOUT_MS = 8 * 60_000
const sampleCount = ref(0)
const windowRemainSec = ref(SAMPLE_WINDOW_SEC)
let sampleTimer: number | undefined
let samplingStartedAt = 0

async function startCalibrate() {
  if (torque.torqueNm == null || torque.torqueNm <= 0) return ElMessage.warning('请输入拧紧时使用的扭矩（N·m）')
  step.value = 'calibrating'
  errText.value = ''
  sampleCount.value = 0
  windowRemainSec.value = SAMPLE_WINDOW_SEC
  samplingStartedAt = Date.now()
  startPoll()
  // 提交条件是双门槛：从点击此刻起满一个采样窗口 + 窗口内新样本攒够（与后端 minSamples=20 同口径）。
  // 原先只数"最近 180s 内的样本数"，拧完立刻点的话窗口 3/4 是拧紧前的旧状态数据，
  // 平均声时落在两个受力状态之间，K_L 错一个数量级（08-12 CH1/CH2/CH8 实锅，hardening §4.1）。
  // 只统计 samplingStartedAt 之后的样本，窗口必然整个落在"拧紧完成"之后；
  // 即便如此后端护栏仍会复核稳定性（松弛没结束照样打回），双保险。
  sampleTimer = window.setInterval(async () => {
    try {
      const ms = await boltApi.measurements(props.boltId!, 60)
      sampleCount.value = ms.items.filter(
        (m) => m.echoTimeNs != null && new Date(m.ts).getTime() >= samplingStartedAt).length
      const elapsedMs = Date.now() - samplingStartedAt
      windowRemainSec.value = Math.max(0, Math.ceil(SAMPLE_WINDOW_SEC - elapsedMs / 1000))
      if (elapsedMs >= SAMPLE_WINDOW_SEC * 1000 && sampleCount.value >= SAMPLE_TARGET) {
        if (sampleTimer) { clearInterval(sampleTimer); sampleTimer = undefined }
        await submitTorque()
        return
      }
      if (elapsedMs > SAMPLE_TIMEOUT_MS) {
        if (sampleTimer) { clearInterval(sampleTimer); sampleTimer = undefined }
        stopPoll()
        errText.value = `采样超时：${Math.round(SAMPLE_TIMEOUT_MS / 60000)} 分钟内有效声时样本不足（${sampleCount.value}/${SAMPLE_TARGET}）。请确认设备在线、探头耦合正常后重试。`
        step.value = 'torque-input'
      }
    } catch { /* 下轮再试 */ }
  }, 2000)
}

// 采样进度取时间与样本数的短板：两个门槛都到 100% 才会提交
const calibProgressPct = computed(() => {
  const byTime = Math.round(((SAMPLE_WINDOW_SEC - windowRemainSec.value) / SAMPLE_WINDOW_SEC) * 100)
  const byCount = Math.round((sampleCount.value / SAMPLE_TARGET) * 100)
  return Math.min(100, Math.min(byTime, byCount))
})

async function submitTorque() {
  if (props.channelId == null) return
  busy.value = true
  try {
    await calibrationApi.torqueCalibrate(props.channelId, {
      torqueNm: torque.torqueNm!,
      sampleWindowSeconds: SAMPLE_WINDOW_SEC,
      note: `引导标定：安装扭矩 ${torque.torqueNm} N·m(向导流程：零载基线+扭矩反推)`,
    })
    bolt.value = await boltApi.get(props.boltId!)
    step.value = 'done'
    stopPoll()
    emit('done')
  } catch (e) {
    errText.value = (e as Error)?.message || '标定计算失败'
    step.value = 'torque-input'
    stopPoll()
  } finally {
    busy.value = false
  }
}
onUnmounted(() => {
  if (sampleTimer) clearInterval(sampleTimer)
  stopLadderPoll()
})

// ---- B:阶梯精标（hardening §4.2）----
// 逐档"拧到位 → 等稳 → 采点"，方向强制从低到高（卸载有螺纹摩擦滞后，同扭矩下力值偏高，
// 混方向的点不落在一条直线上）。灯绿才放开采点：判据来自后端预检接口（stddev + 半窗趋势），
// 与采点护栏同一套口径，灯绿了就一定采得进去。
interface LadderStability {
  stable: boolean; sampleCount: number; avgEchoNs: number | null
  stddevNs: number | null; halfWindowDriftNs: number | null
}
const ladder = reactive({
  torquesText: '50 / 100 / 150 / 200',
  torques: [50, 100, 150, 200] as number[],
  idx: 0,
  phase: 'tighten' as 'tighten' | 'waiting',
  stepStartedAt: 0,
  captured: [] as number[],           // 已采档位（N·m）
  stab: null as LadderStability | null,
  waitRemainSec: 0,
  capturing: false,
  fit: null as TorqueFitSummary | null,
  applying: false,
})
let ladderTimer: number | undefined

function resetLadderRun() {
  ladder.idx = 0
  ladder.phase = 'tighten'
  ladder.captured = []
  ladder.stab = null
  ladder.fit = null
}

// 开始第一档前档位可改（空格/逗号/顿号分隔），开采后锁定——中途改档位会让"已采第几档"错位
const ladderEditable = computed(() => ladder.idx === 0 && ladder.phase === 'tighten' && !ladder.captured.length)
function applyLadderTorques(): boolean {
  const nums = ladder.torquesText.split(/[\s,，、/]+/).filter(Boolean).map(Number)
  if (nums.length < 3 || nums.some((n) => !Number.isFinite(n) || n <= 0)) {
    ElMessage.warning('档位需为至少 3 个正数（拟合最少 3 点），例如：50 / 100 / 150 / 200')
    return false
  }
  for (let i = 1; i < nums.length; i++)
    if (nums[i] <= nums[i - 1]) {
      ElMessage.warning('档位必须严格递增（加载方向）——卸载方向有摩擦滞后，点不落在一条直线上')
      return false
    }
  ladder.torques = nums
  return true
}

function markTightened() {
  if (ladderEditable.value && !applyLadderTorques()) return
  ladder.phase = 'waiting'
  ladder.stepStartedAt = Date.now()
  ladder.stab = null
  startPoll()        // live-box 显示当前读数
  startLadderPoll()  // 预检指示灯
}

function startLadderPoll() {
  stopLadderPoll()
  ladderTimer = window.setInterval(() => void pollLadder(), 5000)
  void pollLadder()
}
function stopLadderPoll() {
  if (ladderTimer) { clearInterval(ladderTimer); ladderTimer = undefined }
}
async function pollLadder() {
  if (props.channelId == null) return
  ladder.waitRemainSec = Math.max(0, Math.ceil(SAMPLE_WINDOW_SEC - (Date.now() - ladder.stepStartedAt) / 1000))
  try {
    ladder.stab = await calibrationApi.echoStability(props.channelId, SAMPLE_WINDOW_SEC)
  } catch { /* 下轮再试 */ }
}
// 绿灯 = 预检稳定 + 样本够（后端 minSamples 同口径）。不额外卡倒计时：
// 窗口里还有上一档/加载过程的数据时预检必然不稳，倒计时只是给人看的预期管理
const ladderReady = computed(() => ladder.stab != null && ladder.stab.stable && ladder.stab.sampleCount >= SAMPLE_TARGET)

async function captureLadderPoint() {
  const cid = props.channelId
  if (cid == null) return
  const torqueNm = ladder.torques[ladder.idx]
  ladder.capturing = true
  try {
    await calibrationApi.captureTorquePoint(cid, { torqueNm, sampleWindowSeconds: SAMPLE_WINDOW_SEC })
    ladder.captured.push(torqueNm)
    ElMessage.success(`已采 ${torqueNm} N·m 档（${ladder.captured.length}/${ladder.torques.length}）`)
    stopLadderPoll()
    if (ladder.captured.length >= ladder.torques.length) {
      stopPoll()
      step.value = 'ladder-fit'
      await loadLadderFit()
    } else {
      ladder.idx++
      ladder.phase = 'tighten'
      ladder.stab = null
    }
  } catch (e) {
    // 多半是后端护栏打回（等待期间又扳动了/松弛没完），提示后留在等待态继续观测
    ElMessage.error((e as Error)?.message || '采点失败，请等稳定后重试')
  } finally {
    ladder.capturing = false
  }
}

async function loadLadderFit() {
  if (props.channelId == null) return
  try {
    ladder.fit = await calibrationApi.torquePointSummary(props.channelId)
  } catch (e) {
    errText.value = (e as Error)?.message || '拉取拟合摘要失败'
  }
}

async function applyLadderFit() {
  const cid = props.channelId
  if (cid == null) return
  ladder.applying = true
  errText.value = ''
  try {
    const r = await calibrationApi.applyTorqueFit(cid, {})
    if (r.passed) {
      bolt.value = await boltApi.get(props.boltId!)
      step.value = 'done'
      emit('done')
    } else {
      // 未达合格线只留档不激活（后端语义），引导去专家模式剔点重采，不能装作成功
      ElMessage.warning(`R²=${Number(r.rSquared).toFixed(4)} 未达 0.98 合格线，已留档但未激活。请到专家模式"扭矩阶梯"页剔除波动大的点后重采。`)
    }
  } catch (e) {
    errText.value = (e as Error)?.message || '应用拟合失败'
  } finally {
    ladder.applying = false
  }
}

function close() {
  stopPoll()
  stopLadderPoll()
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
      <p class="wz-lead">标定的作用：告诉系统"这颗螺栓拧到多紧时声音走多快"，之后系统才能把超声读数换算成预紧力。请选择这颗螺栓的实际情况：</p>
      <div class="scene-cards">
        <div class="scene-card" @click="chooseScene('fresh')">
          <div class="sc-head"><el-icon><CircleCheckFilled /></el-icon> 快速开始：单点扭矩标定</div>
          <div class="sc-body">两步完成：先在<b>未拧紧</b>状态记录基准，再拧紧到位并输入扭矩。最快看到预紧力数值，绝对值精度受扭矩系数 ±30% 支配，适合先跑起来看趋势。</div>
          <div class="sc-req">需要：力矩扳手 · 全程约 5 分钟（不含拧紧时间）</div>
        </div>
        <div class="scene-card" @click="chooseScene('ladder')">
          <div class="sc-head"><el-icon><TrendCharts /></el-icon> 多档阶梯精标<span class="sc-tag">验收前推荐</span></div>
          <div class="sc-body">松开设零载基准后，按 <b>50→100→150→200 N·m</b> 逐档拧紧采点、线性拟合。每档等声时稳定（指示灯变绿）才采，能看出线性度（R²），比单点可靠得多。</div>
          <div class="sc-req">需要：力矩扳手 · 每档约 3~4 分钟，四档全程约 20 分钟</div>
        </div>
        <div class="scene-card" @click="chooseScene('inservice')">
          <div class="sc-head"><el-icon><View /></el-icon> 已拧紧在役，不能松开</div>
          <div class="sc-body">把<b>当前状态记录为基准</b>，系统持续监测它相对现在是变松还是变紧（趋势与告警可用，<b>不显示绝对吨位</b>）。待检修窗口可升级为完整标定。</div>
          <div class="sc-req">需要：无工具 · 1 分钟</div>
        </div>
      </div>
      <div class="wz-foot-links">
        <span class="link" @click="resumeAfterTighten">已做过第一步基准，直接输入扭矩 →</span>
        <span class="link dim" @click="emit('expert'); close()">专家模式（载荷标定/手动参数）</span>
      </div>
    </template>

    <!-- 基准确认 + 实时稳定性 -->
    <template v-else-if="step === 'baseline-confirm' || step === 'baseline-sampling'">
      <div class="wz-stepline">{{ scene !== 'inservice' ? '第 1 步：记录未拧紧（零载）基准' : '记录当前状态为基准' }}</div>
      <p class="wz-lead" v-if="scene !== 'inservice'">
        请确认螺栓处于<b>未拧紧</b>状态（刚穿入、螺母仅手拧贴合，不受力）。确认后点下方按钮，系统会把当前的超声读数记录为"零载基准"。
        若螺栓其实还带着载，系统会按现行标定核对并拒绝——那说明得先把它拧松。
      </p>
      <!-- 用权威 calibrated 判"在监测中"（含过期判定）：过期标定不出预紧力、脱落告警不会触发,
           不必吓唬人去做维护抑制;bolts.state 过期后仍是 'calibrated',按它判会误提示 -->
      <p class="wz-lead warn-lead" v-if="scene !== 'inservice' && bolt?.calibrated">
        注意：该螺栓已在监测中，松开它会触发"预紧力脱落"告警。请先到告警中心对它做<b>维护抑制</b>（填写检修原因），再松开重标，避免值班误响应。
      </p>
      <p class="wz-lead" v-if="scene === 'inservice'">
        无需任何操作，系统将把<b>此刻</b>的超声读数记录为比较基准。之后页面上会显示这颗螺栓相对现在的松紧变化。
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
          <b v-else-if="echoStable" style="color: var(--ok)">稳定（波动 {{ echoSpreadNs.toFixed(1) }} ns）</b>
          <b v-else style="color: var(--warn)">波动较大（{{ echoSpreadNs.toFixed(1) }} ns），请检查探头耦合后再试</b>
        </div>
      </div>
      <div class="wz-actions">
        <el-button @click="step = 'scene'; stopPoll()">上一步</el-button>
        <el-button type="primary" :loading="busy" :disabled="liveEcho == null" @click="setBaseline">
          {{ scene !== 'inservice' ? '确认未拧紧，记录基准' : '记录当前为基准' }}
        </el-button>
      </div>
    </template>

    <!-- A:去拧紧 -->
    <template v-else-if="step === 'tighten'">
      <div class="wz-stepline">第 2 步 / 共 3 步：拧紧螺栓</div>
      <div class="wz-ok-note"><el-icon><CircleCheckFilled /></el-icon> 基准已记录（{{ baselineResult ? baselineResult.refNs.toFixed(0) + ' ns' : '' }}）</div>
      <p class="wz-lead">
        现在请按安装工艺，用力矩扳手把螺栓<b>拧紧到规定扭矩</b>。<br />
        拧紧期间可以关闭本窗口——回来后在向导首页点“已做过第一步基准，直接输入扭矩”继续。
      </p>
      <div class="wz-actions">
        <el-button @click="close">先去拧紧，稍后继续</el-button>
        <el-button type="primary" @click="step = 'torque-input'">已拧紧完成，下一步</el-button>
      </div>
    </template>

    <!-- A:输入扭矩 -->
    <template v-else-if="step === 'torque-input'">
      <div class="wz-stepline">第 3 步 / 共 3 步：输入安装扭矩</div>
      <p class="wz-lead">输入刚才拧紧时力矩扳手设定的扭矩值，系统将自动完成换算与标定：</p>
      <div class="torque-row">
        <el-input-number v-model="torque.torqueNm" :min="1" :step="10" :precision="0" size="large" style="width: 220px" />
        <span class="torque-unit">N·m</span>
        <span v-if="bolt?.installTorqueNm" class="dim">（档案记录的安装扭矩：{{ Number(bolt.installTorqueNm).toFixed(0) }} N·m）</span>
      </div>
      <p class="wz-hint">提交后系统会连续采集测量数据取平均（约 1~3 分钟，取决于设备轮询的通道数），期间请勿再扳动螺栓。</p>
      <div class="wz-actions">
        <el-button @click="step = 'scene'">返回</el-button>
        <el-button type="primary" :disabled="torque.torqueNm == null" @click="startCalibrate">开始标定</el-button>
      </div>
    </template>

    <!-- A:采样计算中 -->
    <template v-else-if="step === 'calibrating'">
      <div class="wz-stepline">正在标定，请勿扳动螺栓…</div>
      <div class="prog-box">
        <el-progress :percentage="calibProgressPct" :stroke-width="14" striped striped-flow />
        <p class="dim">
          等满采样窗口还需 {{ windowRemainSec }} 秒 · 新样本 {{ sampleCount }}/{{ SAMPLE_TARGET }}
          <template v-if="liveEcho != null"> · 当前读数 {{ liveEcho.toFixed(0) }} ns</template>
        </p>
        <p class="dim" style="margin-top:4px">采样从点击"开始标定"起重新计时——之前的旧数据不算，保证窗口里全是拧紧后的读数。</p>
      </div>
    </template>

    <!-- B:阶梯逐档 -->
    <template v-else-if="step === 'ladder-run'">
      <div class="wz-stepline">第 2 步：逐档拧紧采点（{{ ladder.captured.length }}/{{ ladder.torques.length }} 档已采）</div>

      <div class="ladder-chips">
        <span v-for="(t, i) in ladder.torques" :key="t"
          class="lc" :class="{ done: i < ladder.captured.length, cur: i === ladder.idx && ladder.captured.length < ladder.torques.length }">
          {{ t }} N·m<el-icon v-if="i < ladder.captured.length"><CircleCheckFilled /></el-icon>
        </span>
      </div>

      <div v-if="ladderEditable" class="ladder-edit">
        <el-input v-model="ladder.torquesText" size="small" style="width: 260px" placeholder="50 / 100 / 150 / 200" />
        <span class="dim">档位可改（≥3 个、严格递增=加载方向），开始第一档后锁定</span>
      </div>

      <template v-if="ladder.phase === 'tighten'">
        <p class="wz-lead">
          请用力矩扳手把螺栓拧到 <b class="ladder-target">{{ ladder.torques[ladder.idx] }} N·m</b>，拧到位后点下方按钮开始等稳。
          全程只往紧的方向拧，<b>不要回松</b>——回松会引入摩擦滞后，点就不在一条直线上了。
        </p>
        <div class="wz-actions">
          <el-button @click="close">暂停，稍后继续</el-button>
          <el-button type="primary" @click="markTightened">已拧到 {{ ladder.torques[ladder.idx] }} N·m，开始等稳</el-button>
        </div>
      </template>

      <template v-else>
        <p class="wz-lead">
          保持螺栓静置，等声时稳住。指示灯变绿后即可采点——判据与后端护栏完全一致，绿了就一定采得进去。
        </p>
        <div class="live-box">
          <div class="lb-row">
            <span class="lb-k">当前读数</span>
            <b class="mono">{{ liveEcho != null ? liveEcho.toFixed(1) + ' ns' : '等待设备数据…' }}</b>
          </div>
          <div class="lb-row">
            <span class="lb-k">窗口波动 / 趋势</span>
            <b class="mono" v-if="ladder.stab">
              ±{{ ladder.stab.stddevNs != null ? Number(ladder.stab.stddevNs).toFixed(2) : '—' }} ns ·
              {{ ladder.stab.halfWindowDriftNs != null ? Number(ladder.stab.halfWindowDriftNs).toFixed(2) : '—' }} ns
            </b>
            <b class="dim" v-else>评估中…</b>
          </div>
          <div class="lb-row">
            <span class="lb-k">窗口样本</span>
            <b class="mono">{{ ladder.stab?.sampleCount ?? '—' }}/{{ SAMPLE_TARGET }}</b>
          </div>
          <div class="lb-row">
            <span class="lb-k">状态</span>
            <b v-if="ladderReady" style="color: var(--ok)">稳定，可以采点</b>
            <b v-else-if="ladder.waitRemainSec > 0" style="color: var(--warn)">等待新窗口填满，约还需 {{ ladder.waitRemainSec }} 秒</b>
            <b v-else style="color: var(--warn)">窗口已满但仍在变化（松弛未结束），继续等待…</b>
          </div>
        </div>
        <div class="wz-actions">
          <el-button @click="ladder.phase = 'tighten'; stopLadderPoll(); stopPoll()">返回</el-button>
          <el-button type="primary" :disabled="!ladderReady" :loading="ladder.capturing" @click="captureLadderPoint">
            采点（{{ ladder.torques[ladder.idx] }} N·m）
          </el-button>
        </div>
      </template>
    </template>

    <!-- B:拟合与应用 -->
    <template v-else-if="step === 'ladder-fit'">
      <div class="wz-stepline">第 3 步：拟合并应用</div>
      <div class="wz-ok-note"><el-icon><CircleCheckFilled /></el-icon> {{ ladder.captured.length }} 个阶梯点已全部采集</div>
      <div class="live-box" v-if="ladder.fit">
        <div class="lb-row"><span class="lb-k">有效点</span><b class="mono">{{ ladder.fit.pointCount }}</b></div>
        <div class="lb-row"><span class="lb-k">K_L（斜率）</span><b class="mono">{{ ladder.fit.kLByLinearFitNsPerN != null ? ladder.fit.kLByLinearFitNsPerN.toExponential(3) + ' ns/N' : '—' }}</b></div>
        <div class="lb-row"><span class="lb-k">截距</span><b class="mono">{{ ladder.fit.interceptNs != null ? ladder.fit.interceptNs.toFixed(2) + ' ns' : '—' }}</b></div>
        <div class="lb-row">
          <span class="lb-k">线性度 R²</span>
          <b class="mono" :style="(ladder.fit.rSquared ?? 0) >= 0.98 ? 'color: var(--ok)' : 'color: var(--warn)'">
            {{ ladder.fit.rSquared != null ? ladder.fit.rSquared.toFixed(4) : '—' }}
          </b>
        </div>
      </div>
      <p class="wz-hint">
        R² ≥ 0.98 应用后立即生效（自签临时标定）；不达标会留档但不激活——去专家模式"扭矩阶梯"页剔除波动大的点重采。
        拟合带截距项，零载基线的小偏差会被截距吸收，斜率不受影响。
      </p>
      <div class="wz-actions">
        <el-button @click="emit('expert'); close()">专家模式（看每个点）</el-button>
        <el-button type="primary" :loading="ladder.applying"
          :disabled="(ladder.fit?.pointCount ?? 0) < 3 || (ladder.fit?.rSquared ?? 0) < 0.95"
          @click="applyLadderFit">应用拟合为当前标定</el-button>
      </div>
    </template>

    <!-- A:完成 -->
    <template v-else-if="step === 'done'">
      <div class="done-box">
        <el-icon class="done-ic"><CircleCheckFilled /></el-icon>
        <h3>标定完成</h3>
        <div class="done-kpi" v-if="doneKn">
          当前预紧力 <b>{{ doneKn }} kN</b>
          <template v-if="doneRatioPct">（为目标的 <b>{{ doneRatioPct }}</b>）</template>
        </div>
        <div class="done-kpi dim" v-else>预紧力将在下一次测量后显示（几秒内）</div>
        <p class="done-note">
          本次为<b>{{ scene === 'ladder' ? '扭矩阶梯拟合标定（多点+线性度校验）' : '扭矩法现场标定（单点估算）' }}</b>（可信等级：临时），已可用于日常监测与告警。<br />
          正式验收如需计量级数据，请在实验室用载荷标定替换（专家模式）。
        </p>
        <el-button type="primary" @click="close">完成</el-button>
      </div>
    </template>

    <!-- C:完成(趋势) -->
    <template v-else-if="step === 'done-rel'">
      <div class="done-box">
        <el-icon class="done-ic"><CircleCheckFilled /></el-icon>
        <h3>基准已记录，趋势监测已开启</h3>
        <p class="done-note">
          螺栓详情页将显示它相对当前状态的<b>松紧变化</b>（偏松/偏紧/基线附近）。<br />
          由于未做载荷标定，暂不显示绝对预紧力数值；待检修窗口允许松开复拧时，重新运行本向导选第一项即可升级。
        </p>
        <el-button type="primary" @click="close">完成</el-button>
      </div>
    </template>
  </el-dialog>
</template>

<style scoped>
.wz-lead { font-size: 13.5px; line-height: 1.8; color: var(--text-2); margin: 0 0 14px; }
.wz-lead b { color: var(--text-1); }
.warn-lead { color: var(--warn); border: 1px solid var(--warn); border-radius: 8px; padding: 8px 12px; font-size: 12.5px; }
.warn-lead b { color: var(--warn); }
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

.ladder-chips { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 12px; }
.lc { display: inline-flex; align-items: center; gap: 5px; font-size: 12.5px; padding: 4px 12px; border-radius: 9px; border: 1px solid var(--hairline); color: var(--text-3); }
.lc.cur { color: var(--accent); border-color: var(--accent); font-weight: 600; }
.lc.done { color: var(--ok); border-color: rgba(16, 185, 129, 0.5); }
.ladder-edit { display: flex; align-items: center; gap: 10px; margin-bottom: 12px; font-size: 12px; }
.ladder-target { font-size: 17px; color: var(--accent); }

.done-box { text-align: center; padding: 10px 8px 4px; }
.done-ic { font-size: 44px; color: var(--ok); }
.done-box h3 { margin: 10px 0 12px; }
.done-kpi { font-size: 15px; margin-bottom: 12px; }
.done-kpi b { font-size: 20px; color: var(--accent); }
.done-note { font-size: 12.5px; color: var(--text-3); line-height: 1.8; margin-bottom: 16px; }
.dim { color: var(--text-3); }
.mono { font-family: var(--font-mono); }
</style>
