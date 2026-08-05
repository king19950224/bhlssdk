using BoltPreload.Application.Alarm;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BoltPreload.Tests.Alarm;

/// <summary>
/// StaticThresholdAlarmEvaluator 状态机回归测试。
/// 重点覆盖安全攸关的 preload_loss 路径 + 重启状态恢复（防止重复触发 critical 告警）。
/// </summary>
public class StaticThresholdAlarmEvaluatorTests
{
    private static StaticThresholdAlarmEvaluator NewEvaluator(StaticThresholdAlarmOptions? opt = null) =>
        new(new FixedAlarmRuleProvider(opt), NullLogger<StaticThresholdAlarmEvaluator>.Instance);

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

    // ===== 上限分支：过拧告警 =====
    // 此前只有 Restore_PreloadHigh_PreventsDuplicateRetrigger 从"已 active"那一侧碰过一下，
    // 触发路径本身从未被走过 —— 也就是说"过拧根本报不出来"这种失效当时测不出来。

    [Fact]
    public void PreloadHigh_FifthConsecutiveSample_Triggers()
    {
        var sut = NewEvaluator();   // TriggerConsecutive 默认 5

        for (int i = 0; i < 4; i++)
            sut.Evaluate(Input(1.20m)).Should().BeEmpty($"第 {i + 1} 条还没攒够 on-delay");

        var ev = sut.Evaluate(Input(1.20m));

        ev.Should().ContainSingle();
        ev[0].Action.Should().Be(AlarmAction.Trigger);
        ev[0].Type.Should().Be("preload_high");
        ev[0].Severity.Should().Be("warn", "偏差 0.20 未达 critical 档的 0.30");
        ev[0].ConsecutiveCount.Should().Be(5);
    }

    [Fact]
    public void PreloadHigh_DeviationExactlyAtCriticalCut_IsCritical()
    {
        // |1 - 1.30| 恰好 0.30，分级判定用 >=，应当升到 critical。差一个等号这里就退成 warn，
        // 而 warn 在现场是不弹窗的。
        var sut = NewEvaluator();

        IReadOnlyList<AlarmEvent> ev = System.Array.Empty<AlarmEvent>();
        for (int i = 0; i < 5; i++) ev = sut.Evaluate(Input(1.30m));

        ev.Should().ContainSingle();
        ev[0].Severity.Should().Be("critical");
    }

    [Fact]
    public void PreloadHigh_StreakInterruptedByNormalSample_DoesNotTrigger()
    {
        // on-delay 要求"连续"。中间夹一条正常值必须清零，否则零星尖峰会慢慢攒成一条误报。
        var sut = NewEvaluator();

        for (int i = 0; i < 4; i++) sut.Evaluate(Input(1.20m));
        sut.Evaluate(Input(1.00m)).Should().BeEmpty();

        for (int i = 0; i < 4; i++)
            sut.Evaluate(Input(1.20m)).Should().BeEmpty("计数已被正常值清零，要重新攒满 5 条");
    }

    [Fact]
    public void PreloadHigh_ClearsAfterOffDelay()
    {
        var opt = new StaticThresholdAlarmOptions { ClearConsecutive = 3 };
        var sut = NewEvaluator(opt);
        for (int i = 0; i < opt.TriggerConsecutive; i++) sut.Evaluate(Input(1.20m));

        IReadOnlyList<AlarmEvent> last = System.Array.Empty<AlarmEvent>();
        for (int i = 0; i < opt.ClearConsecutive; i++) last = sut.Evaluate(Input(1.00m));

        last.Should().ContainSingle();
        last[0].Action.Should().Be(AlarmAction.Clear);
        last[0].Type.Should().Be("preload_high");
    }

    // ===== 阈值边界 =====
    // 触发与恢复之间的滞回带只有 0.02（0.70/0.72、1.10/1.08）。任何一个比较符少写或多写一个等号，
    // 后果要么是恰好压线的螺栓反复触发又恢复地刷屏，要么是该报的不报，而且两种都不会抛异常。

