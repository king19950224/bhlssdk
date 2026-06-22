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

// 全局 401：清登录态、断开实时连接并跳登录页
setUnauthorizedHandler(() => {
  const auth = useAuth(pinia)
  void stopRealtime()
  auth.logout()
  if (router.currentRoute.value.name !== 'login') router.replace({ name: 'login' })
})

app.mount('#app')
