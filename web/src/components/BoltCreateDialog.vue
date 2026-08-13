<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import {
  assetApi, channelApi,
  type ChannelRow, type UnboundSummaryRow,
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

// 规格/等级预设与后端 BoltSpecPresets 对齐；截面积等由后端按规格补默认值
const SPECS = ['M12', 'M16', 'M20', 'M24', 'M30', 'M36', 'M42', 'M48']
const GRADES = ['8.8', '10.9', '12.9']

const form = reactive({
  channelId: null as number | null,
  groupId: null as number | null,
  code: '',
  positionLabel: '',
  nominalDiam: 'M20',
  grade: '8.8',
  targetPreloadN: undefined as number | undefined,
  minPreloadN: undefined as number | undefined,
  backfillUnbound: true,
  note: '',
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
    const [chList, ub, projs] = await Promise.all([
      channelApi.list(),
      channelApi.unboundSummary(),
      assetApi.projects(),
    ])
    channels.value = chList.items
    unbound.value = ub.items

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
  form.targetPreloadN = undefined
  form.minPreloadN = undefined
  form.backfillUnbound = true
  form.note = ''
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

async function submit() {
  if (form.channelId == null) return ElMessage.warning('请选择通道')
  if (form.groupId == null) return ElMessage.warning('请选择监测组')
  if (form.minPreloadN != null && form.targetPreloadN != null && form.minPreloadN > form.targetPreloadN)
    return ElMessage.warning('报警下限不能大于目标预紧力')
  submitting.value = true
  try {
    const r = await assetApi.createBolt(form.groupId, {
      channelId: form.channelId,
      code: form.code.trim() || undefined,
      positionLabel: form.positionLabel.trim() || undefined,
      nominalDiam: form.nominalDiam,
      grade: form.grade,
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
  <el-dialog v-model="visible" title="建档并绑定：新接入的螺栓" width="560px" top="8vh" destroy-on-close>
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
      <el-form-item label="规格 / 等级">
        <div class="spec-row">
          <el-select v-model="form.nominalDiam" style="width: 130px">
            <el-option v-for="s in SPECS" :key="s" :value="s" :label="s" />
          </el-select>
          <el-select v-model="form.grade" style="width: 110px">
            <el-option v-for="g in GRADES" :key="g" :value="g" :label="g + ' 级'" />
          </el-select>
        </div>
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
</style>
