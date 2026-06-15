// 螺栓状态 + 预紧力变化程度的统一模型。
// 状态口径与服务端 bolt_status CTE 一致（仅看安全下限 min_preload_n），
// 在此之上补充"变化程度"的量化指标：残余率 / 损失 / 安全裕度 / 松弛分级。

export type PreloadStatus = 'normal' | 'warn' | 'loose' | 'uncalibrated' | 'nodata'

export const STATUS_META: Record<PreloadStatus, { label: string; color: string; cls: string }> = {
  normal: { label: '正常', color: '#10b981', cls: 'led--ok' },
  warn: { label: '预警', color: '#f59e0b', cls: 'led--warn' },
  loose: { label: '松动', color: '#f43f5e', cls: 'led--crit' },
  uncalibrated: { label: '未标定', color: '#61748f', cls: 'led--off' },
  nodata: { label: '无数据', color: '#3b4659', cls: 'led--off' },
}

export interface PreloadInput {
  calibrated?: boolean
  state?: string | null
  lastTs?: string | null
  current?: number | string | null
  target?: number | string | null
  min?: number | string | null
  staleMinutes?: number
}

export interface RelaxBand {
  band: 'good' | 'watch' | 'significant'
  label: string
  color: string
}

export interface PreloadHealth {
  status: PreloadStatus
  meta: { label: string; color: string; cls: string }
  current: number | null
  target: number | null
  min: number | null
  residualRatio: number | null // 当前 / 目标
  lossN: number | null // 目标 − 当前（>0 为损失）
  lossPct: number | null // 损失 / 目标
  safetyMargin: number | null // (当前 − 下限)/(目标 − 下限)，<0 表示越限
  relax: RelaxBand | null
  reason: string
}

const num = (v: number | string | null | undefined): number | null =>
  v == null || v === '' ? null : Number(v)

export function fmtN(n: number | null | undefined): string {
  return n == null ? '—' : Math.round(Number(n)).toLocaleString('en-US')
}
export function pct(r: number | null | undefined, digits = 0): string {
  return r == null ? '—' : (Number(r) * 100).toFixed(digits) + '%'
}
export function signedN(n: number | null | undefined): string {
  if (n == null) return '—'
  const v = Math.round(Number(n))
  return (v > 0 ? '+' : '') + v.toLocaleString('en-US')
}

export function computePreloadHealth(inp: PreloadInput): PreloadHealth {
  const target = num(inp.target)
  const min = num(inp.min)
  const current = num(inp.current)
  const calibrated = inp.calibrated ?? inp.state === 'calibrated'
  const stale = inp.lastTs ? Date.now() - new Date(inp.lastTs).getTime() > (inp.staleMinutes ?? 10) * 60000 : false

  const residualRatio = current != null && target != null && target > 0 ? current / target : null
  const lossN = current != null && target != null ? target - current : null
  const lossPct = lossN != null && target != null && target > 0 ? lossN / target : null
  let safetyMargin: number | null = null
  if (current != null && min != null && target != null && target > min) safetyMargin = (current - min) / (target - min)

  let status: PreloadStatus
  let reason: string
  if (!calibrated) {
    status = 'uncalibrated'
    reason = '通道未标定,不出具预紧力'
  } else if (current == null || stale) {
    status = 'nodata'
    reason = '无近期测量数据'
  } else if (min != null && current < min) {
    status = 'loose'
    reason = `低于安全下限(当前 ${fmtN(current)} < 下限 ${fmtN(min)})`
  } else if (min != null && current < min * 1.1) {
    status = 'warn'
    reason = `接近下限,安全裕度仅 ${pct(safetyMargin)}`
  } else {
    status = 'normal'
    reason = target != null ? `残余 ${pct(residualRatio)} · 裕度 ${pct(safetyMargin)}` : '运行正常'
  }

  // 松弛程度：变化程度的定性分级（独立于安全状态）。风电螺栓常以损失 10% 作为复拧阈。
  let relax: RelaxBand | null = null
  if (lossPct != null && status !== 'uncalibrated' && status !== 'nodata') {
    if (lossPct < 0.05) relax = { band: 'good', label: '松弛轻微', color: '#10b981' }
    else if (lossPct < 0.1) relax = { band: 'watch', label: '松弛关注', color: '#f59e0b' }
    else relax = { band: 'significant', label: '松弛显著', color: '#f43f5e' }
  }

  return { status, meta: STATUS_META[status], current, target, min, residualRatio, lossN, lossPct, safetyMargin, relax, reason }
}
