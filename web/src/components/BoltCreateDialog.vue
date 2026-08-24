<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import {
  assetApi, channelApi, specLibraryApi,
  type ChannelRow, type UnboundSummaryRow, type MaterialSpec, type BoltSpecPreset, type SpecResolveResult,
} from '../api'

// “建档并绑定”对话框：现场接了新探头/新螺栓时的一键闭环。
// 选未绑定通道 + 监测组 → 填位号与规格（预设可改）→ 提交后：
// 建 bolts 档案、通道置 bound、暂存数据回填成正式测量，一个事务完成。
const props = withDefaults(defineProps<{
  modelValue: boolean
  /** 预选通道（设备页行按钮 / 法兰页引导条传入） */
  channelId?: number | null
  /** 预选监测组（法兰页传当前组） */
  groupId?: number | null
}>(), { channelId: null, groupId: null })

const emit = defineEmits<{
  'update:modelValue': [boolean]
  // 带出新档案标识,父页面拿到后可直接弹标定向导(建档→绑定→标定一口气走完)
  done: [payload?: { boltId: number; channelId: number; code: string }]
}>()

const visible = computed({
  get: () => props.modelValue,
  set: (v) => emit('update:modelValue', v),
})

// 规格/等级下拉：常用值 + 可输入（allow-create）；国标推导（Rp0.2/As）走 resolve 展开器
const SPECS = ['M12', 'M16', 'M20', 'M24', 'M27', 'M30', 'M33', 'M36', 'M39', 'M42', 'M48', 'M56', 'M64']
const GRADES = ['5.6', '6.8', '8.8', '9.8', '10.9', '12.9', 'A2-70', 'A4-70']

const form = reactive({
  channelId: null as number | null,
  groupId: null as number | null,
  code: '',
  positionLabel: '',
  nominalDiam: 'M20',
  grade: '8.8',
  materialCode: '42CrMo',
  effectiveLengthMm: undefined as number | undefined,
  totalLengthMm: undefined as number | undefined,
  targetPreloadN: undefined as number | undefined,
  minPreloadN: undefined as number | undefined,
  backfillUnbound: true,
  note: '',
})

// ---- 参数字典联动（material-library-design §3）----
// 选规格/等级/材料 → resolve 一次带出 Rp0.2/As/E/声速/温补；预设一键填全表。
// 展开值随建档落到螺栓行（不落引用），改字典不追溯历史螺栓。
const materialsList = ref<MaterialSpec[]>([])
const presetsList = ref<BoltSpecPreset[]>([])
const presetId = ref<number | null>(null)
const resolved = ref<SpecResolveResult | null>(null)
// 预设的覆盖值（非标件）优先于国标推导
const specOverride = reactive({ crossSectionMm2: null as number | null, rp02Mpa: null as number | null })

async function refreshResolve() {
  try {
    resolved.value = await specLibraryApi.resolve({
      nominalDiam: form.nominalDiam, grade: form.grade, materialCode: form.materialCode,
    })
  } catch { resolved.value = null }
}
watch(() => [form.nominalDiam, form.grade, form.materialCode], () => void refreshResolve())

function applyPreset(id: number | null) {
  presetId.value = id
  const p = presetsList.value.find((x) => x.id === id)
  if (!p) { specOverride.crossSectionMm2 = null; specOverride.rp02Mpa = null; return }
  form.nominalDiam = p.nominalDiam
  form.grade = p.grade
  form.materialCode = p.materialCode
  form.effectiveLengthMm = Number(p.effectiveLengthMm)
  form.totalLengthMm = Number(p.totalLengthMm)
  if (p.targetPreloadN != null) form.targetPreloadN = Number(p.targetPreloadN)
  if (p.minPreloadN != null) form.minPreloadN = Number(p.minPreloadN)
  specOverride.crossSectionMm2 = p.crossSectionMm2 != null ? Number(p.crossSectionMm2) : null
  specOverride.rp02Mpa = p.rp02Mpa != null ? Number(p.rp02Mpa) : null
}

