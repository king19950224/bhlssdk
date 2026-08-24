<script setup lang="ts">
// 设备生命周期管理面板（Devices 页"设备管理"入口，Admin）：表视角列表 + 新增/试连/改参/启停/转主/软删。
// 全链路不重启进程：新增落库建目录后运行时热加，采集由对账循环数秒内拉起；
// 禁用会停采集断 TCP 并连带闭合名下 signal_lost。删除是终态（必须先禁用），要求输入设备编码双确认。
import { reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { deviceMgmtApi, type AdminDevice } from '../api'

const props = defineProps<{ modelValue: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [boolean]; changed: [] }>()

const items = ref<AdminDevice[]>([])
const showDeleted = ref(false)
const loading = ref(false)
const loadError = ref<string | null>(null)
let mutated = false

async function load() {
  loading.value = true
  try {
    items.value = (await deviceMgmtApi.list(showDeleted.value)).items
    loadError.value = null
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : '设备列表加载失败'
  } finally {
    loading.value = false
  }
}
watch(() => props.modelValue, (open) => {
  if (open) {
    mutated = false
    load()
  }
})
function close() {
  emit('update:modelValue', false)
  if (mutated) emit('changed')
}
function markMutated() {
  mutated = true
  load()
}

function stateChip(d: AdminDevice) {
  if (d.deletedAt) return { label: '已删除', cls: 'st-dead' }
  if (!d.enabled) return { label: '已禁用', cls: 'st-off' }
  if (d.isIngesting) return { label: '采集中', cls: 'st-ok' }
  if (d.inRuntime) return { label: '待连接', cls: 'st-pending' }
  return { label: '未挂载', cls: 'st-off' }
}

// ---- 新增 ----
const createDlg = reactive({
  visible: false, saving: false,
  code: '', commParams: '', channelCount: 12, address: 1, model: 3, commType: 1,
})
function openCreate() {
  createDlg.code = ''
  createDlg.commParams = ''
  createDlg.channelCount = 12
  createDlg.address = 1
  createDlg.model = 3
  createDlg.commType = 1
  createDlg.visible = true
}
async function submitCreate() {
  if (!createDlg.code.trim()) return ElMessage.warning('设备编码必填')
  if (!createDlg.commParams.trim()) return ElMessage.warning('通讯地址必填（如 192.168.1.50:23）')
  createDlg.saving = true
  try {
    const r = await deviceMgmtApi.create({
      code: createDlg.code.trim(), commParams: createDlg.commParams.trim(),
      channelCount: createDlg.channelCount, address: createDlg.address,
      model: createDlg.model, commType: createDlg.commType,
    })
    ElMessage.success(r.note ?? `设备已建档（${r.channelsCreated} 通道），采集将自动拉起`)
    createDlg.visible = false
    markMutated()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '新增失败')
  } finally {
    createDlg.saving = false
  }
}

// ---- 试连 ----
const testing = ref<Record<number, boolean>>({})
async function testConn(d: AdminDevice) {
  if (d.isIngesting) {
    try {
      await ElMessageBox.confirm('该设备正在采集：试连会抢占其 TCP 连接位，可能造成短暂断线重连。继续？', '试连', { type: 'warning' })
    } catch { return }
  }
  testing.value[d.id] = true
  try {
    const r = await deviceMgmtApi.testConnection(d.id)
    if (r.ok) ElMessage.success(`'${d.code}' 连接成功`)
    else ElMessage.error(`'${d.code}' 连接失败：${r.error ?? r.state}`)
  } catch (e) {
    ElMessage.error((e as Error)?.message || '试连失败')
  } finally {
    testing.value[d.id] = false
  }
}

// ---- 编辑连接参数 ----
const editDlg = reactive({ visible: false, saving: false, id: 0, code: '', commParams: '', address: 1, transferMode: 1 })
function openEdit(d: AdminDevice) {
  editDlg.id = d.id
  editDlg.code = d.code
  editDlg.commParams = d.commParams
  editDlg.address = d.address
  editDlg.transferMode = d.transferMode
  editDlg.visible = true
}
async function submitEdit() {
  editDlg.saving = true
  try {
    const r = await deviceMgmtApi.update(editDlg.id, {
      commParams: editDlg.commParams.trim(), address: editDlg.address, transferMode: editDlg.transferMode,
    })
    ElMessage.success(r.restarted ? '已保存并热重启连接' : (r.note ?? '已保存'))
    editDlg.visible = false
    markMutated()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '保存失败')
  } finally {
    editDlg.saving = false
  }
}

