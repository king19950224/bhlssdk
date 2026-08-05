import { http } from './client'

// ---- 鉴权 ----
export interface LoginResponse {
  accessToken: string
  userId: number
  username: string
  role: string
}
export const authApi = {
  login: (username: string, password: string) =>
    http.post<LoginResponse>('/api/auth/login', { username, password }),
  // 服务端吊销令牌（登出后旧令牌立即失效,而不是等自然过期）
  logout: () => http.post<{ loggedOut: boolean }>('/api/auth/logout'),
  // 自助改密（改后所有旧令牌失效,须重新登录）
  changePassword: (oldPassword: string, newPassword: string) =>
    http.post<{ changed: boolean }>('/api/auth/change-password', { oldPassword, newPassword }),
}

// ---- 运行时 / 设备 ----
export interface RuntimeDevice {
  deviceId: number
  name: string
  isPrimary: boolean
  state: string
  // 运行时选项（含物理通道数 channelCount，热插拔全通道轮询范围 1..N）
  options: { mode: string; communicationParameters: string; address: number; transferMode: number; model: number; channelCount: number }
  lastEnvelopeAt: string | null
  lastChannelResultAt: string | null
  envelopeCount: number
  channelResultCount: number
  writeFailureCount: number
  sessionChannels: { address: number; channel: number; probeFreqMhz: number; measMode: number }[]
}
export interface RuntimeResponse {
  count: number
  wal: { enabled: boolean; pending: number }
  license: {
    valid: boolean
    found: boolean
    licensedTo: string | null
    expiresAt: string | null
    maxDevices: number | null
    maxChannels: number | null
    currentDevices: number
    currentChannels: number
    overDeviceLimit: boolean
    overChannelLimit: boolean
    warning: string | null
  }
  items: RuntimeDevice[]
}
export const runtimeApi = {
  devices: () => http.get<RuntimeResponse>('/api/runtime/devices'),
}

// ---- 告警 ----
export interface AlarmItem {
  id: number
  boltId: number
  boltCode: string
  channelId: number
  type: string
  severity: string
  triggeredAt: string
  acknowledgedAt: string | null
  clearedAt: string | null
  preloadN: number | null
  preloadRatio: number | null
  targetPreloadN: number | null
  thresholdLow: number | null
  thresholdHigh: number | null
  consecutive: number
  message: string | null
  clearedReason: string | null
  ackedBy: string | null
  clearedBy: string | null
  ackedNote: string | null
  // 条件最近一次命中时刻（null=旧数据）：区分"仍在越限"与"已缓解未闭合"
  lastActiveAt: string | null
  isOpen: boolean
  isAcked: boolean
}
export interface AlarmSummaryRow {
  severity: string
  type: string
  open: number
  openUnacked: number
  total: number
}
export interface AlarmShelving {
  id: number
  boltId: number | null
  boltCode: string | null
  alarmType: string | null
  shelvedAt: string
  expiresAt: string
  reason: string
  shelvedBy: number | null
  shelvedByName: string | null
}
export const alarmApi = {
  list: (params = '') => http.get<{ count: number; items: AlarmItem[] }>(`/api/alarms/${params}`),
  summary: () => http.get<{ items: AlarmSummaryRow[] }>('/api/alarms/summary'),
  ack: (id: number, note?: string) => http.post(`/api/alarms/${id}/ack`, { note }),
  clear: (id: number, reason?: string) => http.post(`/api/alarms/${id}/clear`, { reason }),
  // 维护时段抑制某 bolt+type 的告警（hours 1..168，reason ≥5 字）
  shelve: (body: { boltId?: number; alarmType?: string; hours: number; reason: string }) =>
    http.post<{ shelvingId: number; expiresAt: string }>('/api/alarms/shelve', body),
  // 生效中的抑制（可见可管理，避免"被静默的告警"成为盲区）
  shelvings: () => http.get<{ count: number; items: AlarmShelving[] }>('/api/alarms/shelvings'),
  cancelShelve: (id: number) => http.post(`/api/alarms/shelve/${id}/cancel`),
}

