<script setup lang="ts">
// 参数字典（material-library-design）：材料声学库（admin 可改）+ 螺栓规格预设（reviewer+ 可改）。
// 国标值（等级→Rp0.2、螺纹→As）在后端代码表里，不在这里维护——能改国标等于能造"自定义的 8.8"。
// 建档/改规格对话框从这里取数联动；改库不追溯已建档螺栓（证据链不可变）。
import { onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { specLibraryApi, type MaterialSpec, type BoltSpecPreset } from '../api'
import { useAuth } from '../stores/auth'

const auth = useAuth()
const tab = ref<'materials' | 'presets'>('materials')
const materials = ref<MaterialSpec[]>([])
const presets = ref<BoltSpecPreset[]>([])
const loading = ref(false)
const error = ref<string | null>(null)

async function load() {
  loading.value = true
  error.value = null
  try {
    const [m, p] = await Promise.all([specLibraryApi.materials(), specLibraryApi.presets()])
    materials.value = m.items
    presets.value = p.items
  } catch (e) {
    error.value = (e as Error)?.message || '加载失败'
  } finally {
    loading.value = false
  }
}
onMounted(load)

// ---- 材料编辑（admin）----
const matDlg = reactive({ visible: false, saving: false, editId: null as number | null })
const matForm = reactive({
  code: '', name: '', soundVelocityMs: 5900 as number | null, tempCoefficientPerC: 0.00010269 as number | null,
  acousticElasticFactor: 2.5 as number | null, elasticModulusGpa: 206 as number | null, note: '',
})
function openMatDialog(m?: MaterialSpec) {
  matDlg.editId = m?.id ?? null
  matForm.code = m?.code ?? ''
  matForm.name = m?.name ?? ''
  matForm.soundVelocityMs = m?.soundVelocityMs != null ? Number(m.soundVelocityMs) : 5900
  matForm.tempCoefficientPerC = m?.tempCoefficientPerC != null ? Number(m.tempCoefficientPerC) : 0.00010269
  matForm.acousticElasticFactor = m?.acousticElasticFactor != null ? Number(m.acousticElasticFactor) : 2.5
  matForm.elasticModulusGpa = m?.elasticModulusGpa != null ? Number(m.elasticModulusGpa) : 206
  matForm.note = m?.note ?? ''
  matDlg.visible = true
}
async function saveMaterial() {
  if (!matForm.code.trim() || !matForm.name.trim()) return ElMessage.warning('code / 名称 必填')
  matDlg.saving = true
  try {
    const body = {
      code: matForm.code.trim(), name: matForm.name.trim(),
      soundVelocityMs: matForm.soundVelocityMs ?? undefined,
      tempCoefficientPerC: matForm.tempCoefficientPerC ?? undefined,
      acousticElasticFactor: matForm.acousticElasticFactor ?? undefined,
      elasticModulusGpa: matForm.elasticModulusGpa ?? undefined,
      note: matForm.note || undefined,
    }
    if (matDlg.editId != null) await specLibraryApi.updateMaterial(matDlg.editId, body)
    else await specLibraryApi.createMaterial(body)
    ElMessage.success('已保存（历史螺栓不追溯，新建档开始生效）')
    matDlg.visible = false
    await load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '保存失败')
  } finally {
    matDlg.saving = false
  }
}
async function toggleMaterial(m: MaterialSpec) {
  try {
    await specLibraryApi.updateMaterial(m.id, { enabled: !m.enabled })
    await load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '操作失败')
  }
}

