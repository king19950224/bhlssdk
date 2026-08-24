<script setup lang="ts">
// 标定对话框：快速标定直接录 K_L，扭矩分步标定逐档加扭矩取样再拟合。
// 全部路径提交即自签生效（2026-08-19 取消审批环节）；正式载荷标定与临时标定的差异只在判据与档案等级。
import { computed, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { calibrationApi, boltApi, reportApi, type CalibRecord, type TorqueTestPoint, type TorqueFitSummary } from '../api'
import { useAuth } from '../stores/auth'
import { downloadFile } from '../utils/download'
import { calibrationMethodLabel } from '../utils/labels'

const auth = useAuth()

const props = withDefaults(defineProps<{
  modelValue: boolean
  channelId: number | null
  boltId: number | null
  boltCode?: string
}>(), { boltCode: '' })

const emit = defineEmits<{ 'update:modelValue': [boolean]; done: [] }>()

const visible = computed({
  get: () => props.modelValue,
  set: (v) => emit('update:modelValue', v),
})

const tab = ref<'quick' | 'torque' | 'step' | 'load' | 'history'>('quick')
const loadingData = ref(false)

const points = ref<TorqueTestPoint[]>([])
const fitSummary = ref<TorqueFitSummary | null>(null)
const history = ref<CalibRecord[]>([])

const quick = reactive({ kL: undefined as number | undefined, refTimeNs: undefined as number | undefined, refTempC: undefined as number | undefined, kLUncertainty: undefined as number | undefined, note: '' })
const torque = reactive({ torqueNm: undefined as number | undefined, nutFactor: 0.2 as number | undefined, nominalDiameterMm: undefined as number | undefined, note: '' })
const step = reactive({ torqueNm: undefined as number | undefined, nutFactor: 0.2 as number | undefined, nominalDiameterMm: undefined as number | undefined })
// loadingDevice/loadingDeviceCert 是计量追溯字段（后端会写入标定档案）：正式标定必须记录
// 用什么加载装置、证书号是多少——原表单漏采这两项，档案里恒为空，追溯链断在源头
const load = reactive({
  refTimeNs: undefined as number | undefined, refTempC: undefined as number | undefined,
  systematicRelativeUncertainty: undefined as number | undefined,
  loadingDevice: '', loadingDeviceCert: '', note: '',
})
const loadPoints = ref<{ forceN: number | undefined; deltaTofNs: number | undefined }[]>([])

const busy = ref('')

// 历史记录的签署状态（过去的事实性展示）
const REVIEW: Record<string, { label: string; cls: string }> = {
  approved: { label: '已签署', cls: 'rv-ok' },
  self_signed: { label: '已签署', cls: 'rv-ok' },
  pending: { label: '未生效', cls: 'rv-pending' },
  rejected: { label: '已作废', cls: 'rv-rej' },
}

function resetForms() {
  quick.kL = quick.refTimeNs = quick.refTempC = quick.kLUncertainty = undefined
  quick.note = ''
  torque.torqueNm = torque.nominalDiameterMm = undefined
  torque.nutFactor = 0.2
  torque.note = ''
  step.torqueNm = step.nominalDiameterMm = undefined
  step.nutFactor = 0.2
  load.refTimeNs = load.refTempC = load.systematicRelativeUncertainty = undefined
  load.loadingDevice = ''
  load.loadingDeviceCert = ''
  load.note = ''
  loadPoints.value = Array.from({ length: 4 }, () => ({ forceN: undefined, deltaTofNs: undefined }))
}

async function refresh() {
  if (props.channelId == null) return
  loadingData.value = true
  try {
    const [pts, sum, hist] = await Promise.all([
      calibrationApi.torquePoints(props.channelId),
      calibrationApi.torquePointSummary(props.channelId),
      boltApi.calibrations(props.channelId),
    ])
    points.value = pts.items
    fitSummary.value = sum
    history.value = hist.items
  } catch (e) {
    ElMessage.error((e as Error)?.message || '加载标定数据失败')
  } finally {
    loadingData.value = false
  }
}

watch(() => [props.modelValue, props.channelId], ([open]) => {
  if (open && props.channelId != null) {
    tab.value = 'quick'
    resetForms()
    refresh()
  }
})

function guard(): number | null {
  if (props.channelId == null) { ElMessage.warning('未选择通道'); return null }
  return props.channelId
}
async function run(key: string, fn: (cid: number) => Promise<unknown>, ok: string) {
  const cid = guard(); if (cid == null) return
  busy.value = key
  try {
    await fn(cid)
    ElMessage.success(ok)
    await refresh()
    emit('done')
  } catch (e) {
    ElMessage.error((e as Error)?.message || '操作失败')
  } finally {
    busy.value = ''
  }
}

async function submitQuick() {
  if (quick.kL == null || quick.refTimeNs == null || quick.refTempC == null)
    return ElMessage.warning('kL / 基线声时 / 基线温度 必填')
  // 前端先拦一道量级错误（本系统 ns/N 约定量级约 1e-4;按证书 1/N 约定填会差 7 个量级）,后端仍有物理护栏
  if (quick.kL < 1e-6 || quick.kL > 1e-2) {
    try {
      await ElMessageBox.confirm(
        `kL=${quick.kL} 偏离常见量级（约 1e-4 ns/N）。请确认按本系统 ns/N 约定填写，而非标定证书的 1/N 约定。继续提交？`,
        'kL 量级确认', { type: 'warning', confirmButtonText: '确认提交', cancelButtonText: '返回修改' })
    } catch { return }
  }
  run('quick', (cid) => calibrationApi.quickCalibrate(cid, {
    kL: quick.kL!, refTimeNs: quick.refTimeNs!, refTempC: quick.refTempC!,
    kLUncertainty: quick.kLUncertainty, note: quick.note || undefined,
  }), '快速标定已生效（自签）')
}
// 采样窗口 180s：热插拔全通道轮询下单通道刷新率约 0.25Hz，默认 60s 窗口攒不够后端 minSamples=20
const SAMPLE_WINDOW_SEC = 180
function submitTorque() {
  if (torque.torqueNm == null) return ElMessage.warning('扭矩必填')
  run('torque', (cid) => calibrationApi.torqueCalibrate(cid, {
    torqueNm: torque.torqueNm!, nutFactor: torque.nutFactor, nominalDiameterMm: torque.nominalDiameterMm,
    sampleWindowSeconds: SAMPLE_WINDOW_SEC, note: torque.note || undefined,
  }), '扭矩法临时标定已生效（自签）')
}
function captureStepPoint() {
  if (step.torqueNm == null) return ElMessage.warning('扭矩必填')
  run('capture', (cid) => calibrationApi.captureTorquePoint(cid, {
    torqueNm: step.torqueNm!, nutFactor: step.nutFactor, nominalDiameterMm: step.nominalDiameterMm,
    sampleWindowSeconds: SAMPLE_WINDOW_SEC,
  }), '已采集一个扭矩阶梯点')
}
function applyFit() {
  run('applyfit', (cid) => calibrationApi.applyTorqueFit(cid, {}), '已将扭矩阶梯拟合应用为当前标定（自签）')
}

// 声时波动阈值：判断这个点是不是在"拧到位、力稳住了"的状态下采的。
// 采样窗口取的是最近 180s 的平均，扳手还在拧的时候采，窗口里混着加载过程的声时，
// 均值落在两个扭矩档中间，把拟合斜率带偏还看不出来 —— 波动大是这种坏点最直接的特征。
// 阈值按现场经验：1ns 声时差约合 2kN，稳态下同一档的抖动通常在 0.5ns 内。
const STDDEV_WARN_NS = 0.5
const STDDEV_BAD_NS = 2
const num = (v: number | null | undefined) => (v == null ? null : Number(v))
function stddevLevel(v: number | null): 'ok' | 'warn' | 'bad' | 'na' {
  if (v == null) return 'na'
  if (v >= STDDEV_BAD_NS) return 'bad'
  if (v >= STDDEV_WARN_NS) return 'warn'
  return 'ok'
}
// "有效"=未剔除且基线匹配：基线重设后旧点参考系失效，后端拟合/摘要已不认它们，
// 这里的计数口径必须跟上，否则按钮提示"点够了"而后端说"点不足"
const unstablePoints = computed(() =>
  points.value.filter((p) => !p.excluded && p.baselineMatched && stddevLevel(num(p.stddevEchoTimeNs)) === 'bad').length)
const validPointCount = computed(() => points.value.filter((p) => !p.excluded && p.baselineMatched).length)
const stalePointCount = computed(() => points.value.filter((p) => !p.excluded && !p.baselineMatched).length)

async function toggleExclude(p: TorqueTestPoint) {
  const cid = guard(); if (cid == null) return
  if (p.excluded) {
    await run('ex' + p.id, () => calibrationApi.patchTorquePoint(cid, p.id, { excluded: false }),
      `已恢复 ${p.torqueNm} N·m 这个点，重新参与拟合`)
    return
  }
  let reason = ''
  try {
    const r = await ElMessageBox.prompt(
      `剔除 ${p.torqueNm} N·m 这个点（Δt ${Number(p.deltaTofNs).toFixed(1)} ns）。原因会记入标定档案与阶梯报告。`,
      '剔除实验点',
      {
        confirmButtonText: '确认剔除', cancelButtonText: '取消',
        inputPlaceholder: '例如：扳手还在拧就采了 / 扭矩输错 / 该时段信号丢',
        inputValidator: (v: string) => (v || '').trim().length > 0 || '请填写剔除原因',
      })
    reason = (r.value || '').trim()
  } catch { return }
  await run('ex' + p.id, () => calibrationApi.patchTorquePoint(cid, p.id, { excluded: true, reason }),
    `已剔除 ${p.torqueNm} N·m 这个点，不再参与拟合`)
}
function submitLoad() {
  const valid = loadPoints.value.filter((p) => p.forceN != null && p.deltaTofNs != null)
  if (valid.length < 4) return ElMessage.warning('正式载荷标定至少需要 4 个有效载荷点（F 与 Δt 都填）')
  if (load.refTimeNs == null || load.refTempC == null) return ElMessage.warning('基线声时 / 基线温度 必填')
  run('load', (cid) => calibrationApi.loadCalibrate(cid, {
    points: valid.map((p) => ({ forceN: p.forceN!, deltaTofNs: p.deltaTofNs! })),
    refTimeNs: load.refTimeNs!, refTempC: load.refTempC!,
    systematicRelativeUncertainty: load.systematicRelativeUncertainty,
    loadingDevice: load.loadingDevice.trim() || undefined,
    loadingDeviceCert: load.loadingDeviceCert.trim() || undefined,
    note: load.note || undefined,
  }), '正式载荷标定已生效，并记入标定档案')
}
function addLoadRow() { loadPoints.value.push({ forceN: undefined, deltaTofNs: undefined }) }
function delLoadRow(i: number) { loadPoints.value.splice(i, 1) }

async function genReport() {
  const cid = guard(); if (cid == null) return
  busy.value = 'report'
  try {
    const r = await reportApi.generateTorqueStep(cid)
    ElMessage.success('扭矩阶梯报告已生成')
    await downloadReport(r.reportId)
    emit('done')
  } catch (e) {
    ElMessage.error((e as Error)?.message || '生成报告失败（需有扭矩实验点）')
  } finally {
    busy.value = ''
  }
}
async function downloadReport(id: number) {
  try {
    await downloadFile(reportApi.downloadUrl(id), `扭矩阶梯报告-${id}.html`)
  } catch {
    ElMessage.error('下载失败')
  }
}
</script>

<template>
  <el-dialog v-model="visible" :title="`标定工作台 · ${boltCode || '通道 ' + channelId}`" width="760px" append-to-body destroy-on-close>
    <el-alert type="info" :closable="false" show-icon style="margin-bottom: 14px">
      K_L 量纲约定 <b>ns/N（量级约 1e-4）</b>；勿照标定证书的 1/N（约 1e-11）填。各标定路径<b>提交即生效</b>，签署人与时间自动记入档案。
    </el-alert>

    <el-tabs v-model="tab">
      <!-- 快速标定 -->
      <el-tab-pane label="快速标定" name="quick">
        <el-form label-width="120px" label-position="left">
          <el-form-item label="K_L (ns/N)"><el-input-number v-model="quick.kL" :step="0.0001" :precision="6" controls-position="right" style="width:100%" /></el-form-item>
          <el-form-item label="基线声时 (ns)"><el-input-number v-model="quick.refTimeNs" :step="100" :min="0" controls-position="right" style="width:100%" /></el-form-item>
          <el-form-item label="基线温度 (°C)"><el-input-number v-model="quick.refTempC" :step="0.1" controls-position="right" style="width:100%" /></el-form-item>
          <el-form-item label="K_L 不确定度"><el-input-number v-model="quick.kLUncertainty" :step="0.00001" :precision="7" controls-position="right" style="width:100%" /></el-form-item>
          <el-form-item label="备注"><el-input v-model="quick.note" type="textarea" :rows="2" /></el-form-item>
          <el-button type="primary" :loading="busy === 'quick'" @click="submitQuick">应用快速标定</el-button>
        </el-form>
      </el-tab-pane>

      <!-- 扭矩法 -->
      <el-tab-pane label="扭矩法" name="torque">
        <el-form label-width="120px" label-position="left">
          <el-form-item label="扭矩 (N·m)"><el-input-number v-model="torque.torqueNm" :step="10" :min="0" controls-position="right" style="width:100%" /></el-form-item>
          <el-form-item label="扭矩系数 K"><el-input-number v-model="torque.nutFactor" :step="0.01" :min="0" :precision="3" controls-position="right" style="width:100%" /></el-form-item>
          <el-form-item label="公称直径 (mm)"><el-input-number v-model="torque.nominalDiameterMm" :step="1" :min="0" controls-position="right" style="width:100%" /></el-form-item>
          <el-form-item label="备注"><el-input v-model="torque.note" type="textarea" :rows="2" /></el-form-item>
          <el-button type="primary" :loading="busy === 'torque'" @click="submitTorque">扭矩法临时标定</el-button>
          <p class="hint">T = K·F·d 估算预紧力，再用最近 EchoTime 反推 K_L；临时估算，非正式标定。</p>
        </el-form>
      </el-tab-pane>

      <!-- 扭矩阶梯 -->
      <el-tab-pane label="扭矩阶梯" name="step">
        <p class="steps">
          每档的做法：<b>拧到目标扭矩 → 松手等声时稳住（约 {{ SAMPLE_WINDOW_SEC }} 秒）→ 再采点</b>。
          采样取的是最近 {{ SAMPLE_WINDOW_SEC }} 秒的平均，扳手还在拧的时候采，窗口里会混进加载过程的声时，
          均值落在两档中间，拟合就被带偏了。采完看“声时波动”那列，偏大的点直接剔除重采。
        </p>
        <div class="cap-row">
          <el-input-number v-model="step.torqueNm" :step="10" :min="0" placeholder="扭矩 N·m" controls-position="right" />
          <el-input-number v-model="step.nutFactor" :step="0.01" :min="0" :precision="3" placeholder="K" controls-position="right" />
          <el-input-number v-model="step.nominalDiameterMm" :step="1" :min="0" placeholder="直径 mm" controls-position="right" />
          <el-button :loading="busy === 'capture'" @click="captureStepPoint">采点</el-button>
        </div>
        <div class="tbl step-tbl">
          <div class="tr th"><span>扭矩</span><span>估算预紧力</span><span>Δt (ns)</span><span>声时波动</span><span>样本</span><span>状态</span><span></span></div>
          <div v-for="p in points" :key="p.id" class="tr" :class="{ ex: p.excluded || !p.baselineMatched }">
            <span class="mono">{{ p.torqueNm }}</span>
            <span class="mono">{{ Math.round(p.estimatedPreloadN) }}</span>
            <span class="mono">{{ Number(p.deltaTofNs).toFixed(1) }}</span>
            <span class="mono" :class="'sd-' + stddevLevel(num(p.stddevEchoTimeNs))"
              :title="stddevLevel(num(p.stddevEchoTimeNs)) === 'bad' ? '采样窗口内声时波动很大，多半是拧的过程中采的，建议剔除重采' : ''">
              {{ p.stddevEchoTimeNs != null ? '±' + Number(p.stddevEchoTimeNs).toFixed(2) : '—' }}
            </span>
            <span class="mono">{{ p.sampleCount }}</span>
            <span :title="p.excluded ? (p.excludeReason || '') : (!p.baselineMatched ? '采于旧基线（此后基线被重设），参考系失效，不参与拟合' : '')">
              {{ p.excluded ? '剔除' : p.baselineMatched ? '纳入' : '旧基线' }}
            </span>
            <span>
              <el-button size="small" text :type="p.excluded ? 'primary' : 'danger'"
                :loading="busy === 'ex' + p.id" @click="toggleExclude(p)">
                {{ p.excluded ? '恢复' : '剔除' }}
              </el-button>
            </span>
          </div>
          <div v-if="!points.length" class="tbl-empty">尚无扭矩阶梯点，按 50/100/150/200 N·m 逐级采点</div>
        </div>
        <div v-if="unstablePoints" class="warn-line">
          有 {{ unstablePoints }} 个点的声时波动偏大（≥{{ STDDEV_BAD_NS }} ns），很可能是拧的过程中采的，建议剔除后重采。
        </div>
        <div v-if="stalePointCount" class="warn-line">
          有 {{ stalePointCount }} 个点采于旧基线（此后基线被重设，Δt 参考系已失效），不参与拟合，仅留档备查。
        </div>
        <div v-if="fitSummary" class="fit">
          有效点 <b>{{ fitSummary.pointCount }}</b> · K_L <b class="mono">{{ fitSummary.kLByLinearFitNsPerN != null ? fitSummary.kLByLinearFitNsPerN.toExponential(3) : '—' }}</b>
          · R² <b class="mono" :style="(fitSummary.rSquared ?? 0) >= 0.98 ? 'color:var(--ok)' : 'color:var(--warn)'">{{ fitSummary.rSquared != null ? fitSummary.rSquared.toFixed(4) : '—' }}</b>
        </div>
        <div class="step-actions">
          <el-button type="primary" :loading="busy === 'applyfit'" :disabled="validPointCount < 3" @click="applyFit">
            应用拟合为当前标定<template v-if="validPointCount < 3">（还差 {{ 3 - validPointCount }} 个点）</template>
          </el-button>
          <el-button :loading="busy === 'report'" :disabled="!points.length" @click="genReport">生成扭矩阶梯报告</el-button>
        </div>
      </el-tab-pane>

      <!-- 正式载荷标定 -->
      <el-tab-pane label="正式载荷标定" name="load">
        <div class="tbl lp">
          <div class="tr th"><span>已知载荷 F (N)</span><span>温补后 Δt (ns)</span><span></span></div>
          <div v-for="(p, i) in loadPoints" :key="i" class="tr">
            <span><el-input-number v-model="p.forceN" :step="1000" :min="0" controls-position="right" style="width:100%" /></span>
            <span><el-input-number v-model="p.deltaTofNs" :step="1" controls-position="right" style="width:100%" /></span>
            <span><el-button size="small" text @click="delLoadRow(i)">删</el-button></span>
          </div>
        </div>
        <el-button size="small" text @click="addLoadRow">+ 增加载荷点</el-button>
        <el-form label-width="120px" label-position="left" style="margin-top:10px">
          <el-form-item label="基线声时 (ns)"><el-input-number v-model="load.refTimeNs" :step="100" :min="0" controls-position="right" style="width:100%" /></el-form-item>
          <el-form-item label="基线温度 (°C)"><el-input-number v-model="load.refTempC" :step="0.1" controls-position="right" style="width:100%" /></el-form-item>
          <el-form-item label="系统相对不确定度"><el-input-number v-model="load.systematicRelativeUncertainty" :step="0.001" :min="0" :precision="4" controls-position="right" style="width:100%" /></el-form-item>
          <el-form-item label="加载装置"><el-input v-model="load.loadingDevice" placeholder="如 200kN 液压拉伸器 / 万能试验机（记入档案，供计量追溯）" maxlength="128" /></el-form-item>
          <el-form-item label="装置检定证书号"><el-input v-model="load.loadingDeviceCert" placeholder="加载装置的检定/校准证书编号" maxlength="128" /></el-form-item>
          <el-form-item label="备注"><el-input v-model="load.note" type="textarea" :rows="2" /></el-form-item>
          <el-button type="primary" :loading="busy === 'load'" @click="submitLoad">提交正式标定（立即生效）</el-button>
          <p class="hint">≥4 点最小二乘，强制 R²≥0.98 + JJF 不确定度评定；提交后立即生效并记入标定档案。</p>
        </el-form>
      </el-tab-pane>

      <!-- 标定档案 -->
      <el-tab-pane label="标定档案" name="history">
        <div v-loading="loadingData" class="tbl hist">
          <div class="tr th"><span>方法</span><span>K_L</span><span>R²</span><span>状态</span><span>到期</span></div>
          <div v-for="c in history" :key="c.id" class="tr">
            <span>{{ calibrationMethodLabel(c.method) }} <i v-if="c.isActive" class="active">生效</i></span>
            <span class="mono">{{ Number(c.kL).toExponential(2) }}</span>
            <!-- 单点法(快速/扭矩估算)无回归,R² 占位 0 显示成"0.000 拟合极差"会误导 → 显示 — -->
            <span class="mono">{{ ['quick', 'torque_estimate', 'sim_baseline'].includes(c.method) && Number(c.rSquared) === 0 ? '—' : Number(c.rSquared).toFixed(3) }}</span>
            <span><i class="rv" :class="REVIEW[c.reviewState]?.cls">{{ REVIEW[c.reviewState]?.label || c.reviewState }}</i></span>
            <span class="mono dim">{{ new Date(c.expiresAt).toLocaleDateString('zh-CN') }}</span>
          </div>
          <div v-if="!loadingData && !history.length" class="tbl-empty">该通道暂无标定档案</div>
        </div>
      </el-tab-pane>
    </el-tabs>

    <template #footer><el-button @click="visible = false">关闭</el-button></template>
  </el-dialog>
</template>

<style scoped>
.hint { font-size: 12px; color: var(--text-3); margin: 10px 0 0; line-height: 1.6; }
.cap-row { display: flex; gap: 8px; margin-bottom: 12px; flex-wrap: wrap; }
.cap-row .el-input-number { width: 130px; }
.tbl { border: 1px solid var(--hairline); border-radius: 8px; overflow: hidden; }
.tbl.lp .tr, .tbl.lp .tr.th { grid-template-columns: 1fr 1fr 50px; }
.tr { display: grid; grid-template-columns: 1fr 1.2fr 1fr 0.7fr 1fr; align-items: center; gap: 8px; padding: 8px 12px; border-bottom: 1px solid var(--hairline); font-size: 13px; }
.tbl.hist .tr { grid-template-columns: 1.3fr 1fr 0.8fr 1fr 1fr; }
.tbl.step-tbl .tr { grid-template-columns: 0.7fr 1fr 0.8fr 0.9fr 0.6fr 0.7fr 0.7fr; }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.06em; background: rgba(148,163,184,0.04); }
.tr.ex { opacity: 0.5; }
.tr:last-child { border-bottom: none; }
.tbl-empty { padding: 20px; text-align: center; color: var(--text-3); font-size: 13px; }
.fit { margin: 12px 0; font-size: 13px; color: var(--text-2); }
.steps { font-size: 12px; color: var(--text-3); line-height: 1.7; margin: 0 0 12px; }
.steps b { color: var(--text-2); font-weight: 600; }
.warn-line { margin-top: 10px; padding: 8px 12px; border-radius: 8px; font-size: 12px; color: var(--warn); background: rgba(245, 158, 11, 0.08); border: 1px solid rgba(245, 158, 11, 0.35); }
.sd-ok { color: var(--ok); }
.sd-warn { color: var(--warn); }
.sd-bad { color: var(--crit); }
.sd-na { color: var(--text-3); }
.step-actions { display: flex; gap: 10px; margin-top: 6px; }
.active { font-size: 10px; color: var(--accent); border: 1px solid var(--panel-border-strong); border-radius: 7px; padding: 0 5px; font-style: normal; margin-left: 6px; }
.dim { color: var(--text-3); }
.rv { font-size: 11px; padding: 1px 8px; border-radius: 9px; border: 1px solid; font-style: normal; }
.rv-ok { color: var(--ok); border-color: rgba(16,185,129,0.5); }
.rv-self { color: var(--text-2); border-color: var(--hairline); }
.rv-pending { color: var(--warn); border-color: var(--warn); }
.rv-rej { color: var(--crit); border-color: var(--crit); }
</style>
