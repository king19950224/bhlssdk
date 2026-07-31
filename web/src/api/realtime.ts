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
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Warning)
    .build()

  for (const evt of Object.keys(handlers)) {
    c.on(evt, (p: unknown) => handlers[evt].forEach((h) => { try { h(p) } catch { /* 单订阅异常不影响其它 */ } }))
  }
  c.onreconnecting(() => { realtimeState.value = 'connecting' })
  c.onreconnected(() => { realtimeState.value = 'connected' })
  c.onclose(() => { realtimeState.value = 'disconnected' })
  return c
}

/** 启动实时连接（已连接则忽略；无 token 不连）。 */
export async function startRealtime(): Promise<void> {
  if (!getAuthToken()) return
  if (connection && connection.state === signalR.HubConnectionState.Connected) return
  if (starting) return starting
  connection ??= build()
  realtimeState.value = 'connecting'
  starting = connection
    .start()
    .then(() => { realtimeState.value = 'connected' })
    .catch((e) => { realtimeState.value = 'disconnected'; if (import.meta.env.DEV) console.warn('SignalR 连接失败', e) })
    .finally(() => { starting = null })
  return starting
}

/** 关闭实时连接（登出 / 401 时调用）。 */
export async function stopRealtime(): Promise<void> {
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