// ---- 告警阈值规则（三级 scope + 两步异人复核，出厂兜底在 appsettings） ----
export interface AlarmRule {
  id: number
  scope: 'global' | 'group' | 'bolt'
  groupId: number | null
  groupCode: string | null
  boltId: number | null
  boltCode: string | null
  triggerLowRatio: number
  clearLowRatio: number
  triggerHighRatio: number
  clearHighRatio: number
  triggerConsecutive: number
  clearConsecutive: number
  criticalDeviation: number
  preloadLossRatio: number
  reason: string
  reviewState: 'pending' | 'active' | 'rejected' | 'superseded'
  createdBy: number
  createdByName: string | null
  createdAt: string
  reviewedByName: string | null
  reviewedAt: string | null
}
export interface AlarmRuleProposeBody {
  scope: 'global' | 'group' | 'bolt'
  groupId?: number | null
  boltId?: number | null
  triggerLowRatio: number
  clearLowRatio: number
  triggerHighRatio: number
  clearHighRatio: number
  triggerConsecutive: number
  clearConsecutive: number
  criticalDeviation: number
  preloadLossRatio: number
  reason: string
}
// 某颗螺栓生效的阈值及来源级别；source=default 表示库里没有可用规则、走 appsettings 出厂值
export interface EffectiveAlarmRule {
  boltId: number
  source: 'bolt' | 'group' | 'global' | 'default'
  ruleId: number | null
  triggerLowRatio: number
  clearLowRatio: number
  triggerHighRatio: number
  clearHighRatio: number
  triggerConsecutive: number
  clearConsecutive: number
  criticalDeviation: number
  preloadLossRatio: number
  reason: string | null
  approvedAt: string | null
}
export interface AlarmRuleTargets {
  groups: { id: number; code: string; name: string }[]
  bolts: { id: number; code: string; groupCode: string }[]
}
export const alarmRuleApi = {
  list: () => http.get<{ count: number; items: AlarmRule[] }>('/api/alarm-rules/'),
  targets: () => http.get<AlarmRuleTargets>('/api/alarm-rules/targets'),
  propose: (body: AlarmRuleProposeBody) => http.post<{ id: number; reviewState: string }>('/api/alarm-rules/', body),
  approve: (id: number) => http.post<{ id: number; reviewState: string }>(`/api/alarm-rules/${id}/approve`),
  reject: (id: number, note?: string) => http.post<{ id: number; reviewState: string }>(`/api/alarm-rules/${id}/reject`, { note }),
  effective: (boltId: number) => http.get<EffectiveAlarmRule>(`/api/bolts/${boltId}/effective-alarm-rule`),
}

// ---- 出站通知（渠道 / 规则 / 发件箱）----
export interface NotificationChannel {
  id: number
  name: string
  kind: string
  url: string | null
  format: string | null
  hasSecret: boolean // secret 永不回显，只报有无
  minIntervalSeconds: number
  enabled: boolean
  createdAt: string
  updatedAt: string
  ruleCount: number
}
export interface NotificationChannelBody {
  name: string
  kind: string
  url: string
  format: string
  // 更新时：undefined/null=保留现有，空串=清除，有值=替换
  secret?: string | null
  minIntervalSeconds: number
  enabled: boolean
}
export interface NotificationRule {
  id: number
  channelId: number
  channelName: string
  minSeverity: string
  alarmTypes: string[] | null // null = 全部类型
  groupId: number | null // null = 全站
  groupCode: string | null
  notifyOnClear: boolean
  quietStart: string | null // HH:mm，服务器本地时区；critical 无视静默
  quietEnd: string | null
  enabled: boolean
}
export interface NotificationRuleBody {
  channelId: number
  minSeverity: string
  alarmTypes?: string[] | null
  groupId?: number | null
  notifyOnClear: boolean
  quietStart?: string | null
  quietEnd?: string | null
  enabled: boolean
}
export interface NotificationOutboxItem {
  id: number
  alarmId: number
  channelId: number
  channelName: string
  eventKind: string // trigger / clear
  status: string // pending / sent / dead
  attempts: number
  nextAttemptAt: string
  lastError: string | null
  createdAt: string
  sentAt: string | null
  boltCode: string | null
  message: string | null
}
export const notificationApi = {
  channels: () => http.get<{ count: number; items: NotificationChannel[] }>('/api/notification-channels/'),
  createChannel: (body: NotificationChannelBody) => http.post<{ id: number }>('/api/notification-channels/', body),
  updateChannel: (id: number, body: NotificationChannelBody) =>
    http.patch<{ id: number; updated: boolean }>(`/api/notification-channels/${id}`, body),
  deleteChannel: (id: number) => http.delete<{ id: number; deleted: boolean }>(`/api/notification-channels/${id}`),
  // 直发测试消息（不入发件箱），发送失败返回 502 带对端错误
  testChannel: (id: number) => http.post<{ ok: boolean }>(`/api/notification-channels/${id}/test`),
  rules: () => http.get<{ count: number; items: NotificationRule[] }>('/api/notification-rules/'),
  createRule: (body: NotificationRuleBody) => http.post<{ id: number }>('/api/notification-rules/', body),
  updateRule: (id: number, body: NotificationRuleBody) =>
    http.patch<{ id: number; updated: boolean }>(`/api/notification-rules/${id}`, body),
  deleteRule: (id: number) => http.delete<{ id: number; deleted: boolean }>(`/api/notification-rules/${id}`),
  outbox: (limit = 50, status?: string) =>
    http.get<{ count: number; items: NotificationOutboxItem[] }>(
      `/api/notification-outbox/?limit=${limit}${status ? `&status=${status}` : ''}`),
  retryOutbox: (id: number) => http.post<{ id: number; requeued: boolean }>(`/api/notification-outbox/${id}/retry`),
}