// 提交时的最终推导值：预设覆盖 > 国标/材料库；都没有则不传（后端有缺省兜底）
const effectiveAs = computed(() => specOverride.crossSectionMm2 ?? resolved.value?.stressAreaMm2 ?? null)
const effectiveRp02 = computed(() => specOverride.rp02Mpa ?? resolved.value?.rp02Mpa ?? null)
const resolvedSummary = computed(() => {
  const m = resolved.value?.material
  const parts: string[] = []
  parts.push(`As ${effectiveAs.value != null ? effectiveAs.value + ' mm²' : '未知（需手填/非标）'}`)
  parts.push(`Rp0.2 ${effectiveRp02.value != null ? effectiveRp02.value + ' MPa' : '未知等级'}`)
  if (m) parts.push(`E ${m.elasticModulusGpa} GPa · 声速 ${m.soundVelocityMs} m/s · 温补 ${Number(m.tempCoefficientPerC).toExponential(2)}`)
  else parts.push('材料未选/已停用')
  return parts.join(' ｜ ')
})

const loading = ref(false)
const submitting = ref(false)

// 可选通道 = 未绑定螺栓的通道；标注暂存数据条数（有数据堆积的排前面，就是现场刚接的那路）
const channels = ref<ChannelRow[]>([])
const unbound = ref<UnboundSummaryRow[]>([])
const channelOptions = computed(() => {
  const cnt = new Map(unbound.value.map((u) => [u.channelId, u.count]))
  return channels.value
    .filter((c) => c.boltId == null)
    .map((c) => ({
      value: c.id,
      channelNo: c.channelNo,
      deviceId: c.deviceId,
      pending: cnt.get(c.id) ?? 0,
    }))
    .sort((a, b) => b.pending - a.pending || a.channelNo - b.channelNo)
})
const multiDevice = computed(() => new Set(channelOptions.value.map((o) => o.deviceId)).size > 1)

// 组选项：projects → turbines → groups 级联拉平（部署规模小，打开对话框时拉一次可接受）
const groupOptions = ref<{ id: number; label: string }[]>([])

async function loadOptions() {
  loading.value = true
  try {
    const [chList, ub, projs, mats, pres] = await Promise.all([
      channelApi.list(),
      channelApi.unboundSummary(),
      assetApi.projects(),
      specLibraryApi.materials().catch(() => ({ count: 0, items: [] as MaterialSpec[] })),
      specLibraryApi.presets().catch(() => ({ count: 0, items: [] as BoltSpecPreset[] })),
    ])
    channels.value = chList.items
    unbound.value = ub.items
    materialsList.value = mats.items.filter((m) => m.enabled)
    presetsList.value = pres.items.filter((p) => p.enabled)
    void refreshResolve()

    const turbineLists = await Promise.all(projs.items.map((p) => assetApi.turbines(p.id)))
    const turbines = turbineLists.flatMap((t) => t.items)
    const groupLists = await Promise.all(turbines.map((t) => assetApi.groups(t.id)))
    groupOptions.value = groupLists.flatMap((g, i) =>
      g.items.map((gg) => ({ id: gg.id, label: `${turbines[i].name} · ${gg.name}` })))
  } catch (e) {
    ElMessage.error((e as Error)?.message || '加载通道/监测组失败')
  } finally {
    loading.value = false
  }
}

watch(() => props.modelValue, (open) => {
  if (!open) return
  form.channelId = props.channelId ?? null
  form.groupId = props.groupId ?? null
  form.code = ''
  form.positionLabel = ''
  form.nominalDiam = 'M20'
  form.grade = '8.8'
  form.materialCode = '42CrMo'
  form.effectiveLengthMm = undefined
  form.totalLengthMm = undefined
  form.targetPreloadN = undefined
  form.minPreloadN = undefined
  form.backfillUnbound = true
  form.note = ''
  presetId.value = null
  specOverride.crossSectionMm2 = null
  specOverride.rp02Mpa = null
  loadOptions().then(() => {
    // 预选通道若已绑定螺栓（不在可选项里），丢弃预选；再默认选"暂存数据最多"的那路（现场刚接上的探头）
    if (form.channelId != null && !channelOptions.value.some((o) => o.value === form.channelId))
      form.channelId = null
    if (form.channelId == null && channelOptions.value.length) form.channelId = channelOptions.value[0].value
    if (form.groupId == null && groupOptions.value.length === 1) form.groupId = groupOptions.value[0].id
  })
})

const selectedPending = computed(
  () => channelOptions.value.find((o) => o.value === form.channelId)?.pending ?? 0,
)