// ---- 预设编辑（reviewer+）----
const preDlg = reactive({ visible: false, saving: false, editId: null as number | null })
const preForm = reactive({
  name: '', nominalDiam: 'M20', grade: '8.8', materialCode: '42CrMo',
  effectiveLengthMm: null as number | null, totalLengthMm: null as number | null,
  targetPreloadKn: null as number | null, minPreloadKn: null as number | null,
  crossSectionMm2: null as number | null, rp02Mpa: null as number | null, note: '',
})
function openPreDialog(p?: BoltSpecPreset) {
  preDlg.editId = p?.id ?? null
  preForm.name = p?.name ?? ''
  preForm.nominalDiam = p?.nominalDiam ?? 'M20'
  preForm.grade = p?.grade ?? '8.8'
  preForm.materialCode = p?.materialCode ?? (materials.value[0]?.code ?? '42CrMo')
  preForm.effectiveLengthMm = p?.effectiveLengthMm != null ? Number(p.effectiveLengthMm) : null
  preForm.totalLengthMm = p?.totalLengthMm != null ? Number(p.totalLengthMm) : null
  preForm.targetPreloadKn = p?.targetPreloadN != null ? Number(p.targetPreloadN) / 1000 : null
  preForm.minPreloadKn = p?.minPreloadN != null ? Number(p.minPreloadN) / 1000 : null
  preForm.crossSectionMm2 = p?.crossSectionMm2 != null ? Number(p.crossSectionMm2) : null
  preForm.rp02Mpa = p?.rp02Mpa != null ? Number(p.rp02Mpa) : null
  preForm.note = p?.note ?? ''
  preDlg.visible = true
}
async function savePreset() {
  if (!preForm.name.trim()) return ElMessage.warning('预设名必填')
  if (preForm.effectiveLengthMm == null || preForm.totalLengthMm == null)
    return ElMessage.warning('有效长度 / 总长必填')
  preDlg.saving = true
  try {
    const body = {
      name: preForm.name.trim(), nominalDiam: preForm.nominalDiam, grade: preForm.grade,
      materialCode: preForm.materialCode,
      effectiveLengthMm: preForm.effectiveLengthMm, totalLengthMm: preForm.totalLengthMm,
      targetPreloadN: preForm.targetPreloadKn != null ? preForm.targetPreloadKn * 1000 : undefined,
      minPreloadN: preForm.minPreloadKn != null ? preForm.minPreloadKn * 1000 : undefined,
      crossSectionMm2: preForm.crossSectionMm2 ?? undefined,
      rp02Mpa: preForm.rp02Mpa ?? undefined,
      note: preForm.note || undefined,
    }
    if (preDlg.editId != null) await specLibraryApi.updatePreset(preDlg.editId, body)
    else await specLibraryApi.createPreset(body)
    ElMessage.success('预设已保存')
    preDlg.visible = false
    await load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '保存失败')
  } finally {
    preDlg.saving = false
  }
}
async function togglePreset(p: BoltSpecPreset) {
  try {
    await specLibraryApi.updatePreset(p.id, { enabled: !p.enabled })
    await load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '操作失败')
  }
}

const fmt = (n: number | null | undefined, digits = 2) => (n == null ? '—' : Number(n).toFixed(digits))
</script>

