using System.Text.RegularExpressions;
using BoltPreload.Api.Endpoints;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Api;

/// <summary>
/// 锁住"交出去的东西不带机器码"这条线。
///
/// 报告、CSV、审计页面上的动作名都从 <see cref="DisplayLabels"/> 取。
/// 这套东西最早前端后端各存了一份，后端加了 user_change_password、wiring_import
/// 之类的动作，前端那份没人记得补，客户在审计页面上直接看到英文码。
/// 收到一处之后再用这几条测试钉住：词表不许缺、译名里不许残留 snake_case。
/// </summary>
public class DisplayLabelsTests
{
    // 出现过的动作码，来自代码里的写入点加上真实库里 audit_log 的现存取值。
    // 删词条会让这里挂掉——老审计记录也要能翻译，不能因为代码不再产生就删。
    private static readonly string[] ActionsInUse =
    [
        "alarm_ack", "alarm_clear", "alarm_rule_approve", "alarm_rule_propose", "alarm_rule_reject",
        "alarm_shelve", "alarm_shelve_cancel",
        "apply_torque_fit", "audit_export", "bolt_move_channel", "calibration_renew",
        "channel_accept", "channel_bind", "channel_commission", "channel_recalibrate",
        "channel_rename", "channel_unbind", "config_change_approve", "config_change_execute",
        "config_change_reject", "config_change_verify", "create_bolt",
        "device_channel_catalog_update", "device_create", "device_default_group_update",
        "device_delete", "device_disable", "device_enable", "device_update",
        "load_calibrate_countersign", "load_calibrate_reject", "load_calibrate_submit",
        "move_bolt_channel",
        "notification_channel_create", "notification_channel_delete", "notification_channel_update",
        "notification_retry",
        "notification_rule_create", "notification_rule_delete", "notification_rule_update",
        "notification_test_send",
        "quick_calibrate", "report_sign", "restore_multichannel_loading",
        "sync_channel_config", "system_degraded", "torque_calibrate", "torque_point_capture",
        "update_bolt", "update_bolt_spec", "update_group_config", "update_target_preload",
        "user_change_password", "user_create", "user_toggle", "wiring_import",
    ];

    private static readonly string[] TargetTypesInUse =
    [
        "alarm", "alarm_rule", "alarm_shelving", "bolt", "calibration", "channel", "device", "monitor_group",
        "notification_channel", "notification_outbox", "notification_rule", "report", "system", "user",
    ];

    private static readonly string[] HardwareStatesInUse =
        ["healthy", "noisy", "suspect", "no_data", "no_probe", "unknown"];

    private static readonly string[] AcceptanceMethodsInUse = ["sequential", "loopcheck", "manual"];

    private static readonly string[] CalibrationMethodsInUse =
        ["load_calibrated", "gb_single_wave", "gb_bi_wave", "torque_fit", "torque_estimate", "quick", "sim_baseline"];

    // 告警类型会拼进审计备注跟着 CSV 导出去,后端这份要和前端告警中心对得上。
    // no_data 不是告警类型(它是 hardware_state),列在这里只为锁住那条历史词条不被删。
    private static readonly string[] AlarmTypesInUse =
        ["preload_low", "preload_high", "preload_loss", "signal_lost", "wiring_suspect", "clock_drift", "no_data"];

    // 连着 3 个以上小写字母就当成漏译的机器码。
    // "GB 单波法"里的 GB 是大写缩写，不会被这条抓到。
    private static readonly Regex RawCode = new("[a-z]{3,}", RegexOptions.Compiled);

    public static TheoryData<string, string> AllLabels()
    {
        var data = new TheoryData<string, string>();
        foreach (var c in ActionsInUse) data.Add("动作", c);
        foreach (var c in TargetTypesInUse) data.Add("对象类型", c);
        foreach (var c in HardwareStatesInUse) data.Add("硬件状态", c);
        foreach (var c in AcceptanceMethodsInUse) data.Add("验收方式", c);
        foreach (var c in CalibrationMethodsInUse) data.Add("标定方法", c);
        foreach (var c in AlarmTypesInUse) data.Add("告警类型", c);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllLabels))]
    public void 在用的码都有中文名且不残留机器码(string kind, string code)
    {
        var label = kind switch
        {
            "动作" => DisplayLabels.AuditAction(code),
            "对象类型" => DisplayLabels.AuditTargetType(code),
            "硬件状态" => DisplayLabels.HardwareState(code),
            "验收方式" => DisplayLabels.AcceptanceMethod(code),
            "告警类型" => DisplayLabels.AlarmType(code),
            _ => DisplayLabels.CalibrationMethod(code),
        };

        label.Should().NotContain("未登记", $"{kind} {code} 没在 DisplayLabels 里登记中文名");
        RawCode.IsMatch(label).Should().BeFalse($"{kind} {code} 的译名 \"{label}\" 里还留着机器码");
    }

    [Fact]
    public void 没登记的码保留原码但标成中文而不是直接吐英文()
    {
        // 兜底不能把记录悄悄抹掉：审计要追溯，得让人看见是哪个码没登记。
        var label = DisplayLabels.AuditAction("some_future_action");
        label.Should().StartWith("未登记").And.Contain("some_future_action");
    }

    [Fact]
    public void 标定方法为空时给破折号而不是未登记()
    {
        // 报告里"这颗没标定过"和"标定方法不认识"是两回事，不能混成一个提示。
        DisplayLabels.CalibrationMethod(null).Should().Be("—");
        DisplayLabels.CalibrationMethod("").Should().Be("—");
    }
}
