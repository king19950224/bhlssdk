<script setup lang="ts">
import { useRouter } from 'vue-router'
const router = useRouter()
</script>

<template>
  <div class="nf">
    <div class="nf-code mono">404</div>
    <h2>页面不存在</h2>
    <p>你访问的路径不存在或已移除。</p>
    <div class="nf-actions">
      <el-button type="primary" @click="router.replace({ name: 'dashboard' })">返回总览</el-button>
      <el-button @click="router.back()">返回上一页</el-button>
    </div>
  </div>
</template>

<style scoped>
.nf { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 12px; padding: 80px 20px; text-align: center; }
.nf-code { font-size: 72px; font-weight: 800; color: var(--accent); letter-spacing: 4px; line-height: 1; }
.nf h2 { margin: 8px 0 0; font-size: 22px; }
.nf p { color: var(--text-3); font-size: 14px; margin: 0; }
.nf-actions { display: flex; gap: 12px; margin-top: 14px; }
</style>
