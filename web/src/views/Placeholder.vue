<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'

const route = useRoute()
const labels: Record<string, string> = {
  devices: '设备管理',
  alarms: '告警中心',
  calibration: '标定中心',
  configChanges: '参数变更审批',
  audit: '审计日志',
  diagnostics: '波形诊断',
}
const label = computed(() => labels[route.name as string] ?? '该模块')
</script>

<template>
  <div class="ph panel panel--glow rise">
    <div class="scan-mini">
      <span class="ring" />
      <el-icon class="ph-icon"><MagicStick /></el-icon>
    </div>
    <h2>{{ label }} · 建设中</h2>
    <p>后端能力已就绪并通过端到端验证，前端正按统一设计语言逐页落地。</p>
  </div>
</template>

<style scoped>
.ph {
  height: 70vh;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 14px;
  text-align: center;
}
.scan-mini {
  position: relative;
  width: 78px;
  height: 78px;
  display: grid;
  place-items: center;
  margin-bottom: 6px;
}
.ring {
  position: absolute;
  inset: 0;
  border-radius: 50%;
  border: 1px dashed rgba(34, 211, 238, 0.4);
  animation: spin 14s linear infinite;
}
@keyframes spin { to { transform: rotate(360deg); } }
.ph-icon {
  font-size: 30px;
  color: var(--accent);
  filter: drop-shadow(0 0 12px var(--accent-glow));
}
.ph h2 {
  margin: 0;
  font-size: 20px;
}
.ph p {
  margin: 0;
  color: var(--text-3);
  max-width: 420px;
  font-size: 13px;
}
</style>
