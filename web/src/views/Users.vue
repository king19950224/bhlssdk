<script setup lang="ts">
// 页面:用户管理(仅管理员)——建号、停启用、角色分配。
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { userApi, type UserRow } from '../api'
import { useAuth } from '../stores/auth'

const auth = useAuth()
const users = ref<UserRow[]>([])
const loading = ref(false)

const ROLE: Record<string, { label: string; color: string }> = {
  admin: { label: '管理员', color: '#818cf8' },
  reviewer: { label: '复核员', color: '#38bdf8' },
  operator: { label: '操作员', color: '#10b981' },
}
const ROLE_OPTS = [
  { value: 'operator', label: '操作员 · 采集/提交/处置' },
  { value: 'reviewer', label: '复核员 · 审批/会签/签发' },
  { value: 'admin', label: '管理员 · 全部 + 用户管理' },
]

const dialog = reactive({ visible: false, mode: 'create' as 'create' | 'reset', saving: false })
const form = reactive({ username: '', fullName: '', password: '', role: 'operator' })

async function load() {
  loading.value = true
  try {
    users.value = (await userApi.list()).items
  } finally {
    loading.value = false
  }
}
onMounted(load)

function openCreate() {
  dialog.mode = 'create'
  Object.assign(form, { username: '', fullName: '', password: '', role: 'operator' })
  dialog.visible = true
}
function openReset(u: UserRow) {
  dialog.mode = 'reset'
  Object.assign(form, { username: u.username, fullName: u.fullName || '', password: '', role: u.role })
  dialog.visible = true
}
async function submit() {
  if (!form.username || !form.password) return ElMessage.warning('用户名与密码必填')
  if (form.password.length < 8) return ElMessage.warning('密码至少 8 位')
  dialog.saving = true
  try {
    await userApi.create({ username: form.username, password: form.password, role: form.role, fullName: form.fullName || undefined })
    ElMessage.success(dialog.mode === 'create' ? '用户已创建' : '密码已重置')
    dialog.visible = false
    load()
  } catch (e) {
    ElMessage.error((e as Error)?.message || '操作失败')
  } finally {
    dialog.saving = false
  }
}
async function toggle(u: UserRow) {
  try {
    await ElMessageBox.confirm(`确认${u.enabled ? '停用' : '启用'}用户「${u.username}」?`, u.enabled ? '停用' : '启用', {
      type: 'warning', confirmButtonText: '确认', cancelButtonText: '取消',
    })
    await userApi.toggle(u.id)
    ElMessage.success('已更新')
    load()
  } catch (e) {
    if (e !== 'cancel' && e !== 'close') ElMessage.error((e as Error)?.message || '操作失败')
  }
}
function fmt(iso: string | null) {
  return iso ? new Date(iso).toLocaleString('zh-CN', { hour12: false }) : '从未登录'
}
</script>

<template>
  <div class="users rise">
    <div class="u-head">
      <span class="panel-title"><i class="hd-mark" />系统用户 · 角色与权限</span>
      <el-button type="primary" @click="openCreate"><el-icon><Plus /></el-icon> 新建用户</el-button>
    </div>

    <div class="panel panel--glow">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark" />用户列表</span>
        <span class="hd-sub mono">{{ users.length }} 个</span>
      </div>
      <div class="tbl">
        <div class="tr th"><span>用户名</span><span>姓名</span><span>角色</span><span>状态</span><span>最近登录</span><span>操作</span></div>
        <div v-for="u in users" :key="u.id" class="tr">
          <span class="mono">{{ u.username }}<span v-if="u.id === auth.userId" class="self">本人</span></span>
          <span>{{ u.fullName || '—' }}</span>
          <span><span class="role" :style="`color:${ROLE[u.role]?.color};border-color:${ROLE[u.role]?.color}`">{{ ROLE[u.role]?.label || u.role }}</span></span>
          <span class="st">
            <span class="led" :class="u.enabled ? 'led--ok' : 'led--off'" />{{ u.enabled ? '启用' : '停用' }}
            <span v-if="u.locked" class="locked">锁定</span>
          </span>
          <span class="mono dim">{{ fmt(u.lastLoginAt) }}</span>
          <span class="ops">
            <el-button size="small" text @click="openReset(u)">重置密码</el-button>
            <el-button size="small" text :type="u.enabled ? 'danger' : 'primary'" :disabled="u.id === auth.userId" @click="toggle(u)">
              {{ u.enabled ? '停用' : '启用' }}
            </el-button>
          </span>
        </div>
        <div v-if="!loading && !users.length" class="tbl-empty">暂无用户</div>
      </div>
    </div>

    <el-dialog v-model="dialog.visible" :title="dialog.mode === 'create' ? '新建用户' : `重置密码 · ${form.username}`" width="440px">
      <el-form label-width="76px" label-position="left">
        <el-form-item label="用户名">
          <el-input v-model="form.username" :disabled="dialog.mode === 'reset'" placeholder="登录用户名" />
        </el-form-item>
        <el-form-item label="姓名" v-if="dialog.mode === 'create'">
          <el-input v-model="form.fullName" placeholder="（可选）真实姓名" />
        </el-form-item>
        <el-form-item label="角色">
          <!-- 重置密码走的是 upsert：角色锁定为原角色,防止重置密码时被误改权限 -->
          <el-select v-model="form.role" style="width: 100%" :disabled="dialog.mode === 'reset'">
            <el-option v-for="o in ROLE_OPTS" :key="o.value" :label="o.label" :value="o.value" />
          </el-select>
        </el-form-item>
        <el-form-item :label="dialog.mode === 'create' ? '密码' : '新密码'">
          <el-input v-model="form.password" type="password" show-password placeholder="至少 8 位" />
        </el-form-item>
        <div v-if="dialog.mode === 'reset'" class="reset-hint">
          重置后该账户将解除锁定并处于启用状态；若此前已停用，如仍需停用请重置后再次点「停用」。
        </div>
      </el-form>
      <template #footer>
        <el-button @click="dialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="dialog.saving" @click="submit">{{ dialog.mode === 'create' ? '创建' : '重置' }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.users { display: flex; flex-direction: column; gap: 16px; }
.u-head { display: flex; align-items: center; justify-content: space-between; }
.panel-hd { display: flex; align-items: center; justify-content: space-between; padding: 13px 18px 9px; }
.tbl { padding: 4px 12px 14px; overflow-x: auto; }
.tr { display: grid; grid-template-columns: 145px 110px 86px 80px minmax(0, 1fr) 150px; align-items: center; gap: 8px; padding: 10px 8px; border-radius: 8px; font-size: 13px; min-width: 700px; }
.tr:not(.th) { box-shadow: inset 0 -1px 0 var(--hairline); }
.tr:not(.th):hover { background: rgba(56, 189, 248, 0.05); }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; text-transform: uppercase; }
.self { font-size: 10px; color: var(--accent); border: 1px solid var(--panel-border-strong); border-radius: 8px; padding: 1px 6px; margin-left: 8px; }
.role { font-size: 12px; padding: 2px 10px; border: 1px solid; border-radius: 10px; }
.st { display: flex; align-items: center; gap: 8px; }
.locked { font-size: 10px; color: var(--warn); border: 1px solid var(--warn); border-radius: 8px; padding: 1px 6px; }
.dim { color: var(--text-3); }
.ops { display: flex; gap: 2px; }
.tbl-empty { padding: 30px; text-align: center; color: var(--text-3); }
.reset-hint { font-size: 12px; color: var(--warn); line-height: 1.6; padding: 0 4px; }
</style>
