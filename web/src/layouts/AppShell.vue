<script setup lang="ts">
// 应用外壳：左边导航，顶上一排状态灯（设备/采集/告警/实时连接）加用户菜单。登录后每个页面都套在这里面。
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useAuth } from '../stores/auth'
import { alarmApi, authApi } from '../api'
import { useRuntime } from '../stores/runtime'
import { startRealtime, stopRealtime, onRealtime, realtimeState } from '../api/realtime'

const route = useRoute()
const router = useRouter()
const auth = useAuth()

const navGroups = [
  {
    title: '监测运营',
    items: [
      { name: 'dashboard', label: '总览', icon: 'Odometer' },
      { name: 'live', label: '实时波形', icon: 'TrendCharts' },
      { name: 'assets', label: '资产监测', icon: 'Histogram' },
      { name: 'alarms', label: '告警中心', icon: 'BellFilled' },
    ],
  },
  {
    title: '计量与变更',
    items: [
      // 标定中心路由 meta.roles=reviewer/admin：与路由守卫一致，operator 不显示（否则点了被弹回首页）
      { name: 'calibration', label: '标定中心', icon: 'Aim', reviewerOnly: true },
      { name: 'configChanges', label: '参数审批', icon: 'DocumentChecked' },
    ],
  },
  {
    title: '设备与诊断',
    items: [
      { name: 'devices', label: '采集设备', icon: 'Cpu' },
      { name: 'identify', label: '点位与验收', icon: 'Aim' },
      { name: 'diagnostics', label: '波形诊断', icon: 'TrendCharts', reviewerOnly: true },
    ],
  },
  {
    title: '治理',
    items: [
      { name: 'audit', label: '审计日志', icon: 'Tickets', reviewerOnly: true },
      { name: 'reports', label: '报告', icon: 'Document' },
      { name: 'users', label: '用户管理', icon: 'User', adminOnly: true },
    ],
  },
]

// 菜单可见性与路由 meta.roles 对齐：admin-only 仅管理员、reviewer-only 复核员+管理员可见。
// 纵深防御 + 一致体验（后端仍有 403 兜底）。
function visibleItems(items: { name: string; label: string; icon: string; adminOnly?: boolean; reviewerOnly?: boolean }[]) {
  return items.filter((i) => (!i.adminOnly || auth.isAdmin) && (!i.reviewerOnly || auth.isReviewer))
}

const titleMap: Record<string, string> = Object.fromEntries(
  navGroups.flatMap((g) => g.items).map((n) => [n.name, n.label]),
)
const pageTitle = computed(() =>
  titleMap[route.name as string]
  ?? (route.name === 'boltDetail' ? '螺栓详情' : route.name === 'reportDetail' ? '报告详情' : '总览'))

function isActive(name: string) {
  if (route.name === name) return true
  // 螺栓详情归属资产监测
  if (name === 'assets' && route.name === 'boltDetail') return true
  return false
}

// 设备遥测改由共享 runtime store 提供（与 Dashboard 共用一次轮询，避免重复请求）
const runtime = useRuntime()
const devicesOnline = computed(() => runtime.online)
const devicesTotal = computed(() => runtime.total)
const walPending = computed(() => runtime.walPending)
const licenseValid = computed(() => runtime.license?.valid ?? true)
const openAlarms = ref(0)
let timer: number | undefined

async function refresh() {
  try {
    const sum = await alarmApi.summary()
    openAlarms.value = sum.items.reduce((a, r) => a + r.open, 0)
  } catch {
    /* 顶栏告警角标尽力刷新 */
  }
}
let unsubAlarm: (() => void) | undefined
const realtimeLabel = computed(() =>
  realtimeState.value === 'connected' ? '实时已连' : realtimeState.value === 'connecting' ? '实时连接中' : '实时断开',
)