    [Theory]
    [InlineData(0.6999, true, "低于触发下限，判定用 <")]
    [InlineData(0.7000, false, "恰好等于触发下限，不该触发")]
    [InlineData(0.7100, false, "在触发线之上")]
    public void PreloadLow_TriggerBoundary_IsExclusive(decimal ratio, bool shouldTrigger, string why)
    {
        var sut = NewEvaluator();

        IReadOnlyList<AlarmEvent> ev = System.Array.Empty<AlarmEvent>();
        for (int i = 0; i < 5; i++) ev = sut.Evaluate(Input(ratio));

        if (shouldTrigger) ev.Should().ContainSingle(why).Which.Type.Should().Be("preload_low");
        else ev.Should().BeEmpty(why);
    }

    [Theory]
    [InlineData(1.1001, true, "高于触发上限，判定用 >")]
    [InlineData(1.1000, false, "恰好等于触发上限，不该触发")]
    [InlineData(1.0900, false, "在触发线之下")]
    public void PreloadHigh_TriggerBoundary_IsExclusive(decimal ratio, bool shouldTrigger, string why)
    {
        var sut = NewEvaluator();

        IReadOnlyList<AlarmEvent> ev = System.Array.Empty<AlarmEvent>();
        for (int i = 0; i < 5; i++) ev = sut.Evaluate(Input(ratio));

        if (shouldTrigger) ev.Should().ContainSingle(why).Which.Type.Should().Be("preload_high");
        else ev.Should().BeEmpty(why);
    }

    [Theory]
    [InlineData(0.0500, true, "恰好等于脱落阈值，判定用 <= 应当计入")]
    [InlineData(0.0501, false, "略高于阈值就不算脱落，走普通下限的 on-delay")]
    public void PreloadLoss_Boundary_IsInclusive(decimal ratio, bool isLoss, string why)
    {
        var sut = NewEvaluator();

        var ev = sut.Evaluate(Input(ratio));

        if (isLoss) ev.Should().ContainSingle(why).Which.Type.Should().Be("preload_loss");
        else ev.Should().BeEmpty(why);
    }

    [Theory]
    [InlineData(0.7200, true, "恰好等于恢复线，判定用 >= 应当计入")]
    [InlineData(0.7199, false, "仍在滞回带内，不算恢复")]
    public void PreloadLow_ClearBoundary_IsInclusive(decimal ratio, bool shouldClear, string why)
    {
        var opt = new StaticThresholdAlarmOptions { ClearConsecutive = 3 };
        var sut = NewEvaluator(opt);
        for (int i = 0; i < opt.TriggerConsecutive; i++) sut.Evaluate(Input(0.60m));

        IReadOnlyList<AlarmEvent> last = System.Array.Empty<AlarmEvent>();
        for (int i = 0; i < opt.ClearConsecutive; i++) last = sut.Evaluate(Input(ratio));

        if (shouldClear) last.Should().ContainSingle(why).Which.Action.Should().Be(AlarmAction.Clear);
        else last.Should().BeEmpty(why);
    }

    [Theory]
    [InlineData(1.0800, true, "恰好等于恢复线，判定用 <= 应当计入")]
    [InlineData(1.0801, false, "仍在滞回带内，不算恢复")]
    public void PreloadHigh_ClearBoundary_IsInclusive(decimal ratio, bool shouldClear, string why)
    {
        var opt = new StaticThresholdAlarmOptions { ClearConsecutive = 3 };
        var sut = NewEvaluator(opt);
        for (int i = 0; i < opt.TriggerConsecutive; i++) sut.Evaluate(Input(1.20m));

        IReadOnlyList<AlarmEvent> last = System.Array.Empty<AlarmEvent>();
        for (int i = 0; i < opt.ClearConsecutive; i++) last = sut.Evaluate(Input(ratio));

        if (shouldClear) last.Should().ContainSingle(why).Which.Action.Should().Be(AlarmAction.Clear);
        else last.Should().BeEmpty(why);
    }

