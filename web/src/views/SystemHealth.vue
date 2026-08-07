<script setup lang="ts">
// 系统自检 BITE（plan §9.23）：一屏看清 DB / 后台服务 / 内存 / 磁盘 / WAL / 入库延迟。
// 数据来自匿名 /api/health/ready（503 也要读 body，不能走统一 http 抛错路径）。
import { computed, onMounted, onBeforeUnmount, ref } from 'vue'
import { healthApi, type ReadyStatus } from '../api'

const ready = ref<ReadyStatus | null>(null)
const loadError = ref('')
const loading = ref(false)
let timer: number | undefined
let seq = 0

async function load() {
  const mine = ++seq
  loading.value = true
  try {
    const d = await healthApi.ready()
    if (mine !== seq) return
    ready.value = d
    loadError.value = ''
  } catch (e) {
    if (mine !== seq) return
    loadError.value = e instanceof Error ? e.message : '健康度加载失败'
  } finally {
    if (mine === seq) loading.value = false
  }
}

const overall = computed(() => {
  const r = ready.value
  if (!r) return { level: 'off' as const, text: '尚未取得自检结果' }
  if (r.db === 'down') return { level: 'crit' as const, text: '数据库不可用' }
  if (r.status === 'degraded' || (r.deadServices?.length ?? 0) > 0)
    return { level: 'crit' as const, text: `监测降级：${(r.deadServices ?? []).join('、') || '后台服务失联'}` }
  if ((r.wal?.pending ?? 0) > 0) return { level: 'warn' as const, text: `WAL 待补传 ${r.wal!.pending} 条` }
  if (r.freeDiskGb != null && r.freeDiskGb < 2) return { level: 'warn' as const, text: `磁盘余量仅 ${r.freeDiskGb} GB` }
  if (r.ingestLagSeconds != null && r.ingestLagSeconds > 120)
    return { level: 'warn' as const, text: `入库延迟 ${r.ingestLagSeconds} 秒` }
  return { level: 'ok' as const, text: '系统就绪' }
})

function fmtSec(v: number | null | undefined) {
  if (v == null) return '—'
  if (v < 60) return v.toFixed(1) + ' s'
  return (v / 60).toFixed(1) + ' min'
}

onMounted(() => {
  load()
  timer = window.setInterval(load, 15000)
})
onBeforeUnmount(() => {
  if (timer) window.clearInterval(timer)
})
</script>