// ---- 新告警提示音:监控值班场景不能只靠角标变红(可开关,默认开;偏好存本地) ----
const soundOn = ref(localStorage.getItem('bp_alarm_sound') !== 'off')
function toggleSound() {
  soundOn.value = !soundOn.value
  localStorage.setItem('bp_alarm_sound', soundOn.value ? 'on' : 'off')
  if (soundOn.value) beep() // 开启时试听一声
}
let audioCtx: AudioContext | null = null
let lastBeepAt = 0
function beep() {
  // 用 WebAudio 合成"嘟-嘟"双音,不依赖音频资源文件;5s 节流防告警风暴时连环响
  const now = Date.now()
  if (now - lastBeepAt < 5000) return
  lastBeepAt = now
  try {
    audioCtx ??= new AudioContext()
    for (const [offset, freq] of [[0, 880], [0.22, 660]] as const) {
      const osc = audioCtx.createOscillator()
      const gain = audioCtx.createGain()
      osc.type = 'sine'
      osc.frequency.value = freq
      gain.gain.setValueAtTime(0.0001, audioCtx.currentTime + offset)
      gain.gain.exponentialRampToValueAtTime(0.28, audioCtx.currentTime + offset + 0.02)
      gain.gain.exponentialRampToValueAtTime(0.0001, audioCtx.currentTime + offset + 0.18)
      osc.connect(gain).connect(audioCtx.destination)
      osc.start(audioCtx.currentTime + offset)
      osc.stop(audioCtx.currentTime + offset + 0.2)
    }
  } catch { /* 浏览器未授权音频时静默 */ }
}

onMounted(() => {
  // 设备状态轮询由 runtime store 统一负责（含可见性暂停与实时刷新）
  runtime.start()
  refresh()
  // 告警角标 30s 轮询作为 SignalR 的兜底（实时推送为主，断线时仍能慢刷新）
  timer = window.setInterval(refresh, 30000)
  // 实时推送：连上 Hub；收到告警立即刷新顶栏角标，不再纯靠轮询
  startRealtime()
  unsubAlarm = onRealtime('OnAlarm', (a) => {
    refresh()
    if (soundOn.value && a.action === 'trigger') beep()
  })
})
onUnmounted(() => {
  if (timer) clearInterval(timer)
  unsubAlarm?.()
  runtime.stop()
})

async function logout() {
  try {
    await ElMessageBox.confirm('确认退出登录？', '退出', { type: 'warning', confirmButtonText: '退出', cancelButtonText: '取消' })
  } catch {
    return /* 取消 */
  }
  await stopRealtime()
  await auth.logout()
  router.replace({ name: 'login' })
}

// ---- 自助改密 ----
const pwd = reactive({ visible: false, saving: false, oldPassword: '', newPassword: '', confirm: '' })
function openChangePwd() {
  Object.assign(pwd, { visible: true, saving: false, oldPassword: '', newPassword: '', confirm: '' })
}
async function submitChangePwd() {
  if (!pwd.oldPassword || !pwd.newPassword) return ElMessage.warning('请填写旧密码与新密码')
  if (pwd.newPassword.length < 8) return ElMessage.warning('新密码至少 8 位')
  if (pwd.newPassword !== pwd.confirm) return ElMessage.warning('两次输入的新密码不一致')
  pwd.saving = true
  try {
    await authApi.changePassword(pwd.oldPassword, pwd.newPassword)
    pwd.visible = false
    ElMessage.success('密码已修改，请用新密码重新登录')
    // 改密后服务端已吊销全部旧令牌,本地直接清会话回登录页
    await stopRealtime()
    auth.clearLocal()
    router.replace({ name: 'login' })
  } catch (e) {
    ElMessage.error((e as Error)?.message || '修改失败')
  } finally {
    pwd.saving = false
  }
}

function onUserCommand(c: string) {
  if (c === 'logout') logout()
  else if (c === 'changePwd') openChangePwd()
  else if (c === 'sound') toggleSound()
}
</script>