// 扭矩→目标预紧力联动：F = T/(K·d)。目标值与现场实际扭矩对不上是假告警的主要来源之一，
// 建档时就按实拧扭矩换算，而不是让人凭记忆填一个"看起来合理"的数。
const torqueCalc = reactive({ torqueNm: undefined as number | undefined, nutFactor: 0.20 })
function applyTorqueCalc() {
  const t = torqueCalc.torqueNm
  if (!t || t <= 0) return
  const dm = Number((form.nominalDiam.match(/(\d+(?:\.\d+)?)/) || [])[1])
  if (!dm) return ElMessage.warning('无法从规格解析公称直径，请检查规格（如 M20）')
  const f = Math.round(t / (torqueCalc.nutFactor * (dm / 1000)))
  form.targetPreloadN = f
  form.minPreloadN = Math.round(f * 0.7)
  ElMessage.success(`已按 ${t} N·m / K=${torqueCalc.nutFactor} 换算：目标 ${f.toLocaleString()} N，下限 ${Math.round(f * 0.7).toLocaleString()} N`)
}

async function submit() {
  if (form.channelId == null) return ElMessage.warning('请选择通道')
  if (form.groupId == null) return ElMessage.warning('请选择监测组')
  if (form.minPreloadN != null && form.targetPreloadN != null && form.minPreloadN > form.targetPreloadN)
    return ElMessage.warning('报警下限不能大于目标预紧力')
  submitting.value = true
  try {
    const m = resolved.value?.material
    const r = await assetApi.createBolt(form.groupId, {
      channelId: form.channelId,
      code: form.code.trim() || undefined,
      positionLabel: form.positionLabel.trim() || undefined,
      nominalDiam: form.nominalDiam,
      grade: form.grade,
      // 参数字典展开值（预设覆盖优先）；undefined 走后端缺省，行为与联动之前一致
      material: form.materialCode || undefined,
      rp02Mpa: effectiveRp02.value ?? undefined,
      crossSectionMm2: effectiveAs.value ?? undefined,
      elasticModulusGpa: m?.elasticModulusGpa != null ? Number(m.elasticModulusGpa) : undefined,
      soundVelocityMs: m?.soundVelocityMs != null ? Number(m.soundVelocityMs) : undefined,
      tempCoefficientPerC: m?.tempCoefficientPerC != null ? Number(m.tempCoefficientPerC) : undefined,
      effectiveLengthMm: form.effectiveLengthMm,
      totalLengthMm: form.totalLengthMm,
      targetPreloadN: form.targetPreloadN,
      minPreloadN: form.minPreloadN,
      backfillUnbound: form.backfillUnbound,
      note: form.note.trim() || undefined,
    })
    ElMessage.success(
      `螺栓 ${r.code} 已建档并绑定 CH${r.channelNo}` +
      (r.backfilled > 0 ? `，回填 ${r.backfilled} 条暂存数据` : ''),
    )
    visible.value = false
    emit('done', { boltId: r.boltId, channelId: r.channelId, code: r.code })
  } catch (e) {
    ElMessage.error((e as Error)?.message || '建档失败')
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog v-model="visible" title="建档并绑定：新接入的螺栓" width="560px" append-to-body destroy-on-close>
    <el-alert type="info" :closable="false" show-icon style="margin-bottom: 14px">
      为现场新接的探头建立螺栓档案并绑定通道。绑定前暂存的测量数据可自动回填，<b>一条不丢</b>；
      规格按预设填充，可稍后在螺栓设置中修正。绑定后需完成标定才有正式预紧力。
    </el-alert>

    <el-form v-loading="loading" label-width="110px" label-position="left">
      <el-form-item label="通道" required>
        <el-select v-model="form.channelId" placeholder="选择未绑定的通道" style="width: 100%" filterable>
          <el-option
            v-for="o in channelOptions" :key="o.value" :value="o.value"
            :label="`${multiDevice ? '设备' + o.deviceId + ' · ' : ''}CH${o.channelNo}${o.pending ? ` · ${o.pending.toLocaleString()} 条暂存` : ''}`" />
        </el-select>
        <div v-if="selectedPending" class="fld-hint">该通道已暂存 {{ selectedPending.toLocaleString() }} 条测量，提交后自动回填</div>
      </el-form-item>
      <el-form-item label="监测组" required>
        <el-select v-model="form.groupId" placeholder="螺栓所属部位（法兰）" style="width: 100%" filterable>
          <el-option v-for="g in groupOptions" :key="g.id" :value="g.id" :label="g.label" />
        </el-select>
      </el-form-item>
      <el-form-item label="现场位号">
        <el-input v-model="form.positionLabel" placeholder="如“5号”——俯视图按位号落孔位，强烈建议填" maxlength="64" />
      </el-form-item>
      <el-form-item label="编号">
        <!-- 提示与后端 ChannelNaming.DefaultBoltCode 实际格式一致（原文案"组编号-CH通道号"与生成结果不符） -->
        <el-input v-model="form.code" placeholder="留空自动生成（如 通道2-3号螺栓，重名自动加 -2/-3）" maxlength="64" />
      </el-form-item>
      <el-form-item v-if="presetsList.length" label="规格预设">
        <el-select :model-value="presetId" placeholder="选预设一键带出全参数（可再手改）" style="width: 100%" clearable
          @update:model-value="applyPreset">
          <el-option v-for="p in presetsList" :key="p.id" :value="p.id"
            :label="`${p.name}（${p.nominalDiam} ${p.grade} ${p.materialCode}）`" />
        </el-select>
      </el-form-item>
      <el-form-item label="规格 / 等级">
        <div class="spec-row">
          <el-select v-model="form.nominalDiam" style="width: 130px" filterable allow-create>
            <el-option v-for="s in SPECS" :key="s" :value="s" :label="s" />
          </el-select>
          <el-select v-model="form.grade" style="width: 110px" filterable allow-create>
            <el-option v-for="g in GRADES" :key="g" :value="g" :label="g + ' 级'" />
          </el-select>
          <el-select v-model="form.materialCode" style="width: 170px" filterable placeholder="材料">
            <el-option v-for="m in materialsList" :key="m.code" :value="m.code" :label="m.code" />
          </el-select>
        </div>
        <div class="fld-hint">自动带出：{{ resolvedSummary }}</div>
      </el-form-item>
      <el-form-item label="有效长/总长 mm">
        <div class="spec-row">
          <el-input-number v-model="form.effectiveLengthMm" :min="1" :max="5000" controls-position="right" style="width: 150px" placeholder="有效长度" />
          <el-input-number v-model="form.totalLengthMm" :min="1" :max="5000" controls-position="right" style="width: 150px" placeholder="总长" />
          <span class="fld-hint inline">留空走系统缺省，可后补</span>
        </div>
      </el-form-item>
      <el-form-item label="安装扭矩换算">
        <div class="torque-calc">
          <el-input-number v-model="torqueCalc.torqueNm" :step="10" :min="0" controls-position="right" style="width: 130px" placeholder="扭矩 N·m" />
          <el-select v-model="torqueCalc.nutFactor" style="width: 160px">
            <el-option label="干燥螺纹 K=0.20" :value="0.20" />
            <el-option label="轻度润滑 K=0.15" :value="0.15" />
            <el-option label="良好润滑 K=0.13" :value="0.13" />
          </el-select>
          <el-button size="small" type="primary" plain :disabled="!torqueCalc.torqueNm" @click="applyTorqueCalc">换算填入</el-button>
        </div>
        <div class="fld-hint">F = T/(K·d)：按实际拧紧扭矩自动算目标预紧力与下限（70%），避免目标值与现场对不上</div>
      </el-form-item>
      <el-form-item label="目标预紧力 (N)">
        <el-input-number v-model="form.targetPreloadN" :step="1000" :min="0" controls-position="right" style="width: 100%" placeholder="如 100000（=100 kN），可后补" />
      </el-form-item>
      <el-form-item label="报警下限 (N)">
        <el-input-number v-model="form.minPreloadN" :step="1000" :min="0" controls-position="right" style="width: 100%" placeholder="低于此值触发松动告警，可后补" />
      </el-form-item>
      <el-form-item label="回填暂存数据">
        <el-switch v-model="form.backfillUnbound" />
        <span class="fld-hint inline">绑定前该通道的暂存测量迁入正式数据并清空暂存</span>
      </el-form-item>
      <el-form-item label="备注">
        <el-input v-model="form.note" type="textarea" :rows="2" placeholder="如：现场新增接入（记入绑定历史与审计）" />
      </el-form-item>
    </el-form>

    <template #footer>
      <el-button @click="visible = false">取消</el-button>
      <el-button type="primary" :loading="submitting" @click="submit">建档并绑定</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.spec-row { display: flex; gap: 10px; }
.fld-hint { font-size: 11.5px; color: var(--text-3); line-height: 1.5; margin-top: 4px; }
.fld-hint.inline { margin: 0 0 0 10px; }
.torque-calc { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
</style>
