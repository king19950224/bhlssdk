namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 库里存的是 load_calibrate_countersign、no_data 这类机器码，交到客户手上的东西不能带着它们出去。
/// 报告、CSV 导出、审计页面要显示的中文名都在这里，全系统就这一份。
///
/// 以前前端自己维护了一份动作对照表，后端加动作时没人记得去补，
/// 结果 user_change_password、wiring_import 这些直接以英文出现在审计页面上。
/// 现在由后端出中文名，前端照着显示。
///
/// 加新的动作码/状态码时这里必须一起补，<c>DisplayLabelsTests</c> 会拦住漏补的。
/// </summary>
public static class DisplayLabels
{
    private static readonly Dictionary<string, string> AuditActions = new(StringComparer.Ordinal)
    {
        // 标定
        ["load_calibrate_submit"] = "提交正式标定",
        ["load_calibrate_countersign"] = "标定会签",
        ["load_calibrate_reject"] = "标定驳回",
        ["quick_calibrate"] = "快速标定",
        ["torque_calibrate"] = "扭矩标定",
        ["apply_torque_fit"] = "应用扭矩拟合",
        ["torque_point_capture"] = "采集扭矩点",
        ["calibration_renew"] = "标定续期",

        // 参数变更审批
        ["config_change_approve"] = "参数审批",
        ["config_change_reject"] = "参数驳回",
        ["config_change_execute"] = "参数执行",
        ["config_change_verify"] = "参数验证",

        // 告警处置
        ["alarm_ack"] = "告警确认",
        ["alarm_clear"] = "告警闭合",
        ["alarm_shelve"] = "告警抑制",
        ["alarm_shelve_cancel"] = "取消抑制",

        // 告警阈值规则（两步异人复核）
        ["alarm_rule_propose"] = "提案告警阈值",
        ["alarm_rule_approve"] = "批准告警阈值",
        ["alarm_rule_reject"] = "驳回告警阈值",

        // 出站通知（渠道/规则配置与发件箱处置）
        ["notification_channel_create"] = "新建通知渠道",
        ["notification_channel_update"] = "更新通知渠道",
        ["notification_channel_delete"] = "删除通知渠道",
        ["notification_rule_create"] = "新建通知规则",
        ["notification_rule_update"] = "更新通知规则",
        ["notification_rule_delete"] = "删除通知规则",
        ["notification_test_send"] = "通知测试发送",
        ["notification_retry"] = "通知手工重发",

        // 通道与螺栓
        ["sync_channel_config"] = "同步通道配置",
        ["move_bolt_channel"] = "螺栓换通道",
        ["bolt_move_channel"] = "螺栓换通道",
        ["channel_bind"] = "绑定通道",
        ["channel_unbind"] = "解绑通道",
        ["channel_commission"] = "调试期直发参数",
        ["channel_recalibrate"] = "重标定基线",
        ["channel_rename"] = "通道重命名",
        ["channel_accept"] = "通道验收",
        ["create_bolt"] = "螺栓建档",
        ["update_bolt"] = "更新螺栓信息",
        ["update_bolt_spec"] = "更新螺栓规格",
        ["update_target_preload"] = "更新目标预紧力",
        ["update_group_config"] = "更新部位配置",

        // 设备与点位
        ["device_default_group_update"] = "设置默认监测部位",
        ["device_channel_catalog_update"] = "重建通道目录",
        ["device_create"] = "新建设备",
        ["device_update"] = "更新设备参数",
        ["device_enable"] = "启用设备",
        ["device_disable"] = "禁用设备",
        ["device_delete"] = "删除设备",
        ["restore_multichannel_loading"] = "恢复多通道轮询",
        ["wiring_import"] = "导入点位表",

        // 报告与账号
        ["report_sign"] = "报告签发",
        ["user_create"] = "创建/重置用户",
        ["user_toggle"] = "启停用户",
        ["user_change_password"] = "修改密码",

        // 审计自身（导出也要留痕，§9.5）
        ["audit_export"] = "导出审计日志",

        // 系统级事件：采集入库降级窗口结束后由 sink 补写（user_id 为空，见 PostgresMeasurementSink）
        ["system_degraded"] = "采集降级窗口",
    };