<template>
  <div class="shell">
    <aside class="sidebar">
      <div class="brand">
        <div class="brand-mark">
          <svg viewBox="0 0 32 32" width="26" height="26">
            <polygon points="16,3 27,9 27,23 16,29 5,23 5,9" fill="none" stroke="var(--accent)" stroke-width="1.6" />
            <circle cx="16" cy="16" r="4.5" fill="var(--accent)" opacity="0.9" />
            <circle cx="16" cy="16" r="8.5" fill="none" stroke="var(--accent)" stroke-width="1" opacity="0.4" />
          </svg>
        </div>
        <div class="brand-text">
          <div class="brand-name">螺栓预紧力在线监测系统</div>
          <div class="brand-sub">V1.1 在线监测</div>
        </div>
      </div>

      <nav class="nav">
        <div v-for="g in navGroups" :key="g.title" class="nav-group">
          <div class="nav-group-title">{{ g.title }}</div>
          <router-link
            v-for="n in visibleItems(g.items)"
            :key="n.name"
            :to="{ name: n.name }"
            class="nav-item"
            :class="{ active: isActive(n.name) }"
          >
            <el-icon class="nav-icon"><component :is="n.icon" /></el-icon>
            <span class="nav-label">{{ n.label }}</span>
            <span v-if="n.name === 'alarms' && openAlarms > 0" class="nav-badge">{{ openAlarms }}</span>
          </router-link>
        </div>
      </nav>

      <div class="sidebar-foot">
        <div class="sys-line">
          <span class="led" :class="licenseValid ? 'led--ok' : 'led--crit'" />
          <span>{{ licenseValid ? '授权有效' : '授权异常' }}</span>
        </div>
        <div class="sys-line">
          <span class="led" :class="realtimeState === 'connected' ? 'led--ok' : realtimeState === 'connecting' ? 'led--warn' : 'led--off'" />
          <span>{{ realtimeLabel }}</span>
        </div>
      </div>
    </aside>

    <div class="main">
      <header class="topbar">
        <div class="topbar-title">
          <h1>{{ pageTitle }}</h1>
          <div class="crumb mono">螺栓预紧力在线监测系统 / {{ pageTitle }}</div>
        </div>

        <div class="topbar-stats">
          <div class="stat-chip">
            <span class="led" :class="devicesOnline > 0 ? 'led--ok' : 'led--off'" />
            <span class="chip-k">设备在线</span>
            <span class="chip-v mono">{{ devicesOnline }}/{{ devicesTotal }}</span>
          </div>
          <div class="stat-chip" :class="{ alert: openAlarms > 0 }">
            <el-icon><BellFilled /></el-icon>
            <span class="chip-k">未闭合告警</span>
            <span class="chip-v mono">{{ openAlarms }}</span>
          </div>
        <div class="stat-chip alert" v-if="walPending > 0">
          <span class="chip-k">断网待补传</span>
          <span class="chip-v mono">{{ walPending }}</span>
        </div>
        <div
          class="stat-chip stat-chip--btn"
          role="button"
          tabindex="0"
          @click="router.push({ name: 'board' })"
          @keydown.enter="router.push({ name: 'board' })"
          @keydown.space.prevent="router.push({ name: 'board' })"
          title="打开态势大屏"
        >
          <el-icon><FullScreen /></el-icon>
          <span class="chip-k">大屏</span>
        </div>
      </div>

        <el-dropdown trigger="click" @command="onUserCommand">
          <div class="user-chip">
            <div class="avatar">{{ (auth.username || '?').slice(0, 1).toUpperCase() }}</div>
            <div class="user-meta">
              <div class="user-name">{{ auth.username }}</div>
              <div class="user-role">{{ auth.roleLabel }}</div>
            </div>
            <el-icon><ArrowDown /></el-icon>
          </div>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item command="changePwd"><el-icon><Lock /></el-icon> 修改密码</el-dropdown-item>
              <el-dropdown-item command="sound"><el-icon><BellFilled /></el-icon> 告警提示音：{{ soundOn ? '开' : '关' }}</el-dropdown-item>
              <el-dropdown-item command="logout" divided><el-icon><SwitchButton /></el-icon> 退出登录</el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>

      </header>

      <main class="content">
        <router-view v-slot="{ Component }">
          <transition name="fade-up" mode="out-in">
            <component :is="Component" />
          </transition>
        </router-view>
      </main>
    </div>

    <el-dialog v-model="pwd.visible" title="修改密码" width="420px" append-to-body>
      <el-form label-width="92px" label-position="left">
        <el-form-item label="旧密码">
          <el-input v-model="pwd.oldPassword" type="password" show-password autocomplete="current-password" />
        </el-form-item>
        <el-form-item label="新密码">
          <el-input v-model="pwd.newPassword" type="password" show-password placeholder="至少 8 位" autocomplete="new-password" />
        </el-form-item>
        <el-form-item label="确认新密码">
          <el-input v-model="pwd.confirm" type="password" show-password autocomplete="new-password" @keyup.enter="submitChangePwd" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="pwd.visible = false">取消</el-button>
        <el-button type="primary" :loading="pwd.saving" @click="submitChangePwd">确认修改</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.shell { display: flex; height: 100%; }