// ---- 启停 / 转主 / 删除 ----
async function toggle(d: AdminDevice) {
  if (d.enabled) {
    try {
      await ElMessageBox.confirm(
        `禁用 '${d.code}'？会停止采集、断开 TCP，名下未闭合的信号丢失告警按"设备已禁用"闭合。预紧力类告警保持最后已知状态。`,
        '禁用设备', { type: 'warning' })
    } catch { return }
    try {
      const r = await deviceMgmtApi.disable(d.id)
      ElMessage.success(`已禁用${r.closedSignalLost ? `，连带闭合 ${r.closedSignalLost} 条信号丢失` : ''}`)
      markMutated()
    } catch (e) {
      ElMessage.error((e as Error)?.message || '禁用失败')
    }
  } else {
    try {
      const r = await deviceMgmtApi.enable(d.id)
      ElMessage.success(r.note ?? '已启用，采集将自动拉起')
      markMutated()
    } catch (e) {
      ElMessage.error((e as Error)?.message || '启用失败（可能超授权容量）')
    }
  }
}
async function makePrimary(d: AdminDevice) {
  try {
    await ElMessageBox.confirm(
      `把主设备转移到 '${d.code}'？主设备是不带设备号的旧接口的默认路由；转移是运行时动作，进程重启后按配置重选。`,
      '转移主设备', { type: 'warning' })
  } catch { return }
  try {
    await deviceMgmtApi.update(d.id, { makePrimary: true })
    ElMessage.success('主设备已转移')
    markMutated()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '转移失败')
  }
}
async function remove(d: AdminDevice) {
  let typed: string
  try {
    const { value } = await ElMessageBox.prompt(
      `删除是终态动作（软删，历史测量保留）。请输入设备编码 '${d.code}' 确认：`, '删除设备',
      { confirmButtonText: '删除', cancelButtonText: '取消', type: 'error' })
    typed = value
  } catch { return }
  if (typed !== d.code) return ElMessage.warning('输入的编码不一致，已取消')
  try {
    await deviceMgmtApi.remove(d.id)
    ElMessage.success('已删除')
    markMutated()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '删除失败（须先禁用）')
  }
}
</script>