<template>
  <div class="speclib rise">
    <div v-if="error" class="lib-error">加载失败：{{ error }} <el-button size="small" plain @click="load">重试</el-button></div>

    <div class="panel panel--glow">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark" />参数字典</span>
        <span class="hd-sub">材料声学参数与规格预设 · 建档/改规格自动带出 · 国标值（Rp0.2 / 应力截面积）由系统内置，不可修改</span>
      </div>

      <el-tabs v-model="tab" class="lib-tabs">
        <el-tab-pane label="材料声学库" name="materials">
          <div class="lib-bar">
            <span class="dim">改动只影响之后的建档/改规格，<b>不追溯已建档螺栓</b>（标定证据链不可变）</span>
            <el-button v-if="auth.isAdmin" type="primary" size="small" @click="openMatDialog()">新建材料</el-button>
          </div>
          <div class="tbl" v-loading="loading">
            <div class="tr th">
              <span>code</span><span>名称</span><span>声速 m/s</span><span>温度系数 /°C</span>
              <span>声弹系数</span><span>E GPa</span><span>状态</span><span>操作</span>
            </div>
            <div v-for="m in materials" :key="m.id" class="tr" :class="{ 'row-off': !m.enabled }">
              <span class="mono">{{ m.code }}</span>
              <span>{{ m.name }}</span>
              <span class="mono">{{ fmt(m.soundVelocityMs, 0) }}</span>
              <span class="mono">{{ Number(m.tempCoefficientPerC).toExponential(3) }}</span>
              <span class="mono">{{ fmt(m.acousticElasticFactor, 1) }}</span>
              <span class="mono">{{ fmt(m.elasticModulusGpa, 0) }}</span>
              <span><span class="led" :class="m.enabled ? 'led--ok' : 'led--off'" /> {{ m.enabled ? '启用' : '停用' }}</span>
              <span>
                <template v-if="auth.isAdmin">
                  <el-button size="small" text type="primary" @click="openMatDialog(m)">编辑</el-button>
                  <el-button size="small" text :type="m.enabled ? 'warning' : 'success'" @click="toggleMaterial(m)">
                    {{ m.enabled ? '停用' : '启用' }}
                  </el-button>
                </template>
                <span v-else class="dim">只读</span>
              </span>
            </div>
            <div v-if="!loading && !materials.length" class="tbl-empty">暂无材料</div>
          </div>
        </el-tab-pane>

        <el-tab-pane label="规格预设" name="presets">
          <div class="lib-bar">
            <span class="dim">整颗螺栓模板：建档时选预设一键带出全参数（可再手改）</span>
            <el-button v-if="auth.isReviewer" type="primary" size="small" @click="openPreDialog()">新建预设</el-button>
          </div>
          <div class="tbl" v-loading="loading">
            <div class="tr th tr-preset">
              <span>预设名</span><span>规格</span><span>等级</span><span>材料</span>
              <span>有效长 mm</span><span>目标力 kN</span><span>状态</span><span>操作</span>
            </div>
            <div v-for="p in presets" :key="p.id" class="tr tr-preset" :class="{ 'row-off': !p.enabled }">
              <span>{{ p.name }}</span>
              <span class="mono">{{ p.nominalDiam }}</span>
              <span class="mono">{{ p.grade }}</span>
              <span class="mono">{{ p.materialCode }}</span>
              <span class="mono">{{ fmt(p.effectiveLengthMm, 0) }}</span>
              <span class="mono">{{ p.targetPreloadN != null ? (Number(p.targetPreloadN) / 1000).toFixed(1) : '—' }}</span>
              <span><span class="led" :class="p.enabled ? 'led--ok' : 'led--off'" /> {{ p.enabled ? '启用' : '停用' }}</span>
              <span>
                <template v-if="auth.isReviewer">
                  <el-button size="small" text type="primary" @click="openPreDialog(p)">编辑</el-button>
                  <el-button size="small" text :type="p.enabled ? 'warning' : 'success'" @click="togglePreset(p)">
                    {{ p.enabled ? '停用' : '启用' }}
                  </el-button>
                </template>
                <span v-else class="dim">只读</span>
              </span>
            </div>
            <div v-if="!loading && !presets.length" class="tbl-empty">暂无预设。沉淀常用规格（如"塔筒法兰 M36 10.9 42CrMo"），建档就不用逐项填参数了。</div>
          </div>
        </el-tab-pane>
      </el-tabs>
    </div>

    <!-- 材料编辑 -->
    <el-dialog v-model="matDlg.visible" :title="matDlg.editId != null ? '编辑材料' : '新建材料'" width="460px">
      <el-form label-width="130px" size="small">
        <el-form-item label="code" required>
          <el-input v-model="matForm.code" :disabled="matDlg.editId != null" placeholder="如 42CrMo" maxlength="32" />
        </el-form-item>
        <el-form-item label="名称" required><el-input v-model="matForm.name" maxlength="64" /></el-form-item>
        <el-form-item label="纵波声速 m/s" required>
          <el-input-number v-model="matForm.soundVelocityMs" :min="1000" :max="9999" :step="10" controls-position="right" />
        </el-form-item>
        <el-form-item label="温度系数 /°C" required>
          <el-input-number v-model="matForm.tempCoefficientPerC" :min="0.00001" :max="0.001" :step="0.00001" :precision="8" controls-position="right" style="width: 200px" />
        </el-form-item>
        <el-form-item label="声弹综合系数">
          <el-input-number v-model="matForm.acousticElasticFactor" :min="1" :max="5" :step="0.1" :precision="2" controls-position="right" />
          <div class="f-hint">理论 K_L ≈ k·t/(E·A) 的经验因子，在役锚定估算用；载荷标定实测值永远优先</div>
        </el-form-item>
        <el-form-item label="弹性模量 GPa" required>
          <el-input-number v-model="matForm.elasticModulusGpa" :min="50" :max="400" :step="1" controls-position="right" />
        </el-form-item>
        <el-form-item label="备注"><el-input v-model="matForm.note" type="textarea" :rows="2" maxlength="200" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="matDlg.visible = false">取消</el-button>
        <el-button type="primary" :loading="matDlg.saving" @click="saveMaterial">保存</el-button>
      </template>
    </el-dialog>

    <!-- 预设编辑 -->
    <el-dialog v-model="preDlg.visible" :title="preDlg.editId != null ? '编辑规格预设' : '新建规格预设'" width="520px">
      <el-form label-width="130px" size="small">
        <el-form-item label="预设名" required><el-input v-model="preForm.name" placeholder="如 塔筒法兰 M36 10.9 42CrMo L=380" maxlength="64" /></el-form-item>
        <el-form-item label="规格" required>
          <el-input v-model="preForm.nominalDiam" placeholder="M20" style="width: 120px" />
          <span class="f-hint" style="margin-left: 10px">应力截面积按 GB/T 16823.1 自动取；非标件在下方覆盖</span>
        </el-form-item>
        <el-form-item label="性能等级" required>
          <el-select v-model="preForm.grade" style="width: 140px" filterable allow-create>
            <el-option v-for="g in ['5.6', '6.8', '8.8', '9.8', '10.9', '12.9', 'A2-70', 'A4-70', 'A2-80', 'A4-80']" :key="g" :label="g" :value="g" />
          </el-select>
          <span class="f-hint" style="margin-left: 10px">Rp0.2 按 GB/T 3098.1 自动取</span>
        </el-form-item>
        <el-form-item label="材料" required>
          <el-select v-model="preForm.materialCode" style="width: 200px">
            <el-option v-for="m in materials.filter((x) => x.enabled)" :key="m.code" :label="`${m.code}（${m.name}）`" :value="m.code" />
          </el-select>
        </el-form-item>
        <el-form-item label="有效长度 mm" required>
          <el-input-number v-model="preForm.effectiveLengthMm" :min="1" :max="5000" controls-position="right" />
        </el-form-item>
        <el-form-item label="总长 mm" required>
          <el-input-number v-model="preForm.totalLengthMm" :min="1" :max="5000" controls-position="right" />
        </el-form-item>
        <el-form-item label="目标预紧力 kN">
          <el-input-number v-model="preForm.targetPreloadKn" :min="0.1" :max="10000" :precision="1" controls-position="right" />
        </el-form-item>
        <el-form-item label="报警下限 kN">
          <el-input-number v-model="preForm.minPreloadKn" :min="0.1" :max="10000" :precision="1" controls-position="right" />
        </el-form-item>
        <el-form-item label="截面积覆盖 mm²">
          <el-input-number v-model="preForm.crossSectionMm2" :min="1" :max="10000" controls-position="right" />
          <div class="f-hint">留空 = 按国标粗牙 As；细牙/非标件才填</div>
        </el-form-item>
        <el-form-item label="Rp0.2 覆盖 MPa">
          <el-input-number v-model="preForm.rp02Mpa" :min="100" :max="2000" controls-position="right" />
          <div class="f-hint">留空 = 按国标等级值；非标材料才填</div>
        </el-form-item>
        <el-form-item label="备注"><el-input v-model="preForm.note" type="textarea" :rows="2" maxlength="200" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="preDlg.visible = false">取消</el-button>
        <el-button type="primary" :loading="preDlg.saving" @click="savePreset">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.speclib { display: flex; flex-direction: column; gap: 16px; }
.lib-error { display: flex; align-items: center; gap: 10px; padding: 10px 14px; border: 1px solid var(--crit); border-radius: 8px; color: var(--crit); font-size: 13px; }
.lib-tabs { padding: 0 18px 14px; }
.lib-bar { display: flex; justify-content: space-between; align-items: center; padding: 4px 2px 10px; font-size: 12px; }
.lib-bar .dim b { color: var(--text-2); }
.tbl { overflow-x: auto; }
.tr { display: grid; grid-template-columns: 110px 1.4fr 90px 120px 80px 70px 80px 140px; align-items: center; padding: 9px 8px; font-size: 12.5px; min-width: 820px; }
.tr.tr-preset { grid-template-columns: 1.6fr 70px 70px 110px 90px 90px 80px 140px; }
.tr:not(.th) { box-shadow: inset 0 -1px 0 var(--hairline); }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.06em; }
.row-off { opacity: 0.5; }
.dim { color: var(--text-3); }
.tbl-empty { padding: 26px; text-align: center; color: var(--text-3); font-size: 12.5px; }
.f-hint { font-size: 11px; color: var(--text-3); line-height: 1.5; }
</style>
