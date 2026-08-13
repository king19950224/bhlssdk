<script setup lang="ts">
// 告警阈值规则面板（Alarms 页"阈值规则"标签）：生效规则 + 待审提案 + 历史。
// 修改走两步异人复核：提案(pending) → 另一人批准(active) / 驳回(rejected)，
// 新规则生效时同目标旧规则置 superseded。前端禁用"自批"按钮只是体验，后端才是强制。
// 生效顺序 bolt > group > global > appsettings 出厂兜底，与后端 IAlarmRuleProvider 同一口径。
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { alarmRuleApi, type AlarmRule, type AlarmRuleTargets } from '../api'
import { useAuth } from '../stores/auth'

const auth = useAuth()
const rules = ref<AlarmRule[]>([])
const targets = ref<AlarmRuleTargets>({ groups: [], bolts: [] })
const loading = ref(false)
const loadError = ref<string | null>(null)
const showHistory = ref(false)
// 历史记录超出后端 500 条上限被截断（待审规则排最前永不丢，截掉的只会是最老的历史）
const truncated = ref(false)

const activeRules = computed(() => rules.value.filter((r) => r.reviewState === 'active'))
const visibleRules = computed(() =>
  showHistory.value
    ? rules.value
    : rules.value.filter((r) => r.reviewState === 'pending' || r.reviewState === 'active'),
)
const pendingCount = computed(() => rules.value.filter((r) => r.reviewState === 'pending').length)

async function load() {
  loading.value = true
  try {
    const [list, tg] = await Promise.all([alarmRuleApi.list(), alarmRuleApi.targets()])
    rules.value = list.items
    truncated.value = !!list.truncated
    targets.value = tg
    loadError.value = null
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : '规则列表加载失败'
  } finally {
    loading.value = false
  }
}
onMounted(load)

function targetLabel(r: AlarmRule) {
  if (r.scope === 'global') return '全站'
  if (r.scope === 'group') return `部位 ${r.groupCode ?? r.groupId}`
  return `螺栓 ${r.boltCode ?? r.boltId}`
}
const pct = (v: number) => `${(v * 100).toFixed(0)}%`
const STATE: Record<string, { label: string; cls: string }> = {
  pending: { label: '待审', cls: 'st-pending' },
  active: { label: '生效', cls: 'st-active' },
  rejected: { label: '已驳回', cls: 'st-dim' },
  superseded: { label: '已替代', cls: 'st-dim' },
}
function fmtTime(iso: string | null) {
  if (!iso) return '—'
  const d = new Date(iso)
  const p = (n: number) => String(n).padStart(2, '0')
  return `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`
}

// ---- 提案表单 ----
// 出厂默认与后端 appsettings 的 Alarm:StaticThreshold 同值：库里没有可参照的生效规则时用它做底稿
const FACTORY = {
  triggerLowRatio: 0.7, clearLowRatio: 0.72, triggerHighRatio: 1.1, clearHighRatio: 1.08,
  triggerConsecutive: 5, clearConsecutive: 10, criticalDeviation: 0.3, preloadLossRatio: 0.05,
}
const form = reactive({
  visible: false, saving: false,
  scope: 'global' as 'global' | 'group' | 'bolt',
  groupId: null as number | null,
  boltId: null as number | null,
  ...FACTORY,
  reason: '',
})

// 底稿 = 所选目标当前实际生效的那套（同目标 > 全站 > 出厂），改哪项一目了然
function prefill() {
  const sameTarget = activeRules.value.find((r) =>
    form.scope === 'global' ? r.scope === 'global'
    : form.scope === 'group' ? r.scope === 'group' && r.groupId === form.groupId
    : r.scope === 'bolt' && r.boltId === form.boltId,
  )
  const base = sameTarget ?? activeRules.value.find((r) => r.scope === 'global') ?? FACTORY
  form.triggerLowRatio = base.triggerLowRatio
  form.clearLowRatio = base.clearLowRatio
  form.triggerHighRatio = base.triggerHighRatio
  form.clearHighRatio = base.clearHighRatio
  form.triggerConsecutive = base.triggerConsecutive
  form.clearConsecutive = base.clearConsecutive
  form.criticalDeviation = base.criticalDeviation
  form.preloadLossRatio = base.preloadLossRatio
}
watch(() => [form.scope, form.groupId, form.boltId], prefill)