<template>
  <el-dialog :model-value="modelValue" title="设备管理" width="980px" append-to-body @update:model-value="close" @close="close">
    <div class="bar">
      <el-button type="primary" size="small" @click="openCreate">新增设备</el-button>
      <el-checkbox v-model="showDeleted" size="small" @change="load">显示已删除</el-checkbox>
      <span class="bar-hint">新增/启停全程不重启进程；删除须先禁用，历史测量按通道保留</span>
    </div>
    <div v-if="loadError" class="load-error">{{ loadError }} <el-button size="small" text type="primary" @click="load">重试</el-button></div>
    <div class="tbl" v-loading="loading">
      <div class="tr th">
        <span>编码</span><span>通讯地址</span><span>址</span><span>通道</span><span>状态</span><span>连接器</span><span>操作</span>
      </div>
      <div v-for="d in items" :key="d.id" class="tr" :class="{ dim: !!d.deletedAt || !d.enabled }">
        <span class="mono strong">{{ d.code }}<el-tag v-if="d.isPrimary" size="small" effect="dark" class="ptag">主</el-tag></span>
        <span class="mono">{{ d.commParams }}</span>
        <span class="mono">{{ d.address }}</span>
        <span class="mono">{{ d.activeChannels }}/{{ d.channelCount }}</span>
        <span><span class="st" :class="stateChip(d).cls">{{ stateChip(d).label }}</span></span>
        <span class="mono dim2">{{ d.connectorState ?? '—' }}</span>
        <span class="ops">
          <template v-if="!d.deletedAt">
            <el-button size="small" text :loading="testing[d.id]" @click="testConn(d)">试连</el-button>
            <el-button size="small" text @click="openEdit(d)">编辑</el-button>
            <el-button v-if="!d.isPrimary && d.enabled && d.inRuntime" size="small" text @click="makePrimary(d)">转主</el-button>
            <el-button size="small" text :type="d.enabled ? 'warning' : 'success'" :disabled="d.isPrimary && d.enabled"
              :title="d.isPrimary && d.enabled ? '主设备不许禁用，先转移' : ''" @click="toggle(d)">
              {{ d.enabled ? '禁用' : '启用' }}
            </el-button>
            <el-button size="small" text type="danger" :disabled="d.enabled" :title="d.enabled ? '先禁用再删除' : ''" @click="remove(d)">删除</el-button>
          </template>
          <span v-else class="dim2">—</span>
        </span>
      </div>
      <div v-if="!loading && !items.length" class="tbl-empty">无设备记录</div>
    </div>

    <el-dialog v-model="createDlg.visible" title="新增设备" width="480px" append-to-body>
      <el-form label-width="110px" label-position="left">
        <el-form-item label="设备编码"><el-input v-model="createDlg.code" placeholder="如 DEV_SITE_002" maxlength="64" /></el-form-item>
        <el-form-item label="通讯地址"><el-input v-model="createDlg.commParams" placeholder="192.168.1.50:23" maxlength="128" /></el-form-item>
        <el-form-item label="设备地址"><el-input-number v-model="createDlg.address" :min="1" :max="255" controls-position="right" /></el-form-item>
        <el-form-item label="通道数">
          <el-input-number v-model="createDlg.channelCount" :min="1" :max="48" controls-position="right" />
          <span class="unit">建档后可用通道目录重建扩容</span>
        </el-form-item>
        <el-form-item label="型号">
          <el-select v-model="createDlg.model" style="width: 100%">
            <el-option :value="3" label="3 · 多通道监测型（支持全通道轮询）" />
            <el-option :value="2" label="2" /><el-option :value="1" label="1" /><el-option :value="0" label="0" />
          </el-select>
        </el-form-item>
        <el-form-item label="通讯方式">
          <el-select v-model="createDlg.commType" style="width: 100%">
            <el-option :value="1" label="1 · TCP 客户端（串口服务器常用）" />
            <el-option :value="0" label="0 · 串口" /><el-option :value="2" label="2 · TCP 服务端" />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="createDlg.visible = false">取消</el-button>
        <el-button type="primary" :loading="createDlg.saving" @click="submitCreate">建档并接入</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="editDlg.visible" :title="`编辑连接参数 · ${editDlg.code}`" width="440px" append-to-body>
      <el-form label-width="110px" label-position="left">
        <el-form-item label="通讯地址"><el-input v-model="editDlg.commParams" maxlength="128" /></el-form-item>
        <el-form-item label="设备地址"><el-input-number v-model="editDlg.address" :min="1" :max="255" controls-position="right" /></el-form-item>
        <el-form-item label="传输模式">
          <el-radio-group v-model="editDlg.transferMode">
            <el-radio :value="1">直连</el-radio>
            <el-radio :value="0">LoRa</el-radio>
          </el-radio-group>
        </el-form-item>
      </el-form>
      <p class="dlg-sub">保存后若设备正在采集，会自动热重启连接（几秒内断开重连）。</p>
      <template #footer>
        <el-button @click="editDlg.visible = false">取消</el-button>
        <el-button type="primary" :loading="editDlg.saving" @click="submitEdit">保存</el-button>
      </template>
    </el-dialog>
  </el-dialog>
</template>

<style scoped>
.bar { display: flex; align-items: center; gap: 14px; margin-bottom: 10px; }
.bar-hint { font-size: 11.5px; color: var(--text-3); }
.tbl { overflow-x: auto; }
.tr { display: grid; grid-template-columns: 150px 170px 44px 76px 84px 100px 1fr; align-items: center; padding: 8px 6px; border-radius: 8px; font-size: 12.5px; gap: 6px; min-width: 880px; }
.tr:not(.th) { box-shadow: inset 0 -1px 0 var(--hairline); }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.06em; }
.tr.dim { opacity: 0.6; }
.strong { font-weight: 600; display: flex; align-items: center; gap: 6px; }
.ptag { flex: none; }
.dim2 { color: var(--text-3); }
.st { font-size: 11px; padding: 2px 9px; border-radius: 10px; white-space: nowrap; }
.st-ok { color: var(--ok, #34d399); border: 1px solid currentColor; }
.st-pending { color: var(--warn); border: 1px solid var(--warn); }
.st-off { color: var(--text-3); border: 1px solid var(--hairline); }
.st-dead { color: var(--crit, #f43f5e); border: 1px solid currentColor; }
.ops { display: flex; gap: 0; flex-wrap: wrap; }
.tbl-empty { text-align: center; padding: 24px; color: var(--text-3); }
.load-error { padding: 8px 0; color: var(--warn); font-size: 12.5px; }
.unit { margin-left: 10px; font-size: 11.5px; color: var(--text-3); }
.dlg-sub { font-size: 12px; color: var(--text-3); margin: 4px 0 0; }
</style>
