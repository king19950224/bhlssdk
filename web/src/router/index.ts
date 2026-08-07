// 路由表 + 全局守卫：没登录就跳登录页，meta.roles 拦角色。
// 真正说了算的是后端（越权一律 403），这里拦一道是省得让人点进去白跑一趟。
import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { ElMessage } from 'element-plus'
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
      { path: 'live', name: 'live', component: () => import('../views/LivePreload.vue') },
      { path: 'assets', name: 'assets', component: () => import('../views/AssetMonitor.vue') },
      { path: 'bolts/:boltId', name: 'boltDetail', component: () => import('../views/BoltDetail.vue') },
      { path: 'alarms', name: 'alarms', component: () => import('../views/Alarms.vue') },
      // 报警 KPI 单列一页：GB/T 41261 的四项限值验收时要直接拉数对照，混进告警中心的处置视图里不好找
      { path: 'alarm-kpi', name: 'alarmKpi', component: () => import('../views/AlarmKpi.vue') },
      { path: 'calibration', name: 'calibration', component: () => import('../views/Calibration.vue'), meta: { roles: ['reviewer', 'admin'] } },
      { path: 'config-changes', name: 'configChanges', component: () => import('../views/ConfigChanges.vue') },
      { path: 'audit', name: 'audit', component: () => import('../views/Audit.vue'), meta: { roles: ['reviewer', 'admin'] } },
      { path: 'devices', name: 'devices', component: () => import('../views/Devices.vue') },
      { path: 'identify', name: 'identify', component: () => import('../views/ChannelIdentify.vue') },
      { path: 'diagnostics', name: 'diagnostics', component: () => import('../views/Diagnostics.vue'), meta: { roles: ['reviewer', 'admin'] } },
      // 系统自检 BITE（plan §9.23）：一屏核对就绪度，与波形诊断分开
      { path: 'system-health', name: 'systemHealth', component: () => import('../views/SystemHealth.vue') },
      { path: 'reports', name: 'reports', component: () => import('../views/Reports.vue') },
      { path: 'reports/:reportId', name: 'reportDetail', component: () => import('../views/ReportDetail.vue') },
      { path: 'users', name: 'users', component: () => import('../views/Users.vue'), meta: { roles: ['admin'] } },
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
  // 角色守卫：meta.roles 指定允许角色，不满足则回各自首页。后端每个端点仍有 403 兜底，这里只是别让人点进白屏页。
  const roles = to.meta.roles as string[] | undefined
  if (roles && (!auth.role || !roles.includes(auth.role))) {
    // 不再静默弹回首页：告知无权限,避免"点了没反应"的困惑
    ElMessage.warning('当前账号无权访问该页面（需复核员或管理员权限）')
    return { name: auth.homeName }
  }
  return true
})

export default router
