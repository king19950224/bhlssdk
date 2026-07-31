// 应用引导:装配 Pinia / 路由 / Element Plus(中文暗色),并挂 401 统一登出处理。
import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import * as ElementPlusIconsVue from '@element-plus/icons-vue'
import 'element-plus/dist/index.css'
import 'element-plus/theme-chalk/dark/css-vars.css'
import './style.css'
import App from './App.vue'
import router from './router'
import { setUnauthorizedHandler } from './api/client'
import { stopRealtime } from './api/realtime'
import { useAuth } from './stores/auth'

const app = createApp(App)
const pinia = createPinia()
// 关键：显式设中文 locale，否则 Element Plus 内置文案（日期选择器/分页/「No Data」空态/下拉「No data」/
// 弹窗默认按钮等）全是英文——这是界面大面积英文的根因。
app.use(pinia).use(router).use(ElementPlus, { locale: zhCn })

for (const [key, comp] of Object.entries(ElementPlusIconsVue)) {
  app.component(key, comp as never)
}

// 全局 401 / 令牌到期统一处理：清登录态、断开实时连接并跳登录页。
// 去重：多个请求同时 401 只处理一次（避免重复 logout / 重复 router.replace 抖动）。
let handlingUnauthorized = false
function forceLogout() {
  if (handlingUnauthorized) return
  handlingUnauthorized = true
  const auth = useAuth(pinia)
  void stopRealtime()
  auth.clearLocal()
  if (router.currentRoute.value.name !== 'login')
    router.replace({ name: 'login', query: { r: router.currentRoute.value.fullPath } })
  // 跳转完成后复位，允许下次会话再次触发
  setTimeout(() => { handlingUnauthorized = false }, 1000)
}
setUnauthorizedHandler(forceLogout)

// 令牌到期主动登出：不必等下一次 API 401。每 30s 检查一次，已登录但 token 失效即登出。
// 避免长时间挂机后页面仍"看似已登录"、SignalR 仍连着过期令牌。
setInterval(() => {
  const auth = useAuth(pinia)
  if (auth.token && !auth.isAuthed) forceLogout()
}, 30000)

app.mount('#app')
