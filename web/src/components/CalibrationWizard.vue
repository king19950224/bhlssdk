<script setup lang="ts">
// 引导式标定向导。现场主路径（风机在役）：采集基准 → 手动置零 → 认定当前=档案目标 → 看动载荷和衰减。
// 实验室路径（可拧松）：零载基准 → 拧紧/阶梯 → 扭矩反推 K_L。
// 设计原则:
// 1. 在役不填扭矩、不选表面状态 K——目标预紧力走档案已设定的数值，K_L 同批沿用;
// 2. 实验室路径才问"用了多大扭矩、什么表面状态";
// 3. 稳定性判据全程用后端预检接口(echo-stability),与护栏同一套口径——
//    灯绿了就一定提交得进去,不存在"前端说稳、后端打回"的两套标准;
// 4. 被护栏打回不回退重来:留在等待页继续观测,稳了自动重试,不让现场白等一轮又一轮。
import { computed, onUnmounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { boltApi, deviceApi, calibrationApi, waveApi, type BoltListItem, type BoltChannelConfig, type TorqueFitSummary, type TorqueTestPoint, type TrialLive } from '../api'
import { ApiError } from '../api/client'

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
  | 'baseline-confirm' // A/B/C/D:确认螺栓处于基准状态
  | 'baseline-sampling'// 采集稳定声时并写基线
  | 'tighten'          // A:去现场拧紧
  | 'torque-input'     // A:输入安装扭矩
  | 'calibrating'      // A:采样计算中
  | 'ladder-run'       // B:阶梯逐档（拧到位→等稳→采点循环）
  | 'ladder-fit'       // B/D:拟合摘要与应用（试验点与阶梯点同表同链路，页面复用）
  | 'anchor-ask'       // C:在役——置零后按档案目标起算，再看动载荷和衰减
  | 'trial-run'        // D:标定试验（连续加载，力值平台自动分档出点）
  | 'done'             // A/B/C(锚定)/D:完成(有绝对值)
  | 'done-rel'         // C:完成(趋势监测)
const step = ref<Step>('scene')
// inservice=在役监测（风机现场主路径,不可拧动螺栓）；trial=标定试验（实验室主路径，2026-08-18 起
// 吸收单点/阶梯：一个平台=单点、多个平台=阶梯）；fresh/ladder 代码保留但场景页不再展示
const scene = ref<'fresh' | 'ladder' | 'inservice' | 'trial' | null>(null)

const bolt = ref<BoltListItem | null>(null)
const chCfg = ref<BoltChannelConfig | null>(null)
const busy = ref(false)
const errText = ref('')

// ---- 实时声时观测(基线/采样步共用,只做"当前读数"展示) ----
const liveEcho = ref<number | null>(null)
const liveTemp = ref<number | null>(null)
let pollTimer: number | undefined

async function pollLive() {
  if (!chCfg.value) return
  try {
    const r = await deviceApi.channelsLive(chCfg.value.deviceId)
    const it = r.items.find((x) => x.channelNo === chCfg.value!.channelNo)
    if (it?.echoNs != null) {
      liveEcho.value = Number(it.echoNs)
      liveTemp.value = it.tempC != null ? Number(it.tempC) : null
    }
  } catch { /* 观测尽力 */ }
}
function startPoll() {
  stopPoll()
  pollTimer = window.setInterval(pollLive, 1200)
  void pollLive()
}
function stopPoll() {
  if (pollTimer) { clearInterval(pollTimer); pollTimer = undefined }
}
onUnmounted(stopPoll)

// ---- 后端稳定性预检(与护栏同判据,全向导共用) ----
// 基线步 60s 窗(与 recalibrate 护栏一致,等待成本低)、采样/采点步 180s 窗(与标定护栏一致)。
// hardening §4.2 要求"前后端一个标准",此前只有阶梯页接了预检,基线页还在用
// "最近 10 个样本极差<5ns"的旧判据——前端亮绿、后端打回,现场只会觉得系统在耍人。
interface StabSnapshot {
  stable: boolean; sampleCount: number; avgEchoNs: number | null
  stddevNs: number | null; halfWindowDriftNs: number | null
  stddevMaxNs: number; halfWindowDriftMaxNs: number; windowSeconds: number
}
const stab = ref<StabSnapshot | null>(null)
// 每次预检返回的时刻:驱动"等稳超时"类 computed 的响应性(纯 Date.now() 不响应)
const stabUpdatedAt = ref(0)
let stabTimer: number | undefined
function startStabPoll(seconds: number) {
  stopStabPoll()
  stab.value = null
  const tick = async () => {
    if (props.channelId == null) return
    try {
      const purpose = scene.value === 'inservice' ? 'inservice' as const : undefined
      stab.value = await calibrationApi.echoStability(props.channelId, seconds, purpose)
      stabUpdatedAt.value = Date.now()
    } catch { /* 下轮再试 */ }
  }
  stabTimer = window.setInterval(() => void tick(), 5000)
  void tick()
}
function stopStabPoll() {
  if (stabTimer) { clearInterval(stabTimer); stabTimer = undefined }
}
onUnmounted(stopStabPoll)

// 基线步窗口 = recalibrate 护栏窗口(60s);其样本下限 5 条与后端一致
const BASELINE_WINDOW_SEC = 60
const BASELINE_MIN_SAMPLES = 5
// 预检接口不可用(stab 为 null)时不锁按钮——后端护栏仍会把关,别把人锁死在一个坏指示灯上
const baselineReady = computed(() =>
  stab.value == null || (stab.value.stable && stab.value.sampleCount >= BASELINE_MIN_SAMPLES))

// 信号长时间未达判据的强制出口（真机实测:有的探头/工况 sd 常态 1~2.5ns,0.6 判据永远等不绿——
// 不给出口就是把现场锁死在基线页,连"诚实地降级"的机会都没有）。样本充足是前提:
// 样本不足是设备问题,该去查设备而不是强闯。点击走 force 路径,后端记审计。
const BASELINE_STALL_MS = 3 * 60_000
const baselineEnteredAt = ref(0)
const baselineStalled = computed(() =>
  !baselineReady.value
  && stab.value != null && stab.value.sampleCount >= BASELINE_MIN_SAMPLES
  && baselineEnteredAt.value > 0
  && stabUpdatedAt.value - baselineEnteredAt.value > BASELINE_STALL_MS)

async function forceBaseline() {
  try {
    await ElMessageBox.confirm(
      `信号已持续 ${Math.round((stabUpdatedAt.value - baselineEnteredAt.value) / 60000)} 分钟未达稳定判据`
      + `（波动 ±${stab.value?.stddevNs != null ? Number(stab.value.stddevNs).toFixed(2) : '—'} ns，限 ±${stab.value?.stddevMaxNs != null ? Number(stab.value.stddevMaxNs).toFixed(1) : '0.6'}）。`
      + '强制记录会把当前波动带进基准（记入审计）。'
      + (scene.value !== 'inservice' ? '零载场景请再次确认螺栓【确实已完全拧松】。' : '')
      + '确定强制记录基准吗？',
      '信号长时间未稳定', { type: 'warning', confirmButtonText: '强制记录（记审计）', cancelButtonText: '继续等待' })
  } catch { return }
  await doRecalibrate(true)
}

// ---- 打开时加载上下文 ----
watch(() => props.modelValue, async (open) => {
  stopPoll()
  stopStabPoll()
  if (!open || props.boltId == null) return
  step.value = 'scene'
  scene.value = null
  errText.value = ''
  baselineWarning.value = ''
  torque.torqueNm = null
  nutFactor.value = 0.2
  preCalibLastTs.value = null
  anchor.preloadKn = null
  anchor.kL = null
  anchor.peerKL = null
  anchor.peerCount = 0
  anchor.theoreticalKL = null
  resetLadderRun()
  try {
    bolt.value = await boltApi.get(props.boltId)
    chCfg.value = await boltApi.channelConfig(props.boltId)
    if (bolt.value.installTorqueNm != null) torque.torqueNm = Number(bolt.value.installTorqueNm)
  } catch (e) {
    errText.value = (e as Error)?.message || '加载螺栓信息失败'
  }
  // SNR 前置守卫：信号质量差时标定极易锁错回波（CH8 事故根因）——进向导先亮警告，
  // 让人先处理耦合再标，而不是标完出一堆荒谬读数再排查
  snrWarning.value = null
  try {
    const f = await waveApi.featuresByBolt(props.boltId, 1)
    const snr = f.items[0]?.snrDb
    if (snr != null && Number(snr) < 12)
      snrWarning.value = Number(snr)
  } catch { /* 无波形特征不阻断向导 */ }
})

// 最近一条波形特征的 SNR（<12 dB 亮警告条）；null = 正常或无数据
const snrWarning = ref<number | null>(null)

// ---- 扭矩系数 K(单点/阶梯共用) ----
// T = K·F·d 里的 K 由螺栓表面状态与润滑决定,实际范围 0.10~0.30。选错是乘性误差:
// 估算预紧力错 → K_L 整体错 → 之后所有读数按同一比例偏,且阶梯拟合的 R² 对此完全无感
// (x 轴整体缩放不改变线性度)——"R²=0.999"不代表绝对值准。此前向导写死 0.20,
// 涂油螺栓(K≈0.15)的读数会系统性偏差 30%+,这里必须让现场按实际状态选。
const NUT_FACTOR_PRESETS = [
  { label: '干燥无润滑(K≈0.20,常见默认)', value: 0.2 },
  { label: '达克罗/锌铝涂层(K≈0.18)', value: 0.18 },
  { label: '涂油润滑(K≈0.15)', value: 0.15 },
  { label: '二硫化钼/脂润滑(K≈0.12)', value: 0.12 },
] as const
const nutFactor = ref(0.2)

// ---- 场景选择 ----
async function chooseScene(s: 'fresh' | 'ladder' | 'inservice' | 'trial') {
  if (busy.value) return

  // 实验室路径硬拦截：单点/阶梯/试验的第一步都是"拧松设零载基准"，而风机塔筒/基础螺栓根本不允许拧动。
  // 现场为了看到数字硬点"确认未拧紧"（带载记成零载）= 基准错一个数量级、读数全错——
  // 这个误选必须在入口就问死，而不是等第一步的核对护栏（首次标定无 K_L 时核对是盲区）。
  if (s === 'fresh' || s === 'ladder' || s === 'trial') {
    try {
      await ElMessageBox.confirm(
        '该流程第一步需要把螺栓【完全拧松】记录零载基准，适用于实验室、安装前预标定。'
        + '风机塔筒/基础等在役螺栓不允许拧动——若这颗螺栓不能拧松，请返回选择"在役监测"。',
        '确认螺栓可以完全拧松？',
        { type: 'warning', confirmButtonText: '可以拧松，继续', cancelButtonText: '不能拧松，返回' })
    } catch { return }
  }

  scene.value = s

  // 试验场景断点续走：通道上有进行中的试验（关窗/切页/断电都可能留下）直接接回运行页，
  // 基线不重设——试验的点全部挂在开场时的基线上，中途重设基线等于整场作废
  if (s === 'trial' && props.channelId != null) {
    busy.value = true
    try {
      const running = await calibrationApi.trialRunning(props.channelId)
      try {
        await ElMessageBox.confirm(
          `检测到进行中的试验 #${running.trialId}（${new Date(running.startedAt).toLocaleString()} 开始）。`
          + '继续可保留已出档位点；中止则本场作废（其点自动剔除）。',
          '发现未完成的标定试验',
          { type: 'info', confirmButtonText: '继续该试验', cancelButtonText: '中止并重新开始', distinguishCancelAndClose: true })
        trial.id = running.trialId
        enterTrialRun()
        return
      } catch (action) {
        if (action === 'close') { scene.value = null; return }
        await calibrationApi.trialAbort(props.channelId, running.trialId)
        ElMessage.info('已中止上次试验，其档位点已剔除')
        /* 走正常基线流程重新开始 */
      }
    } catch (e) {
      if (!(e instanceof ApiError && e.status === 404)) { /* 查询失败不挡路 */ }
    } finally {
      busy.value = false
    }
  }

  // 阶梯场景先查"当前基线下已采的档位点"(hardening §4.2 承诺的断点续走):
  // 阶梯全程几十分钟,中途关窗/切页很常见;方向强制升序,进度一丢螺栓就回不去低档了,
  // 只能拧松整轮重来。基线匹配(baselineMatched)的点才算本轮进度——
  // 重设过基线的旧点参考系已失效,后端拟合也不认它们。
  if (s === 'ladder' && props.channelId != null) {
    busy.value = true
    try {
      const r = await calibrationApi.torquePoints(props.channelId)
      const resumable = r.items.filter((p) => p.baselineMatched && !p.excluded)
      if (resumable.length > 0) {
        const torques = [...new Set(resumable.map((p) => Number(p.torqueNm)))].sort((a, b) => a - b)
        try {
          await ElMessageBox.confirm(
            `检测到当前基线下已采 ${torques.length} 个有效档位（${torques.join('、')} N·m）。`
            + '继续上次进度可跳过重设基线，直接从下一档开始；重新开始则需先拧松并重设零载基线（旧点自动作废，不参与拟合）。',
            '发现未完成的阶梯标定',
            { type: 'info', confirmButtonText: '继续上次进度', cancelButtonText: '拧松重新开始', distinguishCancelAndClose: true })
          // K 沿用上次采点的值:同一轮阶梯里 K 必须一致,换 K 等于换 x 轴比例,点就不共线了
          nutFactor.value = Number(resumable[resumable.length - 1].nutFactor) || 0.2
          resumeLadderProgress(torques)
          return
        } catch (action) {
          if (action === 'close') { scene.value = null; return }  // 右上角 × = 还没想好,留在场景页
          /* cancel = 重新开始,走正常基线流程 */
        }
      }
    } catch { /* 拉取失败不挡路,走正常流程 */ } finally {
      busy.value = false
    }
  }

  step.value = 'baseline-confirm'
  baselineEnteredAt.value = Date.now()
  startPoll()
  startStabPoll(BASELINE_WINDOW_SEC)
}

// 恢复阶梯进度:已采档位打勾,从下一档继续;档位全采过则直接进拟合页
function resumeLadderProgress(capturedTorques: number[]) {
  resetLadderRun()
  const defaults = [50, 100, 150, 200]
  const maxCap = capturedTorques[capturedTorques.length - 1]
  const remaining = defaults.filter((t) => t > maxCap)
  ladder.torques = [...capturedTorques, ...remaining]
  ladder.torquesText = ladder.torques.join(' / ')
  ladder.captured = [...capturedTorques]
  ladder.idx = capturedTorques.length
  if (ladder.idx >= ladder.torques.length) {
    // 默认档位都采完了:够 3 点就直接看拟合,不够就再加一档
    if (capturedTorques.length >= 3) {
      step.value = 'ladder-fit'
      void loadLadderFit()
      return
    }
    ladder.torques = [...capturedTorques, maxCap + 50]
    ladder.torquesText = ladder.torques.join(' / ')
  }
  step.value = 'ladder-run'
  ladder.phase = 'tighten'
}

// ---- 设基线(A 第 1 步 / C 唯一步) ----
const baselineResult = ref<{ refNs: number; refTemp: number } | null>(null)
// 基线移动提示要跟着人走到下一步页面,不能只做 8 秒 toast——
// "旧标定读数会失真"这件事,现场在拧紧的几分钟里正对着 LivePreload 看
const baselineWarning = ref('')

async function setBaseline() {
  if (!chCfg.value) return

  // 在役场景 + 已有绝对值标定:记基准后 K_L 还挂在通道上,系统会继续按它换算,
  // 而 Δt 相对新基准≈0 → 绝对值读数直接归零/失真。这多半是误选,要说破再放行。
  if (scene.value === 'inservice' && bolt.value?.calibrated) {
    try {
      await ElMessageBox.confirm(
        '该螺栓已有生效的绝对值标定。把"当前状态"记为新基准后，现有标定的预紧力读数会失真（趋近 0），'
        + '直到重新标定才恢复。"在役基准"适合从未标定或标定已失效的螺栓——确定要覆盖基准吗？',
        '通道已有生效标定', { type: 'warning', confirmButtonText: '确定覆盖基准', cancelButtonText: '取消' })
    } catch { return }
  }

  await doRecalibrate(false)
}

// force=true 是"带载核对被拒后现场人工确认零载"的二次提交(后端记审计)
async function doRecalibrate(force: boolean) {
  if (!chCfg.value) return
  busy.value = true
  errText.value = ''
  try {
    // 零载声明（hardening §3）：新装/阶梯场景是在"未拧紧"前提下设基线，后端会核对
    // 当前折算预紧力≈0，带载设零载基线直接被拒——把 08-12 CH3 那种基线错位挡在门外。
    // 在役场景本来就是把带载状态记成相对基准，不声明。
    const r = await deviceApi.recalibrate(chCfg.value.deviceId, chCfg.value.channelNo,
      { expectZeroLoad: scene.value !== 'inservice', ...(force ? { force: true } : {}) })
    baselineWarning.value = r.warning || ''
    if (r.warning) ElMessage.warning({ message: r.warning, duration: 8000 })
    baselineResult.value = { refNs: Number(r.newRefTimeNs), refTemp: Number(r.newRefTempC) }
    chCfg.value = await boltApi.channelConfig(props.boltId!)
    stopStabPoll()
    if (scene.value === 'fresh') {
      step.value = 'tighten'
      stopPoll()
    } else if (scene.value === 'ladder') {
      step.value = 'ladder-run'
      resetLadderRun()
      stopPoll()
    } else if (scene.value === 'trial') {
      // 试验：零载基线记好即开场，之后所有档位点的 Δt 都挂这条基线
      try {
        await startTrial()
      } catch (e) {
        errText.value = (e as Error)?.message || '开始试验失败'
      }
    } else {
      // 在役：已手动置零，下一步认定当前=档案目标，再看动载荷和衰减。
      step.value = 'anchor-ask'
      void loadAnchorDefaults()
    }
  } catch (e) {
    // 带载核对拒绝(响应带 estimatedPreloadN):按现行标定折算出"还带着载"。
    // 但现行标定本身失真时(K_L 偏小 → 折算虚高),真零载也会被误拦——
    // 恰恰是坏标定最需要重标。所以这里必须给现场一个人工确认的出口,不能一拒了之
    // (后端 force 路径会记审计,可追责)。
    if (!force && e instanceof ApiError && e.status === 400 && e.body?.estimatedPreloadN != null) {
      const kn = (Number(e.body.estimatedPreloadN) / 1000).toFixed(1)
      busy.value = false
      try {
        await ElMessageBox.confirm(
          `系统按现行标定把当前读数折算为约 ${kn} kN，判断螺栓可能还带着载。`
          + '如果它确实还没拧松，请先完全拧松再重试；'
          + '如果你确认它已经完全拧松（现行标定可能本身失真，读数不可信），可以强制记录零载基准（会记入审计）。',
          '带载核对未通过', { type: 'warning', confirmButtonText: '我确认已完全拧松，强制记录', cancelButtonText: '先去拧松' })
      } catch { return }
      await doRecalibrate(true)
      return
    }
    errText.value = (e as Error)?.message || '记录基准失败（设备需在线且声时稳定）'
  } finally {
    busy.value = false
  }
}

// 已设过基线的用户从"拧紧完成"直接续走。基线的年代/状态无从考证(可能是上周设的、
// 或根本没设过),用它反推 K_L 前必须让现场确认——这里是唯一的把关机会
async function resumeAfterTighten() {
  try {
    if (props.boltId != null) chCfg.value = await boltApi.channelConfig(props.boltId)
  } catch { /* 用已有缓存 */ }
  if (chCfg.value?.refTimeNs == null) {
    ElMessage.warning('该通道还没有基线（零载基准），请先选择上方场景，从第一步开始。')
    return
  }
  try {
    await ElMessageBox.confirm(
      `当前基线为 ${Number(chCfg.value.refTimeNs).toFixed(0)} ns。请确认它是【本次安装、螺栓完全拧松状态】下记录的——`
      + '如果基线是很久之前设的，或中途重新装卸过探头/螺栓，用它标定会引入不可控偏差，应重新走第一步。',
      '确认零载基准仍然有效', { type: 'warning', confirmButtonText: '确认无误，继续', cancelButtonText: '重新设基准' })
  } catch {
    chooseScene('fresh')
    return
  }
  scene.value = 'fresh'
  step.value = 'torque-input'
}

// ---- C:在役（风机现场主路径） ----
// 现场对螺栓零操作：先采集稳态声时、手动置零，再认定"当前 = 档案已设定的目标预紧力"，
// 之后只看动载荷（Δt 增大）和衰减（Δt 减小）。热路径 F = F_target + Δt/K_L。
// 绝对值是认定参考（认定置零时刻正好等于目标），动载/衰减精度高于这个锚点。
// K_L 是批次属性，不需要拧本颗螺栓：同组同规格同长度已标中位数 ＞ 同批实验室值 ＞ 理论估算（须人工确认）。
const anchor = reactive({
  loading: false,
  preloadKn: null as number | null,      // 档案目标预紧力（kN），可按实际改
  kL: null as number | null,
  peerKL: null as number | null,
  peerCount: 0,
  theoreticalKL: null as number | null,
  submitting: false,
})

async function loadAnchorDefaults() {
  if (props.channelId == null) return
  anchor.loading = true
  try {
    const d = await calibrationApi.anchorDefaults(props.channelId)
    // 档案没目标时也允许手填——那就是"设定好的数值"这一步
    anchor.preloadKn = d.targetPreloadN != null
      ? Number(d.targetPreloadN) / 1000
      : (bolt.value?.targetPreloadN != null ? Number(bolt.value.targetPreloadN) / 1000 : null)
    anchor.peerKL = d.peerKL != null ? Number(d.peerKL) : null
    anchor.peerCount = d.peerCount
    anchor.theoreticalKL = d.theoreticalKL != null ? Number(d.theoreticalKL) : null
    // 只自动预填同批沿用值（有实测依据）；理论值必须人工点"采用"
    anchor.kL = anchor.peerKL
  } catch { /* 预填尽力，全手动也能走 */ } finally {
    anchor.loading = false
  }
}

function adoptTheoreticalKl() {
  if (anchor.theoreticalKL != null) anchor.kL = anchor.theoreticalKL
}

async function submitAnchor() {
  const cid = props.channelId
  if (cid == null) return
  if (anchor.preloadKn == null || anchor.preloadKn <= 0) {
    ElMessage.warning('请确认目标预紧力（kN）——认定置零时刻的当前值 = 档案里已设定的目标')
    return
  }
  if (anchor.kL == null || anchor.kL <= 0) { ElMessage.warning('请提供 K_L：同批标定值自动沿用，或采用理论参考值/手动录入'); return }
  anchor.submitting = true
  errText.value = ''
  const kLSourceLabel = anchor.kL === anchor.peerKL ? '同组同规格沿用' : anchor.kL === anchor.theoreticalKL ? '理论估算·人工确认' : '人工录入'
  try {
    await calibrationApi.torqueCalibrate(cid, {
      anchorInService: true,
      kL: anchor.kL,
      sampleWindowSeconds: SAMPLE_WINDOW_SEC,
      anchorPreloadN: anchor.preloadKn * 1000,
      note: `在役（向导）：置零后认定当前=目标 ${anchor.preloadKn} kN，K_L=${anchor.kL}（${kLSourceLabel}），之后看动载荷和衰减`,
    })
    stopPoll()
    stopStabPoll()
    await markCalibrated()
    step.value = 'done'
    emit('done')
  } catch (e) {
    errText.value = (e as Error)?.message || '按目标起算失败'
  } finally {
    anchor.submitting = false
  }
}

function skipAnchor() {
  stopPoll()
  stopStabPoll()
  step.value = 'done-rel'
  emit('done')
}

// ---- 扭矩输入与标定(A 第 2 步) ----
const torque = reactive({ torqueNm: null as number | null })
const SAMPLE_TARGET = 20
// 采样窗口 180s：热插拔全通道轮询下单通道刷新率被稀释(40 通道约 0.25Hz，60s 最多 15 条攒不够 20 条)，
// 窗口放宽到 3 分钟；后端 torque-calibrate 按同一窗口取平均(clamp 5..600s)。螺栓静置，窗口放宽无物理代价。
const SAMPLE_WINDOW_SEC = 180
// 样本兜底超时只管"设备侧攒不出样本"(离线/信号异常/上报过慢)——报设备问题让人去查,
// 而不是永远转圈。"等松弛"不吃这个超时:那是物理过程,几分钟到几十分钟都正常。
const SAMPLE_TIMEOUT_MS = 8 * 60_000
// 窗口满、样本够、只剩"不稳"持续超过此时长,放出强制提交按钮(force,记审计)——
// 信号天然抖的现场(耦合极限/干扰)不至于被判据锁死,但走的人要签字画押
const STALL_FORCE_AFTER_MS = 10 * 60_000
const sampleCount = ref(0)
const windowRemainSec = ref(SAMPLE_WINDOW_SEC)
// 窗口与样本都达标、只剩等稳:模板据此切换"倒计时"与"等松弛"两种文案
const waitingStable = ref(false)
const stallElapsedSec = ref(0)
const submitting = ref(false)
let sampleTimer: number | undefined
let samplingStartedAt = 0
let stallSince = 0

async function startCalibrate() {
  if (torque.torqueNm == null || torque.torqueNm <= 0) return ElMessage.warning('请输入拧紧时使用的扭矩（N·m）')
  if (nutFactor.value == null || nutFactor.value <= 0) return ElMessage.warning('请选择或输入扭矩系数 K')
  step.value = 'calibrating'
  errText.value = ''
  sampleCount.value = 0
  windowRemainSec.value = SAMPLE_WINDOW_SEC
  waitingStable.value = false
  stallElapsedSec.value = 0
  samplingStartedAt = Date.now()
  stallSince = 0
  startPoll()
  startStabPoll(SAMPLE_WINDOW_SEC)
  // 提交条件是三门槛：从点击此刻起满一个采样窗口 + 窗口内新样本攒够（与后端 minSamples=20 同口径）
  // + 后端预检判稳（与护栏同判据）。
  // 前两条保证窗口全新(hardening §4.1：只统计 samplingStartedAt 之后的样本，
  // 拧完立刻点也混不进旧状态数据)；第三条是这次补上的——原先不看稳定性就盲提交，
  // 松弛没结束被护栏打回后回到输入页从头再等 180s，现场"等3分钟→打回→再等3分钟"来回耗。
  // 现在稳了才提交；万一竞态仍被打回，留在本页继续等稳自动重试，已等的时间不作废。
  sampleTimer = window.setInterval(async () => {
    try {
      const ms = await boltApi.measurements(props.boltId!, 60)
      sampleCount.value = ms.items.filter(
        (m) => m.echoTimeNs != null && new Date(m.ts).getTime() >= samplingStartedAt).length
      const elapsedMs = Date.now() - samplingStartedAt
      windowRemainSec.value = Math.max(0, Math.ceil(SAMPLE_WINDOW_SEC - elapsedMs / 1000))
      if (submitting.value) return

      if (elapsedMs < SAMPLE_WINDOW_SEC * 1000) return
      if (sampleCount.value < SAMPLE_TARGET) {
        waitingStable.value = false
        if (elapsedMs > SAMPLE_TIMEOUT_MS) {
          stopCalibTimers()
          errText.value = `采样超时：${Math.round(SAMPLE_TIMEOUT_MS / 60000)} 分钟内有效声时样本不足（${sampleCount.value}/${SAMPLE_TARGET}）。请确认设备在线、探头耦合正常后重试。`
          step.value = 'torque-input'
        }
        return
      }

      // 窗口满 + 样本够,看预检:预检不可用(null)时按旧行为直接提交,由后端护栏兜底
      const stable = stab.value?.stable ?? true
      if (!stable) {
        waitingStable.value = true
        if (stallSince === 0) stallSince = Date.now()
        stallElapsedSec.value = Math.round((Date.now() - stallSince) / 1000)
        return
      }
      waitingStable.value = false
      await submitTorque(false)
    } catch { /* 下轮再试 */ }
  }, 2000)
}

function stopCalibTimers() {
  if (sampleTimer) { clearInterval(sampleTimer); sampleTimer = undefined }
  stopPoll()
  stopStabPoll()
}

// 现场主动放弃本轮采样(回到扭矩输入,不提交)
function cancelCalibrating() {
  stopCalibTimers()
  errText.value = ''
  step.value = 'torque-input'
}

// 长时间不稳时的人工出口:后端 force 路径,审计留痕
async function forceSubmitTorque() {
  try {
    await ElMessageBox.confirm(
      `信号已持续 ${Math.round(stallElapsedSec.value / 60)} 分钟未达稳定判据`
      + `（波动 ${stab.value?.stddevNs != null ? Number(stab.value.stddevNs).toFixed(2) : '—'} ns / `
      + `趋势 ${stab.value?.halfWindowDriftNs != null ? Number(stab.value.halfWindowDriftNs).toFixed(2) : '—'} ns，限 ±0.6 ns）。`
      + '强制用当前数据标定会把这些波动带进 K_L（会记入审计）。确定强制提交吗？',
      '信号长时间未稳定', { type: 'warning', confirmButtonText: '强制提交（记审计）', cancelButtonText: '继续等待' })
  } catch { return }
  await submitTorque(true)
}

// 采样进度取时间与样本数的短板：两个门槛都到 100% 才会进入等稳/提交
const calibProgressPct = computed(() => {
  const byTime = Math.round(((SAMPLE_WINDOW_SEC - windowRemainSec.value) / SAMPLE_WINDOW_SEC) * 100)
  const byCount = Math.round((sampleCount.value / SAMPLE_TARGET) * 100)
  return Math.min(100, Math.min(byTime, byCount))
})

async function submitTorque(force: boolean) {
  if (props.channelId == null || submitting.value) return
  submitting.value = true
  busy.value = true
  try {
    await calibrationApi.torqueCalibrate(props.channelId, {
      torqueNm: torque.torqueNm!,
      nutFactor: nutFactor.value,
      sampleWindowSeconds: SAMPLE_WINDOW_SEC,
      note: `引导标定：安装扭矩 ${torque.torqueNm} N·m，K=${nutFactor.value}(向导流程：零载基线+扭矩反推)`,
      ...(force ? { force: true } : {}),
    })
    stopCalibTimers()
    await markCalibrated()
    step.value = 'done'
    emit('done')
  } catch (e) {
    // 稳定性护栏打回(响应带 stability 诊断) = 预检与提交间的竞态(又扳动了/松弛反复),
    // 留在本页继续等稳自动重试——已等的窗口时间不作废,这正是修掉"打回就从头再来"的关键
    if (e instanceof ApiError && e.status === 400 && e.body?.stability != null) {
      waitingStable.value = true
      if (stallSince === 0) stallSince = Date.now()
      ElMessage.warning('提交时声时又有波动，继续观测，稳定后将自动重试…')
    } else {
      stopCalibTimers()
      errText.value = (e as Error)?.message || '标定计算失败'
      step.value = 'torque-input'
    }
  } finally {
    submitting.value = false
    busy.value = false
  }
}
onUnmounted(() => {
  if (sampleTimer) clearInterval(sampleTimer)
  stopLadderCountdown()
  stopDonePoll()
})

// ---- 完成页的数据新鲜度 ----
// done 页显示的 lastPreloadN 是"最近一条测量"——标定刚生效时它还是【旧参数】算的
// (旧标定虚高时,认真标完看到的第一眼还是那个鬼数字,"校准不准"的观感就是这么来的)。
// 以"lastTs 相对提交时刻的快照发生变化"判定新数据,不比对两端时钟(客户端时钟不可信)。
const preCalibLastTs = ref<string | null>(null)
let doneTimer: number | undefined

async function markCalibrated() {
  try {
    bolt.value = await boltApi.get(props.boltId!)
  } catch { /* 保留旧值 */ }
  preCalibLastTs.value = bolt.value?.lastTs ?? null
  startDonePoll()
}
function startDonePoll() {
  stopDonePoll()
  let tries = 0
  doneTimer = window.setInterval(async () => {
    tries++
    try {
      bolt.value = await boltApi.get(props.boltId!)
      if (doneFresh.value || tries > 30) stopDonePoll()
    } catch { /* 下轮再试 */ }
  }, 3000)
}
function stopDonePoll() {
  if (doneTimer) { clearInterval(doneTimer); doneTimer = undefined }
}
const doneFresh = computed(() =>
  bolt.value?.lastTs != null && bolt.value.lastTs !== preCalibLastTs.value)

// ---- B:阶梯精标（hardening §4.2）----
// 逐档"拧到位 → 等稳 → 采点"，方向强制从低到高（卸载有螺纹摩擦滞后，同扭矩下力值偏高，
// 混方向的点不落在一条直线上）。灯绿才放开采点：判据来自后端预检接口（stddev + 半窗趋势），
// 与采点护栏同一套口径，灯绿了就一定采得进去。
const ladder = reactive({
  torquesText: '50 / 100 / 150 / 200',
  torques: [50, 100, 150, 200] as number[],
  idx: 0,
  phase: 'tighten' as 'tighten' | 'waiting',
  stepStartedAt: 0,
  captured: [] as number[],           // 已采档位（N·m）
  waitRemainSec: 0,
  capturing: false,
  // 力值直标（选填）：本档位的拉伸机/测力传感器实测读数（kN）。填了就按实测力入拟合，
  // 绕开扭矩折算的摩擦系数误差（±30%）；不填维持扭矩折算。逐档填、采完即清。
  measuredForceKn: null as number | null,
  fit: null as TorqueFitSummary | null,
  points: [] as TorqueTestPoint[],    // 拟合页明细（当前基线下的有效点，升序）
  removing: false,
  applying: false,
})
let ladderTimer: number | undefined

function resetLadderRun() {
  ladder.idx = 0
  ladder.phase = 'tighten'
  ladder.captured = []
  ladder.fit = null
  ladder.points = []
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
  if (ladderEditable.value && (nutFactor.value == null || nutFactor.value <= 0)) {
    ElMessage.warning('请选择或输入扭矩系数 K')
    return
  }
  ladder.phase = 'waiting'
  ladder.stepStartedAt = Date.now()
  startPoll()                          // live-box 显示当前读数
  startStabPoll(SAMPLE_WINDOW_SEC)     // 预检指示灯（与采点护栏同窗口）
  startLadderCountdown()
}

// 倒计时只是预期管理（窗口里还有上一档/加载过程的数据时预检必然不稳）；判稳一律以预检为准
function startLadderCountdown() {
  stopLadderCountdown()
  const tick = () => {
    ladder.waitRemainSec = Math.max(0, Math.ceil(SAMPLE_WINDOW_SEC - (Date.now() - ladder.stepStartedAt) / 1000))
  }
  ladderTimer = window.setInterval(tick, 1000)
  tick()
}
function stopLadderCountdown() {
  if (ladderTimer) { clearInterval(ladderTimer); ladderTimer = undefined }
}
// 绿灯 = 预检稳定 + 样本够（后端 minSamples 同口径）
const ladderReady = computed(() => stab.value != null && stab.value.stable && stab.value.sampleCount >= SAMPLE_TARGET)

// 等稳超时的强制出口（同基线页):信号天然抖的探头在 0.6 判据下可能永远等不绿,
// 阶梯不给出口就整轮卡死。8 分钟 = 窗口 3 分钟 + 正常松弛尾巴的余量;强制采的点波动大,
// 会拖低 R²,拟合页看得到、剔得掉——比卡死强
const LADDER_STALL_MS = 8 * 60_000
const ladderStalled = computed(() =>
  ladder.phase === 'waiting' && !ladderReady.value
  && stab.value != null && stab.value.sampleCount >= SAMPLE_TARGET
  && stabUpdatedAt.value - ladder.stepStartedAt > LADDER_STALL_MS)

async function forceCaptureLadderPoint() {
  try {
    await ElMessageBox.confirm(
      `信号已持续 ${Math.round((stabUpdatedAt.value - ladder.stepStartedAt) / 60000)} 分钟未达稳定判据`
      + `（波动 ±${stab.value?.stddevNs != null ? Number(stab.value.stddevNs).toFixed(2) : '—'} ns，限 ±0.6）。`
      + '强制采点会把波动带进这个档位的数据（记入审计），可能拖低拟合 R²——拟合页可见、可剔除。确定强制采点吗？',
      '信号长时间未稳定', { type: 'warning', confirmButtonText: '强制采点（记审计）', cancelButtonText: '继续等待' })
  } catch { return }
  await captureLadderPoint(true)
}

async function captureLadderPoint(force = false) {
  const cid = props.channelId
  if (cid == null) return
  const torqueNm = ladder.torques[ladder.idx]
  const forceKn = ladder.measuredForceKn
  const forceMode = forceKn != null && forceKn > 0
  ladder.capturing = true
  try {
    await calibrationApi.captureTorquePoint(cid, {
      torqueNm, nutFactor: nutFactor.value, sampleWindowSeconds: SAMPLE_WINDOW_SEC,
      ...(force ? { force: true } : {}),
      ...(forceMode ? { measuredForceN: forceKn * 1000 } : {}),
    })
    ladder.captured.push(torqueNm)
    ladder.measuredForceKn = null
    ElMessage.success(forceMode
      ? `已采 ${torqueNm} N·m 档（实测 ${forceKn} kN 力值直标，${ladder.captured.length}/${ladder.torques.length}）`
      : `已采 ${torqueNm} N·m 档（${ladder.captured.length}/${ladder.torques.length}）`)
    stopLadderCountdown()
    stopStabPoll()
    if (ladder.captured.length >= ladder.torques.length) {
      stopPoll()
      step.value = 'ladder-fit'
      await loadLadderFit()
    } else {
      ladder.idx++
      ladder.phase = 'tighten'
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
    const [fit, pts] = await Promise.all([
      calibrationApi.torquePointSummary(props.channelId),
      calibrationApi.torquePoints(props.channelId),
    ])
    ladder.fit = fit
    // 明细只列"参与拟合"的点（当前基线 + 未剔除），按档位升序——R² 不达标时现场要能看出是哪个点的问题
    ladder.points = pts.items
      .filter((p) => p.baselineMatched && !p.excluded)
      .sort((a, b) => Number(a.torqueNm) - Number(b.torqueNm))
  } catch (e) {
    errText.value = (e as Error)?.message || '拉取拟合摘要失败'
  }
}

// 窗口波动最大的点（R² 不达标时的头号嫌疑）：模板高亮 + 一键剔除的目标
const worstPoint = computed(() => {
  if (ladder.points.length === 0) return null
  return ladder.points.reduce((worst, p) =>
    Number(p.stddevEchoTimeNs ?? 0) > Number(worst.stddevEchoTimeNs ?? 0) ? p : worst)
})

// 剔除波动最大的点并重拟合（hardening §4.2 承诺的"不达标提示哪个点波动大"闭环）。
// 注意不回采集页重采中间档：方向强制升序，重采中间档等于卸载再加载，摩擦滞后会让该点
// 系统性偏离加载线——物理上正确的选择只有"用剩余点拟合"或"拧松整轮重来"。
async function removeWorstAndRefit() {
  const cid = props.channelId
  const worst = worstPoint.value
  if (cid == null || worst == null) return
  if (ladder.points.length <= 3) {
    ElMessage.warning('剔除后将不足 3 个点，无法拟合。请选择"拧松重做整轮"。')
    return
  }
  ladder.removing = true
  errText.value = ''
  try {
    await calibrationApi.patchTorquePoint(cid, worst.id, {
      excluded: true,
      reason: `向导：R² 未达标，剔除窗口波动最大的点（${Number(worst.torqueNm).toFixed(0)} N·m，±${Number(worst.stddevEchoTimeNs ?? 0).toFixed(2)} ns）`,
    })
    ElMessage.success(`已剔除 ${Number(worst.torqueNm).toFixed(0)} N·m 档，正在重新拟合…`)
    await loadLadderFit()
  } catch (e) {
    errText.value = (e as Error)?.message || '剔除失败'
  } finally {
    ladder.removing = false
  }
}

// 拧松重做整轮：回到基线步重设零载基准。旧点不用手工清理——基线一变它们自动不匹配，
// 后端拟合/摘要不再认（这正是"拟合只认当前基线"这条护栏给流程带来的红利）
async function restartLadder() {
  try {
    await ElMessageBox.confirm(
      '重做整轮需要先把螺栓完全拧松，重新记录零载基准，再逐档拧紧采点。本轮已采的点会自动作废（不参与今后的拟合）。确定重做吗？',
      '拧松重做整轮', { type: 'warning', confirmButtonText: '确定，去拧松', cancelButtonText: '再想想' })
  } catch { return }
  resetLadderRun()
  errText.value = ''
  step.value = 'baseline-confirm'
  baselineEnteredAt.value = Date.now()
  startPoll()
  startStabPoll(BASELINE_WINDOW_SEC)
}

async function applyLadderFit() {
  const cid = props.channelId
  if (cid == null) return
  ladder.applying = true
  errText.value = ''
  try {
    const r = await calibrationApi.applyTorqueFit(cid, {})
    if (r.passed) {
      await markCalibrated()
      step.value = 'done'
      emit('done')
    } else {
      // 未达合格线只留档不生效（后端语义）。向导内现在就有剔点/重做的闭环，不再赶人去专家模式
      ElMessage.warning(`R²=${Number(r.rSquared).toFixed(4)} 未达 0.98 合格线，已留档但未生效。可剔除波动大的点重拟合，或拧松重做整轮。`)
      await loadLadderFit()
    }
  } catch (e) {
    errText.value = (e as Error)?.message || '应用拟合失败'
  } finally {
    ladder.applying = false
  }
}

// ---- D:标定试验（连续加载，力值平台自动分档；force-sync-calibration-design）----
// 后端做全部判定（平台+声时双稳+步距守卫），前端只负责喂力值和展示指引。
// manual 源的"连续"由前端实现：打开进样开关后每 2s 上报一次当前读数——
// 读数不变就是平台，变了就是在加载，与串口测力仪（P2）走同一条进样接口。
const trial = reactive({
  id: null as number | null,
  forceKn: null as number | null,   // 当前测力装置读数（kN）
  feeding: false,
  live: null as TrialLive | null,
  lastHint: '',
  busyAction: false,
})
let trialFeedTimer: number | undefined
let trialLiveTimer: number | undefined

async function startTrial() {
  if (props.channelId == null) return
  const r = await calibrationApi.trialStart(props.channelId, { forceSource: 'manual' })
  trial.id = r.trialId
  enterTrialRun()
}

function enterTrialRun() {
  step.value = 'trial-run'
  trial.feeding = false
  trial.live = null
  trial.lastHint = ''
  startPoll()   // live-box 显示当前声时
  stopTrialTimers()
  trialLiveTimer = window.setInterval(() => void refreshTrialLive(), 2000)
  void refreshTrialLive()
}

async function refreshTrialLive() {
  if (props.channelId == null || trial.id == null) return
  try { trial.live = await calibrationApi.trialLive(props.channelId, trial.id) } catch { /* 下轮再试 */ }
}

function toggleFeeding(on: boolean | string | number) {
  const en = on === true
  trial.feeding = en
  if (trialFeedTimer) { clearInterval(trialFeedTimer); trialFeedTimer = undefined }
  if (en) {
    trialFeedTimer = window.setInterval(() => void feedOnce(), 2000)
    void feedOnce()
  }
}

async function feedOnce() {
  if (!trial.feeding || props.channelId == null || trial.id == null) return
  const kn = trial.forceKn
  if (kn == null || kn <= 0) return
  try {
    const r = await calibrationApi.trialForceSample(props.channelId, trial.id, kn * 1000)
    trial.lastHint = r.hint
    if (r.pointCreated) ElMessage.success(r.hint)
  } catch (e) {
    trial.lastHint = (e as Error)?.message || '进样失败'
  }
}

function stopTrialTimers() {
  if (trialFeedTimer) { clearInterval(trialFeedTimer); trialFeedTimer = undefined }
  if (trialLiveTimer) { clearInterval(trialLiveTimer); trialLiveTimer = undefined }
}
onUnmounted(stopTrialTimers)

const trialValidPoints = computed(() => (trial.live?.points ?? []).filter((p) => !p.excluded))

async function finishTrial() {
  if (props.channelId == null || trial.id == null) return
  trial.busyAction = true
  try {
    const r = await calibrationApi.trialFinish(props.channelId, trial.id)
    ElMessage.success(r.note)
    stopTrialTimers()
    trial.feeding = false
    stopPoll()
    step.value = 'ladder-fit'   // 试验点与阶梯点同表同基线，拟合页直接复用
    await loadLadderFit()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '结束试验失败')
  } finally {
    trial.busyAction = false
  }
}

async function abortTrial() {
  if (props.channelId == null || trial.id == null) return
  try {
    await ElMessageBox.confirm(
      '中止后本场试验作废，已出的档位点会被自动剔除（不参与拟合，可在拟合页恢复）。确定中止？',
      '中止试验', { type: 'warning', confirmButtonText: '中止试验', cancelButtonText: '继续试验' })
  } catch { return }
  trial.busyAction = true
  try {
    await calibrationApi.trialAbort(props.channelId, trial.id)
    stopTrialTimers()
    trial.feeding = false
    stopPoll()
    trial.id = null
    scene.value = null
    step.value = 'scene'
  } catch (e) {
    ElMessage.error((e as Error)?.message || '中止失败')
  } finally {
    trial.busyAction = false
  }
}

function close() {
  stopPoll()
  stopStabPoll()
  stopLadderCountdown()
  stopDonePoll()
  stopTrialTimers()
  if (sampleTimer) { clearInterval(sampleTimer); sampleTimer = undefined }
  visible.value = false
}

const doneRatioPct = computed(() => {
  if (!doneFresh.value) return null
  const r = bolt.value?.lastRatio
  return r != null ? (Number(r) * 100).toFixed(1) + '%' : null
})
const doneKn = computed(() => {
  if (!doneFresh.value) return null
  const n = bolt.value?.lastPreloadN
  return n != null ? (Number(n) / 1000).toFixed(1) : null
})
</script>

<template>
  <el-dialog v-model="visible" :title="`标定向导 · ${boltCode || '螺栓'}`" width="620px" append-to-body :close-on-click-modal="false" @close="close">
    <div v-if="errText" class="wz-err"><el-icon><WarningFilled /></el-icon>{{ errText }}</div>

    <!-- SNR 前置守卫：信号差时先治耦合再标定，避免基线锁错回波 -->
    <el-alert v-if="snrWarning != null" type="warning" :closable="false" show-icon style="margin-bottom: 12px">
      该通道当前信号质量差（SNR {{ Number(snrWarning).toFixed(1) }} dB，低于 12 dB）：标定时容易把基线锁在错误回波上，
      标出的读数会整体错位。建议先重新耦合探头（清洁端面、补耦合剂、压紧），到【波形诊断】确认回波清晰后再标定。
    </el-alert>

    <!-- 场景选择：在役第一位——真实现场（风机塔筒/基础法兰）的螺栓从吊装起就不允许拧动，
         "拧松设零载"的两条路只有实验室/安装前走得通，主次不能颠倒 -->
    <template v-if="step === 'scene'">
      <p class="wz-lead">标定的作用：告诉系统"这颗螺栓拧到多紧时声音走多快"，之后系统才能把超声读数换算成预紧力。请选择这颗螺栓的实际情况：</p>
      <div class="scene-cards">
        <div class="scene-card" @click="chooseScene('inservice')">
          <div class="sc-head"><el-icon><View /></el-icon> 在役监测：螺栓不可拧动<span class="sc-tag">现场推荐</span></div>
          <div class="sc-body">先采集稳态声时、<b>手动置零</b>，认定当前预紧力 = 档案里已设定的目标值，之后看<b>动载荷</b>（声时变大）和<b>衰减</b>（声时变小）。全程不拧、不碰螺栓。</div>
          <div class="sc-req">需要：档案已设定目标预紧力 · K_L 同批自动沿用 · 约 3~5 分钟</div>
        </div>
        <div class="scene-card" @click="chooseScene('trial')">
          <div class="sc-head"><el-icon><TrendCharts /></el-icon> 标定试验：连续加载自动分档（需可完全拧松）<span class="sc-tag">实验室推荐</span></div>
          <div class="sc-body">松开设零载基准后开始试验：逐级加载并<b>持续输入测力读数</b>（拉伸机/测力计/扭矩换算值），
            系统检测<b>力值平台</b>并等声时双稳后<b>自动出点</b>——一个平台就是单点、多个平台就是阶梯，采够档位结束即拟合。
            输入实测力值时完全绕开扭矩系数 ±30% 误差。</div>
          <div class="sc-req">需要：可读数的加载装置、螺栓可拧松 · 每档保持 1~3 分钟（攒声时样本），3 档起可拟合；中途可关窗续走</div>
        </div>
      </div>
      <div class="wz-foot-links">
        <span class="link" @click="resumeAfterTighten">已做过第一步基准，直接输入扭矩 →</span>
        <span class="link dim" @click="emit('expert'); close()">专家模式（载荷标定/手动参数）</span>
      </div>
    </template>

    <!-- 基准确认 + 实时稳定性 -->
    <template v-else-if="step === 'baseline-confirm' || step === 'baseline-sampling'">
      <div class="wz-stepline">{{ scene !== 'inservice' ? '第 1 步：记录未拧紧（零载）基准' : '第 1 步：采集基准，手动置零' }}</div>
      <p class="wz-lead" v-if="scene !== 'inservice'">
        请确认螺栓处于<b>未拧紧</b>状态（刚穿入、螺母仅手拧贴合，不受力）。确认后点下方按钮，系统会把当前的超声读数记录为"零载基准"。
        若螺栓其实还带着载，系统会按现行标定核对并拒绝——那说明得先把它拧松。
      </p>
      <!-- 用权威 calibrated 判"在监测中"（含过期判定）：过期标定不出预紧力、脱落告警不会触发,
           不必吓唬人去做维护抑制;bolts.state 过期后仍是 'calibrated',按它判会误提示 -->
      <p class="wz-lead warn-lead" v-if="scene !== 'inservice' && bolt?.calibrated">
        注意：该螺栓已在监测中，松开它会触发"预紧力脱落"告警。请先到告警中心对它做<b>维护抑制</b>（填写检修原因），再松开重标，避免值班误响应。<br />
        另外：记录新基准后到完成本次标定前，页面上按旧标定算出的预紧力会失真（可能归零或消失），完成标定即恢复，属正常现象。
      </p>
      <p class="wz-lead" v-if="scene === 'inservice'">
        无需碰螺栓。等信号稳定后<b>手动置零</b>：把此刻声时记为比较基准。
        下一步认定当前预紧力 = 档案里已设定的目标值，之后声时变大是动载荷、变小是衰减。
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
        <!-- 稳定性判据 = 后端 recalibrate 护栏同款（60s 窗 stddev + 半窗趋势 + 样本下限）：
             这里亮绿、提交就一定过护栏，不再有"前端说稳、后端打回"的两套口径 -->
        <div class="lb-row">
          <span class="lb-k">窗口波动 / 趋势</span>
          <b class="mono" v-if="stab">
            ±{{ stab.stddevNs != null ? Number(stab.stddevNs).toFixed(2) : '—' }} ns ·
            {{ stab.halfWindowDriftNs != null ? Number(stab.halfWindowDriftNs).toFixed(2) : '—' }} ns（限 ±{{ Number(stab.stddevMaxNs).toFixed(1) }} / ±{{ Number(stab.halfWindowDriftMaxNs).toFixed(1) }}）
          </b>
          <b class="dim" v-else>评估中…</b>
        </div>
        <div class="lb-row">
          <span class="lb-k">信号稳定性</span>
          <b v-if="stab == null" class="dim">评估中…</b>
          <b v-else-if="stab.sampleCount < BASELINE_MIN_SAMPLES" style="color: var(--warn)">
            窗口样本不足（{{ stab.sampleCount }}/{{ BASELINE_MIN_SAMPLES }}），请确认设备在线、通道在轮询
          </b>
          <b v-else-if="stab.stable" style="color: var(--ok)">{{ scene === 'inservice' ? '可以置零' : '稳定，可以记录基准' }}</b>
          <b v-else style="color: var(--warn)">{{ scene === 'inservice' ? '声时变化偏大，请确认无人在拧螺栓' : '声时仍在变化，请保持螺栓静置等稳' }}</b>
        </div>
      </div>
      <div class="wz-actions">
        <el-button @click="step = 'scene'; stopPoll(); stopStabPoll()">上一步</el-button>
        <el-button v-if="baselineStalled" type="warning" plain :loading="busy" @click="forceBaseline">
          信号长时间未稳，强制记录（记审计）
        </el-button>
        <el-button type="primary" :loading="busy" :disabled="liveEcho == null || !baselineReady" @click="setBaseline">
          {{ scene !== 'inservice' ? '确认未拧紧，记录基准' : '手动置零' }}
        </el-button>
      </div>
    </template>

    <!-- A:去拧紧 -->
    <template v-else-if="step === 'tighten'">
      <div class="wz-stepline">第 2 步 / 共 3 步：拧紧螺栓</div>
      <div class="wz-ok-note"><el-icon><CircleCheckFilled /></el-icon> 基准已记录（{{ baselineResult ? baselineResult.refNs.toFixed(0) + ' ns' : '' }}）</div>
      <!-- 基线移动提示要跟着人走(不能只做 8 秒 toast):拧紧的几分钟里现场正对着实时页看读数 -->
      <p class="wz-lead warn-lead" v-if="baselineWarning">{{ baselineWarning }}</p>
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
      <!-- K 是"扭矩→预紧力"换算的核心系数,由螺栓表面/润滑状态决定。选错是乘性误差:
           之后所有读数按同一比例整体偏,拟合 R² 再高也救不回来——必须按实物状态选 -->
      <div class="nut-row">
        <span class="nut-label">螺栓表面状态（扭矩系数 K）</span>
        <el-select v-model="nutFactor" style="width: 250px" size="default">
          <el-option v-for="p in NUT_FACTOR_PRESETS" :key="p.value" :label="p.label" :value="p.value" />
        </el-select>
        <el-input-number v-model="nutFactor" :min="0.05" :max="0.5" :step="0.01" :precision="3" controls-position="right" style="width: 120px" />
      </div>
      <p class="wz-hint">
        K 值决定"扭矩 → 预紧力"的换算比例，选错读数会整体偏差 30% 以上。按螺栓实际表面/润滑状态选择，有出厂文档以文档为准（可直接改数字）。<br />
        提交后系统会连续采集测量数据取平均（约 3 分钟起，需等应力松弛结束），期间请勿再扳动螺栓。
      </p>
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
        <template v-if="!waitingStable">
          <p class="dim">
            等满采样窗口还需 {{ windowRemainSec }} 秒 · 新样本 {{ sampleCount }}/{{ SAMPLE_TARGET }}
            <template v-if="liveEcho != null"> · 当前读数 {{ liveEcho.toFixed(0) }} ns</template>
          </p>
          <p class="dim" style="margin-top:4px">采样从点击"开始标定"起重新计时——之前的旧数据不算，保证窗口里全是拧紧后的读数。稳定后自动提交。</p>
        </template>
        <template v-else>
          <!-- 窗口/样本已达标,只差"稳":这是应力松弛的物理过程,几分钟到几十分钟都正常。
               把趋势值亮出来让现场看到它在收敛,而不是对着一根停住的进度条怀疑死机 -->
          <p class="dim" style="color: var(--warn)">
            窗口与样本已达标，等待应力松弛结束（已等 {{ Math.floor(stallElapsedSec / 60) }} 分 {{ stallElapsedSec % 60 }} 秒）
          </p>
          <p class="dim" style="margin-top:4px">
            当前波动 ±{{ stab?.stddevNs != null ? Number(stab.stddevNs).toFixed(2) : '—' }} ns ·
            趋势 {{ stab?.halfWindowDriftNs != null ? Number(stab.halfWindowDriftNs).toFixed(2) : '—' }} ns（限 ±0.6 ns）
            <template v-if="liveEcho != null"> · 当前读数 {{ liveEcho.toFixed(0) }} ns</template>
          </p>
          <p class="dim" style="margin-top:4px">松弛通常持续数分钟到数十分钟，趋势值收敛到 ±0.6 ns 内即自动提交，请保持螺栓静置。</p>
        </template>
        <div class="wz-actions" style="justify-content: center">
          <el-button size="small" @click="cancelCalibrating">取消，返回上一步</el-button>
          <!-- 长时间不稳(信号天然抖/耦合极限)的人工出口:force 提交,后端记审计 -->
          <el-button v-if="waitingStable && stallElapsedSec >= STALL_FORCE_AFTER_MS / 1000" size="small" type="warning" plain
            :loading="submitting" @click="forceSubmitTorque">信号长时间未稳，强制提交（记审计）</el-button>
        </div>
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
      <div v-if="ladderEditable" class="nut-row">
        <span class="nut-label">螺栓表面状态（扭矩系数 K）</span>
        <el-select v-model="nutFactor" style="width: 250px" size="small">
          <el-option v-for="p in NUT_FACTOR_PRESETS" :key="p.value" :label="p.label" :value="p.value" />
        </el-select>
        <el-input-number v-model="nutFactor" :min="0.05" :max="0.5" :step="0.01" :precision="3" size="small" controls-position="right" style="width: 110px" />
      </div>

      <p class="wz-lead warn-lead" v-if="baselineWarning && ladder.phase === 'tighten' && ladder.idx === 0">{{ baselineWarning }}</p>

      <template v-if="ladder.phase === 'tighten'">
        <p class="wz-lead">
          请用力矩扳手把螺栓拧到 <b class="ladder-target">{{ ladder.torques[ladder.idx] }} N·m</b>，拧到位后点下方按钮开始等稳。
          全程只往紧的方向拧，<b>不要回松</b>——回松会引入摩擦滞后，点就不在一条直线上了。
        </p>
        <div class="wz-actions">
          <el-button @click="close">暂停，稍后继续（进度已保存）</el-button>
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
            <b class="mono" v-if="stab">
              ±{{ stab.stddevNs != null ? Number(stab.stddevNs).toFixed(2) : '—' }} ns ·
              {{ stab.halfWindowDriftNs != null ? Number(stab.halfWindowDriftNs).toFixed(2) : '—' }} ns（限 ±0.6）
            </b>
            <b class="dim" v-else>评估中…</b>
          </div>
          <div class="lb-row">
            <span class="lb-k">窗口样本</span>
            <b class="mono">{{ stab?.sampleCount ?? '—' }}/{{ SAMPLE_TARGET }}</b>
          </div>
          <div class="lb-row">
            <span class="lb-k">状态</span>
            <b v-if="ladderReady" style="color: var(--ok)">稳定，可以采点</b>
            <b v-else-if="ladder.waitRemainSec > 0" style="color: var(--warn)">等待新窗口填满，约还需 {{ ladder.waitRemainSec }} 秒</b>
            <b v-else style="color: var(--warn)">窗口已满但仍在松弛，趋势收敛到 ±0.6 ns 内即可采（通常数分钟，视工况可能更久）</b>
          </div>
          <div class="lb-row">
            <span class="lb-k">实测力值（选填）</span>
            <el-input-number v-model="ladder.measuredForceKn" :min="0.1" :max="10000" :step="1" :precision="2"
              size="small" controls-position="right" style="width: 130px" />
            <span class="dim">kN·拉伸机/测力传感器读数。填了按<b>力值直标</b>入拟合（绕开扭矩折算 ±30% 摩擦误差）；不填按扭矩折算</span>
          </div>
        </div>
        <div class="wz-actions">
          <el-button @click="ladder.phase = 'tighten'; stopLadderCountdown(); stopStabPoll(); stopPoll()">返回</el-button>
          <el-button v-if="ladderStalled" type="warning" plain :loading="ladder.capturing" @click="forceCaptureLadderPoint">
            信号长时间未稳，强制采点（记审计）
          </el-button>
          <el-button type="primary" :disabled="!ladderReady" :loading="ladder.capturing" @click="captureLadderPoint()">
            采点（{{ ladder.torques[ladder.idx] }} N·m）
          </el-button>
        </div>
      </template>
    </template>

    <!-- D:标定试验（连续加载，平台自动出点） -->
    <template v-else-if="step === 'trial-run'">
      <div class="wz-stepline">第 2 步：逐级加载，系统自动分档出点（试验 #{{ trial.id }}）</div>
      <p class="wz-lead">
        用加载装置逐级加载，把测力读数填在下方并打开<b>持续进样</b>。读数保持不动就是一个档位平台——
        系统等力值和声时双双稳定后<b>自动出点</b>；出点后加载到下一档（步距 ≥ 2 kN）。
        没有测力装置时可填扭矩换算值，但有实测力永远优先（绕开扭矩系数 ±30% 误差）。
      </p>
      <div class="live-box">
        <div class="lb-row">
          <span class="lb-k">当前声时</span>
          <b class="mono">{{ liveEcho != null ? liveEcho.toFixed(1) + ' ns' : '等待设备数据…' }}</b>
        </div>
        <div class="lb-row">
          <span class="lb-k">测力读数（kN）</span>
          <span style="display: inline-flex; align-items: center; gap: 10px">
            <el-input-number v-model="trial.forceKn" :min="0.1" :max="10000" :step="1" :precision="2"
              size="small" controls-position="right" style="width: 140px" />
            <el-switch :model-value="trial.feeding" inline-prompt active-text="持续进样中" inactive-text="开始进样"
              style="--el-switch-on-color: #10b981" @change="toggleFeeding" />
          </span>
        </div>
        <div class="lb-row">
          <span class="lb-k">平台状态</span>
          <b v-if="trial.live?.currentPlateau" style="color: var(--ok)">
            平台 {{ (trial.live.currentPlateau.meanForceN / 1000).toFixed(1) }} kN ·
            已保持 {{ trial.live.currentPlateau.durationSeconds.toFixed(0) }}s ·
            声时样本 {{ trial.live.echoCountInPlateau }}/12
          </b>
          <b v-else class="dim">未形成平台（加载中或读数抖动）</b>
        </div>
        <div class="lb-row" v-if="trial.lastHint || trial.live?.hint">
          <span class="lb-k">指引</span>
          <b style="max-width: 420px; text-align: right">{{ trial.lastHint || trial.live?.hint }}</b>
        </div>
      </div>

      <table class="pt-table" v-if="trialValidPoints.length">
        <thead><tr><th>#</th><th>实测力</th><th>Δt (ns)</th><th>声时样本</th><th>时间</th></tr></thead>
        <tbody>
          <tr v-for="(p, i) in trialValidPoints" :key="p.id">
            <td>{{ i + 1 }}</td>
            <td class="mono">{{ (p.forceN / 1000).toFixed(1) }} kN</td>
            <td class="mono">{{ Number(p.deltaTofNs).toFixed(1) }}</td>
            <td class="mono">{{ p.sampleCount }}</td>
            <td class="mono dim">{{ new Date(p.at).toLocaleTimeString() }}</td>
          </tr>
        </tbody>
      </table>
      <p class="wz-hint" v-else>还没有档位点。加载到第一档并保持，出点会自动出现在这里。</p>

      <div class="wz-actions">
        <el-button :disabled="trial.busyAction" @click="close">暂停，稍后继续（试验保持进行中）</el-button>
        <el-button type="danger" plain :loading="trial.busyAction" @click="abortTrial">中止试验（作废）</el-button>
        <el-button type="primary" :disabled="trialValidPoints.length < 3" :loading="trial.busyAction" @click="finishTrial">
          结束并拟合（已 {{ trialValidPoints.length }}/3+ 档）
        </el-button>
      </div>
    </template>

    <!-- B:拟合与应用 -->
    <template v-else-if="step === 'ladder-fit'">
      <div class="wz-stepline">第 3 步：拟合并应用</div>
      <div class="wz-ok-note"><el-icon><CircleCheckFilled /></el-icon> {{ ladder.captured.length }} 个阶梯点已全部采集</div>
      <p class="wz-lead warn-lead" v-if="(ladder.fit?.stalePointCount ?? 0) > 0">
        另有 {{ ladder.fit?.stalePointCount }} 个旧基线下采的历史点未参与本次拟合（基线重设后参考系失效，仅留档备查）。
      </p>
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
      <!-- 每点明细:R² 不达标时现场要能一眼看出哪个点波动大(hardening §4.2 承诺的闭环),
           不再把人赶去专家模式。波动最大的点标黄,一键剔除重拟合 -->
      <table class="pt-table" v-if="ladder.points.length">
        <thead>
          <tr><th>档位</th><th>Δt (ns)</th><th>窗口波动 (ns)</th><th>样本</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="p in ladder.points" :key="p.id" :class="{ worst: worstPoint?.id === p.id && (ladder.fit?.rSquared ?? 1) < 0.98 }">
            <td class="mono">{{ Number(p.torqueNm).toFixed(0) }} N·m</td>
            <td class="mono">{{ Number(p.deltaTofNs).toFixed(2) }}</td>
            <td class="mono">±{{ p.stddevEchoTimeNs != null ? Number(p.stddevEchoTimeNs).toFixed(2) : '—' }}</td>
            <td class="mono">{{ p.sampleCount }}</td>
            <td>{{ worstPoint?.id === p.id && (ladder.fit?.rSquared ?? 1) < 0.98 ? '波动最大' : '' }}</td>
          </tr>
        </tbody>
      </table>
      <p class="wz-hint">
        R² ≥ 0.98 应用后立即生效（自签临时标定）；不达标可剔除波动大的点重拟合（剩余点 ≥3），或拧松重做整轮。
        拟合带截距项，零载基线的小偏差会被截距吸收；注意绝对值精度仍受扭矩系数 K（本轮取 {{ nutFactor }}）支配。
      </p>
      <div class="wz-actions">
        <el-button v-if="(ladder.fit?.rSquared ?? 1) < 0.98 && ladder.points.length > 3"
          type="warning" plain :loading="ladder.removing" @click="removeWorstAndRefit">剔除波动最大的点，重新拟合</el-button>
        <el-button v-if="(ladder.fit?.rSquared ?? 1) < 0.98" @click="restartLadder">拧松重做整轮</el-button>
        <el-button type="primary" :loading="ladder.applying"
          :disabled="(ladder.fit?.pointCount ?? 0) < 3 || (ladder.fit?.rSquared ?? 0) < 0.95"
          @click="applyLadderFit">应用拟合为当前标定</el-button>
      </div>
    </template>

    <!-- C:置零后按目标起算 -->
    <template v-else-if="step === 'anchor-ask'">
      <div class="wz-stepline">第 2 步：认定当前 = 目标，开始看动载荷和衰减</div>
      <div class="wz-ok-note"><el-icon><CircleCheckFilled /></el-icon> 已手动置零（{{ baselineResult ? baselineResult.refNs.toFixed(0) + ' ns' : '' }}）</div>
      <p class="wz-lead">
        认定<b>此刻预紧力 = 档案里已设定的目标值</b>。之后声时变大是动载荷、变小是衰减。全程不需要碰螺栓。
        <span v-if="anchor.loading" class="dim">读取档案…</span>
      </p>
      <div class="torque-row">
        <span class="nut-label" style="width: 130px">目标预紧力</span>
        <el-input-number v-model="anchor.preloadKn" :min="0.1" :step="10" :precision="1" style="width: 200px" />
        <span class="torque-unit">kN</span>
        <span class="dim">档案已设定的数值（可按实际修改）</span>
      </div>
      <div class="nut-row">
        <span class="nut-label" style="width: 130px">力-声时系数 K_L</span>
        <el-input-number v-model="anchor.kL" :min="0" :step="0.0001" :precision="7" controls-position="right" style="width: 200px" />
        <span class="dim">ns/N · 把声时变化换成 kN</span>
      </div>
      <p class="wz-hint" v-if="anchor.peerKL != null">
        已自动沿用同组同规格 {{ anchor.peerCount }} 颗已标螺栓的 K_L 中位数（{{ anchor.peerKL.toExponential(3) }}）——K_L 是批次属性，同批沿用是推荐做法。
      </p>
      <p class="wz-hint" v-else-if="anchor.theoreticalKL != null">
        同组暂无可沿用的标定。理论估算参考值：{{ anchor.theoreticalKL.toExponential(3) }} ns/N（±40%，按螺栓几何/材料推算）
        <span class="link" @click="adoptTheoreticalKl">采用理论值</span>；
        更可靠的做法是对同批备件螺栓做一次实验室标定后录入。
      </p>
      <p class="wz-hint" v-else>
        同组暂无可沿用的标定，且档案缺少弹性模量/截面积（无法理论估算）。请录入同批实验室标定的 K_L，或先补全螺栓档案。
      </p>
      <div class="wz-actions">
        <el-button @click="skipAnchor">跳过，仅看相对松紧</el-button>
        <el-button type="primary" :loading="anchor.submitting"
          :disabled="anchor.preloadKn == null || anchor.kL == null"
          @click="submitAnchor">按目标起算，开始监测</el-button>
      </div>
    </template>

    <!-- A/B/C(锚定):完成 -->
    <template v-else-if="step === 'done'">
      <div class="done-box">
        <el-icon class="done-ic"><CircleCheckFilled /></el-icon>
        <h3>标定完成</h3>
        <!-- 只显示【新标定生效后】的测量(lastTs 相对提交时刻变化才算):
             标定刚完成时"最近一条"还是旧参数算的,旧标定虚高时把它亮出来,
             现场看到的第一眼就是"标了还是不准" -->
        <div class="done-kpi" v-if="doneKn">
          当前预紧力 <b>{{ doneKn }} kN</b>
          <template v-if="scene === 'inservice'">（认定参考）</template>
          <template v-else-if="doneRatioPct">（为目标的 <b>{{ doneRatioPct }}</b>）</template>
        </div>
        <div class="done-kpi dim" v-else>正在等待新标定下的第一条测量（通常几秒到半分钟）…</div>
        <p class="done-note" v-if="scene === 'inservice'">
          已按目标预紧力起算：认定置零时刻 = {{ anchor.preloadKn }} kN。
          声时变大 → 动载荷升高；声时变小 → 衰减/松弛。绝对数值是认定参考（未计入安装偏差与装机后已发生的松弛），<b>动载、衰减与告警按声时变化监测</b>。<br />
          如需更准的绝对值，可对同批备件螺栓做实验室载荷标定后更新 K_L。
        </p>
        <p class="done-note" v-else>
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
          螺栓详情页将显示它相对置零点的<b>松紧变化</b>（偏松/偏紧/基线附近），松弛告警已生效。<br />
          需要按目标显示绝对预紧力时，重新运行本向导选"在役监测"，确认档案目标与 K_L 即可——不用碰螺栓。
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
.scene-card { border: 1px solid var(--panel-border); border-radius: 12px; padding: 12px 14px; cursor: pointer; transition: border-color 0.15s, background 0.15s; }
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
.nut-row { display: flex; align-items: center; gap: 10px; margin: 12px 0 4px; flex-wrap: wrap; }
.nut-label { font-size: 12.5px; color: var(--text-3); }

.pt-table { width: 100%; border-collapse: collapse; font-size: 12.5px; margin: 4px 0 8px; }
.pt-table th { text-align: left; color: var(--text-3); font-weight: 500; padding: 4px 8px; border-bottom: 1px solid var(--panel-border); }
.pt-table td { padding: 4px 8px; border-bottom: 1px solid var(--hairline); }
.pt-table tr.worst td { color: var(--warn); background: rgba(245, 158, 11, 0.08); }
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