function openPropose() {
  form.scope = 'global'
  form.groupId = null
  form.boltId = null
  form.reason = ''
  prefill()
  form.visible = true
}

// 与后端 ValidateThresholds 同一条参数链，提交前先在本地拦一道
function localValidate(): string | null {
  if (form.scope === 'group' && !form.groupId) return '请选择监测部位'
  if (form.scope === 'bolt' && !form.boltId) return '请选择螺栓'
  if (form.triggerLowRatio <= form.preloadLossRatio) return '触发下限必须大于脱落档'
  if (form.clearLowRatio <= form.triggerLowRatio) return '恢复下限必须大于触发下限（回差）'
  if (form.clearHighRatio <= form.clearLowRatio) return '恢复上限必须大于恢复下限'
  if (form.triggerHighRatio <= form.clearHighRatio) return '触发上限必须大于恢复上限（回差）'
  if (!form.reason || form.reason.trim().length < 5) return '修改依据至少 5 字（国标条款/合同条款/标定记录编号）'
  return null
}

async function submitPropose() {
  const err = localValidate()
  if (err) return ElMessage.warning(err)
  form.saving = true
  try {
    await alarmRuleApi.propose({
      scope: form.scope,
      groupId: form.scope === 'group' ? form.groupId : null,
      boltId: form.scope === 'bolt' ? form.boltId : null,
      triggerLowRatio: form.triggerLowRatio,
      clearLowRatio: form.clearLowRatio,
      triggerHighRatio: form.triggerHighRatio,
      clearHighRatio: form.clearHighRatio,
      triggerConsecutive: form.triggerConsecutive,
      clearConsecutive: form.clearConsecutive,
      criticalDeviation: form.criticalDeviation,
      preloadLossRatio: form.preloadLossRatio,
      reason: form.reason.trim(),
    })
    ElMessage.success('已提案，等待另一位复核员批准')
    form.visible = false
    load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '提案失败')
  } finally {
    form.saving = false
  }
}

async function approve(r: AlarmRule) {
  try {
    await ElMessageBox.confirm(
      `批准【${targetLabel(r)}】的阈值提案？批准后 60 秒内生效，同目标旧规则自动置为已替代。`,
      '批准阈值规则', { type: 'warning' })
  } catch { return }
  try {
    await alarmRuleApi.approve(r.id)
    ElMessage.success('已批准并生效')
    load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '批准失败')
  }
}

async function reject(r: AlarmRule) {
  let note: string | undefined
  try {
    const { value } = await ElMessageBox.prompt('驳回理由（可不填，填了记入审计）:', `驳回提案 · ${targetLabel(r)}`, {
      confirmButtonText: '驳回', cancelButtonText: '取消', inputType: 'textarea', inputValue: '',
    })
    note = value || undefined
  } catch { return }
  try {
    await alarmRuleApi.reject(r.id, note)
    ElMessage.success('已驳回')
    load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '驳回失败')
  }
}
</script>

