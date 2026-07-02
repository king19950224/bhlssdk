using BoltPreload.Application.Ingestion;
using BoltPreload.Fx;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Ingestion;

/// <summary>
/// 看门狗"是否重连"纯判定的单测（不碰真实连接器/DB）。锁定：
/// 1) 刚(重)连的 null/断开前旧时间戳有完整无数据宽限，不被误判无穷陈旧；
/// 2) 慢写库(IsWriting)期间跳过重连；
/// 3) 扫描期(ProbeScan)跳过；
/// 4) 建连中(Connecting/Reconnecting)宽限内不打断、超宽限才强制重连。
/// </summary>
public class IngestionWatchdogDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const int Timeout = 60;
    private const int ConnectGrace = 45;

    // 便捷包装：默认已连接、无 in-flight，测试仅覆盖各自关注的入参。
    private static bool Should(
        FxConnectorState state, bool isWriting = false,
        DateTimeOffset? lastChannelResultAt = null, DateTimeOffset? connectedSince = null,
        DateTimeOffset? inFlightSince = null, bool probeScanRunning = false)
        => IngestionWatchdogService.ShouldReconnect(
            probeScanRunning, state, isWriting,
            lastChannelResultAt, connectedSince, inFlightSince, Now, Timeout, ConnectGrace);

    [Fact]
    public void ProbeScanRunning_NeverReconnects()
        => Should(FxConnectorState.Disconnected, probeScanRunning: true).Should().BeFalse();

    [Fact]
    public void Disconnected_Reconnects()
        => Should(FxConnectorState.Disconnected, lastChannelResultAt: Now.AddSeconds(-5)).Should().BeTrue();

    [Fact]
    public void Faulted_Reconnects()
        => Should(FxConnectorState.Faulted).Should().BeTrue();

    [Fact]
    public void Connecting_WithinGrace_DoesNotReconnect()
        // 建连中 10s，未超 45s 宽限 → 不打断进行中的连接 + provisioning
        => Should(FxConnectorState.Connecting, inFlightSince: Now.AddSeconds(-10)).Should().BeFalse();

    [Fact]
    public void Connecting_JustEntered_NullInFlight_DoesNotReconnect()
        // 本轮才观察到 Connecting（inFlightSince 尚未记录）→ 视为宽限内，不重连
        => Should(FxConnectorState.Connecting, inFlightSince: null).Should().BeFalse();

    [Fact]
    public void Connecting_BeyondGrace_Reconnects()
        // 建连中滞留 60s > 45s 宽限（疑似 OnConnected 未回调卡死）→ 强制重连
        => Should(FxConnectorState.Connecting, inFlightSince: Now.AddSeconds(-60)).Should().BeTrue();

    [Fact]
    public void Reconnecting_WithinGrace_DoesNotReconnect()
        => Should(FxConnectorState.Reconnecting, inFlightSince: Now.AddSeconds(-5)).Should().BeFalse();

    [Fact]
    public void Connected_WhileWriting_DoesNotReconnect()
        // 慢写库期间即便"看似陈旧"也不重连（不是连接问题）
        => Should(FxConnectorState.Connected, isWriting: true,
            lastChannelResultAt: Now.AddSeconds(-9999), connectedSince: Now.AddSeconds(-9999)).Should().BeFalse();

    [Fact]
    public void JustConnected_NoData_WithinGrace_DoesNotReconnect()
        // 刚连上 10s、尚未出数据(null) → 60s 宽限内，不重连
        => Should(FxConnectorState.Connected, connectedSince: Now.AddSeconds(-10)).Should().BeFalse();

    [Fact]
    public void Connected_NoData_BeyondTimeoutSinceConnect_Reconnects()
        // 连上 120s 仍无任何数据 → 超时，重连（重踢 MultiMeas）
        => Should(FxConnectorState.Connected, connectedSince: Now.AddSeconds(-120)).Should().BeTrue();

    [Fact]
    public void Connected_RecentData_DoesNotReconnect()
        => Should(FxConnectorState.Connected, lastChannelResultAt: Now.AddSeconds(-5),
            connectedSince: Now.AddSeconds(-300)).Should().BeFalse();

    [Fact]
    public void Connected_StaleData_Reconnects()
        => Should(FxConnectorState.Connected, lastChannelResultAt: Now.AddSeconds(-120),
            connectedSince: Now.AddSeconds(-300)).Should().BeTrue();

    [Fact]
    public void FreshReconnect_OldPreDisconnectData_WithinGrace_DoesNotReconnect()
        // 重连后 last 仍是断开前旧值(500s前)，但本次连接才 10s → 按连接起点计，宽限内不重连
        => Should(FxConnectorState.Connected, lastChannelResultAt: Now.AddSeconds(-500),
            connectedSince: Now.AddSeconds(-10)).Should().BeFalse();
}
