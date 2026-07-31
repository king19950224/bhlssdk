using BoltPreload.Application.Devices.Acceptance;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Devices;

/// <summary>
/// 接入验收会话状态机单测。锁定：
/// 1) 同设备只允许一个会话（重复开启拒绝）；
/// 2) 过期会话 IsActive=false（收敛循环据此恢复全通道轮询），ExpireStale 只清过期的；
/// 3) End 幂等；
/// 4) 过期清理不误删顶替上来的新会话。
/// </summary>
public class AcceptanceSessionStateTests
{
    private static readonly ushort[] Channels = [1, 2, 3];

    [Fact]
    public void TryBegin_SecondSessionOnSameDeviceRejected()
    {
        var s = new AcceptanceSessionState();
        s.TryBegin(1, Channels, TimeSpan.FromMinutes(30), userId: 7).Should().NotBeNull();
        s.TryBegin(1, [9], TimeSpan.FromMinutes(30), userId: 8).Should().BeNull("同设备已有会话");
        s.TryBegin(2, Channels, TimeSpan.FromMinutes(30), userId: 8).Should().NotBeNull("不同设备互不影响");
    }

    [Fact]
    public void IsActive_FalseWhenExpired()
    {
        var s = new AcceptanceSessionState();
        s.TryBegin(1, Channels, TimeSpan.FromMilliseconds(-1), userId: 7); // 立即过期
        s.IsActive(1).Should().BeFalse("已过期的会话不算进行中");
        s.Get(1).Should().NotBeNull("过期但未清理时仍可查到（端点按 ExpiresAt 展示）");
    }

    [Fact]
    public void ExpireStale_RemovesOnlyExpired()
    {
        var s = new AcceptanceSessionState();
        s.TryBegin(1, Channels, TimeSpan.FromMilliseconds(-1), userId: 7);
        s.TryBegin(2, Channels, TimeSpan.FromMinutes(30), userId: 7);

        var expired = s.ExpireStale(DateTimeOffset.UtcNow);
        expired.Should().ContainSingle().Which.DeviceId.Should().Be(1);
        s.Get(1).Should().BeNull();
        s.IsActive(2).Should().BeTrue();
    }

    [Fact]
    public void End_IsIdempotent()
    {
        var s = new AcceptanceSessionState();
        s.TryBegin(1, Channels, TimeSpan.FromMinutes(30), userId: 7);
        s.End(1).Should().NotBeNull();
        s.End(1).Should().BeNull("重复结束无副作用");
        s.TryBegin(1, Channels, TimeSpan.FromMinutes(30), userId: 7).Should().NotBeNull("结束后可再开");
    }

    [Fact]
    public void ExpireStale_DoesNotRemoveReplacementSession()
    {
        var s = new AcceptanceSessionState();
        s.TryBegin(1, Channels, TimeSpan.FromMilliseconds(-1), userId: 7);
        var stale = s.Get(1)!;

        // 过期判定后、清理前被 End+新开顶替（模拟并发）：按 (键,值) 原子移除不应误删新会话
        s.End(1);
        var fresh = s.TryBegin(1, [4, 5], TimeSpan.FromMinutes(30), userId: 8)!;

        s.ExpireStale(DateTimeOffset.UtcNow).Should().BeEmpty("旧会话已被 End，新会话未过期");
        s.Get(1).Should().BeSameAs(fresh);
        stale.ExpiresAt.Should().BeBefore(fresh.ExpiresAt);
    }
}