<template>
  <div class="rules">
    <div class="panel">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark" />阈值规则</span>
        <span class="hd-sub">生效顺序：螺栓 &gt; 部位 &gt; 全站 &gt; 出厂默认；修改需另一位复核员批准</span>
        <span class="hd-actions">
          <el-checkbox v-model="showHistory" size="small">显示历史（驳回/替代）</el-checkbox>
          <el-button v-if="auth.isReviewer" size="small" type="primary" @click="openPropose">提案修改</el-button>
        </span>
      </div>
      <div v-if="loadError" class="load-error">
        规则列表加载失败：{{ loadError }}
        <el-button size="small" text type="primary" @click="load">重试</el-button>
      </div>
      <div v-if="truncated && showHistory" class="load-error">
        历史规则超出显示上限（500 条），最早的历史记录未列出；待审与生效规则不受影响。
      </div>
      <div class="tbl" v-loading="loading">
        <div class="tr th">
          <span>目标</span><span>下限 触发/恢复</span><span>上限 触发/恢复</span><span>连击 触发/恢复</span>
          <span>脱落档</span><span>严重偏差</span><span>依据</span><span>提案</span><span>复核</span><span>状态</span><span>操作</span>
        </div>
        <div v-for="r in visibleRules" :key="r.id" class="tr" :class="{ dim: r.reviewState === 'rejected' || r.reviewState === 'superseded' }">
          <span class="mono tgt">{{ targetLabel(r) }}</span>
          <span class="mono">{{ pct(r.triggerLowRatio) }} / {{ pct(r.clearLowRatio) }}</span>
          <span class="mono">{{ pct(r.triggerHighRatio) }} / {{ pct(r.clearHighRatio) }}</span>
          <span class="mono">{{ r.triggerConsecutive }} / {{ r.clearConsecutive }}</span>
          <span class="mono">{{ pct(r.preloadLossRatio) }}</span>
          <span class="mono">{{ pct(r.criticalDeviation) }}</span>
          <span class="msg" :title="r.reason">{{ r.reason }}</span>
          <span class="time"><b>{{ r.createdByName ?? '—' }}</b><i>{{ fmtTime(r.createdAt) }}</i></span>
          <span class="time"><b>{{ r.reviewedByName ?? '—' }}</b><i>{{ fmtTime(r.reviewedAt) }}</i></span>
          <span><span class="st" :class="STATE[r.reviewState]?.cls">{{ STATE[r.reviewState]?.label ?? r.reviewState }}</span></span>
          <span class="ops">
            <template v-if="r.reviewState === 'pending' && auth.isReviewer">
              <el-button
                size="small" text type="primary"
                :disabled="r.createdBy === auth.userId"
                :title="r.createdBy === auth.userId ? '提案人不得批准自己的提案（异人复核）' : ''"
                @click="approve(r)"
              >批准</el-button>
              <el-button size="small" text type="warning" @click="reject(r)">
                {{ r.createdBy === auth.userId ? '撤回' : '驳回' }}
              </el-button>
            </template>
            <span v-else class="none">—</span>
          </span>
        </div>
        <div v-if="!loading && !visibleRules.length" class="tbl-empty">
          尚无库内规则：全部螺栓按出厂默认阈值（appsettings）判定
        </div>
      </div>
    </div>
    <p v-if="pendingCount" class="pending-hint">有 {{ pendingCount }} 条提案待复核；批准生效前旧阈值继续判定。</p>

    <el-dialog v-model="form.visible" title="提案阈值修改" width="560px">
      <p class="dlg-sub">底稿取所选目标当前实际生效的那套；提交后需另一位复核员批准才生效，全程写审计。</p>
      <el-form label-width="120px" label-position="left">
        <el-form-item label="适用范围">
          <el-radio-group v-model="form.scope">
            <el-radio value="global">全站</el-radio>
            <el-radio value="group">监测部位</el-radio>
            <el-radio value="bolt">单颗螺栓</el-radio>
          </el-radio-group>
        </el-form-item>
        <el-form-item v-if="form.scope === 'group'" label="部位">
          <el-select v-model="form.groupId" filterable placeholder="选择监测部位" style="width: 100%">
            <el-option v-for="g in targets.groups" :key="g.id" :label="`${g.code} ${g.name}`" :value="g.id" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="form.scope === 'bolt'" label="螺栓">
          <el-select v-model="form.boltId" filterable placeholder="选择螺栓" style="width: 100%">
            <el-option v-for="b in targets.bolts" :key="b.id" :label="`${b.code}（${b.groupCode}）`" :value="b.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="触发下限 (ratio)">
          <el-input-number v-model="form.triggerLowRatio" :min="0.01" :max="1" :step="0.01" :precision="4" controls-position="right" />
          <span class="unit">低于此值连击触发偏低告警</span>
        </el-form-item>
        <el-form-item label="恢复下限 (ratio)">
          <el-input-number v-model="form.clearLowRatio" :min="0.01" :max="1.5" :step="0.01" :precision="4" controls-position="right" />
          <span class="unit">须大于触发下限（回差）</span>
        </el-form-item>
        <el-form-item label="触发上限 (ratio)">
          <el-input-number v-model="form.triggerHighRatio" :min="1" :max="5" :step="0.01" :precision="4" controls-position="right" />
        </el-form-item>
        <el-form-item label="恢复上限 (ratio)">
          <el-input-number v-model="form.clearHighRatio" :min="1" :max="5" :step="0.01" :precision="4" controls-position="right" />
          <span class="unit">须小于触发上限（回差）</span>
        </el-form-item>
        <el-form-item label="触发连击 (条)">
          <el-input-number v-model="form.triggerConsecutive" :min="1" :max="1000" :step="1" controls-position="right" />
        </el-form-item>
        <el-form-item label="恢复连击 (条)">
          <el-input-number v-model="form.clearConsecutive" :min="1" :max="1000" :step="1" controls-position="right" />
        </el-form-item>
        <el-form-item label="严重偏差档">
          <el-input-number v-model="form.criticalDeviation" :min="0.01" :max="1" :step="0.01" :precision="4" controls-position="right" />
          <span class="unit">|1−ratio| 达到即升 critical</span>
        </el-form-item>
        <el-form-item label="脱落档 (ratio)">
          <el-input-number v-model="form.preloadLossRatio" :min="0" :max="0.5" :step="0.01" :precision="4" controls-position="right" />
          <span class="unit">低于即报脱落，不走连击</span>
        </el-form-item>
        <el-form-item label="修改依据">
          <el-input v-model="form.reason" type="textarea" :rows="2" placeholder="国标条款 / 合同条款 / 标定记录编号，≥5 字" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="form.visible = false">取消</el-button>
        <el-button type="primary" :loading="form.saving" @click="submitPropose">提交提案</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.rules { display: flex; flex-direction: column; gap: 10px; }
