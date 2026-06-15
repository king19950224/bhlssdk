import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import * as ElementPlusIconsVue from '@element-plus/icons-vue'
import 'element-plus/dist/index.css'
import 'element-plus/theme-chalk/dark/css-vars.css'
import './style.css'
import App from './App.vue'
import router from './router'
import { setUnauthorizedHandler } from './api/client'
import { useAuth } from './stores/auth'

const app = createApp(App)
const pinia = createPinia()
app.use(pinia).use(router).use(ElementPlus)

for (const [key, comp] of Object.entries(ElementPlusIconsVue)) {
  app.component(key, comp as never)
}

// 全局 401：清登录态并跳登录页
setUnauthorizedHandler(() => {
  const auth = useAuth(pinia)
  auth.logout()
  if (router.currentRoute.value.name !== 'login') router.replace({ name: 'login' })
})

app.mount('#app')
