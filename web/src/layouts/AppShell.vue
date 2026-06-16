<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessageBox } from 'element-plus'
import { useAuth } from '../stores/auth'
import { runtimeApi, alarmApi } from '../api'
import { startRealtime, stopRealtime, onRealtime, realtimeState } from '../api/realtime'

const route = useRoute()
const router = useRouter()
const auth = useAuth()

const navGroups = [
  {
    title: '监测运营',
    items: [
      { name: 'dashboard', label: '总览', icon: 'Odometer' },
      { name: 'assets', label: '资产监测', icon: 'Histogram' },
      { name: 'alarms', label: '告警中心', icon: 'BellFilled' },
    ],
  },
  {
    title: '计量与变更',
    items: [
      { name: 'calibration', label: '标定中心', icon: 'Aim' },
      { name: 'configChanges', label: '参数审批', icon: 'DocumentChecked' },
    ],
  },
  {
    title: '设备与诊断',
    items: [
      { name: 'devices', label: '采集设备', icon: 'Cpu' },
      { name: 'identify', label: '通道识别', icon: 'Aim' },
      { name: 'diagnostics', label: '波形诊断', icon: 'TrendCharts' },
    ],
  },
  {
    title: '治理',
    items: [
      { name: 'audit', label: '审计日志', icon: 'Tickets' },
      { name: 'reports', label: '报告', icon: 'Document' },
      { name: 'users', label: '用户管理', icon: 'User', adminOnly: true },
    ],
  },
]

function visibleItems(items: { name: string; label: string; icon: string; adminOnly?: boolean }[]) {
  return items.filter((i) => !i.adminOnly || auth.isAdmin)
}

const titleMap: Record<string, string> = Object.fromEntries(
  navGroups.flatMap((g) => g.items).map((n) => [n.name, n.label]),
)
const pageTitle = computed(() => titleMap[route.name as string] ?? (route.name === 'boltDetail' ? '螺栓详情' : '总览'))

function isActive(name: string) {
  if (route.name === name) return true
  // 螺栓详情归属资产监测
  if (name === 'assets' && route.name === 'boltDetail') return true
  return false
}

const devicesOnline = ref(0)
const devicesTotal = ref(0)
const openAlarms = ref(0)
const walPending = ref(0)
const licenseValid = ref(true)
let timer: number | undefined

async function refresh() {
  try {
    const rt = await runtimeApi.devices()
    devicesTotal.value = rt.count
    devicesOnline.value = rt.items.filter((d) => d.state === 'Connected').length
    walPending.value = rt.wal?.pending ?? 0
    licenseValid.value = rt.license?.valid ?? true
    const sum = await alarmApi.summary()
    openAlarms.value = sum.items.reduce((a, r) => a + r.open, 0)
  } catch {
    /* 顶栏状态尽力刷新 */
  }
}
let unsubAlarm: (() => void) | undefined
const realtimeLabel = computed(() =>
  realtimeState.value === 'connected' ? '实时已连' : realtimeState.value === 'connecting' ? '实时连接中' : '实时断开',
)

onMounted(() => {
  refresh()
  // 轮询频率降到 30s 作为 SignalR 的兜底（实时推送为主，断线时仍能慢刷新）
  timer = window.setInterval(refresh, 30000)
  // 实时推送：连上 Hub；收到告警立即刷新顶栏角标与设备状态，不再纯靠轮询
  startRealtime()
  unsubAlarm = onRealtime('OnAlarm', () => { refresh() })
})
onUnmounted(() => {
  if (timer) clearInterval(timer)
  unsubAlarm?.()
})

async function logout() {
  try {
    await ElMessageBox.confirm('确认退出登录？', '退出', { type: 'warning', confirmButtonText: '退出', cancelButtonText: '取消' })
    await stopRealtime()
    auth.logout()
    router.replace({ name: 'login' })
  } catch {
    /* 取消 */
  }
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
          <div class="brand-sub">V1.0 在线监测</div>
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
        <div class="stat-chip stat-chip--btn" @click="router.push({ name: 'board' })" title="打开态势大屏">
          <el-icon><FullScreen /></el-icon>
          <span class="chip-k">大屏</span>
        </div>
      </div>

        <el-dropdown trigger="click" @command="(c: string) => c === 'logout' && logout()">
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
              <el-dropdown-item command="logout"><el-icon><SwitchButton /></el-icon> 退出登录</el-dropdown-item>
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
.topbar-title h1 { margin: 0; font-size: 19px; font-weight: 600; letter-spacing: 0.01em; }
.crumb { font-size: 11px; color: var(--text-3); margin-top: 2px; }
.topbar-stats { display: flex; gap: 12px; margin-left: auto; }
.stat-chip {
  display: flex; align-items: center; gap: 8px; height: 38px; padding: 0 14px; border-radius: 10px;
  background: var(--panel); border: 1px solid var(--panel-border); font-size: 13px; color: var(--text-2);
}
.stat-chip.alert { border-color: var(--crit); color: #fff; box-shadow: 0 0 18px -6px var(--crit-glow); }
.stat-chip--btn { cursor: pointer; transition: border-color 0.18s, color 0.18s; }
.stat-chip--btn:hover { border-color: var(--accent); color: var(--accent); }
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
