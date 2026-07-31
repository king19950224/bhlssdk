// 螺栓状态 + 预紧力变化程度的统一模型。
// 状态口径与服务端 bolt_status CTE 一致（仅看安全下限 min_preload_n），
// 在此之上补充"变化程度"的量化指标：保持率 / 损失 / 安全裕度 / 松弛分级。

export type PreloadStatus = 'normal' | 'warn' | 'loose' | 'high' | 'uncalibrated' | 'nodata'

// 全项目唯一的螺栓状态元数据（颜色/文案/LED 类）。AssetMonitor / FlangeRadar / HealthBar 等一律引用此表，
// 不再各自复制（历史上三处各写一份、颜色文案还不一致）。
// high(偏高/超张力)：保持率 > 110%，用紫色——与总览"超张力"配色一致，且和 warn 的琥珀(偏低预警)区分开。
export const STATUS_META: Record<PreloadStatus, { label: string; color: string; cls: string }> = {
  normal: { label: '正常', color: '#10b981', cls: 'led--ok' },
  warn: { label: '预警', color: '#f59e0b', cls: 'led--warn' },
  loose: { label: '松动', color: '#f43f5e', cls: 'led--crit' },
  high: { label: '偏高', color: '#a78bfa', cls: 'led--high' },
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

const num = (v: number | string | null | undefined): number | null => {
  if (v == null || v === '') return null
  const n = Number(v)
  return Number.isFinite(n) ? n : null // 挡住 NaN/Infinity，避免脏值流进图表与计算
}

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

/**
 * 把若干计数按总和换算成整数百分比，用最大余数法保证合计恰为 100（避免
 * 各自四舍五入后出现 101% / 99% 的违和）。total 缺省时按 counts 求和；总和为 0 返回全 0。
 */
export function pctParts(counts: number[], total?: number): number[] {
  const sum = total ?? counts.reduce((a, b) => a + b, 0)
  if (!sum) return counts.map(() => 0)
  const raw = counts.map((c) => (c / sum) * 100)
  const out = raw.map((v) => Math.floor(v))
  let remainder = 100 - out.reduce((a, b) => a + b, 0)
  // 余数按小数部分从大到小逐个 +1，分配完为止
  const order = raw
    .map((v, i) => ({ i, frac: v - Math.floor(v) }))
    .sort((a, b) => b.frac - a.frac)
  for (let k = 0; k < order.length && remainder > 0; k++, remainder--) out[order[k].i]++
  return out
}

/** 把时间戳格式化为“N 秒/分钟/小时/天前”，用于“最后已知数据”的陈旧度提示。 */
export function fmtAgo(iso: string | null | undefined, nowMs: number = Date.now()): string {
  if (!iso) return '—'
  const ms = nowMs - new Date(iso).getTime()
  if (!isFinite(ms)) return '—'
  if (ms < 0) return '刚刚'
  const s = Math.floor(ms / 1000)
  if (s < 60) return `${s} 秒前`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m} 分钟前`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h} 小时前`
  return `${Math.floor(h / 24)} 天前`
}

export function computePreloadHealth(inp: PreloadInput, nowMs: number = Date.now()): PreloadHealth {
  const target = num(inp.target)
  const min = num(inp.min)
  const current = num(inp.current)
  const calibrated = inp.calibrated ?? inp.state === 'calibrated'
  const stale = inp.lastTs ? nowMs - new Date(inp.lastTs).getTime() > (inp.staleMinutes ?? 10) * 60000 : false

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
  } else if (residualRatio != null && residualRatio > 1.1) {
    // 偏高/超张力：保持率 > 110%（与服务端 bolt_status CTE 和告警 TriggerHighRatio 口径一致）
    status = 'high'
    reason = `预紧力偏高,当前为目标的 ${pct(residualRatio)}(超张力风险)`
  } else {
    status = 'normal'
    reason = target != null ? `保持 ${pct(residualRatio)} · 裕度 ${pct(safetyMargin)}` : '运行正常'
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