// ---- 参数变更审批 ----
export interface ConfigChange {
  id: number
  deviceId: number
  channelId: number
  channelNo: number
  status: string
  requestedBy: number | null
  requestedAt: string
  approvedBy: number | null
  approvedAt: string | null
  executedBy: number | null
  executedAt: string | null
  verifiedAt: string | null
  failureReason: string | null
  note: string | null
  requested: string | null
  requestedByName: string | null
  approvedByName: string | null
  executedByName: string | null
}
export interface ConfigVerifyResult {
  id: number
  status: string
  verified?: boolean
  note?: string
  comparisons: { param: string; requested: number; actual: number | null; match: boolean }[]
}
export const configChangeApi = {
  // “全部”= 不传 status（后端把未知值当精确匹配,传 all 会永远查空）
  list: (status = 'pending') =>
    http.get<{ count: number; items: ConfigChange[] }>(
      `/api/config-changes${status && status !== 'all' ? `?status=${status}` : ''}`),
  approve: (id: number) => http.post(`/api/config-changes/${id}/approve`),
  reject: (id: number, reason: string) => http.post(`/api/config-changes/${id}/reject`, { reason }),
  execute: (id: number) => http.post(`/api/config-changes/${id}/execute`),
  verify: (id: number) => http.post<ConfigVerifyResult>(`/api/config-changes/${id}/verify`),
  // 提交通道超声参数变更（落审批队列：requested → approved → executed → verified）
  submit: (deviceId: number, channel: number, updates: Record<string, number | undefined>) =>
    http.post<{ submitted: boolean; requestId: number; status: string }>(
      `/api/devices/${deviceId}/diagnostic/configure-channel/${channel}`, updates),
}

// ---- 用户管理 ----
export interface UserRow {
  id: number; username: string; fullName: string | null; role: string
  enabled: boolean; lastLoginAt: string | null; createdAt: string; locked: boolean
}
export const userApi = {
  list: () => http.get<{ count: number; items: UserRow[] }>('/api/auth/users'),
  create: (body: { username: string; password: string; role: string; fullName?: string }) =>
    http.post<{ id: number; username: string; role: string }>('/api/auth/users', body),
  toggle: (id: number) => http.post<{ id: number; username: string; enabled: boolean }>(`/api/auth/users/${id}/toggle`),
}

// ---- 总览(预紧力健康) ----
/** 各健康状态的螺栓数量汇总(区别于 utils/preload.ts 中单颗螺栓的 PreloadHealth 评估结果)。 */
export interface PreloadHealthCounts {
  total: number
  normal: number
  warn: number
  loose: number
  uncalibrated: number
  nodata: number
  high: number
}
export interface LooseBolt {
  boltCode: string
  turbine: string
  group: string
  preloadN: number | null
  preloadRatio: number | null
  minPreloadN: number | null
  status: string
  boltId: number
}
export interface ResidualDist { b100: number; b90: number; b80: number; b70: number; b70d: number; bhigh: number }
export interface ApproachingBolt {
  boltCode: string; turbine: string; group: string
  preloadN: number | null; minPreloadN: number | null; preloadRatio: number | null
  boltId: number; ratePerDay: number | null; daysToLimit: number | null; r2: number | null
}
export interface DashboardTrendPoint {
  ts: string
  avgRatio: number | null
  minRatio: number | null
  count: number
  activeBolts: number
  alarms: number
}
export const dashboardApi = {
  preloadHealth: () =>
    http.get<{
      global: PreloadHealthCounts
      byGroup: { turbine: string; group: string; health: PreloadHealthCounts }[]
      topLoose: LooseBolt[]
      openAlarms: number
      alarmsBySeverity: { severity: string; count: number }[]
      residualDist: ResidualDist
      approachingLimit: ApproachingBolt[]
    }>('/api/dashboard/preload-health'),
  trend: (hours = 24) =>
    http.get<{ bucketMinutes: number; hours: number; points: DashboardTrendPoint[] }>(`/api/dashboard/trend?hours=${hours}`),
}

// ---- 资产层级 ----
export interface ProjectNode {
  id: number; code: string; name: string; industry: string; location: string | null
  turbines: number; health: PreloadHealthCounts; openAlarms: number
}
export interface TurbineNode {
  id: number; code: string; name: string; model: string | null; capacityKw: number | null
  location: string | null; state: string; groups: number; health: PreloadHealthCounts; openAlarms: number
}
export interface GroupNode {
  id: number; code: string; name: string; structureType: string | null
  expectedBoltCount: number | null; targetPreloadN: number | null; health: PreloadHealthCounts; openAlarms: number
}
export interface GroupBolt {
  id: number; code: string; nominalDiam: string; grade: string; positionLabel: string | null
  boltState: string; targetPreloadN: number | null; minPreloadN: number | null
  preloadN: number | null; preloadRatio: number | null; lastTs: string | null; status: string
  channelId: number; channelNo: number; deviceId: number; deviceCode: string; openAlarms: number
}
// 新建螺栓建档（规格字段可空，后端按 nominalDiam/grade 预设补齐）
export interface BoltCreateBody {
  channelId: number
  code?: string
  nominalDiam?: string
  grade?: string
  positionLabel?: string
  targetPreloadN?: number
  minPreloadN?: number
  crossSectionMm2?: number
  effectiveLengthMm?: number
  totalLengthMm?: number
  backfillUnbound?: boolean
  note?: string
}
export interface BoltCreateResult {
  boltId: number; code: string; groupId: number
  channelId: number; channelNo: number; deviceId: number
  positionLabel: string | null; backfilled: number
}
export const assetApi = {
  projects: () => http.get<{ count: number; items: ProjectNode[] }>('/api/projects'),
  turbines: (projectId: number) => http.get<{ count: number; items: TurbineNode[] }>(`/api/projects/${projectId}/turbines`),
  groups: (turbineId: number) => http.get<{ count: number; items: GroupNode[] }>(`/api/turbines/${turbineId}/groups`),
  groupBolts: (groupId: number) => http.get<{ count: number; items: GroupBolt[] }>(`/api/monitor-groups/${groupId}/bolts`),
  // 建档并绑定通道（含暂存数据回填），一个事务
  createBolt: (groupId: number, body: BoltCreateBody) =>
    http.post<BoltCreateResult>(`/api/monitor-groups/${groupId}/bolts`, body),
  // 修改组配置孔位数（法兰实际孔数与配置不符时现场自助修正）
  patchGroup: (groupId: number, body: { expectedBoltCount: number }) =>
    http.patch<{ groupId: number; code: string; expectedBoltCount: number; previous: number | null }>(
      `/api/monitor-groups/${groupId}`, body),
}

