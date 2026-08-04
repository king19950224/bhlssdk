using BoltPreload.Api.Runtime;
using BoltPreload.Application.Devices.Acceptance;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Application.Diagnostics;
using BoltPreload.Application.Ingestion;
using BoltPreload.Application.Realtime;
using BoltPreload.Fx;
using BoltPreload.Infrastructure.Ingestion;
using BoltPreload.Tests.Devices;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 全通道轮询的收敛循环（<see cref="ChannelPresenceService"/> 的 MCR 下发部分）。
/// <para>
/// 厂家设备只测 MCR 轮询集里的通道，集外的口一条数据都不上来。所以这个循环决定的是
/// 哪些通道有数据可采——它不收敛，对应那些螺栓就是一片空白，而系统各处都不会报错：
/// 设备在线、服务活着、心跳正常，就是没数。
/// </para>
/// <para>
/// 最要紧的是与接入验收会话的交接。会话期间 MCR 被临时缩到待验收通道集，本服务让位；
/// 让位时必须把"已下发过"的标记一起清掉，否则会话结束后这一轮直接跳过校验，
/// 设备就永久停在那个缩小的集合上。验收端点的恢复路径整个押在这里，别处没有兜底。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class ChannelPollingConvergenceTests
{
    private readonly PostgresFixture _fx;
    public ChannelPollingConvergenceTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task Converge_SendsFullChannelPolling_WhenTheDeviceIsNotThereYet()
    {
        _fx.EnsureAvailable();
        var connector = new FakeConnector(deviceId: 1) { SessionMcr = [1, 2, 3, 4, 5, 6, 7, 8] };
        var svc = NewService(connector, channelCount: 48);

        await svc.EnsureFullChannelPollingAsync(default);

        connector.McrConfigCalls.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(FullSet(48),
                "轮询集外的通道一条数据都不上来，插在 9..48 口上的探头等于不存在");
        connector.WorkModeCalls.Should().ContainSingle()
            .Which.Mode.Should().Be(FxWorkMode.MultiMeas, "改完轮询集要切回多通道监测才真的开始测");
    }

    [PostgresFact]
    public async Task Converge_DoesNotResendWhenTheSetAlreadyMatches()
    {
        _fx.EnsureAvailable();
        var connector = new FakeConnector(deviceId: 1);
        var svc = NewService(connector, channelCount: 16);

        await svc.EnsureFullChannelPollingAsync(default);
        await svc.EnsureFullChannelPollingAsync(default);
        await svc.EnsureFullChannelPollingAsync(default);

        connector.McrConfigCalls.Should().HaveCount(1,
            "每轮 sweep 都重下发一次 MCR 是在没事的时候反复打断设备的正常轮询");
    }

    [PostgresFact]
    public async Task Converge_StepsAsideDuringAcceptance_ThenRestoresFullPollingAfterwards()
    {
        _fx.EnsureAvailable();
        var connector = new FakeConnector(deviceId: 1);
        var acceptance = new AcceptanceSessionState();
        var svc = NewService(connector, channelCount: 48, acceptance: acceptance);

        await svc.EnsureFullChannelPollingAsync(default);
        connector.McrConfigCalls.Should().HaveCount(1);

        // 验收会话把轮询集缩到待验收的那一路
        acceptance.TryBegin(deviceId: 1, channels: [7], ttl: TimeSpan.FromMinutes(10), userId: 1);
        connector.SessionMcr = [7];
        await svc.EnsureFullChannelPollingAsync(default);
        connector.McrConfigCalls.Should().HaveCount(1, "会话期间不能抢它的轮询集");

        acceptance.End(deviceId: 1);
        await svc.EnsureFullChannelPollingAsync(default);

        connector.McrConfigCalls.Should().HaveCount(2);
        connector.McrConfigCalls.Last().Should().BeEquivalentTo(FullSet(48),
            "会话结束后要收敛回全通道。让位时不清掉“已下发过”的标记，这一轮就直接跳过校验，" +
            "设备永久停在那一路上，其余 47 路再也不出数");
    }

    [PostgresFact]
    public async Task Converge_WhenSomeoneElseAlreadyRestoredTheFullSet_JustAcceptsIt()
    {
        _fx.EnsureAvailable();
        var connector = new FakeConnector(deviceId: 1) { SessionMcr = FullSet(48) };
        var acceptance = new AcceptanceSessionState();
        var svc = NewService(connector, channelCount: 48, acceptance: acceptance);

        // 走一遍会话，把"已下发过"的标记清掉；结束时验收端点自己已经恢复过全通道
        // （AcceptanceEndpoints 会尽力恢复一次），也可能是进程刚重启、设备本来就配好了
        acceptance.TryBegin(deviceId: 1, channels: [7], ttl: TimeSpan.FromMinutes(10), userId: 1);
        await svc.EnsureFullChannelPollingAsync(default);
        acceptance.End(deviceId: 1);

        await svc.EnsureFullChannelPollingAsync(default);
        await svc.EnsureFullChannelPollingAsync(default);

        connector.McrConfigCalls.Should().BeEmpty(
            "设备已经在全通道上了，再下发一次只是白白打断一轮轮询；每次验收结束都来一次更不必要");
    }

    [PostgresFact]
    public async Task Converge_StepsAsideDuringAProbeScan_ThenRechecksAfterwards()
    {
        _fx.EnsureAvailable();
        var connector = new FakeConnector(deviceId: 1);
        var scan = new ProbeScanState();
        var svc = NewService(connector, channelCount: 48, scan: scan);

        await svc.EnsureFullChannelPollingAsync(default);

        // 扫描逐通道改写 MCR，结束时按扫描前的快照恢复，那份快照可能是收敛前的旧集
        var handle = scan.TryBegin(deviceId: 1, scanRunId: 1);
        connector.SessionMcr = [1, 2, 3, 4, 5, 6, 7, 8];
        await svc.EnsureFullChannelPollingAsync(default);
        connector.McrConfigCalls.Should().HaveCount(1, "扫描期间不能动 MCR");

        handle.Should().NotBeNull();
        scan.End(deviceId: 1);
        await svc.EnsureFullChannelPollingAsync(default);

        connector.McrConfigCalls.Should().HaveCount(2, "扫完要重新校验一遍，恢复的快照未必是全通道");
    }

    [PostgresFact]
    public async Task Converge_AfterTheChannelCountGrows_SendsTheWiderSet()
    {
        _fx.EnsureAvailable();
        var connector = new FakeConnector(deviceId: 1);
        var manager = new FakeDeviceManager(connector, channelCount: 40);
        var svc = NewService(connector, manager: manager);

        await svc.EnsureFullChannelPollingAsync(default);
        connector.McrConfigCalls.Last().Should().HaveCount(40);

        // 通道目录扩容（换 48 通道板卡后重读设备参数）
        manager.TryUpdateChannelCount(deviceId: 1, channelCount: 48);
        await svc.EnsureFullChannelPollingAsync(default);

        connector.McrConfigCalls.Should().HaveCount(2);
        connector.McrConfigCalls.Last().Should().HaveCount(48,
            "按“已下发过”永久跳过的话，多出来的 8 路永远进不了轮询集，插上探头也不出数");
    }

    [PostgresFact]
    public async Task Converge_WithoutAChannelCount_DoesNotGuess()
    {
        _fx.EnsureAvailable();
        var connector = new FakeConnector(deviceId: 1);
        var svc = NewService(connector, channelCount: 0);

        await svc.EnsureFullChannelPollingAsync(default);

        connector.McrConfigCalls.Should().BeEmpty(
            "不知道有几路就别猜。猜多了下发到不存在的通道，猜少了把在用的通道踢出轮询集");
    }

    [PostgresFact]
    public async Task Converge_OnAModelWithoutMultiChannelPolling_SendsNothing()
    {
        _fx.EnsureAvailable();
        var connector = new FakeConnector(deviceId: 1);
        var svc = NewService(connector, manager: new FakeDeviceManager(connector, model: 1, channelCount: 8));

        await svc.EnsureFullChannelPollingAsync(default);

        connector.McrConfigCalls.Should().BeEmpty("MCR 只有多通道监测型（model=3）支持");
    }

    // ---- helpers ----

    private static ushort[] FullSet(int count) =>
        Enumerable.Range(1, count).Select(i => (ushort)i).ToArray();

    private ChannelPresenceService NewService(
        FakeConnector connector,
        ushort channelCount = 48,
        FakeDeviceManager? manager = null,
        ProbeScanState? scan = null,
        AcceptanceSessionState? acceptance = null)
    {
        manager ??= new FakeDeviceManager(connector, channelCount: channelCount);
        // 建档器和 tracker 走不到 MCR 下发这条路，接上只是为了满足构造
        var enroller = new BoltAutoEnroller(
            _fx.DataSource,
            new CachedChannelResolver(
                _fx.DataSource, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<CachedChannelResolver>.Instance),
            NullLogger<BoltAutoEnroller>.Instance);
        return new ChannelPresenceService(
            manager,
            new ChannelPresenceTracker(Options.Create(new ChannelPresenceOptions())),
            new NoopPresencePublisher(),
            scan ?? new ProbeScanState(),
            acceptance ?? new AcceptanceSessionState(),
            _fx.DataSource,
            enroller,
            new BackgroundServiceHealth(),
            Options.Create(new ChannelPresenceOptions()),
            NullLogger<ChannelPresenceService>.Instance);
    }

    private sealed class NoopPresencePublisher : IChannelPresencePublisher
    {
        public Task PublishAsync(
            ChannelPresenceChange change, long? channelId, long? boltId, string? boltCode,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
