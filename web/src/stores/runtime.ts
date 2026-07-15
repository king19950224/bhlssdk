import { defineStore } from 'pinia'
import { runtimeApi, type RuntimeResponse } from '../api'
import { onRealtime } from '../api/realtime'

/**
 * 运行时设备状态的单一来源：集中 /api/runtime/devices 轮询，供 AppShell 顶栏与
 * Dashboard「系统状态」共用，避免两个组件各自定时拉取造成的重复请求。
 *
 * - start()/stop() 引用计数：只要还有使用方存活就持续刷新，全部卸载才停。
 * - 标签页隐藏（document.hidden）时跳过轮询，可见时立即补刷。
 * - 订阅 SignalR（OnMeasurement / OnAlarm）做去抖刷新，轮询仅作兜底。
 */

const POLL_MS = 30000
// OnMeasurement 推送很密集，用 5s 去抖把高频推送合并成最多每 5s 一次刷新，避免打爆接口
const DEBOUNCE_MS = 5000

let timer: number | undefined
let debounceTimer: number | undefined
let refCount = 0
let inFlight = false
let lastTotal = 0
let lastTs = 0
let unsubs: (() => void)[] = []
let visBound = false

function scheduleRefresh() {
  if (debounceTimer) return
  debounceTimer = window.setTimeout(() => {
    debounceTimer = undefined
    if (!document.hidden) void useRuntime().refresh()
  }, DEBOUNCE_MS)
}

function onVisibility() {
  if (!document.hidden) void useRuntime().refresh()
}

export const useRuntime = defineStore('runtime', {
  state: () => ({
    online: 0,
    total: 0,
    walPending: 0,
    rate: 0,
    license: null as RuntimeResponse['license'] | null,
    items: [] as RuntimeResponse['items'],
    loaded: false,
    error: null as string | null,
  }),
  actions: {
    async refresh() {
      if (inFlight) return
      inFlight = true
      try {
        const rt = await runtimeApi.devices()
        const now = Date.now()
        const total = rt.items.reduce((a, x) => a + (x.channelResultCount ?? 0), 0)
        // 采集速率：相邻两次累计计数差分（计数器重置时夹到 0，避免负值）
        this.rate = lastTs
          ? Math.max(0, Math.round(((total - lastTotal) / ((now - lastTs) / 1000)) * 10) / 10)
          : 0
        lastTotal = total
        lastTs = now
        this.online = rt.items.filter((x) => x.state === 'Connected').length
        this.total = rt.count
        this.walPending = rt.wal?.pending ?? 0
        this.license = rt.license
        this.items = rt.items
        this.error = null
      } catch (e) {
        this.error = e instanceof Error ? e.message : '设备状态刷新失败'
        // 拉不到运行时状态就不能继续宣称设备在线——顶栏"1/1 在线"挂着而后端已死是假安全
        this.online = 0
        this.rate = 0
      } finally {
        this.loaded = true
        inFlight = false
      }
    },
    start() {
      refCount++
      if (refCount > 1) return
      void this.refresh()
      timer = window.setInterval(() => {
        if (!document.hidden) void this.refresh()
      }, POLL_MS)
      if (!visBound) {
        document.addEventListener('visibilitychange', onVisibility)
        visBound = true
      }
      unsubs.push(onRealtime('OnMeasurement', scheduleRefresh))
      unsubs.push(onRealtime('OnAlarm', scheduleRefresh))
    },
    stop() {
      refCount = Math.max(0, refCount - 1)
      if (refCount > 0) return
      if (timer) {
        clearInterval(timer)
        timer = undefined
      }
      if (debounceTimer) {
        clearTimeout(debounceTimer)
        debounceTimer = undefined
      }
      if (visBound) {
        document.removeEventListener('visibilitychange', onVisibility)
        visBound = false
      }
      unsubs.forEach((u) => u())
      unsubs = []
    },
  },
})
