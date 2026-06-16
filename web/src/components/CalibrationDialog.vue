<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { calibrationApi, boltApi, reportApi, type CalibRecord, type TorqueTestPoint, type TorqueFitSummary } from '../api'
import { getAuthToken } from '../api/client'

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
const load = reactive({ refTimeNs: undefined as number | undefined, refTempC: undefined as number | undefined, systematicRelativeUncertainty: undefined as number | undefined, note: '' })
const loadPoints = ref<{ forceN: number | undefined; deltaTofNs: number | undefined }[]>([])

const busy = ref('')

const METHOD_LABEL: Record<string, string> = {
  load_calibrated: '正式载荷', gb_single_wave: 'GB单波', gb_bi_wave: 'GB双波',
  torque_fit: '扭矩拟合', torque_estimate: '扭矩估算', quick: '快速',
}
const REVIEW: Record<string, { label: string; cls: string }> = {
  approved: { label: '两人会签', cls: 'rv-ok' },
  self_signed: { label: '自签', cls: 'rv-self' },
  pending: { label: '待会签', cls: 'rv-pending' },
  rejected: { label: '驳回', cls: 'rv-rej' },
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

function submitQuick() {
  if (quick.kL == null || quick.refTimeNs == null || quick.refTempC == null)
    return ElMessage.warning('kL / 基线声时 / 基线温度 必填')
  run('quick', (cid) => calibrationApi.quickCalibrate(cid, {
    kL: quick.kL!, refTimeNs: quick.refTimeNs!, refTempC: quick.refTempC!,
    kLUncertainty: quick.kLUncertainty, note: quick.note || undefined,
  }), '快速标定已生效（自签）')
}
function submitTorque() {
  if (torque.torqueNm == null) return ElMessage.warning('扭矩必填')
  run('torque', (cid) => calibrationApi.torqueCalibrate(cid, {
    torqueNm: torque.torqueNm!, nutFactor: torque.nutFactor, nominalDiameterMm: torque.nominalDiameterMm, note: torque.note || undefined,
  }), '扭矩法临时标定已生效（自签）')
}
function captureStepPoint() {
  if (step.torqueNm == null) return ElMessage.warning('扭矩必填')
  run('capture', (cid) => calibrationApi.captureTorquePoint(cid, {
    torqueNm: step.torqueNm!, nutFactor: step.nutFactor, nominalDiameterMm: step.nominalDiameterMm,
  }), '已采集一个扭矩阶梯点')
}
function applyFit() {
  run('applyfit', (cid) => calibrationApi.applyTorqueFit(cid, {}), '已将扭矩阶梯拟合应用为当前标定（自签）')
}
function submitLoad() {
  const valid = loadPoints.value.filter((p) => p.forceN != null && p.deltaTofNs != null)
  if (valid.length < 4) return ElMessage.warning('正式载荷标定至少需要 4 个有效载荷点（F 与 Δt 都填）')
  if (load.refTimeNs == null || load.refTempC == null) return ElMessage.warning('基线声时 / 基线温度 必填')
  run('load', (cid) => calibrationApi.loadCalibrate(cid, {
    points: valid.map((p) => ({ forceN: p.forceN!, deltaTofNs: p.deltaTofNs! })),
    refTimeNs: load.refTimeNs!, refTempC: load.refTempC!,
    systematicRelativeUncertainty: load.systematicRelativeUncertainty, note: load.note || undefined,
  }), '正式载荷标定已提交，待【另一名】复核员会签后生效')
}
async function countersign(c: CalibRecord) {
  const cid = guard(); if (cid == null) return
  try {
    await ElMessageBox.confirm(
      `会签激活该正式载荷标定（K_L=${Number(c.kL).toExponential(3)}，R²=${Number(c.rSquared).toFixed(3)}）。会签人须与提交人不同。`,
      '会签激活', { confirmButtonText: '会签激活', cancelButtonText: '取消', type: 'warning' })
  } catch { return }
  await run('cs' + c.id, () => calibrationApi.countersign(cid, c.id), '已会签激活')
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
    const res = await fetch(reportApi.downloadUrl(id), { headers: { Authorization: `Bearer ${getAuthToken()}` } })
    if (!res.ok) throw new Error()
    const blob = await res.blob()
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `torque-step-report-${id}.html`
    a.click()
    URL.revokeObjectURL(url)
  } catch {
    ElMessage.error('下载失败')
  }
}
</script>