    private static readonly Dictionary<string, string> AuditTargetTypes = new(StringComparer.Ordinal)
    {
        ["channel"] = "通道",
        ["bolt"] = "螺栓",
        ["alarm"] = "告警",
        ["alarm_shelving"] = "告警抑制",
        ["alarm_rule"] = "告警阈值规则",
        ["notification_channel"] = "通知渠道",
        ["notification_rule"] = "通知规则",
        ["notification_outbox"] = "通知发件箱",
        ["calibration"] = "标定",
        ["report"] = "报告",
        ["user"] = "用户",
        ["device"] = "设备",
        ["monitor_group"] = "监测部位",
        ["audit_log"] = "审计日志",
        ["system"] = "系统",
    };

    // 用接线语境的叫法（"在位"而不是"健康"）：点位表是照着现场线缆核对的，
    // 关心的是这一路到底有没有探头。设备页那边用"健康"，是在说信号质量，别混。
    //
    // no_probe 现在没有任何代码会写：探头扫描的 verdict 映射只到 healthy/noisy/suspect/no_data
    // （见 deploy/sql/0013_channel_probe_scan.sql 的映射表）。留着是因为词表只增不删——
    // 老库里存过的值也得翻得出中文，别为了"看着没人用"把它删了。
    private static readonly Dictionary<string, string> HardwareStates = new(StringComparer.Ordinal)
    {
        ["healthy"] = "在位",
        ["noisy"] = "噪声",
        ["suspect"] = "可疑",
        ["no_data"] = "无信号",
        ["no_probe"] = "无探头",
        ["unknown"] = "未知",
    };

    private static readonly Dictionary<string, string> AcceptanceMethods = new(StringComparer.Ordinal)
    {
        ["sequential"] = "顺序接入",
        ["loopcheck"] = "拔插确认",
        ["manual"] = "人工登记",
    };

    // 告警类型会拼进审计备注，跟着 CSV 一起导出去，所以后端也得有一份中文。
    // 用词跟前端告警中心保持一致，同一条告警在页面上和导出表里不该是两个叫法。
    //
    // 产生方各在一处：preload_* 在 StaticThresholdAlarmEvaluator，signal_lost 在 SignalHealthService，
    // wiring_suspect 在 WiringGuardService，clock_drift 在 TimeSyncService。
    // 加新告警类型时四处要一起改（本表、前端 labels.ts、DisplayLabelsTests 的清单、AlarmEndpoints 的可抑制白名单），漏了前两处 G7 门禁会拦。
    //
    // no_data 没有任何评估器会产生，它是 channels.hardware_state 的取值不是告警类型；
    // 保留同样是因为词表只增不删，老库里若存过这个 type 也得翻得出中文。
    private static readonly Dictionary<string, string> AlarmTypes = new(StringComparer.Ordinal)
    {
        ["preload_low"] = "预紧力偏低",
        ["preload_high"] = "预紧力偏高",
        ["preload_loss"] = "预紧力脱落",
        ["signal_lost"] = "信号丢失",
        ["wiring_suspect"] = "接线疑似变动",
        ["clock_drift"] = "时钟漂移",
        ["no_data"] = "数据中断",
    };

    /// <summary>
    /// 标定方法 → 可信等级。验收结论直接依赖它：只有 formal 能记"合格"，
    /// 其余记"合格*"并在报告脚注里说明（见 ReportEndpoints）。
    /// <para>
    /// 收口之前这套判定散在三处且互相矛盾：标定中心把 sim_baseline 落进 unknown，
    /// 验收报告当它是独立的"参考"档，前端 Calibration.vue 又写了一行补丁把 unknown 掰回"参考"。
    /// 同一条标定记录，换个页面看就换个可信级。
    /// </para>
    /// </summary>
    public static string TrustLevel(string method) => method switch
    {
        "load_calibrated" or "gb_single_wave" or "gb_bi_wave" => "formal",
        "torque_fit" => "engineering_estimate",
        "quick" or "torque_estimate" => "temporary",
        // 初始基线不是载荷标定，但也不是"不认识"：它是模拟/演示态的已知来源，单列一档
        "sim_baseline" => "reference",
        _ => "unknown",
    };

    /// <summary>可信等级的中文名。与 TrustLevel 的取值一一对应。</summary>
    public static string TrustLevelLabel(string level) => level switch
    {
        "formal" => "正式",
        "engineering_estimate" => "工程估算",
        "temporary" => "临时",
        "reference" => "参考",
        _ => "未分级",
    };

