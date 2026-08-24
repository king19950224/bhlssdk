<script setup lang="ts">
// 变更记录（纯历史查阅）：参数调整在螺栓详情/波形诊断页直接下发，本页只读不操作。
// 2026-08-19 审批工作流下线；历史记录的状态/处理人照旧展示（过去的事实）。
import { onMounted, onUnmounted, reactive, ref } from 'vue'
import { configChangeApi, type ConfigChange } from '../api'

const items = ref<ConfigChange[]>([])
const loading = ref(false)
let timer: number | undefined

const detail = reactive({ visible: false, item: null as ConfigChange | null })
function openDetail(c: ConfigChange) {
  detail.item = c
  detail.visible = true
}
function paramPairs(c: ConfigChange | null): [string, unknown][] {
  if (!c?.requested) return []
  try {
    const o = JSON.parse(c.requested) as Record<string, unknown>
    return Object.entries(o).filter(([, v]) => v != null)
  } catch {
    return []
  }
}

// 历史记录状态（过去完成的事实，非待办）
const ST: Record<string, { label: string; color: string }> = {
  requested: { label: '未处理', color: '#61748f' },
  approved: { label: '未执行', color: '#61748f' },
  executed: { label: '已下发', color: '#818cf8' },
  verified: { label: '已验证', color: '#10b981' },
  rejected: { label: '已作废', color: '#61748f' },
  failed: { label: '执行失败', color: '#f43f5e' },
}
// 通道超声参数的中文名（与后端 ChannelConfigUpdate 字段一一对应；历史数据可能存 PascalCase,统一小写匹配）
const PARAM_LABEL: Record<string, string> = {
  probefreqmhz: '探头频率（MHz）', gain: '增益（1~47 档）', threshold: '检波阈值', measmode: '测量模式',
  echowinns: '回波窗（ns）', echowidthus: '回波宽度（μs）', tcmode: '温补模式', tempcoefficient: '温度系数',
  reftimens: '参考声时（ns）', reftempc: '参考温度（°C）',
}
const paramName = (k: string) => PARAM_LABEL[k.toLowerCase()] || k

// 拉取失败要显示错误，不能和"无记录"混淆
const loadError = ref<string | null>(null)
// 达到后端单次上限（500）即视为截断，须提示
const truncated = ref(false)
async function load() {
  loading.value = true
  try {
    const r = await configChangeApi.list('all')
    items.value = r.items
    truncated.value = r.items.length >= 500
    loadError.value = null
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : '变更记录加载失败'
  } finally {
    loading.value = false
  }
}
onMounted(() => {
  load()
  timer = window.setInterval(() => { if (!document.hidden) load() }, 8000)
})
onUnmounted(() => timer && clearInterval(timer))
function params(c: ConfigChange) {
  if (!c.requested) return '—'
  try {
    const o = JSON.parse(c.requested)
    return Object.entries(o).filter(([, v]) => v != null).map(([k, v]) => `${paramName(k)}=${v}`).join(', ') || '—'
  } catch {
    return c.requested
  }
}
function fmt(iso: string | null) {
  return iso ? new Date(iso).toLocaleString('zh-CN', { hour12: false }) : '—'
}
</script>

