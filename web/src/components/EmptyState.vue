<script setup lang="ts">
// 统一的空态 / 加载态 / 错误态占位，跨视图复用，消除各页自造的零散空态。
withDefaults(defineProps<{
  loading?: boolean
  error?: string | null
  empty?: boolean
  emptyText?: string
  icon?: string
}>(), {
  loading: false,
  error: null,
  empty: false,
  emptyText: '暂无数据',
  icon: 'Files',
})
</script>

<template>
  <div class="es" v-if="loading || error || empty">
    <template v-if="loading">
      <el-icon class="es-ic spin"><Loading /></el-icon>
      <span class="es-txt">加载中…</span>
    </template>
    <template v-else-if="error">
      <el-icon class="es-ic es-err"><WarningFilled /></el-icon>
      <span class="es-txt es-err">{{ error }}</span>
      <slot name="action" />
    </template>
    <template v-else>
      <el-icon class="es-ic"><component :is="icon" /></el-icon>
      <span class="es-txt">{{ emptyText }}</span>
      <slot name="action" />
    </template>
  </div>
</template>

<style scoped>
.es { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 10px; padding: 40px 20px; color: var(--text-3); text-align: center; }
.es-ic { font-size: 30px; opacity: 0.5; }
.es-ic.es-err { color: var(--crit); opacity: 0.8; }
.es-txt { font-size: 13px; }
.es-txt.es-err { color: var(--crit); }
.spin { animation: es-spin 1s linear infinite; }
@keyframes es-spin { to { transform: rotate(360deg); } }
</style>
