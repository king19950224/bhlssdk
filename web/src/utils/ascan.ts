// A-Scan 回波基础解析：绝对时间轴(echoStart + i·clockInt)、峰值、检出点(过阈)、零交叉。
// Diagnostics.vue（带参考对比）与 BoltDetail.vue（带 RefTime/ΔTOF/闸门）共用同一套基础口径，
// 各自在返回值上再叠加各页特有字段，避免两处实现漂移。

export interface AscanInput {
  waveData: number[]
  echoStart: number
  clockInt: number
  threshold: number | null
}

export interface AscanStats {
  /** 波形采样点（与入参同一引用，方便上层直接 map 成 [x,y]） */
  waveData: number[]
  /** 采样间隔 ns/点（clockInt，回退 1 避免除零） */
  clk: number
  /** 首点绝对时间 ns（echoStart，回退 0） */
  x0: number
  /** 点数 */
  n: number
  /** 时间轴下界 = x0 */
  xmin: number
  /** 时间轴上界 = x0 + (n-1)·clk */
  xmax: number
  /** 峰值点索引（按 |幅值|） */
  peakIdx: number
  /** 峰值绝对幅值 */
  peakAbs: number
  /** 检出点索引：首个 |样值| ≥ |阈值|；无阈值/未越限则回退峰值点 */
  detIdx: number
  /** 检出点时间 ns */
  detNs: number
  /** 检出点之后首个过零的亚采样时间 ns（线性插值），无则 null */
  zeroNs: number | null
  /** |阈值|（无则 null） */
  thr: number | null
}

/** 解析一条 A-Scan 波形的基础统计量。空波形返回 null。 */
export function analyzeAscan(w: AscanInput | null | undefined): AscanStats | null {
  if (!w || !w.waveData.length) return null
  const clk = w.clockInt || 1
  const x0 = w.echoStart || 0
  const d = w.waveData
  const n = d.length
  const xmin = x0
  const xmax = x0 + (n - 1) * clk
  const t = (i: number) => x0 + i * clk

  let peakIdx = 0
  let peakAbs = -1
  for (let i = 0; i < n; i++) {
    const a = Math.abs(d[i])
    if (a > peakAbs) { peakAbs = a; peakIdx = i }
  }

  const thr = w.threshold != null ? Math.abs(w.threshold) : null
  let detIdx = -1
  if (thr != null && thr > 0) for (let i = 0; i < n; i++) { if (Math.abs(d[i]) >= thr) { detIdx = i; break } }
  if (detIdx < 0) detIdx = peakIdx
  const detNs = t(detIdx)

  let zeroNs: number | null = null
  for (let i = detIdx; i < n - 1; i++) {
    const a = d[i]
    const b = d[i + 1]
    if ((a <= 0 && b > 0) || (a >= 0 && b < 0)) { zeroNs = t(i) + (a === b ? 0 : a / (a - b)) * clk; break }
  }

  return { waveData: d, clk, x0, n, xmin, xmax, peakIdx, peakAbs, detIdx, detNs, zeroNs, thr }
}