.panel-hd { display: flex; align-items: center; gap: 12px; }
.hd-actions { margin-left: auto; display: flex; align-items: center; gap: 12px; }
.tbl { padding: 4px 12px 14px; overflow-x: auto; }
.tr { display: grid; grid-template-columns: 130px 110px 110px 96px 66px 66px 1fr 110px 110px 70px 120px; align-items: center; padding: 8px; border-radius: 8px; font-size: 12.5px; gap: 4px; min-width: 1100px; }
.tr:not(.th) { box-shadow: inset 0 -1px 0 var(--hairline); }
.tr:not(.th):hover { background: rgba(56, 189, 248, 0.05); }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.06em; }
.tr.dim { opacity: 0.5; }
.tgt { font-weight: 600; }
.msg { color: var(--text-2); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.time { display: flex; flex-direction: column; line-height: 1.25; }
.time b { font-weight: 500; font-size: 12px; }
.time i { font-style: normal; font-size: 10.5px; color: var(--text-3); }
.st { font-size: 11px; padding: 2px 9px; border-radius: 10px; white-space: nowrap; }
.st-pending { color: var(--warn); border: 1px solid var(--warn); }
.st-active { color: var(--ok, #34d399); border: 1px solid currentColor; }
.st-dim { color: var(--text-3); border: 1px solid var(--hairline); }
.ops { display: flex; gap: 2px; }
.none { color: var(--text-3); }
.tbl-empty { text-align: center; padding: 26px; color: var(--text-3); }
.pending-hint { font-size: 12px; color: var(--text-3); margin: 0; padding: 0 4px; }
.load-error { display: flex; align-items: center; gap: 8px; padding: 10px 18px; color: var(--warn); font-size: 12.5px; }
.dlg-sub { font-size: 12.5px; color: var(--text-2); margin: 0 0 14px; }
.unit { margin-left: 10px; font-size: 11.5px; color: var(--text-3); }
</style>