<template>
  <el-dialog v-model="visible" :title="`标定工作台 · ${boltCode || '通道 ' + channelId}`" width="760px" top="6vh" destroy-on-close>
    <el-alert type="info" :closable="false" show-icon style="margin-bottom: 14px">
      K_L 量纲约定 <b>ns/N（量级约 1e-4）</b>；勿照标定证书的 1/N（约 1e-11）填。快速/扭矩/拟合为<b>自签临时标定</b>，正式载荷标定须<b>异人会签</b>方可生效。
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
        <div class="cap-row">
          <el-input-number v-model="step.torqueNm" :step="10" :min="0" placeholder="扭矩 N·m" controls-position="right" />
          <el-input-number v-model="step.nutFactor" :step="0.01" :min="0" :precision="3" placeholder="K" controls-position="right" />
          <el-input-number v-model="step.nominalDiameterMm" :step="1" :min="0" placeholder="直径 mm" controls-position="right" />
          <el-button :loading="busy === 'capture'" @click="captureStepPoint">采点</el-button>
        </div>
        <div class="tbl">
          <div class="tr th"><span>扭矩</span><span>估算预紧力</span><span>Δt (ns)</span><span>样本</span><span>状态</span></div>
          <div v-for="p in points" :key="p.id" class="tr" :class="{ ex: p.excluded }">
            <span class="mono">{{ p.torqueNm }}</span>
            <span class="mono">{{ Math.round(p.estimatedPreloadN) }}</span>
            <span class="mono">{{ Number(p.deltaTofNs).toFixed(1) }}</span>
            <span class="mono">{{ p.sampleCount }}</span>
            <span>{{ p.excluded ? '剔除' : '纳入' }}</span>
          </div>
          <div v-if="!points.length" class="tbl-empty">尚无扭矩阶梯点，按 50/100/150/200 N·m 逐级采点</div>
        </div>
        <div v-if="fitSummary" class="fit">
          有效点 <b>{{ fitSummary.pointCount }}</b> · K_L <b class="mono">{{ fitSummary.kLByLinearFitNsPerN != null ? fitSummary.kLByLinearFitNsPerN.toExponential(3) : '—' }}</b>
          · R² <b class="mono" :style="(fitSummary.rSquared ?? 0) >= 0.98 ? 'color:var(--ok)' : 'color:var(--warn)'">{{ fitSummary.rSquared != null ? fitSummary.rSquared.toFixed(4) : '—' }}</b>
        </div>
        <div class="step-actions">
          <el-button type="primary" :loading="busy === 'applyfit'" :disabled="(fitSummary?.pointCount ?? 0) < 3" @click="applyFit">应用拟合为当前标定</el-button>
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
          <el-form-item label="备注"><el-input v-model="load.note" type="textarea" :rows="2" /></el-form-item>
          <el-button type="primary" :loading="busy === 'load'" @click="submitLoad">提交正式标定（待会签）</el-button>
          <p class="hint">≥4 点最小二乘，强制 R²≥0.98 + JJF 不确定度评定；提交后须另一名复核员到「标定档案」会签激活。</p>
        </el-form>
      </el-tab-pane>

      <!-- 标定档案 -->
      <el-tab-pane label="标定档案" name="history">
        <div v-loading="loadingData" class="tbl hist">
          <div class="tr th"><span>方法</span><span>K_L</span><span>R²</span><span>状态</span><span>到期</span><span>操作</span></div>
          <div v-for="c in history" :key="c.id" class="tr">
            <span>{{ METHOD_LABEL[c.method] || c.method }} <i v-if="c.isActive" class="active">生效</i></span>
            <span class="mono">{{ Number(c.kL).toExponential(2) }}</span>
            <span class="mono">{{ Number(c.rSquared).toFixed(3) }}</span>
            <span><i class="rv" :class="REVIEW[c.reviewState]?.cls">{{ REVIEW[c.reviewState]?.label || c.reviewState }}</i></span>
            <span class="mono dim">{{ new Date(c.expiresAt).toLocaleDateString('zh-CN') }}</span>
            <span><el-button v-if="c.reviewState === 'pending'" size="small" type="primary" text :loading="busy === 'cs' + c.id" @click="countersign(c)">会签</el-button></span>
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
.tbl.hist .tr { grid-template-columns: 1.3fr 1fr 0.8fr 1fr 1fr 0.8fr; }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.06em; background: rgba(148,163,184,0.04); }
.tr.ex { opacity: 0.5; }
.tr:last-child { border-bottom: none; }
.tbl-empty { padding: 20px; text-align: center; color: var(--text-3); font-size: 13px; }
.fit { margin: 12px 0; font-size: 13px; color: var(--text-2); }
.step-actions { display: flex; gap: 10px; margin-top: 6px; }
.active { font-size: 10px; color: var(--accent); border: 1px solid var(--panel-border-strong); border-radius: 7px; padding: 0 5px; font-style: normal; margin-left: 6px; }
.dim { color: var(--text-3); }
.rv { font-size: 11px; padding: 1px 8px; border-radius: 9px; border: 1px solid; font-style: normal; }
.rv-ok { color: var(--ok); border-color: rgba(16,185,129,0.5); }
.rv-self { color: var(--text-2); border-color: var(--hairline); }
.rv-pending { color: var(--warn); border-color: var(--warn); }
.rv-rej { color: var(--crit); border-color: var(--crit); }
</style>