.sidebar {
  width: 236px;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  padding: 20px 14px;
  background: linear-gradient(180deg, rgba(13, 19, 32, 0.92), rgba(8, 11, 20, 0.72));
  border-right: 1px solid var(--panel-border);
  backdrop-filter: blur(10px);
}
.brand { display: flex; align-items: center; gap: 12px; padding: 6px 8px 18px; }
.brand-mark { filter: drop-shadow(0 0 10px var(--accent-glow)); display: grid; place-items: center; }
.brand-name {
  font-weight: 700; font-size: 14px; line-height: 1.3; letter-spacing: 0.01em;
  background: linear-gradient(90deg, #fff, var(--accent));
  -webkit-background-clip: text; background-clip: text; -webkit-text-fill-color: transparent;
}
.brand-sub { font-size: 11px; color: var(--text-3); letter-spacing: 0.18em; }

.nav { flex: 1; overflow-y: auto; display: flex; flex-direction: column; gap: 14px; }
.nav-group { display: flex; flex-direction: column; gap: 2px; }
.nav-group-title {
  font-size: 10.5px; font-weight: 600; letter-spacing: 0.16em; text-transform: uppercase;
  color: var(--text-3); padding: 4px 12px 4px;
}
.nav-item {
  display: flex; align-items: center; gap: 12px;
  padding: 10px 12px; border-radius: var(--radius-sm);
  color: var(--text-2); font-size: 14px; font-weight: 500; position: relative;
  transition: all 0.18s ease;
}
.nav-item:hover { color: var(--text-1); background: rgba(56, 189, 248, 0.07); }
.nav-item.active {
  color: #fff;
  background: linear-gradient(90deg, rgba(34, 211, 238, 0.16), rgba(34, 211, 238, 0.03));
  box-shadow: inset 0 0 0 1px var(--panel-border-strong);
}
.nav-item.active::before {
  content: ''; position: absolute; left: -14px; top: 8px; bottom: 8px; width: 3px;
  border-radius: 0 3px 3px 0; background: var(--accent); box-shadow: 0 0 12px var(--accent);
}
.nav-icon { font-size: 17px; }
.nav-label { flex: 1; }
.nav-badge {
  background: var(--crit); color: #fff; font-size: 11px; font-weight: 700;
  min-width: 18px; height: 18px; border-radius: 9px; display: grid; place-items: center;
  padding: 0 5px; box-shadow: 0 0 10px var(--crit-glow);
}
.sidebar-foot { border-top: 1px solid var(--hairline); padding-top: 14px; margin-top: 10px; display: flex; flex-direction: column; gap: 7px; }
.sys-line { display: flex; align-items: center; gap: 8px; font-size: 12px; color: var(--text-3); }

.main { flex: 1; display: flex; flex-direction: column; min-width: 0; }
.topbar {
  height: 72px; flex-shrink: 0; display: flex; align-items: center; gap: 20px; padding: 0 28px;
  border-bottom: 1px solid var(--panel-border); background: rgba(8, 11, 20, 0.5); backdrop-filter: blur(10px);
}
/* 标题列允许收缩并省略，防止窄窗口把面包屑挤成竖排字 */
.topbar-title { min-width: 0; flex-shrink: 1; overflow: hidden; }
.topbar-title h1 { margin: 0; font-size: 19px; font-weight: 600; letter-spacing: 0.01em; white-space: nowrap; }
.crumb { font-size: 11px; color: var(--text-3); margin-top: 2px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.topbar-stats { display: flex; gap: 12px; margin-left: auto; flex-shrink: 0; }
.stat-chip {
  display: flex; align-items: center; gap: 8px; height: 38px; padding: 0 14px; border-radius: 10px;
  background: var(--panel); border: 1px solid var(--panel-border); font-size: 13px; color: var(--text-2);
  white-space: nowrap;
}
.stat-chip.alert { border-color: var(--crit); color: #fff; box-shadow: 0 0 18px -6px var(--crit-glow); }
.stat-chip--btn { cursor: pointer; transition: border-color 0.18s, color 0.18s; }
.stat-chip--btn:hover { border-color: var(--accent); color: var(--accent); }
.stat-chip--btn:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.stat-chip .chip-v { color: var(--text-1); font-weight: 600; }
.user-chip {
  display: flex; align-items: center; gap: 10px; height: 42px; padding: 0 10px 0 6px;
  border-radius: 12px; cursor: pointer; border: 1px solid transparent; transition: all 0.18s;
}
.user-chip:hover { background: var(--panel); border-color: var(--panel-border); }
.avatar {
  width: 32px; height: 32px; border-radius: 9px; display: grid; place-items: center;
  font-weight: 700; color: #04121a; background: linear-gradient(135deg, var(--accent), var(--accent-2));
  box-shadow: 0 0 14px -2px var(--accent-glow);
}
.user-name { font-size: 13px; font-weight: 600; line-height: 1.1; }
.user-role { font-size: 11px; color: var(--text-3); }

.content { flex: 1; overflow-y: auto; padding: 24px 28px 40px; }

.fade-up-enter-active { transition: all 0.32s cubic-bezier(0.22, 1, 0.36, 1); }
.fade-up-enter-from { opacity: 0; transform: translateY(10px); }
</style>