// ---- 螺栓详情 ----
export interface TrendPoint {
  ts: string; avgPreloadN: number | null; minPreloadN: number | null; maxPreloadN: number | null
  avgRatio: number | null; avgTempC: number | null; avgEchoNs: number | null; n: number; nValid: number
}
export interface BoltListItem {
  id: number; code: string; nominalDiam: string; grade: string; targetPreloadN: number | null
  positionLabel: string | null; state: string; channelId: number; channelNo: number
  calibrated: boolean; kL: number | null; refTimeNs: number | null; refTempC: number | null
  lastTs: string | null; lastPreloadN: number | null; lastRatio: number | null
  lastEchoTimeNs: number | null; lastSpTempC: number | null; minPreloadN: number | null
  material: string | null; elasticModulusGpa: number | null; soundVelocityMs: number | null
  effectiveLengthMm: number | null; crossSectionMm2: number | null; installTorqueNm: number | null
}
export interface BoltSettings {
  targetPreloadN?: number | null; minPreloadN?: number | null; material?: string | null
  elasticModulusGpa?: number | null; soundVelocityMs?: number | null; effectiveLengthMm?: number | null
  crossSectionMm2?: number | null; installTorqueNm?: number | null; positionLabel?: string | null
}
export interface CalibRecord {
  id: string; method: string; kL: number; kLUncertainty: number; kLInterceptNs: number | null
  rSquared: number; refTimeNs: number; refTempC: number; passed: boolean
  createdAt: string; expiresAt: string; supersededBy: string | null; isActive: boolean; notes: string | null
  // 复核状态：pending=待会签 / self_signed=自签临时 / approved=两人会签 / rejected
  reviewState: string; countersigned: boolean
  // 提交人（会签必须异人,前端据此禁用本人会签按钮）
  operatorUserId: number | null
}
export const boltApi = {
  list: () => http.get<{ count: number; items: BoltListItem[] }>('/api/bolts'),
  // 详情页用单查（不存在 404 抛错），避免"全量拉列表再 find"在规模超过列表分页护栏后静默失配
  get: (id: number) => http.get<BoltListItem>(`/api/bolts/${id}`),
  preloadTrend: (id: number, qs = '?bucket=auto') => http.get<{ items: TrendPoint[] }>(`/api/bolts/${id}/preload-trend${qs}`),
  measurements: (id: number, limit = 30) =>
    http.get<{ items: { ts: string; echoTimeNs: number | null; spTempC: number | null; statusCode: number; preloadN: number | null; preloadRatio: number | null }[] }>(
      `/api/measurements/by-bolt/${id}/latest?limit=${limit}`),
  calibrations: (channelId: number) => http.get<{ items: CalibRecord[] }>(`/api/channels/${channelId}/calibrations`),
  updateSettings: (id: number, body: BoltSettings) => http.post<{ boltId: number; code: string; updated: boolean }>(`/api/bolts/${id}/settings`, body),
  channelConfig: (id: number) => http.get<BoltChannelConfig>(`/api/bolts/${id}/channel-config`),
  prediction: (id: number, windowDays = 7) => http.get<BoltPrediction>(`/api/bolts/${id}/prediction?windowDays=${windowDays}`),
}
export interface BoltPrediction {
  boltId: number; windowDays: number; sampleCount: number
  current: number | null; target: number | null; min: number | null
  ratePerDay: number | null; r2: number | null
  trend: 'falling' | 'rising' | 'stable' | 'insufficient'
  daysToLimit: number | null
}
export interface BoltChannelConfig {
  deviceId: number; channelNo: number; probeFreqMhz: number; gain: number; threshold: number; measMode: number
  echoWinNs: number | null; tcMode: number; refTimeNs: number | null; refTempC: number | null
  kL: number | null; kLUncertainty: number | null; configSource: string | null; configSyncedAt: string | null
}