<template>
  <div class="bite rise" v-loading="loading && !ready">
    <div v-if="loadError" class="banner-err">健康度拉取失败：{{ loadError }}</div>

    <div class="head panel" :class="`head--${overall.level}`">
      <div>
        <div class="h-title">系统自检 · BITE</div>
        <div class="h-sub">plan §9.23：开机与运行中均可核对就绪状态。阈值告警由运维侧配置，本页只展示事实。</div>
      </div>
      <div class="verdict">{{ overall.text }}</div>
    </div>

    <div v-if="ready" class="grid">
      <div class="c panel" :class="ready.db === 'up' ? 'ok' : 'bad'">
        <div class="c-k">数据库</div>
        <div class="c-v">{{ ready.db === 'up' ? '连通' : '断开' }}</div>
      </div>
      <div class="c panel" :class="(ready.deadServices?.length ?? 0) ? 'bad' : 'ok'">
        <div class="c-k">后台服务</div>
        <div class="c-v">{{ (ready.deadServices?.length ?? 0) ? ready.deadServices!.length + ' 失联' : '全部存活' }}</div>
      </div>
      <div class="c panel">
        <div class="c-k">设备连接</div>
        <div class="c-v mono">{{ ready.devices?.connected ?? 0 }}/{{ ready.devices?.total ?? 0 }}</div>
      </div>
      <div class="c panel" :class="ready.ingestLagSeconds != null && ready.ingestLagSeconds > 120 ? 'warn' : ''">
        <div class="c-k">入库延迟</div>
        <div class="c-v mono">{{ fmtSec(ready.ingestLagSeconds) }}</div>
      </div>
      <div class="c panel" :class="ready.maxIdleSeconds != null && ready.maxIdleSeconds > 120 ? 'warn' : ''">
        <div class="c-k">最大空闲</div>
        <div class="c-v mono">{{ fmtSec(ready.maxIdleSeconds) }}</div>
      </div>
      <div class="c panel" :class="(ready.wal?.pending ?? 0) > 0 ? 'warn' : 'ok'">
        <div class="c-k">WAL 积压</div>
        <div class="c-v mono">{{ ready.wal?.pending ?? 0 }} <span class="unit">条</span></div>
        <div class="c-sub mono">体积 {{ ready.wal?.sizeMb ?? '—' }} MB · {{ ready.wal?.enabled ? '已启用' : '未启用' }}</div>
      </div>
      <div class="c panel" :class="ready.freeDiskGb != null && ready.freeDiskGb < 2 ? 'warn' : ''">
        <div class="c-k">磁盘余量</div>
        <div class="c-v mono">{{ ready.freeDiskGb ?? '—' }} <span class="unit">GB</span></div>
      </div>
      <div class="c panel">
        <div class="c-k">进程内存</div>
        <div class="c-v mono">{{ ready.memory?.workingSetMb ?? '—' }} <span class="unit">MB</span></div>
        <div class="c-sub mono">私有 {{ ready.memory?.privateMb ?? '—' }} · GC {{ ready.memory?.gcHeapMb ?? '—' }}</div>
      </div>
    </div>

    <div v-if="ready?.backgroundServices?.length" class="panel svc">
      <div class="panel-hd"><span class="panel-title">后台服务心跳</span></div>
      <div class="svc-tbl">
        <div class="tr th"><span>服务</span><span>状态</span><span>距上次心跳</span></div>
        <div v-for="s in ready.backgroundServices" :key="s.name" class="tr">
          <span class="mono">{{ s.name }}</span>
          <span :class="s.stale ? 'bad' : 'ok'">{{ s.stale ? '失联' : '正常' }}</span>
          <span class="mono">{{ fmtSec(s.lastBeatSecondsAgo) }}</span>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.bite { display: flex; flex-direction: column; gap: 14px; }
.banner-err { padding: 12px 16px; border-radius: 8px; background: rgba(244, 63, 94, 0.08); border: 1px solid var(--crit); color: var(--text-1); font-size: 13px; }
.head { display: flex; justify-content: space-between; align-items: center; gap: 16px; padding: 16px 20px; border-left: 3px solid var(--text-3); }
.head--ok { border-left-color: var(--ok); }
.head--warn { border-left-color: var(--warn); }
.head--crit { border-left-color: var(--crit); background: rgba(244, 63, 94, 0.05); }
.head--off { border-left-color: var(--text-3); }
.h-title { font-size: 18px; font-weight: 700; }
.h-sub { font-size: 12px; color: var(--text-3); margin-top: 4px; max-width: 640px; }
.verdict { font-size: 16px; font-weight: 700; }
.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 12px; }
.c { padding: 14px 16px; }
.c.ok { border-color: rgba(16, 185, 129, 0.35); }
.c.warn { border-color: rgba(245, 158, 11, 0.45); }
.c.bad { border-color: rgba(244, 63, 94, 0.45); }
.c-k { font-size: 11px; color: var(--text-3); letter-spacing: 0.06em; text-transform: uppercase; }
.c-v { font-size: 22px; font-weight: 700; margin-top: 8px; line-height: 1.1; }
.c-sub { font-size: 11px; color: var(--text-3); margin-top: 6px; }
.unit { font-size: 12px; font-weight: 500; color: var(--text-3); }
.panel-hd { padding: 12px 16px 0; }
.panel-title { font-size: 12px; font-weight: 600; letter-spacing: 0.08em; text-transform: uppercase; color: var(--text-2); }
.svc-tbl { padding: 8px 12px 14px; }
.tr { display: grid; grid-template-columns: 1.4fr 0.6fr 0.8fr; gap: 8px; padding: 8px; font-size: 13px; }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.06em; }
.ok { color: var(--ok); }
.bad { color: var(--crit); }
</style>
