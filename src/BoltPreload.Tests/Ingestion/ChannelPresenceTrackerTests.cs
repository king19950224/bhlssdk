using BoltPreload.Application.Ingestion;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BoltPreload.Tests.Ingestion;

/// <summary>
/// 探头热插拔在位状态机的单测（纯内存，不碰连接器/DB）。锁定：
/// 1) 插入需连续 N 条有回波才触发（防单次噪声误报），首个确认带 Initial 标记；
/// 2) 无回波打断连击，重新累计；
/// 3) 拔出由 Sweep 按静默时长判定，未超阈值不误翻；
/// 4) 设备挂起（离线/probe-scan）期间不误判拔出，恢复后静默重新起算；
/// 5) 拔出后重新插入 → 再次 plugged 事件（非 Initial）。
/// </summary>
public class ChannelPresenceTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ChannelPresenceTracker Make(int plugConsecutive = 3, int unplugSilence = 20)
        => new(Options.Create(new ChannelPresenceOptions
        {
            Enabled = true,
            PlugConsecutiveEchoes = plugConsecutive,
            UnplugSilenceSeconds = unplugSilence,
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
        t.Observe(1, 5, hasEcho: true, T0);
        t.Observe(1, 5, hasEcho: true, T0.AddSeconds(1));
        Drain(t).Should().BeEmpty("2 条回波不够 3 条阈值");

        t.Observe(1, 5, hasEcho: true, T0.AddSeconds(2));
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
        t.Observe(1, 5, true, T0);
        t.Observe(1, 5, true, T0.AddSeconds(1));
        t.Observe(1, 5, false, T0.AddSeconds(2));   // 打断
        t.Observe(1, 5, true, T0.AddSeconds(3));
        t.Observe(1, 5, true, T0.AddSeconds(4));
        Drain(t).Should().BeEmpty("连击被打断后需重新累计");

        t.Observe(1, 5, true, T0.AddSeconds(5));
        Drain(t).Should().ContainSingle();
    }

    [Fact]
    public void Unplug_AfterSilenceThreshold_ViaSweep()
    {
        var t = Make(plugConsecutive: 1, unplugSilence: 20);
        t.Observe(1, 5, true, T0);
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
    public void Unplug_SuspendedDeviceIsNotJudged_AndFloorResetsAfterResume()
    {
        var t = Make(plugConsecutive: 1, unplugSilence: 20);
        t.Observe(1, 5, true, T0);
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
        t.Observe(1, 5, true, T0);
        t.Observe(1, 5, true, T0.AddSeconds(1));
        t.Sweep(T0.AddSeconds(30));
        Drain(t).Should().HaveCount(2); // plugged(initial) + unplugged

        t.Observe(1, 5, true, T0.AddSeconds(40));
        t.Observe(1, 5, true, T0.AddSeconds(41));
        var events = Drain(t);
        events.Should().ContainSingle();
        events[0].Present.Should().BeTrue();
        events[0].Initial.Should().BeFalse("非进程启动后的首次确认");
    }

    [Fact]
    public void Unplug_UnobservableChannelIsNotJudged_AndFloorResetsWhenObservableAgain()
    {
        var t = Make(plugConsecutive: 1, unplugSilence: 20);
        t.Observe(1, 5, true, T0);
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
        t.Observe(1, 5, true, T0);
        t.Observe(1, 6, true, T0);
        Drain(t);

        // 只有 ch6 在轮询集内：ch5 挂起判定，ch6 正常判拔出
        var changes = t.Sweep(T0.AddSeconds(30), isChannelObservable: (_, ch) => ch == 6);
        changes.Should().ContainSingle();
        changes[0].ChannelNo.Should().Be((ushort)6);
    }

    [Fact]
    public void Disabled_NeverEmits()
    {
        var t = new ChannelPresenceTracker(Options.Create(new ChannelPresenceOptions { Enabled = false }));
        t.Observe(1, 5, true, T0);
        t.Observe(1, 5, true, T0.AddSeconds(1));
        t.Observe(1, 5, true, T0.AddSeconds(2));
        t.Sweep(T0.AddSeconds(100));
        Drain(t).Should().BeEmpty();
    }

    [Fact]
    public void MultipleDevicesAndChannels_TrackedIndependently()
    {
        var t = Make(plugConsecutive: 1, unplugSilence: 20);
        t.Observe(1, 1, true, T0);
        t.Observe(1, 2, true, T0);
        t.Observe(2, 1, true, T0);
        Drain(t).Should().HaveCount(3);

        // 只有 device=1 的 ch2 持续有回波，其余静默
        t.Observe(1, 2, true, T0.AddSeconds(25));
        var changes = t.Sweep(T0.AddSeconds(25));
        changes.Should().HaveCount(2);
        changes.Should().OnlyContain(e => !e.Present);
        changes.Select(e => (e.DeviceId, e.ChannelNo))
            .Should().BeEquivalentTo(new[] { (1L, (ushort)1), (2L, (ushort)1) });
    }
}
