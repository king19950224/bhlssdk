using BoltPreload.Application.Alarm;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BoltPreload.Tests.Alarm;

/// <summary>
/// StaticThresholdAlarmEvaluator 状态机回归测试。
/// 重点覆盖安全攸关的 preload_loss 路径 + 重启状态恢复（防止重复触发 critical 告警）。
/// </summary>
public class StaticThresholdAlarmEvaluatorTests
{
    private static StaticThresholdAlarmEvaluator NewEvaluator(StaticThresholdAlarmOptions? opt = null) =>
        new(Options.Create(opt ?? new StaticThresholdAlarmOptions()),
            NullLogger<StaticThresholdAlarmEvaluator>.Instance);

    private static AlarmInput Input(decimal ratio, long boltId = 1) =>
        new(BoltId: boltId, ChannelId: 1, Ts: DateTimeOffset.UtcNow,
            PreloadN: ratio * 80000m, PreloadRatio: ratio, TargetPreloadN: 80000m, SdkStatusCode: 0);

    [Fact]
    public void PreloadLoss_FirstTime_TriggersCriticalImmediately()
    {
        var sut = NewEvaluator();

        var events = sut.Evaluate(Input(0.02m));   // ratio ≤ PreloadLossRatio(0.05)

        events.Should().ContainSingle();
        events[0].Action.Should().Be(AlarmAction.Trigger);
        events[0].Type.Should().Be("preload_loss");
        events[0].Severity.Should().Be("critical");
    }

    [Fact]
    public void Restore_PreloadLoss_PreventsDuplicateRetrigger()
    {
        var sut = NewEvaluator();

        // 模拟重启：DB 里有一条开启的 preload_loss，重建内存状态
        sut.Restore(new[] { (1L, "preload_loss") });

        // 重启后第一条仍是脱落 ratio：不应再次触发（修复前 Restore 不认 preload_loss，会重复报 critical）
        var events = sut.Evaluate(Input(0.02m));

        events.Should().BeEmpty("Restore 已把 preload_loss 置为 active，不应重复触发");
    }

    [Fact]
    public void PreloadLoss_Recovery_ClearsAsLossType_NotLow()
    {
        var opt = new StaticThresholdAlarmOptions { ClearConsecutive = 3 };
        var sut = NewEvaluator(opt);

        sut.Evaluate(Input(0.02m)).Should().ContainSingle();   // 触发 preload_loss

        // 连续 ClearConsecutive 条恢复（ratio ≥ ClearLowRatio=0.72）后应闭合
        IReadOnlyList<AlarmEvent> last = System.Array.Empty<AlarmEvent>();
        for (int i = 0; i < opt.ClearConsecutive; i++)
            last = sut.Evaluate(Input(0.95m));

        last.Should().ContainSingle();
        last[0].Action.Should().Be(AlarmAction.Clear);
        last[0].Type.Should().Be("preload_loss",
            "脱落恢复应关闭 preload_loss 行，修复前会错误地发 preload_low 的 Clear");
    }

    [Fact]
    public void PreloadLow_ThenDropsToLoss_EscalatesToLossCritical()
    {
        var sut = NewEvaluator();   // TriggerConsecutive 默认 5

        // 先连续 5 条 ratio<0.70 → 开 preload_low（模拟螺栓渐松）
        IReadOnlyList<AlarmEvent> ev = System.Array.Empty<AlarmEvent>();
        for (int i = 0; i < 5; i++) ev = sut.Evaluate(Input(0.60m));
        ev.Should().ContainSingle();
        ev[0].Type.Should().Be("preload_low");

        // 继续跌进脱落区(≤0.05) → 必须升级：关闭 preload_low + 触发 preload_loss critical
        // （修复前：loss 触发被 !LowActive 挡掉，最危险状态被漏报）
        var esc = sut.Evaluate(Input(0.02m));
        esc.Should().Contain(e => e.Action == AlarmAction.Clear && e.Type == "preload_low");
        esc.Should().Contain(e => e.Action == AlarmAction.Trigger && e.Type == "preload_loss" && e.Severity == "critical");
    }

    [Fact]
    public void Restore_PreloadHigh_PreventsDuplicateRetrigger()
    {
        var opt = new StaticThresholdAlarmOptions();
        var sut = NewEvaluator(opt);
        sut.Restore(new[] { (1L, "preload_high") });

        // 已 active，单条越上限不会立刻再次触发（仍走 off-delay 恢复路径）
        var events = sut.Evaluate(Input(1.20m));

        events.Should().BeEmpty("preload_high 已恢复为 active，不应重复触发");
    }
}
