using BoltPreload.Api.Runtime;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Api;

/// <summary>
/// 系统告警状态机（<see cref="SystemAlertService.Decide"/>）。
/// <para>
/// 这条通道是库挂掉之后唯一还能把消息送到人手上的路径，状态机错了只有两种结局：
/// 该报的不报（等于这条路没通），或者每分钟刷一条（值班第一件事就是把它静音，等于这条路也没通）。
/// 两种都是"出了事没人知道"，所以每个分支都钉一遍。
/// </para>
/// </summary>
public class SystemAlertDecisionTests
{
    private static readonly TimeSpan Repeat = TimeSpan.FromMinutes(30);
    private static readonly DateTimeOffset T0 = new(2026, 8, 12, 10, 0, 0, TimeSpan.FromHours(8));

    private static SystemHealthVerdict Healthy() => new([], [], 100.0);
    private static SystemHealthVerdict Faulty(params string[] reasons) => new(reasons, [], 100.0);

    [Fact]
    public void Healthy_AndWasHealthy_SaysNothing()
    {
        var d = SystemAlertService.Decide(Healthy(), wasUnhealthy: false, "", default, T0, Repeat);

        // 一切正常时每分钟报一次平安，等于训练所有人忽略这个群
        d.Should().Be(SystemAlertAction.None);
    }

    [Fact]
    public void FirstFault_FiresImmediately()
    {
        var d = SystemAlertService.Decide(Faulty("db_down"), wasUnhealthy: false, "", default, T0, Repeat);

        d.Should().Be(SystemAlertAction.Fault);
    }

    [Fact]
    public void SameFault_WithinRepeatWindow_StaysQuiet()
    {
        var v = Faulty("db_down");

        var d = SystemAlertService.Decide(
            v, wasUnhealthy: true, v.Fingerprint(), lastSentAt: T0, now: T0.AddMinutes(29), Repeat);

        d.Should().Be(SystemAlertAction.None, "同一个故障没恢复，29 分钟内不重复打扰");
    }

    [Fact]
    public void SameFault_AfterRepeatWindow_RemindsAgain()
    {
        var v = Faulty("db_down");

        var d = SystemAlertService.Decide(
            v, wasUnhealthy: true, v.Fingerprint(), lastSentAt: T0, now: T0.AddMinutes(30), Repeat);

        d.Should().Be(SystemAlertAction.Fault, "一直没恢复要按间隔提醒，不能发一条就没下文");
    }

    [Fact]
    public void FaultSurfaceGrows_FiresWithoutWaitingForRepeat()
    {
        var before = Faulty("db_down");
        var after = Faulty("db_down", "wal_saturated");

        // 从"库连不上"恶化成"库连不上而且已经开始丢数据"，等 30 分钟再说就晚了
        var d = SystemAlertService.Decide(
            after, wasUnhealthy: true, before.Fingerprint(), lastSentAt: T0, now: T0.AddMinutes(1), Repeat);

        d.Should().Be(SystemAlertAction.Fault);
    }

    [Fact]
    public void FaultSurfaceShrinks_ButNotClear_StillFires()
    {
        var before = Faulty("db_down", "low_disk");
        var after = Faulty("low_disk");

        var d = SystemAlertService.Decide(
            after, wasUnhealthy: true, before.Fingerprint(), lastSentAt: T0, now: T0.AddMinutes(1), Repeat);

        d.Should().Be(SystemAlertAction.Fault, "好了一项也是变化，值班要知道现在还剩什么没好");
    }

    [Fact]
    public void Recovered_FiresOnce()
    {
        var d = SystemAlertService.Decide(
            Healthy(), wasUnhealthy: true, "db_down", lastSentAt: T0, now: T0.AddMinutes(1), Repeat);

        d.Should().Be(SystemAlertAction.Recovered);
    }

    [Fact]
    public void RecoveredThenQuiet_DoesNotKeepAnnouncing()
    {
        // 发完恢复通知后，wasUnhealthy 归 false，之后不该再有动静
        var d = SystemAlertService.Decide(
            Healthy(), wasUnhealthy: false, "", lastSentAt: T0, now: T0.AddHours(5), Repeat);

        d.Should().Be(SystemAlertAction.None);
    }

    [Fact]
    public void Fingerprint_IgnoresOrdering()
    {
        // 指纹不稳定的后果是每一轮都判成"故障面变了"，直接变成每分钟刷屏
        var a = new SystemHealthVerdict(["db_down", "low_disk"], ["signal-health"], null);
        var b = new SystemHealthVerdict(["low_disk", "db_down"], ["signal-health"], null);

        a.Fingerprint().Should().Be(b.Fingerprint());
    }

    [Fact]
    public void Fingerprint_SeparatesReasonsFromServiceNames()
    {
        // 降级理由和服务名撞到一起会让两种不同故障看起来一样
        var reason = new SystemHealthVerdict(["signal-health"], [], null);
        var dead = new SystemHealthVerdict([], ["signal-health"], null);

        reason.Fingerprint().Should().NotBe(dead.Fingerprint());
    }

    [Fact]
    public void DeadServiceAlone_CountsAsFault()
    {
        // 没有任何降级理由、但后台服务失联：进程活着监测已停，这正是最该报的一种
        var v = new SystemHealthVerdict([], ["signal-health"], 100.0);

        v.Healthy.Should().BeFalse();
        SystemAlertService.Decide(v, wasUnhealthy: false, "", default, T0, Repeat)
            .Should().Be(SystemAlertAction.Fault);
    }
}
