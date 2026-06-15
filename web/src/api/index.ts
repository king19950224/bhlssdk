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
}

// ---- 运行时 / 设备 ----
export interface RuntimeDevice {
  deviceId: number
  name: string
  isPrimary: boolean
  state: string
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
  message: string | null
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
export const alarmApi = {
  list: (params = '') => http.get<{ count: number; items: AlarmItem[] }>(`/api/alarms/${params}`),
  summary: () => http.get<{ items: AlarmSummaryRow[] }>('/api/alarms/summary'),
  ack: (id: number, note?: string) => http.post(`/api/alarms/${id}/ack`, { note }),
  clear: (id: number, reason?: string) => http.post(`/api/alarms/${id}/clear`, { reason }),
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
}
export const configChangeApi = {
  list: (status = 'pending') =>
    http.get<{ count: number; items: ConfigChange[] }>(`/api/config-changes?status=${status}`),
  approve: (id: number) => http.post(`/api/config-changes/${id}/approve`),
  reject: (id: number, reason: string) => http.post(`/api/config-changes/${id}/reject`, { reason }),
  execute: (id: number) => http.post(`/api/config-changes/${id}/execute`),
  verify: (id: number) => http.post(`/api/config-changes/${id}/verify`),
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
export interface PreloadHealth {
  total: number
  normal: number
  warn: number
  loose: number
  uncalibrated: number
  nodata: number
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
export interface ResidualDist { b100: number; b90: number; b80: number; b70: number; b70d: number }
export interface ApproachingBolt {
  boltCode: string; turbine: string; group: string
  preloadN: number | null; minPreloadN: number | null; preloadRatio: number | null
  boltId: number; ratePerDay: number | null; daysToLimit: number | null; r2: number | null
}
export const dashboardApi = {
  preloadHealth: () =>
    http.get<{
      global: PreloadHealth
      byGroup: { turbine: string; group: string; health: PreloadHealth }[]
      topLoose: LooseBolt[]
      openAlarms: number
      alarmsBySeverity: { severity: string; count: number }[]
      residualDist: ResidualDist
      approachingLimit: ApproachingBolt[]
    }>('/api/dashboard/preload-health'),
}

// ---- 资产层级 ----
export interface ProjectNode {
  id: number; code: string; name: string; industry: string; location: string | null
  turbines: number; health: PreloadHealth; openAlarms: number
}
export interface TurbineNode {
  id: number; code: string; name: string; model: string | null; capacityKw: number | null
  location: string | null; state: string; groups: number; health: PreloadHealth; openAlarms: number
}
export interface GroupNode {
  id: number; code: string; name: string; structureType: string | null
  expectedBoltCount: number | null; targetPreloadN: number | null; health: PreloadHealth; openAlarms: number
}
export interface GroupBolt {
  id: number; code: string; nominalDiam: string; grade: string; positionLabel: string | null
  boltState: string; targetPreloadN: number | null; minPreloadN: number | null
  preloadN: number | null; preloadRatio: number | null; lastTs: string | null; status: string
  channelId: number; channelNo: number; deviceId: number; deviceCode: string; openAlarms: number
}
export const assetApi = {
  projects: () => http.get<{ count: number; items: ProjectNode[] }>('/api/projects'),
  turbines: (projectId: number) => http.get<{ count: number; items: TurbineNode[] }>(`/api/projects/${projectId}/turbines`),
  groups: (turbineId: number) => http.get<{ count: number; items: GroupNode[] }>(`/api/turbines/${turbineId}/groups`),
  groupBolts: (groupId: number) => http.get<{ count: number; items: GroupBolt[] }>(`/api/monitor-groups/${groupId}/bolts`),
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
}
export const boltApi = {
  list: () => http.get<{ count: number; items: BoltListItem[] }>('/api/bolts'),
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
  // 实时通道识别：各通道最新回波 + 信号强度（敲击识别法）
  channelsLive: (deviceId: number) =>
    http.get<{ deviceId: number; count: number; items: ChannelLive[] }>(`/api/devices/${deviceId}/channels/live`),
}
export interface ChannelLive {
  channelNo: number; boltId: number | null; boltCode: string | null; positionLabel: string | null
  echoNs: number | null; tempC: number | null; lastTs: string | null
  peakToPeak: number | null; snrDb: number | null
}

// ---- 波形诊断 ----
export interface WaveFeature {
  id: number; boltId: number | null; channelId: number; ts: string; pointCount: number
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
  summary: { total: number; calibrated: number; passed: number; failed: number; overall: string }
  bolts: FlangeBoltRow[]
}
export const reportApi = {
  list: (type = '') => http.get<{ count: number; items: ReportItem[] }>(`/api/reports${type ? `?type=${type}` : ''}`),
  get: (id: number) =>
    http.get<{
      id: number; title: string; reportType: string; status: string; generatedAt: string; notes: string | null
      generatedBy: number | null; generatedByName: string | null
      signedAt: string | null; signedBy: number | null; signedByName: string | null
      snapshot: FlangeSnapshot
    }>(`/api/reports/${id}`),
  generateFlange: (groupId: number) =>
    http.post<{ reportId: number; title: string; overall: string; total: number; passed: number; calibrated: number }>(
      `/api/reports/flange-acceptance/${groupId}`),
  sign: (id: number) => http.post<{ reportId: number; status: string; signedBy: number }>(`/api/reports/${id}/sign`),
}

// ---- 标定中心 ----
export interface CalibMethod {
  method: string; trustLevel: string; total: number; active: number; expired: number; avgR2: number | null
}
export const calibrationApi = {
  summary: () => http.get<{ methods: CalibMethod[]; policy: Record<string, string> }>('/api/calibration-center/summary'),
}

// ---- 审计日志 ----
export interface AuditEntry {
  id: number
  userId: number | null
  username: string | null
  action: string
  targetType: string | null
  targetId: number | null
  note: string | null
  at: string
}
export const auditApi = {
  query: (qs = '') => http.get<{ count: number; hasMore: boolean; items: AuditEntry[] }>(`/api/audit-log/${qs}`),
  actions: () => http.get<{ items: { action: string; count: number }[] }>('/api/audit-log/actions'),
}