// ---- 设备 / 通道健康 ----
export interface ChannelHealth {
  channelId: number; channelNo: number; state: string; samplingEnabled: boolean
  hardwareState: string; businessState: string
  bolt: { id: number; code: string } | null
  measurement: { total: number; withEcho: number; echoRatePct: number; avgEchoNs: number | null; avgTempC: number | null; lastTs: string | null; healthy: boolean }
  latestWave: { snrDb: number | null; peakToPeak: number | null; noiseRms: number | null }
}
export const deviceApi = {
  channelHealth: (deviceId: number, minutes = 5) =>
    http.get<{ deviceId: number; connectorState: string; count: number; items: ChannelHealth[] }>(
      `/api/devices/${deviceId}/channels/health?minutes=${minutes}`),
  // 重标定：把当前回波声时设为零载/参考基线（设备侧整组写入 RefTime/RefTemp）
  recalibrate: (deviceId: number, channel: number, body: { refTimeNs?: number; refTempC?: number } = {}) =>
    http.post<{ newRefTimeNs: number; newRefTempC: number }>(`/api/devices/${deviceId}/channels/${channel}/recalibrate`, body),
  // 点位与验收页数据源：各通道最新回波 + 信号强度 + 在位/验收状态
  channelsLive: (deviceId: number) =>
    http.get<{ deviceId: number; count: number; items: ChannelLive[] }>(`/api/devices/${deviceId}/channels/live`),
  // 下发设备工作模式（Standby / MultiMeas / Reboot ...；扫描期间会被拒绝）
  setMode: (deviceId: number, mode: string, opts: { channel?: number; address?: number } = {}) => {
    const qs = new URLSearchParams()
    if (opts.channel != null) qs.set('channel', String(opts.channel))
    if (opts.address != null) qs.set('address', String(opts.address))
    const q = qs.toString()
    return http.post<{ triggered: boolean; deviceId: number; mode: string }>(
      `/api/devices/${deviceId}/diagnostic/set-mode/${mode}${q ? `?${q}` : ''}`)
  },
  // 重建通道目录（reviewer+）：更新 channel_count、复活被删通道、补插缺失行，运行时即时生效
  rebuildChannelCatalog: (deviceId: number, channelCount: number, note?: string) =>
    http.post<ChannelCatalogRebuildResult>(`/api/devices/${deviceId}/channel-catalog/`, { channelCount, note }),
  // 设备设置：默认监测组（探头在位自动建档的归属组）+ 同项目可选组列表
  getSettings: (deviceId: number) =>
    http.get<DeviceSettings>(`/api/devices/${deviceId}/settings/`),
  // 设置默认监测组（reviewer+）；设置成功后后端会对当前在位未建档的通道立即补建档
  patchSettings: (deviceId: number, defaultGroupId: number | null) =>
    http.patch<DeviceSettingsPatchResult>(`/api/devices/${deviceId}/settings/`, { defaultGroupId }),
}
export interface DeviceSettings {
  deviceId: number; projectId: number
  defaultGroupId: number | null; defaultGroupCode: string | null
  availableGroups: { id: number; code: string; name: string }[]
}
export interface DeviceSettingsPatchResult {
  deviceId: number; defaultGroupId: number | null; defaultGroupCode: string | null
  enrolledBolts: { channelNo: number; boltId: number; code: string }[]
}
export interface ChannelCatalogRebuildResult {
  deviceId: number; channelCount: number; previousChannelCount: number
  resurrectedChannels: number[]; createdChannels: number[]; extraActiveChannels: number
  runtimeUpdated: boolean; sessionProvisioned: boolean; mcrReconfigured: boolean
  warning: string | null
}

// ---- 通道清单 / 绑定 ----
export interface ChannelRow {
  id: number; deviceId: number; channelNo: number; state: string
  firstSeenAt: string | null; lastSeenAt: string | null
  kL: number | null; refTimeNs: number | null; refTempC: number | null
  boltId: number | null; boltCode: string | null
}
export interface UnboundSummaryRow {
  deviceId: number; channelId: number; channelNo: number; channelState: string
  count: number; firstTs: string; lastTs: string; validEchoCount: number
  /** 在位状态机维护的硬件状态——"检测到探头"引导只认 healthy/noisy/suspect，暂存行数≠有信号 */
  hardwareState: string
}
export const channelApi = {
  list: (opts: { state?: string; deviceId?: number } = {}) => {
    const qs = new URLSearchParams()
    if (opts.state) qs.set('state', opts.state)
    if (opts.deviceId != null) qs.set('deviceId', String(opts.deviceId))
    const q = qs.toString()
    return http.get<{ count: number; items: ChannelRow[] }>(`/api/channels${q ? `?${q}` : ''}`)
  },
  unboundSummary: () => http.get<{ items: UnboundSummaryRow[] }>('/api/channels/unbound/summary'),
  unbind: (channelId: number, note?: string) =>
    http.post<{ unbound: boolean; channelId: number; boltId: number }>(`/api/channels/${channelId}/unbind-bolt`, { note }),
}
export interface ChannelLive {
  channelNo: number; boltId: number | null; boltCode: string | null; positionLabel: string | null
  echoNs: number | null; tempC: number | null; lastTs: string | null
  peakToPeak: number | null; snrDb: number | null
  // 点位与验收页增删螺栓用：通道 id（建档绑定/解绑入参）+ 在位状态 + 验收时间
  channelId: number; channelName: string | null; hardwareState: string; lastSeenAt: string | null
  acceptedAt: string | null
  /** 曾在位标记（仅在位事件写入）：失联=建档且曾在位但当前无信号；待接入=建档但从未在位 */
  lastPresentAt: string | null
  /** 目标预紧力；自动建档缺省为空 →“待补规格” */
  targetPreloadN: number | null
}