    /// <summary>
    /// 标定方法 → 该方法下预紧力的标称精度，出处为 plan.md §8.3“三种模型的精度对照”。
    /// <para>
    /// 单列一处的原因：说明书 V1.03 写的是 ±3%，而 ±3% 对应双波法，本系统在 model=3
    /// 多通道监测设备上拿不到横波声时（见 <c>GbBiWaveCalculator</c> 类注释的实测结论），
    /// ≥M12 螺栓实际全部走单波法。口径散在注释里对不上账，交付时会被业主逐条核，
    /// 所以收敛到这里，由标定中心摘要与验收报告共同引用。
    /// </para>
    /// </summary>
    public static string PrecisionSpec(string? method) => method switch
    {
        "gb_bi_wave" => "±3%（双波法，本机型无横波声时，当前不可用）",
        "load_calibrated" or "gb_single_wave" => "±5%",
        "torque_fit" => "±10%~20%（扭矩法工程估算）",
        "quick" or "torque_estimate" => "±15%~25%（临时，仅看趋势）",
        "sim_baseline" => "不适用（演示基线）",
        _ => "未标定（±15%~25%，简化模型仅看趋势）",
    };

    private static readonly Dictionary<string, string> CalibrationMethods = new(StringComparer.Ordinal)
    {
        ["load_calibrated"] = "正式载荷标定",
        ["gb_single_wave"] = "GB 单波法",
        ["gb_bi_wave"] = "GB 双波法",
        ["torque_fit"] = "扭矩阶梯拟合",
        ["torque_estimate"] = "扭矩估算",
        ["quick"] = "快速标定",
        ["sim_baseline"] = "初始基线",
    };

    /// <summary>各词表已登记的码，供测试核对覆盖率。</summary>
    public static IReadOnlyCollection<string> KnownAuditActions => AuditActions.Keys;

    /// <summary>已登记的审计对象类型码。</summary>
    public static IReadOnlyCollection<string> KnownAuditTargetTypes => AuditTargetTypes.Keys;

    /// <summary>已登记的通道硬件状态码。</summary>
    public static IReadOnlyCollection<string> KnownHardwareStates => HardwareStates.Keys;

    /// <summary>已登记的验收方式码。</summary>
    public static IReadOnlyCollection<string> KnownAcceptanceMethods => AcceptanceMethods.Keys;

    /// <summary>已登记的标定方法码。</summary>
    public static IReadOnlyCollection<string> KnownCalibrationMethods => CalibrationMethods.Keys;

    /// <summary>已登记的告警类型码。</summary>
    public static IReadOnlyCollection<string> KnownAlarmTypes => AlarmTypes.Keys;

    /// <summary>
    /// 查不到就把原码括进来标一句"未登记"，不要直接吐英文、也不要抹成空。
    /// 审计和报告都要能追溯，宁可让人看见一个没登记的码，也不能把这条记录悄悄弄丢。
    /// </summary>
    private static string Lookup(Dictionary<string, string> table, string? code, string kind) =>
        string.IsNullOrEmpty(code) ? ""
        : table.TryGetValue(code, out var label) ? label
        : $"未登记{kind}（{code}）";

    /// <summary>审计动作码转中文。</summary>
    public static string AuditAction(string? code) => Lookup(AuditActions, code, "动作");

    /// <summary>审计对象类型码转中文。</summary>
    public static string AuditTargetType(string? code) => Lookup(AuditTargetTypes, code, "类型");

    /// <summary>通道硬件状态码转中文。</summary>
    public static string HardwareState(string? code) => Lookup(HardwareStates, code, "状态");

    /// <summary>验收方式码转中文。</summary>
    public static string AcceptanceMethod(string? code) => Lookup(AcceptanceMethods, code, "方式");

    /// <summary>告警类型码转中文。传空表示"不限类型"，由调用方给措辞。</summary>
    public static string AlarmType(string? code) => Lookup(AlarmTypes, code, "告警类型");

    /// <summary>标定方法码转中文。空值给破折号，报告里"没标定过"和"标定方法不认识"要能分开。</summary>
    public static string CalibrationMethod(string? code) =>
        string.IsNullOrEmpty(code) ? "—" : Lookup(CalibrationMethods, code, "标定方法");
}
