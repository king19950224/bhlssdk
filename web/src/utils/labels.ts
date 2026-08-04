// 机器码到中文名的前端对照表，全项目唯一一份（同 utils/preload.ts 的 STATUS_META 那样收口）。
// 这几张表原先在 4 个页面各存一份，标定方法已经漂出了"正式载荷标定 / 正式载荷"
// "GB 单波法 / GB单波""快速标定 / 快速"这些不一致的叫法——同一条标定记录，
// 换个页面看就换个名字。
//
// 内容必须与后端 src/BoltPreload.Api/Endpoints/DisplayLabels.cs 的同名词表逐字一致：
// 同一条记录在页面上和导出的 CSV / 报告里不能是两个叫法。改一边忘另一边，
// code-audit.py 的 G7 门禁会逐条核对并拦下。
//
// 通道硬件状态特意不放这里：设备页用"健康 / 无数据"说的是信号质量，
// 点位表用"在位 / 无信号"说的是现场这一路有没有插探头，两套叫法是有意分开的
// （理由写在 DisplayLabels.cs 的 HardwareStates 上方）。

/** 告警类型 → 中文。与后端 DisplayLabels.AlarmTypes 对齐。 */
const ALARM_TYPE_LABELS: Record<string, string> = {
  preload_low: '预紧力偏低',
  preload_high: '预紧力偏高',
  preload_loss: '预紧力脱落',
  signal_lost: '信号丢失',
  wiring_suspect: '接线疑似变动',
  no_data: '数据中断',
}

/** 标定方法 → 中文。与后端 DisplayLabels.CalibrationMethods 对齐。 */
const CALIBRATION_METHOD_LABELS: Record<string, string> = {
  load_calibrated: '正式载荷标定',
  gb_single_wave: 'GB 单波法',
  gb_bi_wave: 'GB 双波法',
  torque_fit: '扭矩阶梯拟合',
  torque_estimate: '扭矩估算',
  quick: '快速标定',
  sim_baseline: '初始基线',
}

/** 告警类型中文名。没登记的码原样显示，不要显示成空白——排障时要看得见是哪个码。 */
export function alarmTypeLabel(code: string): string {
  return ALARM_TYPE_LABELS[code] ?? code
}

/**
 * 标定方法中文名。空值给破折号：报告和列表里"这颗没标定过"要和
 * "标定方法不认识"分得开（与后端 DisplayLabels.CalibrationMethod 同口径）。
 */
export function calibrationMethodLabel(code: string | null | undefined): string {
  if (!code) return '—'
  return CALIBRATION_METHOD_LABELS[code] ?? code
}