// ---- 点位与验收 ----
export interface AcceptanceSessionView {
  active: boolean; deviceId: number
  channels?: number[]; startedAt?: string; expiresAt?: string
  remainingSeconds?: number; estimatedRefreshSeconds?: number; startedByUserId?: number
}
export interface AcceptanceItem {
  channelNo: number; channelId: number; channelName: string | null
  boltId: number | null; boltCode: string | null
  acceptedAt: string | null; method: string | null; cableLabel: string | null
  tofNs: number | null; acceptedBy: string | null; note: string | null
}
export interface AcceptResult {
  acceptanceId: number; deviceId: number; channelNo: number; channelId: number; method: string
  boltId: number | null; boltCode: string | null; tofNs: number | null
  peakToPeak: number | null; snrDb: number | null; positionLabel: string | null; warning: string | null
}
// 点位表一行（CSV 前 7 列解析结果）
export interface WiringRowBody {
  channelNo: number; positionLabel?: string; boltCode?: string
  nominalDiam?: string; grade?: string; targetPreloadN?: number; minPreloadN?: number
}
export interface WiringImportResult {
  deviceId: number; total: number; updatedPositions: number
  createdBolts: { channelNo: number; boltId: number; code: string }[]
  skippedBound: number[]; backfilled: number
}
export const acceptanceApi = {
  // 验收会话：MCR 临时缩到待验收通道集，插拔确认提速（reviewer+；TTL 过期自动恢复全通道轮询）
  session: (deviceId: number) => http.get<AcceptanceSessionView>(`/api/devices/${deviceId}/acceptance-session/`),
  startSession: (deviceId: number, body: { channels?: number[]; ttlMinutes?: number } = {}) =>
    http.post<AcceptanceSessionView>(`/api/devices/${deviceId}/acceptance-session/`, body),
  endSession: (deviceId: number) =>
    http.delete<{ ended: boolean; fullPollingRestored?: boolean; note?: string }>(`/api/devices/${deviceId}/acceptance-session/`),
  // 确认验收：sequential=顺序接入 / loopcheck=拔插确认 / manual=人工登记；落记录 + 声时指纹快照
  accept: (deviceId: number, channelNo: number, body: { method: string; cableLabel?: string; positionLabel?: string; note?: string }) =>
    http.post<AcceptResult>(`/api/devices/${deviceId}/channels/${channelNo}/accept`, body),
  list: (deviceId: number) =>
    http.get<{ deviceId: number; total: number; accepted: number; items: AcceptanceItem[] }>(`/api/devices/${deviceId}/acceptance/`),
  reportUrl: (deviceId: number) => `/api/devices/${deviceId}/acceptance/export.csv`,
  // 点位表：导出 CSV（前 7 列可修改后回导）；导入（reviewer+，全或无事务，可批量建档）
  wiringExportUrl: (deviceId: number) => `/api/devices/${deviceId}/wiring-table/export.csv`,
  importWiring: (deviceId: number, body: { rows: WiringRowBody[]; groupId?: number; createBolts?: boolean; note?: string }) =>
    http.post<WiringImportResult>(`/api/devices/${deviceId}/wiring-table/import`, body),
}

// ---- 波形诊断 ----
export interface WaveFeature {
  id: number; boltId: number | null; channelId: number; channelNo: number | null; ts: string; pointCount: number
  threshold: number | null; peakToPeak: number | null; rmsValue: number | null
  noiseRms: number | null; snrDb: number | null; waveUrl: string
}
export interface WaveFile { fc: number; echoStart: number; clockInt: number; threshold: number | null; pointCount: number; truncated: boolean; waveData: number[] }
// 波形对比诊断（§8.6）：当前波形 vs 通道参考波形
export interface WaveCompareWave {
  featureId?: number; ts: string; echoStart: number; clockInt: number
  threshold: number | null; pointCount: number; snrDb?: number | null; peakToPeak?: number | null
  waveData: number[]
}
export interface WaveCompareMetrics {
  similarity: number; bestLagNs: number; zeroLagCorrelation: number
  attenuationRatio: number; healthScore: number
  scoreSnr: number; scoreSimilarity: number; scoreAttenuation: number
}
export interface WaveCompareResult {
  channelId: number; hasCurrent: boolean; hasReference: boolean
  referenceFeatureId?: number | null
  current?: WaveCompareWave; reference?: WaveCompareWave; metrics?: WaveCompareMetrics
  // 文件在但解析不了（断电可能留下截断的 JSON）。这条和 hasCurrent=false 一起回来，
  // 但含义完全不同：不是"该通道没采到波形"，别把两者显示成同一句话。
  waveError?: string | null
}
export const waveApi = {
  features: (limit = 50) => http.get<{ count: number; items: WaveFeature[] }>(`/api/waves/features/recent?limit=${limit}`),
  featuresByBolt: (boltId: number, limit = 1) =>
    http.get<{ count: number; items: WaveFeature[] }>(`/api/waves/features/recent?boltId=${boltId}&limit=${limit}`),
  file: (waveUrl: string) => http.get<WaveFile>(waveUrl),
  compare: (channelId: number) => http.get<WaveCompareResult>(`/api/waves/compare/${channelId}`),
  setReference: (featureId: number, note?: string) =>
    http.post<{ referenceWaveId: number; channelId: number; active: boolean }>(`/api/waves/features/${featureId}/set-reference`, { note }),
}

