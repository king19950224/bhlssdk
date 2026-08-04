// SignalR 实时通道：第一次有人订阅才连，断了自动重连，登出就关掉。收到的消息按事件类型分给各页面。
import * as signalR from '@microsoft/signalr'
import { ref } from 'vue'
import { getAuthToken } from './client'

// SignalR 推送事件 payload（与后端 SignalRMeasurementPublisher 严格对齐）
export interface OnMeasurement {
  type: string; deviceId: number; ts: string; channel: number
  statusCode: number; echoTimeNs: number | null; spTempC: number | null; gain: number; hasWave: boolean
}
export interface OnAlarm {
  action: string; boltId: number; channelId: number; type: string; severity: string; ts: string
  preloadN: number | null; preloadRatio: number | null; targetPreloadN: number | null
  thresholdLow: number | null; thresholdHigh: number | null; consecutive: number; message: string | null
}
export interface OnDeviceParameter {
  deviceId: number; ts: string; address: number; channel: number | null; paramName: string; valueText: string | null
}
/** 探头热插拔在位变化（initial=true 是进程启动后的首次确认，前端只刷状态不弹提示） */
export interface OnChannelPresence {
  deviceId: number; channelNo: number; present: boolean; ts: string; initial: boolean
  channelId: number | null; boltId: number | null; boltCode: string | null
}
interface EventMap {
  OnMeasurement: OnMeasurement
  OnAlarm: OnAlarm
  OnDeviceParameter: OnDeviceParameter
  OnChannelPresence: OnChannelPresence
}

/** 连接状态，供 UI 显示“实时”指示灯。 */
export const realtimeState = ref<'disconnected' | 'connecting' | 'connected'>('disconnected')

let connection: signalR.HubConnection | null = null
let starting: Promise<void> | null = null

/**
 * 重连退避：前几次快速试，覆盖交换机重启、Wi-Fi 抖动这类几秒到十几秒的波动；
 * 之后固定 30 秒一直试下去。
 *
 * 关键是**永不放弃**。SignalR 的默认策略是 [0, 2s, 10s, 30s, null]，末尾的 null
 * 意味着约 42 秒后彻底停止重连并触发 onclose，此后不会再自愈，只能靠人刷新页面。
 * 对 7×24 的监测系统这是最难发现的一种失效：页面还在、图表还在，数字却冻在
 * 断线那一刻，指示灯虽然变成"实时断开"，但没人会一直盯着指示灯看。
 */
const RETRY_DELAYS_MS = [0, 2000, 5000, 10000]
const RETRY_STEADY_MS = 30000

function retryDelay(attempt: number): number {
  return RETRY_DELAYS_MS[attempt] ?? RETRY_STEADY_MS
}

/** 首次连接失败后的重试计时器（withAutomaticReconnect 只管连上之后的掉线，管不了首连失败）。 */
let startRetryTimer: ReturnType<typeof setTimeout> | null = null
let startAttempt = 0
/** 显式登出标志。置位后不再自动重试，否则登出了还在后台重连，徒劳刷 401。 */
let stoppedByUser = false

/**
 * 安排一次重连。用于两种 withAutomaticReconnect 覆盖不到的情况：
 * 首次 start() 失败，以及连接被彻底关闭后触发的 onclose。
 */
function scheduleReconnect(): void {
  if (stoppedByUser || startRetryTimer !== null) return
  if (!getAuthToken()) return
  const delay = retryDelay(startAttempt++)
  startRetryTimer = setTimeout(() => {
    startRetryTimer = null
    void startRealtime()
  }, delay)
}

// 内部统一存成 (p: unknown) => void，订阅入口做类型收窄，规避联合函数调用的 TS 限制。
const handlers: Record<string, Set<(p: unknown) => void>> = {
  OnMeasurement: new Set(),
  OnAlarm: new Set(),
  OnDeviceParameter: new Set(),
  OnChannelPresence: new Set(),
}

function build(): signalR.HubConnection {
  const c = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/measurements', { accessTokenFactory: () => getAuthToken() ?? '' })
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => retryDelay(ctx.previousRetryCount) })
    .configureLogging(signalR.LogLevel.Warning)
    .build()

  for (const evt of Object.keys(handlers)) {
    c.on(evt, (p: unknown) => handlers[evt].forEach((h) => { try { h(p) } catch { /* 单订阅异常不影响其它 */ } }))
  }
  c.onreconnecting(() => { realtimeState.value = 'connecting' })
  c.onreconnected(() => { realtimeState.value = 'connected'; startAttempt = 0 })
  c.onclose(() => {
    realtimeState.value = 'disconnected'
    // 上面的无限重试策略下正常走不到这里；真走到了说明连接被彻底关掉
    // （握手被拒、token 过期等），仍然要自己爬起来，绝不停在"断开"不动。
    scheduleReconnect()
  })
  return c
}

/** 启动实时连接（已连接则忽略；无 token 不连）。连不上会按退避一直重试到连上或登出。 */
export async function startRealtime(): Promise<void> {
  if (!getAuthToken()) return
  stoppedByUser = false
  if (connection && connection.state === signalR.HubConnectionState.Connected) return
  if (starting) return starting
  connection ??= build()
  realtimeState.value = 'connecting'
  starting = connection
    .start()
    .then(() => { realtimeState.value = 'connected'; startAttempt = 0 })
    .catch((e) => {
      realtimeState.value = 'disconnected'
      if (import.meta.env.DEV) console.warn('SignalR 连接失败', e)
      // 首连失败也要接着试：后端比前端晚起来几秒是常态，
      // 原先这里只把状态置为断开就完事，页面从此不会再连上。
      scheduleReconnect()
    })
    .finally(() => { starting = null })
  return starting
}

/** 关闭实时连接（登出 / 401 时调用）。置停止标志，避免关掉后又被自动重连拉起来。 */
export async function stopRealtime(): Promise<void> {
  stoppedByUser = true
  if (startRetryTimer !== null) { clearTimeout(startRetryTimer); startRetryTimer = null }
  startAttempt = 0
  const c = connection
  connection = null
  realtimeState.value = 'disconnected'
  if (c) { try { await c.stop() } catch { /* ignore */ } }
}

/** 订阅一类实时事件。返回的是退订函数，组件 onUnmounted 里记得调，不然会漏。 */
export function onRealtime<K extends keyof EventMap>(evt: K, handler: (p: EventMap[K]) => void): () => void {
  const h = handler as (p: unknown) => void
  handlers[evt].add(h)
  return () => { handlers[evt].delete(h) }
}
