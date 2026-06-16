import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { useAuth } from '../stores/auth'
import AppShell from '../layouts/AppShell.vue'

const routes: RouteRecordRaw[] = [
  { path: '/login', name: 'login', component: () => import('../views/Login.vue'), meta: { public: true } },
  // 大屏 HMI 态势看板：全屏、无外壳导航，需登录
  { path: '/board', name: 'board', component: () => import('../views/BigScreen.vue') },
  {
    path: '/',
    component: AppShell,
    children: [
      { path: '', name: 'dashboard', component: () => import('../views/Dashboard.vue') },
      { path: 'assets', name: 'assets', component: () => import('../views/AssetMonitor.vue') },
      { path: 'bolts/:boltId', name: 'boltDetail', component: () => import('../views/BoltDetail.vue') },
      { path: 'alarms', name: 'alarms', component: () => import('../views/Alarms.vue') },
      { path: 'calibration', name: 'calibration', component: () => import('../views/Calibration.vue') },
      { path: 'config-changes', name: 'configChanges', component: () => import('../views/ConfigChanges.vue') },
      { path: 'audit', name: 'audit', component: () => import('../views/Audit.vue') },
      { path: 'devices', name: 'devices', component: () => import('../views/Devices.vue') },
      { path: 'identify', name: 'identify', component: () => import('../views/ChannelIdentify.vue') },
      { path: 'diagnostics', name: 'diagnostics', component: () => import('../views/Diagnostics.vue') },
      { path: 'reports', name: 'reports', component: () => import('../views/Reports.vue') },
      { path: 'reports/:reportId', name: 'reportDetail', component: () => import('../views/ReportDetail.vue') },
      { path: 'users', name: 'users', component: () => import('../views/Users.vue') },
      // 已登录下的未知路径 → 在外壳内渲染 404（保留导航），而非静默跳首页
      { path: ':pathMatch(.*)*', name: 'notFound', component: () => import('../views/NotFound.vue') },
    ],
  },
]

const router = createRouter({ history: createWebHistory(), routes })

router.beforeEach((to) => {
  const auth = useAuth()
  if (!to.meta.public && !auth.isAuthed) return { name: 'login', query: { r: to.fullPath } }
  if (to.name === 'login' && auth.isAuthed) return { name: auth.homeName }
  return true
})

export default router