// ---- 报告 ----
export interface ReportItem {
  id: number; reportType: string; title: string; status: string
  subjectType: string | null; subjectId: number | null; fileUrl: string | null; generatedAt: string; notes: string | null
}
export interface FlangeBoltRow {
  code: string; spec: string; position: string | null; channelNo: number
  targetN: number | null; minN: number | null; preloadN: number | null; ratio: number | null
  lastTs: string | null; calibMethod: string | null; trust: string; rSquared: number | null; uncertaintyN: number | null; result: string
}
export interface FlangeSnapshot {
  generatedAt: string; generatedBy: number; standard: string; project: string; turbine: string
  flange: { name: string; code: string; structureType: string | null; targetPreloadN: number | null }
  // uncalibrated/nodata 为新版快照字段（旧报告快照无此字段,前端需兜底）
  summary: { total: number; calibrated: number; passed: number; failed: number; uncalibrated?: number; nodata?: number; overall: string }
  bolts: FlangeBoltRow[]
}
// 扭矩阶梯报告快照（report_type='torque_step'）
export interface TorquePointSnapshot {
  id: number; torqueNm: number; estimatedPreloadN: number; sampleCount: number
  avgEchoTimeNs: number; stddevEchoTimeNs: number | null; avgTempC: number
  normalizedEchoTimeNs: number; deltaTofNs: number; excluded: boolean; excludeReason: string | null; note: string | null
}
export interface TorqueStepSnapshot {
  generatedAt: string
  channel: { channelId: number; deviceId: number; channelNo: number; kL: number | null; kLInterceptNs: number | null; activeCalibrationId: string | null }
  bolt: { boltId: number; code: string; nominalDiameter: string; targetPreloadN: number | null; installTorqueNm: number | null }
  points: TorquePointSnapshot[]
  fit: { pointCount: number; kL: number; interceptNs: number; rSquared: number } | null
  conclusion: string
}
export interface ReportDetail {
  id: number; title: string; reportType: string; status: string; generatedAt: string; notes: string | null
  subjectType: string | null; subjectId: number | null; fileUrl: string | null
  generatedBy: number | null; generatedByName: string | null
  signedAt: string | null; signedBy: number | null; signedByName: string | null
  // 快照按 reportType 区分：flange_acceptance → FlangeSnapshot；torque_step → TorqueStepSnapshot
  snapshot: FlangeSnapshot | TorqueStepSnapshot | null
}
export const reportApi = {
  list: (type = '') => http.get<{ count: number; items: ReportItem[] }>(`/api/reports${type ? `?type=${type}` : ''}`),
  get: (id: number) => http.get<ReportDetail>(`/api/reports/${id}`),
  generateFlange: (groupId: number) =>
    http.post<{ reportId: number; title: string; overall: string; total: number; passed: number; calibrated: number }>(
      `/api/reports/flange-acceptance/${groupId}`),
  // 扭矩阶梯报告：基于通道已采集的扭矩实验点生成 HTML 报告（reviewer+）
  generateTorqueStep: (channelId: number, body: { title?: string; notes?: string } = {}) =>
    http.post<{ reportId: number; title: string; fileUrl: string; downloadUrl: string }>(
      `/api/reports/torque-step/${channelId}`, body),
  sign: (id: number) => http.post<{ reportId: number; status: string; signedBy: number }>(`/api/reports/${id}/sign`),
  // 报告下载（HTML）地址，供 <a> 直接打开/下载
  downloadUrl: (id: number) => `/api/reports/${id}/download`,
}

