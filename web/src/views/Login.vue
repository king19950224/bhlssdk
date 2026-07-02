<script setup lang="ts">
import { ref } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { ElMessage } from 'element-plus'
import { useAuth } from '../stores/auth'

const auth = useAuth()
const router = useRouter()
const route = useRoute()

const username = ref('')
const password = ref('')
const loading = ref(false)

// 只接受站内绝对路径作为登录后跳转目标，挡住 open-redirect（//host、http(s)://、/\ 反斜杠变体）。
function safeRedirect(r: unknown): string | null {
  if (typeof r !== 'string' || !r.startsWith('/')) return null
  if (r.startsWith('//') || r.startsWith('/\\')) return null
  return r
}

async function submit() {
  if (!username.value || !password.value) {
    ElMessage.warning('请输入用户名和密码')
    return
  }
  loading.value = true
  try {
    await auth.login(username.value, password.value)
    router.replace(safeRedirect(route.query.r) ?? { name: auth.homeName })
  } catch (e) {
    ElMessage.error((e as Error)?.message || '登录失败')
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="login">
    <!-- 左：品牌 hero -->
    <section class="hero">
      <div class="hero-top">
        <div class="logo">
          <svg viewBox="0 0 32 32" width="34" height="34">
            <polygon points="16,3 27,9 27,23 16,29 5,23 5,9" fill="none" stroke="var(--accent)" stroke-width="1.6" />
            <circle cx="16" cy="16" r="4.5" fill="var(--accent)" />
          </svg>
          <span>螺栓预紧力在线监测系统</span>
        </div>
      </div>

      <div class="hero-mid">
        <div class="scan">
          <span class="ring r1" />
          <span class="ring r2" />
          <span class="ring r3" />
          <span class="core" />
        </div>
        <h1>螺栓预紧力<br />在线监测系统</h1>
        <p>实时声时测量 · 标定可溯源 · 全程可审计 · 7×24 安全守护</p>
        <div class="kpis">
          <div><b class="mono glow-accent">GB/T</b><span>43232 标准方法</span></div>
          <div><b class="mono glow-accent">7×24</b><span>在线监测</span></div>
          <div><b class="mono glow-accent">JJF</b><span>不确定度评定</span></div>
        </div>
      </div>

      <div class="hero-foot mono">GB/T 43232 · JJF 1059.1 · 双人复核 · 断网续传</div>
    </section>

    <!-- 右：登录卡 -->
    <section class="form-wrap">
      <div class="card panel panel--glow">
        <div class="card-head">
          <h2>登录控制台</h2>
          <p>请输入您的账号凭据</p>
        </div>

        <el-form @submit.prevent="submit" size="large">
          <el-form-item>
            <el-input v-model="username" placeholder="用户名" :prefix-icon="'User'" autocomplete="username" @keyup.enter="submit" />
          </el-form-item>
          <el-form-item>
            <el-input
              v-model="password"
              type="password"
              show-password
              placeholder="密码"
              :prefix-icon="'Lock'"
              autocomplete="current-password"
              @keyup.enter="submit"
            />
          </el-form-item>
          <el-button type="primary" class="login-btn" :loading="loading" @click="submit">
            进入系统
          </el-button>
        </el-form>

        <div class="card-foot">
          <span class="led led--ok" /> 系统就绪 · 访问受授权与审计保护
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.login {
  height: 100%;
  display: grid;
  grid-template-columns: 1.15fr 0.85fr;
}
@media (max-width: 880px) {
  .login { grid-template-columns: 1fr; }
  .hero { display: none; }
}

/* hero */
.hero {
  position: relative;
  padding: 40px 56px;
  display: flex;
  flex-direction: column;
  border-right: 1px solid var(--panel-border);
  overflow: hidden;
}
.logo {
  display: flex;
  align-items: center;
  gap: 12px;
  font-size: 20px;
  font-weight: 700;
  filter: drop-shadow(0 0 10px var(--accent-glow));
}
.hero-mid {
  margin: auto 0;
  max-width: 520px;
}
.hero-mid h1 {
  font-size: 40px;
  line-height: 1.18;
  margin: 28px 0 16px;
  font-weight: 700;
  letter-spacing: 0.01em;
}
.hero-mid p {
  color: var(--text-2);
  font-size: 15px;
  letter-spacing: 0.04em;
}
.kpis {
  display: flex;
  gap: 38px;
  margin-top: 34px;
}
.kpis div {
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.kpis b {
  font-size: 26px;
}
.kpis span {
  font-size: 12px;
  color: var(--text-3);
  letter-spacing: 0.1em;
}
.hero-foot {
  color: var(--text-3);
  font-size: 12px;
  letter-spacing: 0.12em;
}

/* 扫描环装饰 */
.scan {
  position: absolute;
  right: -120px;
  top: 50%;
  transform: translateY(-50%);
  width: 460px;
  height: 460px;
  opacity: 0.28;
  pointer-events: none;
}
.ring {
  position: absolute;
  inset: 0;
  margin: auto;
  border-radius: 50%;
  border: 1px solid rgba(34, 211, 238, 0.25);
}
/* 静态同心环装饰（去掉旋转动画的演示感，仅作沉稳背景纹样） */
.r1 { width: 100%; height: 100%; border-style: dashed; }
.r2 { width: 70%; height: 70%; border-color: rgba(34, 211, 238, 0.28); }
.r3 { width: 40%; height: 40%; border-style: dashed; }
.core {
  position: absolute;
  inset: 0;
  margin: auto;
  width: 14%;
  height: 14%;
  border-radius: 50%;
  background: radial-gradient(circle, var(--accent), transparent 70%);
}

/* 表单 */
.form-wrap {
  display: grid;
  place-items: center;
  padding: 40px;
}
.card {
  width: 100%;
  max-width: 380px;
  padding: 38px 34px;
}
.card-head h2 {
  margin: 0 0 6px;
  font-size: 23px;
  font-weight: 700;
}
.card-head p {
  margin: 0 0 26px;
  color: var(--text-3);
  font-size: 13px;
}
.login-btn {
  width: 100%;
  height: 46px;
  font-size: 15px;
  font-weight: 600;
  letter-spacing: 0.1em;
  background: linear-gradient(135deg, var(--accent), var(--accent-2));
  border: none;
  box-shadow: 0 8px 26px -8px var(--accent-glow);
}
.card-foot {
  margin-top: 22px;
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  color: var(--text-3);
}
</style>
