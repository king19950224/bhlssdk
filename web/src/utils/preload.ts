// 螺栓状态 + 预紧力变化程度的统一模型。
// 状态口径与服务端 bolt_status CTE 一致：安全下限 min_preload_n 之外还看保持率
// （<70% 松动、<90% 预警，与告警评估器 TriggerLowRatio 默认对齐），
// 在此之上补充"变化程度"的量化指标：保持率 / 损失 / 安全裕度 / 松弛分级。
// outofrange 两个来源：服务端按 algo_version 拒算；本地对荒谬换算值（>300% / <5%）兜底——
// 客户端实时换算不过范围门，基线(RefTime)锁错回波时会算出目标几十倍的值，
// 不兜底就会顶着"偏高 4769%"的字面状态上屏（详情页头部与趋势图口径分裂的事故来源）。

export type PreloadStatus = 'normal' | 'warn' | 'loose' | 'high' | 'uncalibrated' | 'nodata' | 'outofrange'

// 螺栓状态元数据（颜色/文案/LED 类）。AssetMonitor / FlangeLayout / HealthBar 等一律引用此表，
// 不再各自复制（历史上三处各写一份、颜色文案还不一致）。
// high(偏高/超张力)：保持率 > 110%，用紫色——与总览"超张力"配色一致，且和 warn 的琥珀(偏低预警)区分开。
// 只有 BigScreen.vue 例外，它自己留了一份：值班室墙上那块屏是隔着距离看的，
// "未标定/过期""无近期数据"这种长说法不容易读错。颜色与本表一致，只有文案更长，改这里记得看一眼那边。
export const STATUS_META: Record<PreloadStatus, { label: string; color: string; cls: string }> = {
  normal: { label: '正常', color: '#10b981', cls: 'led--ok' },
  warn: { label: '预警', color: '#f59e0b', cls: 'led--warn' },
  loose: { label: '松动', color: '#f43f5e', cls: 'led--crit' },
  high: { label: '偏高', color: '#a78bfa', cls: 'led--high' },
  uncalibrated: { label: '未标定', color: '#61748f', cls: 'led--off' },
  nodata: { label: '无数据', color: '#3b4659', cls: 'led--off' },
  // 超范围：回波正常、预紧力换算超出规格可信区间被拒算——要处理的是规格/目标配置或现场载荷，
  // 不是探头链路。橙色与 warn 的琥珀区分开
  outofrange: { label: '超范围', color: '#fb923c', cls: 'led--warn' },
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
  } else if (residualRatio != null && (residualRatio > 3 || residualRatio < 0.05)) {
    status = 'outofrange'
    reason = `换算读数为目标的 ${pct(residualRatio)}，超出可信范围——参考基线或规格配置异常，读数按无效处理`
  } else if (min != null && current < min) {
    status = 'loose'
    reason = `低于安全下限(当前 ${fmtN(current)} < 下限 ${fmtN(min)})`
  } else if (residualRatio != null && residualRatio < 0.7) {
    // 保持率退路(与服务端 CTE / 告警 TriggerLowRatio 0.70 对齐)：min 未配时不能一路判 normal
    status = 'loose'
    reason = `保持率 ${pct(residualRatio)} 低于 70%`
  } else if (min != null && current < min * 1.1) {
    status = 'warn'
    reason = `接近下限,安全裕度仅 ${pct(safetyMargin)}`
  } else if (residualRatio != null && residualRatio < 0.9) {
    status = 'warn'
    reason = `保持率 ${pct(residualRatio)} 低于 90%`
  } else if (residualRatio != null && residualRatio > 1.1) {
    // 偏高/超张力：保持率 > 110%（与服务端 bolt_status CTE 和告警 TriggerHighRatio 口径一致）。
    // 已知的口径时点差异：服务端用测量入库时存的 preload_ratio（按当时目标算），这里用
    // 当前目标现算——修改目标预紧力后到下一条测量入库前的短窗口（采集周期+缓存 5min TTL）内，
    // 两边结论可能不同；窗口一过自然收敛，属有意取舍（前端反映最新目标更符合直觉）。
    status = 'high'
    reason = `预紧力偏高,当前为目标的 ${pct(residualRatio)}(超张力风险)`
  } else {
    status = 'normal'
    reason = target != null ? `保持 ${pct(residualRatio)} · 裕度 ${pct(safetyMargin)}` : '运行正常'
  }

  // 松弛程度：变化程度的定性分级（独立于安全状态）。风电螺栓常以损失 10% 作为复拧阈。
  let relax: RelaxBand | null = null
  if (lossPct != null && status !== 'uncalibrated' && status !== 'nodata' && status !== 'outofrange') {
    if (lossPct < 0.05) relax = { band: 'good', label: '松弛轻微', color: '#10b981' }
    else if (lossPct < 0.1) relax = { band: 'watch', label: '松弛关注', color: '#f59e0b' }
    else relax = { band: 'significant', label: '松弛显著', color: '#f43f5e' }
  }

  return { status, meta: STATUS_META[status], current, target, min, residualRatio, lossN, lossPct, safetyMargin, relax, reason }
}