    // ===== C1：无状态 critical 安全网（WAL 重放兜底，不污染状态机）=====

    [Fact]
    public void CriticalStateless_PreloadLoss_ReturnsCritical()
    {
        var sut = NewEvaluator();
        var ev = sut.EvaluateCriticalStateless(Input(0.02m));
        ev.Should().NotBeNull();
        ev!.Type.Should().Be("preload_loss");
        ev.Severity.Should().Be("critical");
        ev.Action.Should().Be(AlarmAction.Trigger);
    }

    [Fact]
    public void CriticalStateless_SevereLowAndHigh_ReturnCritical()
    {
        var sut = NewEvaluator();
        sut.EvaluateCriticalStateless(Input(0.60m))!.Type.Should().Be("preload_low");   // 偏差 0.40 ≥ 0.30
        sut.EvaluateCriticalStateless(Input(1.40m))!.Type.Should().Be("preload_high");  // 偏差 0.40 ≥ 0.30
    }

    [Fact]
    public void CriticalStateless_NormalOrNoRatio_ReturnsNull()
    {
        var sut = NewEvaluator();
        sut.EvaluateCriticalStateless(Input(0.95m)).Should().BeNull();
        sut.EvaluateCriticalStateless(new AlarmInput(1, 1, DateTimeOffset.UtcNow, null, null, 80000m, 0))
            .Should().BeNull("无 ratio 不应补判");
    }

    [Fact]
    public void CriticalStateless_DoesNotMutateStateMachine()
    {
        var sut = NewEvaluator();
        // 反复无状态评估不得污染 _states：随后有状态评估首条脱落仍应正常触发
        for (int i = 0; i < 10; i++) sut.EvaluateCriticalStateless(Input(0.02m));
        var events = sut.Evaluate(Input(0.02m));
        events.Should().ContainSingle();
        events[0].Type.Should().Be("preload_loss");
    }

    // ===== C2：持久化失败回滚状态机，使后续测量能重试（杜绝永久漏报）=====

    [Fact]
    public void Rollback_AfterTrigger_AllowsImmediateRetrigger()
    {
        var sut = NewEvaluator();   // TriggerConsecutive 默认 5
        IReadOnlyList<AlarmEvent> ev = System.Array.Empty<AlarmEvent>();
        for (int i = 0; i < 5; i++) ev = sut.Evaluate(Input(0.60m));
        ev.Should().ContainSingle();
        ev[0].Type.Should().Be("preload_low");

        // 不回滚：状态机已 active，下一条越限不会再触发
        sut.Evaluate(Input(0.60m)).Should().BeEmpty();

        // 模拟持久化失败 → 回滚 → 下一条越限应立即重新触发
        sut.RollbackTransition(ev[0]);
        var retry = sut.Evaluate(Input(0.60m));
        retry.Should().ContainSingle();
        retry[0].Action.Should().Be(AlarmAction.Trigger);
        retry[0].Type.Should().Be("preload_low");
    }

    [Fact]
    public void Rollback_AfterClear_ReopensSoClearCanRetry()
    {
        var opt = new StaticThresholdAlarmOptions { ClearConsecutive = 3 };
        var sut = NewEvaluator(opt);
        sut.Evaluate(Input(0.02m)).Should().ContainSingle();   // preload_loss active

        IReadOnlyList<AlarmEvent> clear = System.Array.Empty<AlarmEvent>();
        for (int i = 0; i < 3; i++) clear = sut.Evaluate(Input(0.95m));
        clear.Should().ContainSingle();
        clear[0].Action.Should().Be(AlarmAction.Clear);

        // 模拟 Clear 持久化失败 → 回滚重新 active → 下一条恢复样本应再次产生 Clear（关闭正确类型）
        sut.RollbackTransition(clear[0]);
        var retry = sut.Evaluate(Input(0.95m));
        retry.Should().ContainSingle();
        retry[0].Action.Should().Be(AlarmAction.Clear);
        retry[0].Type.Should().Be("preload_loss");
    }
}