// ---- 标定中心 ----
export interface CalibMethod {
  method: string; trustLevel: string; total: number; active: number; expired: number; avgR2: number | null
}
export interface TorqueTestPoint {
  id: number; torqueNm: number; nutFactor: number; nominalDiameterMm: number; estimatedPreloadN: number
  sampleCount: number; avgEchoTimeNs: number; stddevEchoTimeNs: number | null; avgTempC: number
  normalizedEchoTimeNs: number; deltaTofNs: number; note: string | null; createdAt: string
  excluded: boolean; excludeReason: string | null
}
export interface TorqueFitSummary {
  channelId: number; pointCount: number; kLByLinearFitNsPerN: number | null; interceptNs: number | null
  rSquared: number | null; minTorqueNm: number | null; maxTorqueNm: number | null
}
export interface LoadCalibratePoint { forceN: number; deltaTofNs: number }
export const calibrationApi = {
  summary: () => http.get<{ methods: CalibMethod[]; policy: Record<string, string> }>('/api/calibration-center/summary'),
  // 标定写入工作流（均要求 reviewer+）。channelId 为通道 id。
  // quick / torque / apply-torque-fit 为自签临时标定（review_state=self_signed，立即生效）。
  quickCalibrate: (channelId: number, body: { kL: number; refTimeNs: number; refTempC: number; kLUncertainty?: number; note?: string; expiresDays?: number }) =>
    http.post<{ channelId: number; calibrationId: string }>(`/api/channels/${channelId}/quick-calibrate`, body),
  // sampleWindowSeconds：声时取样窗口。热插拔全通道轮询下单通道刷新率被稀释（40 通道约 0.25Hz），
  // 默认 60s 窗口攒不够 20 条样本，调用方应按实际采样时长传窗口（后端 clamp 5..600）。
  torqueCalibrate: (channelId: number, body: { torqueNm: number; nutFactor?: number; nominalDiameterMm?: number; sampleWindowSeconds?: number; note?: string }) =>
    http.post<{ channelId: number; calibrationId: string; activeCalibration: { kL: number } }>(`/api/channels/${channelId}/torque-calibrate`, body),
  // 扭矩阶梯实验点：采集 / 列表 / 线性拟合摘要 / 应用拟合（含截距）为当前标定
  torquePoints: (channelId: number) => http.get<{ channelId: number; count: number; items: TorqueTestPoint[] }>(`/api/channels/${channelId}/torque-test-points`),
  captureTorquePoint: (channelId: number, body: { torqueNm: number; nutFactor?: number; nominalDiameterMm?: number; sampleWindowSeconds?: number; note?: string }) =>
    http.post<{ id: number; channelId: number; estimatedPreloadN: number }>(`/api/channels/${channelId}/torque-test-points`, body),
  torquePointSummary: (channelId: number) => http.get<TorqueFitSummary>(`/api/channels/${channelId}/torque-test-points/summary`),
  applyTorqueFit: (channelId: number, body: { minPoints?: number; note?: string } = {}) =>
    http.post<{ channelId: number; calibrationId: string; kL: number; rSquared: number; passed: boolean }>(`/api/channels/${channelId}/apply-torque-fit`, body),
  // 正式多点载荷标定（提交后 pending，需异人会签 countersign 才激活，review_state=approved）
  loadCalibrate: (channelId: number, body: { points: LoadCalibratePoint[]; refTimeNs: number; refTempC: number; systematicRelativeUncertainty?: number; loadingDevice?: string; loadingDeviceCert?: string; note?: string }) =>
    http.post<{ calibrationId: string; channelId: number; kL: number; rSquared: number; reviewState: string }>(`/api/channels/${channelId}/load-calibrate`, body),
  countersign: (channelId: number, calibrationId: string) =>
    http.post<{ activated: boolean; calibrationId: string; reviewState: string }>(`/api/channels/${channelId}/calibrations/${calibrationId}/countersign`),
  // 驳回 pending 正式标定（reviewer）：置 rejected，不激活、不动通道
  reject: (channelId: number, calibrationId: string, reason?: string) =>
    http.post<{ rejected: boolean; calibrationId: string; reviewState: string; reason: string }>(
      `/api/channels/${channelId}/calibrations/${calibrationId}/reject${reason ? `?reason=${encodeURIComponent(reason)}` : ''}`,
    ),
}

// ---- 通道自动检测（probe-scan） ----
export interface ProbeScanChannelResult {
  channelNo: number; verdict: string; snrDb: number | null; echoStdNs: number | null
  peakToPeak: number | null; sampleCount: number; reason: string | null
}
export interface ProbeScanView {
  scanRunId: number; deviceId: number; status: string; strategy: string
  requestedChannels: number[]; settleSeconds: number; sampleSeconds: number
  requestedAt: string; startedAt: string | null; finishedAt: string | null
  results: ProbeScanChannelResult[]
}
export const probeScanApi = {
  start: (deviceId: number, body: { channels?: number[]; settleSeconds?: number; sampleSeconds?: number; strategy?: string } = {}) =>
    http.post<{ scanRunId: number; deviceId: number; status: string; estimatedSeconds: number; warning: string }>(
      `/api/devices/${deviceId}/channels/probe-scan/`, body),
  latest: (deviceId: number) => http.get<ProbeScanView>(`/api/devices/${deviceId}/channels/probe-scan/latest`),
  abort: (deviceId: number, scanRunId: number) =>
    http.post<{ aborted: boolean; scanRunId: number }>(`/api/devices/${deviceId}/channels/probe-scan/${scanRunId}/abort`),
}

// ---- 审计日志 ----
export interface AuditEntry {
  id: number
  userId: number | null
  username: string | null
  action: string
  // 中文名由后端 AuditLabels 统一给，前端不再自己维护一份对照表——
  // 以前两边各有一份，后端加了动作前端忘了补，页面上就直接显示英文码了
  actionLabel: string
  targetType: string | null
  targetTypeLabel: string | null
  targetId: number | null
  note: string | null
  at: string
}
export const auditApi = {
  query: (qs = '') => http.get<{ count: number; hasMore: boolean; items: AuditEntry[] }>(`/api/audit-log/${qs}`),
  actions: () => http.get<{ items: { action: string; label: string; count: number }[] }>('/api/audit-log/actions'),
}