<template>
  <div class="cc rise">
    <!-- 2026-08-19 起参数变更直发设备（螺栓详情/波形诊断页），不再进入本队列；
         本页职责收敛为变更历史查阅 + 存量待办处理。文案只描述现状，不叙述产品变更史（商用文案纪律）。 -->
    <el-alert type="info" :closable="false" show-icon style="margin-bottom: 12px">
      通道参数在<b>螺栓详情</b>与<b>波形诊断</b>页调整，下发即生效并自动记录。本页用于查阅历史变更记录。
    </el-alert>
    <div class="panel panel--glow">
      <div class="panel-hd">
        <span class="panel-title"><i class="hd-mark" />变更记录</span>
        <span class="hd-sub mono">{{ items.length }} 条</span>
      </div>
      <div v-if="loadError" class="load-error">
        <el-icon><WarningFilled /></el-icon> 变更记录加载失败：{{ loadError }}
        <el-button size="small" text type="primary" @click="load">重试</el-button>
      </div>
      <div v-if="truncated" class="load-error">
        <el-icon><WarningFilled /></el-icon> 仅显示最近 500 条变更记录，更早的历史未列出。
      </div>
      <div class="tbl">
        <div class="tr th"><span>#</span><span>设备/通道</span><span>变更参数</span><span>状态</span><span>提交</span><span>处理人</span><span>操作</span></div>
        <div v-for="c in items" :key="c.id" class="tr">
          <span class="mono">{{ c.id }}</span>
          <span class="mono">设备{{ c.deviceId }} · CH{{ c.channelNo }}</span>
          <span class="params mono">{{ params(c) }}</span>
          <span><span class="st" :style="`color:${ST[c.status]?.color};border-color:${ST[c.status]?.color}`">{{ ST[c.status]?.label || c.status }}</span></span>
          <span class="who">{{ c.requestedByName || (c.requestedBy != null ? `用户#${c.requestedBy}` : '—') }}<br /><span class="dim mono">{{ fmt(c.requestedAt) }}</span></span>
          <span class="who">{{ c.approvedByName || (c.approvedBy != null ? `用户#${c.approvedBy}` : '—') }}</span>
          <span class="ops">
            <el-button size="small" text @click="openDetail(c)">详情</el-button>
          </span>
        </div>
        <div v-if="!loading && !loadError && !items.length" class="tbl-empty"><span class="led led--ok" /> 无变更记录</div>
      </div>
    </div>

    <div class="flow-note panel">
      <span class="panel-title"><i class="hd-mark" />变更流程</span>
      <div class="flow">
        <span class="fl">调整<i>螺栓详情 / 波形诊断</i></span><el-icon><ArrowRight /></el-icon>
        <span class="fl">下发设备<i>即时生效</i></span><el-icon><ArrowRight /></el-icon>
        <span class="fl">读回验证<i>参数比对</i></span><el-icon><ArrowRight /></el-icon>
        <span class="fl">留档<i>变更与审计记录</i></span>
      </div>
    </div>

    <el-dialog v-model="detail.visible" :title="detail.item ? `变更请求 #${detail.item.id}` : '变更详情'" width="540px">
      <div v-if="detail.item" class="dd">
        <div class="dd-row"><span class="dd-k">设备 / 通道</span><b class="mono">设备{{ detail.item.deviceId }} · CH{{ detail.item.channelNo }}</b></div>
        <div class="dd-row"><span class="dd-k">状态</span><span class="st" :style="`color:${ST[detail.item.status]?.color};border-color:${ST[detail.item.status]?.color}`">{{ ST[detail.item.status]?.label || detail.item.status }}</span></div>
        <div class="dd-sec">变更参数</div>
        <div v-if="paramPairs(detail.item).length" class="dd-params">
          <div v-for="[k, v] in paramPairs(detail.item)" :key="k" class="dd-param"><span class="dd-pk">{{ paramName(k) }}</span><b class="mono">{{ v }}</b></div>
        </div>
        <div v-else class="dd-empty">无参数</div>
        <div class="dd-sec">流程时间线</div>
        <div class="dd-row"><span class="dd-k">提交</span><span>{{ detail.item.requestedByName || (detail.item.requestedBy != null ? `用户#${detail.item.requestedBy}` : '—') }} · <span class="mono dim">{{ fmt(detail.item.requestedAt) }}</span></span></div>
        <div class="dd-row"><span class="dd-k">处理</span><span>{{ detail.item.approvedByName || (detail.item.approvedBy != null ? `用户#${detail.item.approvedBy}` : '—') }} · <span class="mono dim">{{ fmt(detail.item.approvedAt) }}</span></span></div>
        <div class="dd-row"><span class="dd-k">执行</span><span>{{ detail.item.executedByName || (detail.item.executedBy != null ? `用户#${detail.item.executedBy}` : '—') }} · <span class="mono dim">{{ fmt(detail.item.executedAt) }}</span></span></div>
        <div class="dd-row"><span class="dd-k">验证</span><span class="mono dim">{{ fmt(detail.item.verifiedAt) }}</span></div>
        <div v-if="detail.item.failureReason" class="dd-row dd-fail"><span class="dd-k">失败原因</span><span>{{ detail.item.failureReason }}</span></div>
        <div v-if="detail.item.note" class="dd-row"><span class="dd-k">备注</span><span>{{ detail.item.note }}</span></div>
      </div>
      <template #footer><el-button @click="detail.visible = false">关闭</el-button></template>
    </el-dialog>
  </div>
</template>

<style scoped>
.cc { display: flex; flex-direction: column; gap: 16px; }

.panel-hd { display: flex; align-items: center; justify-content: space-between; padding: 13px 18px 9px; }
.tbl { padding: 4px 12px 14px; overflow-x: auto; }
.tr { display: grid; grid-template-columns: 44px 128px minmax(0, 1.4fr) 80px 128px 80px 120px; align-items: center; padding: 10px 8px; border-radius: 8px; font-size: 13px; min-width: 720px; }
.tr:not(.th) { box-shadow: inset 0 -1px 0 var(--hairline); }
.tr:not(.th):hover { background: rgba(56, 189, 248, 0.05); }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; text-transform: uppercase; }
.params { color: var(--text-2); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.st { font-size: 11px; padding: 2px 9px; border: 1px solid; border-radius: 10px; }
.who { font-size: 12px; }
.dim { color: var(--text-3); font-size: 11px; }
.ops { display: flex; gap: 2px; }
.tbl-empty { display: flex; align-items: center; gap: 8px; justify-content: center; padding: 30px; color: var(--text-3); }

.dd { display: flex; flex-direction: column; gap: 10px; }
.dd-row { display: flex; gap: 14px; align-items: baseline; font-size: 13px; }
.dd-k { width: 80px; color: var(--text-3); flex-shrink: 0; }
.dd-sec { font-size: 12px; color: var(--accent-2); letter-spacing: 0.06em; margin: 8px 0 2px; padding-bottom: 5px; border-bottom: 1px solid var(--hairline); }
.dd-params { display: flex; flex-direction: column; gap: 6px; }
.dd-param { display: flex; justify-content: space-between; padding: 4px 0; }
.dd-pk { color: var(--text-3); }
.dd-empty { color: var(--text-3); font-size: 13px; }
.dd-fail { color: var(--crit); }

.flow-note { padding: 16px 22px; }
.flow { display: flex; align-items: center; gap: 14px; margin-top: 12px; color: var(--text-3); }
.fl { display: flex; flex-direction: column; font-size: 14px; color: var(--text-1); }
.fl i { font-size: 11px; color: var(--text-3); font-style: normal; }
</style>
