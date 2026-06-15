<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { runtimeApi, deviceApi, type RuntimeDevice, type ChannelHealth } from '../api'

const devices = ref<RuntimeDevice[]>([])
const selected = ref<RuntimeDevice | null>(null)
const channels = ref<ChannelHealth[]>([])
const loading = ref(false)
let timer: number | undefined

async function loadDevices() {
  const rt = await runtimeApi.devices()
  devices.value = rt.items
  if (!selected.value && rt.items.length) selectDevice(rt.items[0])
}
async function selectDevice(d: RuntimeDevice) {
  selected.value = d
  loading.value = true
  try {
    channels.value = (await deviceApi.channelHealth(d.deviceId)).items
  } finally {
    loading.value = false
  }
}
onMounted(() => {
  loadDevices()
  timer = window.setInterval(() => selected.value && selectDevice(selected.value), 8000)
})
onUnmounted(() => timer && clearInterval(timer))

function fmt(n: number) {
  return n >= 1000 ? (n / 1000).toFixed(1) + 'k' : String(n)
}
</script>

<template>
  <div class="dev rise">
    <!-- 设备卡 -->
    <div class="dev-cards">
      <div v-for="d in devices" :key="d.deviceId" class="dc panel" :class="{ active: selected?.deviceId === d.deviceId }" @click="selectDevice(d)">
        <div class="dc-hd">
          <span class="led" :class="d.state === 'Connected' ? 'led--ok' : 'led--crit'" />
          <span class="dc-name">{{ d.name }}</span>
          <el-tag v-if="d.isPrimary" size="small" effect="dark" class="ptag">主</el-tag>
        </div>
        <div class="dc-state mono">{{ d.state }}</div>
        <div class="dc-metrics">
          <div><b class="mono">{{ d.sessionChannels?.length ?? 0 }}</b><span>通道</span></div>
          <div><b class="mono">{{ fmt(d.channelResultCount ?? 0) }}</b><span>测量</span></div>
          <div><b class="mono" :style="d.writeFailureCount ? 'color:var(--warn)' : ''">{{ d.writeFailureCount ?? 0 }}</b><span>写失败</span></div>
        </div>
      </div>
      <div v-if="!devices.length" class="empty panel">暂无设备运行时</div>
    </div>

    <!-- 通道健康 -->
    <div class="panel panel--glow" v-if="selected">
      <div class="panel-hd">
        <span class="panel-title">{{ selected.name }} · 通道健康</span>
        <span class="hd-right mono">{{ channels.length }} 通道 · 近 5 分钟</span>
      </div>
      <div class="tbl">
        <div class="tr th"><span>CH</span><span>螺栓</span><span>链路</span><span>测量</span><span>EchoTime</span><span>温度</span><span>SNR</span><span>峰峰值</span></div>
        <div v-for="c in channels" :key="c.channelId" class="tr">
          <span class="mono">{{ c.channelNo }}</span>
          <span class="mono">{{ c.bolt?.code || '未绑定' }}</span>
          <span class="hw"><span class="led" :class="c.measurement.healthy ? 'led--ok' : (c.measurement.total > 0 ? 'led--warn' : 'led--off')" />{{ c.hardwareState }}</span>
          <span class="mono">{{ c.measurement.total }} <i class="dim">/ {{ c.measurement.echoRatePct }}%</i></span>
          <span class="mono">{{ c.measurement.avgEchoNs != null ? Number(c.measurement.avgEchoNs).toFixed(0) : '—' }}</span>
          <span class="mono">{{ c.measurement.avgTempC != null ? Number(c.measurement.avgTempC).toFixed(1) + '°C' : '—' }}</span>
          <span class="mono">{{ c.latestWave.snrDb != null ? Number(c.latestWave.snrDb).toFixed(1) : '—' }}</span>
          <span class="mono">{{ c.latestWave.peakToPeak ?? '—' }}</span>
        </div>
        <div v-if="!loading && !channels.length" class="tbl-empty">该设备暂无通道数据</div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.dev { display: flex; flex-direction: column; gap: 16px; }
.dev-cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 14px; }
.dc { padding: 16px 18px; cursor: pointer; transition: all 0.18s; }
.dc:hover { border-color: var(--panel-border-strong); }
.dc.active { border-color: var(--accent); box-shadow: 0 0 22px -10px var(--accent-glow); }
.dc-hd { display: flex; align-items: center; gap: 9px; }
.dc-name { font-weight: 600; font-size: 14px; flex: 1; }
.ptag { background: rgba(34, 211, 238, 0.16) !important; border-color: var(--panel-border-strong) !important; color: var(--accent) !important; }
.dc-state { font-size: 11px; color: var(--text-3); margin: 4px 0 14px; letter-spacing: 0.08em; }
.dc-metrics { display: flex; justify-content: space-between; }
.dc-metrics > div { display: flex; flex-direction: column; gap: 3px; }
.dc-metrics b { font-size: 17px; }
.dc-metrics span { font-size: 11px; color: var(--text-3); }

.panel-hd { display: flex; align-items: center; justify-content: space-between; padding: 16px 20px 6px; }
.hd-right { font-size: 12px; color: var(--text-3); }
.tbl { padding: 4px 18px 16px; }
.tr { display: grid; grid-template-columns: 50px 110px 130px 120px 100px 80px 70px 80px; align-items: center; padding: 10px 6px; border-bottom: 1px solid var(--hairline); font-size: 12.5px; }
.tr.th { color: var(--text-3); font-size: 11px; letter-spacing: 0.08em; }
.hw { display: flex; align-items: center; gap: 8px; }
.dim { color: var(--text-3); }
.empty, .tbl-empty { padding: 30px; text-align: center; color: var(--text-3); }
.empty { grid-column: 1 / -1; }
</style>
