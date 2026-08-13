using BoltPreload.Application.Ingestion;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BoltPreload.Tests.Ingestion;

/// <summary>
/// 探头热插拔在位状态机的单测（纯内存，不碰连接器/DB）。锁定：
/// 1) 插入需连续 N 条【合理且稳定】的回波才触发（防噪声误报），首个确认带 Initial 标记；
/// 2) 无回波 / 杂散短触发 / 声时乱跳都打断连击，重新累计——"有回波"≠"有螺栓"；
/// 3) 拔出由 Sweep 按静默时长判定，未超阈值不误翻；
/// 4) 设备挂起（离线/probe-scan）期间不误判拔出，恢复后静默重新起算；
/// 5) 拔出后重新插入 → 再次 plugged 事件（非 Initial）。
/// </summary>
public class ChannelPresenceTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>典型真机声时（ns）：实验台螺栓 32µs 量级。</summary>
    private const double Echo = 32000;

    private static ChannelPresenceTracker Make(
        int plugConsecutive = 3, int unplugSilence = 20,
        int minPlausibleNs = 2000, int stabilityBandNs = 2000)
        => new(Options.Create(new ChannelPresenceOptions
        {
            Enabled = true,
            PlugConsecutiveEchoes = plugConsecutive,
            UnplugSilenceSeconds = unplugSilence,
            MinPlausibleEchoNs = minPlausibleNs,
            PlugStabilityBandNs = stabilityBandNs,
        }));

    private static List<ChannelPresenceChange> Drain(ChannelPresenceTracker tracker)
    {
        var list = new List<ChannelPresenceChange>();
        while (tracker.Events.TryRead(out var ev)) list.Add(ev);
        return list;
    }

    [Fact]
    public void Plug_RequiresConsecutiveEchoes()
    {
        var t = Make(plugConsecutive: 3);
        t.Observe(1, 5, Echo, T0);
        t.Observe(1, 5, Echo + 20, T0.AddSeconds(1));
        Drain(t).Should().BeEmpty("2 条回波不够 3 条阈值");

        t.Observe(1, 5, Echo + 40, T0.AddSeconds(2));
        var events = Drain(t);
        events.Should().ContainSingle();
        events[0].Should().BeEquivalentTo(new
        {
            DeviceId = 1L, ChannelNo = (ushort)5, Present = true, Initial = true,
        });
    }

    [Fact]
    public void Plug_NoEchoBreaksStreak()
    {
        var t = Make(plugConsecutive: 3);
        t.Observe(1, 5, Echo, T0);
        t.Observe(1, 5, Echo, T0.AddSeconds(1));
        t.Observe(1, 5, null, T0.AddSeconds(2));   // 打断
        t.Observe(1, 5, Echo, T0.AddSeconds(3));
        t.Observe(1, 5, Echo, T0.AddSeconds(4));
        Drain(t).Should().BeEmpty("连击被打断后需重新累计");

        t.Observe(1, 5, Echo, T0.AddSeconds(5));
        Drain(t).Should().ContainSingle();
    }

    [Fact]
    public void Plug_SubFloorEchoTreatedAsNoEcho()
    {
        // 真机空口偶发 µs 级以下的杂散触发（历史数据见 1485ns）——低于下限门按无回波处理
        var t = Make(plugConsecutive: 3, minPlausibleNs: 2000);
        t.Observe(1, 5, 1485, T0);
        t.Observe(1, 5, 1485, T0.AddSeconds(1));
        t.Observe(1, 5, 1485, T0.AddSeconds(2));
        t.Observe(1, 5, 1485, T0.AddSeconds(3));
        Drain(t).Should().BeEmpty("低于 MinPlausibleEchoNs 的杂散不点亮在位");

        // 且会打断真实回波的连击
        t.Observe(1, 5, Echo, T0.AddSeconds(4));
        t.Observe(1, 5, Echo, T0.AddSeconds(5));
        t.Observe(1, 5, 300, T0.AddSeconds(6));   // 杂散打断
        t.Observe(1, 5, Echo, T0.AddSeconds(7));
        t.Observe(1, 5, Echo, T0.AddSeconds(8));
        Drain(t).Should().BeEmpty();
        t.Observe(1, 5, Echo, T0.AddSeconds(9));
        Drain(t).Should().ContainSingle(e => e.Present);
    }

    [Fact]
    public void Plug_ErraticEchoesNeverPlug()
    {
        // 空通道杂散回波 µs~几十 µs 级乱跳（历史数据见 32k/197k 混杂）——过不了稳定性门
        var t = Make(plugConsecutive: 3, stabilityBandNs: 2000);
        t.Observe(1, 5, 32000, T0);
        t.Observe(1, 5, 197000, T0.AddSeconds(1));
        t.Observe(1, 5, 88000, T0.AddSeconds(2));
        t.Observe(1, 5, 5100, T0.AddSeconds(3));
        t.Observe(1, 5, 141000, T0.AddSeconds(4));
        t.Observe(1, 5, 63000, T0.AddSeconds(5));
        Drain(t).Should().BeEmpty("乱跳的回波连不成稳定序列，不判在位");
    }

    [Fact]
    public void Plug_StreakReanchorsAfterJump_ThenStabilizes()
    {
        // 换接另一根螺栓：声时跳到新值后重新稳定 → 以新值为锚重新累计并正常判插入
        var t = Make(plugConsecutive: 3, stabilityBandNs: 2000);
        t.Observe(1, 5, 32000, T0);
        t.Observe(1, 5, 32010, T0.AddSeconds(1));
        t.Observe(1, 5, 90000, T0.AddSeconds(2));   // 跳变：重新起算（本条为新锚）
        t.Observe(1, 5, 90020, T0.AddSeconds(3));
        Drain(t).Should().BeEmpty("新锚后仅 2 条");
        t.Observe(1, 5, 89990, T0.AddSeconds(4));
        Drain(t).Should().ContainSingle(e => e.Present);
    }

    [Fact]
    public void Plug_StabilityBandIsCumulativeOverStreak()
    {
        // 缓慢漂移累计超带宽也要重新起算：带宽按序列 max-min 算，不是相邻差
        var t = Make(plugConsecutive: 4, stabilityBandNs: 2000);
        t.Observe(1, 5, 30000, T0);
        t.Observe(1, 5, 31200, T0.AddSeconds(1));
        t.Observe(1, 5, 32400, T0.AddSeconds(2));   // 与首条差 2400 > 2000 → 以本条重锚
        t.Observe(1, 5, 32600, T0.AddSeconds(3));
        t.Observe(1, 5, 32800, T0.AddSeconds(4));
        Drain(t).Should().BeEmpty("重锚后仅 3 条，不足 4 条阈值");
        t.Observe(1, 5, 33000, T0.AddSeconds(5));
        Drain(t).Should().ContainSingle(e => e.Present);
    }

    [Fact]
    public void Plug_GatesCanBeDisabled()
    {
        // 两道门都关掉：退化为旧行为（只数非空条数）
        var t = Make(plugConsecutive: 3, minPlausibleNs: 0, stabilityBandNs: 0);
        t.Observe(1, 5, 100, T0);
        t.Observe(1, 5, 197000, T0.AddSeconds(1));
        t.Observe(1, 5, 5, T0.AddSeconds(2));
        Drain(t).Should().ContainSingle(e => e.Present);
    }

    [Fact]
    public void Unplug_AfterSilenceThreshold_ViaSweep()
    {
        var t = Make(plugConsecutive: 1, unplugSilence: 20);
        t.Observe(1, 5, Echo, T0);
        Drain(t).Should().ContainSingle(e => e.Present);

        // 未超静默阈值：不翻
        t.Sweep(T0.AddSeconds(10)).Should().BeEmpty();
        // 超阈值：拔出
        var changes = t.Sweep(T0.AddSeconds(21));
        changes.Should().ContainSingle();
        changes[0].Should().BeEquivalentTo(new
        {
            DeviceId = 1L, ChannelNo = (ushort)5, Present = false, Initial = false,
        });
        Drain(t).Should().ContainSingle(e => !e.Present);

        // 已拔出的不重复上报
        t.Sweep(T0.AddSeconds(60)).Should().BeEmpty();
    }

    [Fact]
    public void Unplug_SubFloorEchoDoesNotKeepChannelAlive()
    {
        // 在位通道只剩杂散短触发：不刷新"活着"时刻，静默判定照常把它翻成拔出
        var t = Make(plugConsecutive: 1, unplugSilence: 20, minPlausibleNs: 2000);
        t.Observe(1, 5, Echo, T0);
        Drain(t).Should().ContainSingle(e => e.Present);

        t.Observe(1, 5, 900, T0.AddSeconds(15));
        t.Observe(1, 5, 1200, T0.AddSeconds(19));
        t.Sweep(T0.AddSeconds(21)).Should().ContainSingle(e => !e.Present,
            "杂散触发不算有效回波，自 T0 起静默已超 20s");
    }

    [Fact]
    public void UnknownChannel_SilentSinceBoot_ConfirmedAbsentWithInitialFlag()
    {
        // 陈旧 hardware_state 自愈：启动后被轮询但持续无有效回波的未知态通道，
        // 静默满阈值确认为无探头（Initial=true，前端不弹通知、服务端降日志级别）
        var t = Make(plugConsecutive: 3, unplugSilence: 20);
        t.Observe(1, 5, null, T0);
        // 静默地板 = 首次观察时刻：未满阈值不确认（防设备刚开始轮询就把真探头闪断）
        t.Sweep(T0.AddSeconds(10)).Should().BeEmpty();

        var changes = t.Sweep(T0.AddSeconds(21));
        changes.Should().ContainSingle();
        changes[0].Present.Should().BeFalse();
        changes[0].Initial.Should().BeTrue("启动后的首次状态确认");

        // 已确认无探头的不重复上报
        t.Sweep(T0.AddSeconds(60)).Should().BeEmpty();
    }

    [Fact]
    public void Unplug_SuspendedDeviceIsNotJudged_AndFloorResetsAfterResume()
    {
        var t = Make(plugConsecutive: 1, unplugSilence: 20);
        t.Observe(1, 5, Echo, T0);
        Drain(t);

        // 设备挂起（离线/扫描）期间静默 100s：不误判拔出
        t.Sweep(T0.AddSeconds(100), isDeviceSuspended: _ => true).Should().BeEmpty();

        // 恢复后：静默从挂起结束时刻重新起算，未到阈值不翻
        t.Sweep(T0.AddSeconds(110), isDeviceSuspended: _ => false).Should().BeEmpty();
        // 恢复后持续静默超阈值才翻
        t.Sweep(T0.AddSeconds(121), isDeviceSuspended: _ => false).Should().ContainSingle(e => !e.Present);
    }

    [Fact]
    public void Replug_AfterUnplug_EmitsNonInitialPlugEvent()
    {
        var t = Make(plugConsecutive: 2, unplugSilence: 20);
        t.Observe(1, 5, Echo, T0);
        t.Observe(1, 5, Echo, T0.AddSeconds(1));
        t.Sweep(T0.AddSeconds(30));
        Drain(t).Should().HaveCount(2); // plugged(initial) + unplugged

        t.Observe(1, 5, Echo, T0.AddSeconds(40));
        t.Observe(1, 5, Echo, T0.AddSeconds(41));
        var events = Drain(t);
        events.Should().ContainSingle();
        events[0].Present.Should().BeTrue();
        events[0].Initial.Should().BeFalse("非进程启动后的首次确认");
    }

    [Fact]
    public void Unplug_UnobservableChannelIsNotJudged_AndFloorResetsWhenObservableAgain()
    {
        var t = Make(plugConsecutive: 1, unplugSilence: 20);
        t.Observe(1, 5, Echo, T0);
        Drain(t);

        // 通道被移出轮询集（如运维手动下发部分 MCR）：静默 100s 也不判拔出——没被轮询 ≠ 探头拔出
        t.Sweep(T0.AddSeconds(100), isChannelObservable: (_, _) => false).Should().BeEmpty();

        // 重新入集：静默从入集时刻重新起算，不因陈旧 LastEchoAt 立刻误翻
        t.Sweep(T0.AddSeconds(110), isChannelObservable: (_, _) => true).Should().BeEmpty();
        // 入集后持续静默超阈值才翻
        t.Sweep(T0.AddSeconds(121), isChannelObservable: (_, _) => true).Should().ContainSingle(e => !e.Present);
    }

    [Fact]
    public void Unplug_ObservabilityIsPerChannel()
    {
        var t = Make(plugConsecutive: 1, unplugSilence: 20);
        t.Observe(1, 5, Echo, T0);
        t.Observe(1, 6, Echo, T0);
        Drain(t);

        // 只有 ch6 在轮询集内：ch5 挂起判定，ch6 正常判拔出
        var changes = t.Sweep(T0.AddSeconds(30), isChannelObservable: (_, ch) => ch == 6);
        changes.Should().ContainSingle();
        changes[0].ChannelNo.Should().Be((ushort)6);
    }

    [Fact]
    public void Unplug_PerDeviceThresholdOverride_TakesPrecedence()
    {
        var t = Make(plugConsecutive: 1, unplugSilence: 20);
        t.Observe(1, 5, Echo, T0);
        Drain(t);

        // 自适应阈值（48 通道 → 24s）：静默 21s 按固定 20s 该翻，但按 24s 不翻
        t.Sweep(T0.AddSeconds(21), unplugSilenceSeconds: _ => 24).Should().BeEmpty();
        // 超过自适应阈值才翻
        t.Sweep(T0.AddSeconds(25), unplugSilenceSeconds: _ => 24).Should().ContainSingle(e => !e.Present);
    }

    [Fact]
    public void Unplug_ThresholdOverrideIsPerDevice()
    {
        var t = Make(plugConsecutive: 1, unplugSilence: 20);
        t.Observe(1, 5, Echo, T0);   // 大板：阈值 24s
        t.Observe(2, 5, Echo, T0);   // 小板：阈值 20s
        Drain(t);

        var changes = t.Sweep(T0.AddSeconds(21), unplugSilenceSeconds: dev => dev == 1 ? 24 : 20);
        changes.Should().ContainSingle();
        changes[0].DeviceId.Should().Be(2L, "只有小板超过了自己的阈值");
    }

    [Theory]
    [InlineData(48, 24)]  // 48×0.1s×5 圈 = 24s > 下限 20s
    [InlineData(40, 20)]  // 40×0.1s×5 圈 = 20s = 下限
    [InlineData(8, 20)]   // 4s < 下限 → 用下限
    [InlineData(0, 20)]   // 通道数未知 → 用下限
    public void EffectiveUnplugSilence_ScalesWithChannelCount(int channelCount, int expected)
    {
        var opt = new ChannelPresenceOptions
        {
            UnplugSilenceSeconds = 20, PerChannelMeasureMs = 100, UnplugSilenceCycles = 5,
        };
        opt.EffectiveUnplugSilenceSeconds(channelCount).Should().Be(expected);
    }

    [Fact]
    public void Snapshot_ReturnsOnlyConfirmedStates()
    {
        var t = Make(plugConsecutive: 1, unplugSilence: 20);
        t.Observe(1, 5, Echo, T0);   // 确认在位
        t.Observe(1, 6, null, T0);   // 未知态（被轮询但无回波，未满静默阈值）

        var snap = t.Snapshot();
        snap.Should().ContainSingle(e => e.ChannelNo == 5 && e.Present);
        snap.Should().NotContain(e => e.ChannelNo == 6, "未知态不进对账快照——没证据就不修库");

        t.Sweep(T0.AddSeconds(30));                // ch5 静默翻拔出；ch6 静默确认无探头
        t.Observe(1, 5, Echo, T0.AddSeconds(31));  // ch5 插回

        snap = t.Snapshot();
        snap.Should().Contain(e => e.ChannelNo == 5 && e.Present && e.LastEchoAt == T0.AddSeconds(31));
        snap.Should().Contain(e => e.ChannelNo == 6 && !e.Present, "静默确认后的'无探头'是已确认态，参与对账");
    }

    [Fact]
    public void Snapshot_EvidenceGoesStaleOnSuspension_AndFreshensOnNewEcho()
    {
        // 挂起（离线/扫描）期间内存"在位"是冻结旧值：对账不能拿它覆盖库，恢复后要有新回波佐证
        var t = Make(plugConsecutive: 1, unplugSilence: 20);
        t.Observe(1, 5, Echo, T0);
        t.Snapshot().Should().ContainSingle(e => e.Present && e.EvidenceFresh);

        t.Sweep(T0.AddSeconds(10), isDeviceSuspended: _ => true);   // 挂起抬高静默地板
        t.Snapshot().Should().ContainSingle(e => e.Present && !e.EvidenceFresh,
            "地板抬过之后，旧回波不再是'仍在位'的证据");

        t.Observe(1, 5, Echo, T0.AddSeconds(15));
        t.Snapshot().Should().ContainSingle(e => e.Present && e.EvidenceFresh,
            "恢复后有了新回波，证据重新可信");
    }

    [Fact]
    public void Disabled_NeverEmits()
    {
        var t = new ChannelPresenceTracker(Options.Create(new ChannelPresenceOptions { Enabled = false }));
        t.Observe(1, 5, Echo, T0);
        t.Observe(1, 5, Echo, T0.AddSeconds(1));
        t.Observe(1, 5, Echo, T0.AddSeconds(2));
        t.Sweep(T0.AddSeconds(100));
        Drain(t).Should().BeEmpty();
    }

    [Fact]
    public void MultipleDevicesAndChannels_TrackedIndependently()
    {
        var t = Make(plugConsecutive: 1, unplugSilence: 20);
        t.Observe(1, 1, Echo, T0);
        t.Observe(1, 2, Echo, T0);
        t.Observe(2, 1, Echo, T0);
        Drain(t).Should().HaveCount(3);

        // 只有 device=1 的 ch2 持续有回波，其余静默
        t.Observe(1, 2, Echo, T0.AddSeconds(25));
        var changes = t.Sweep(T0.AddSeconds(25));
        changes.Should().HaveCount(2);
        changes.Should().OnlyContain(e => !e.Present);
        changes.Select(e => (e.DeviceId, e.ChannelNo))
            .Should().BeEquivalentTo(new[] { (1L, (ushort)1), (2L, (ushort)1) });
    }
}
