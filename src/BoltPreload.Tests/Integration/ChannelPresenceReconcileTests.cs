using BoltPreload.Api.Runtime;
using BoltPreload.Application.Devices.Acceptance;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Application.Diagnostics;
using BoltPreload.Application.Ingestion;
using BoltPreload.Application.Realtime;
using BoltPreload.Domain.Entities;
using BoltPreload.Infrastructure.Ingestion;
using BoltPreload.Tests.Devices;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 在位状态的周期对账（probe-presence-hardening-design.md §2）。
/// <para>
/// 在位事件是一次性消费：PersistAsync 碰上 DB 抖动/WAL 降级抛异常，事件丢了状态机也不会再发
/// （Present 已翻转）；通道行还没被首测自动发现建出来时建档被静默跳过，代码注释写"等下次事件"，
/// 但下次事件永远不来。这两条路都靠对账兜底成最终一致——这组测试钉住对账的修复方向和克制边界
/// （不覆盖 probe-scan 的细分判定、不拿冻结旧值修库、hold 优先）。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class ChannelPresenceReconcileTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const double Echo = 32000;

    private readonly PostgresFixture _fx;
    public ChannelPresenceReconcileTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task Reconcile_RestoresPresenceAndEnrolls_AfterLostEvent()
    {
        _fx.EnsureAvailable();
        var (deviceId, _) = await SeedDeviceWithDefaultGroupAsync();
        var channelId = await SeedChannelAsync(deviceId, chNo: 5, hardwareState: "no_data");

        // 状态机确认在位，但"事件被丢了"（不跑消费循环）——库还停在 no_data、没建档
        var (svc, tracker) = NewService(deviceId);
        ObservePlug(tracker, deviceId, 5);

        await svc.ReconcilePresenceAsync(default);

        var (hw, _, _) = await ReadChannelAsync(channelId);
        hw.Should().Be("healthy", "对账要把丢失的插入状态补写回库");
        (await FindBoltCodeAsync(channelId)).Should().NotBeNull("状态补上之后建档也要一并补");
    }

    [PostgresFact]
    public async Task Reconcile_LeavesConsistentChannelsAlone()
    {
        _fx.EnsureAvailable();
        var (deviceId, groupId) = await SeedDeviceWithDefaultGroupAsync();
        var channelId = await SeedChannelAsync(deviceId, chNo: 5, hardwareState: "noisy");
        await SeedBoltAsync(groupId, channelId, "B-已有");

        var (svc, tracker) = NewService(deviceId);
        ObservePlug(tracker, deviceId, 5);
        var updatedBefore = await ReadUpdatedAtAsync(channelId);

        await svc.ReconcilePresenceAsync(default);

        var (hw, _, _) = await ReadChannelAsync(channelId);
        hw.Should().Be("noisy", "内存在位、库也是在位词表态且已绑定——一致就一个字段都别动");
        (await ReadUpdatedAtAsync(channelId)).Should().Be(updatedBefore, "无 mismatch 不应产生任何写入");
    }

    [PostgresFact]
    public async Task Reconcile_KeepsSuspectRefinement_WhileEnrollingMissingBolt()
    {
        _fx.EnsureAvailable();
        var (deviceId, _) = await SeedDeviceWithDefaultGroupAsync();
        var channelId = await SeedChannelAsync(deviceId, chNo: 5, hardwareState: "suspect");

        var (svc, tracker) = NewService(deviceId);
        ObservePlug(tracker, deviceId, 5);

        await svc.ReconcilePresenceAsync(default);

        var (hw, _, _) = await ReadChannelAsync(channelId);
        hw.Should().Be("suspect", "suspect 是扫描/守卫的细分判定，补建档不许把它抹成 healthy");
        (await FindBoltCodeAsync(channelId)).Should().NotBeNull("channelId 竞态漏掉的建档由对账补上");
    }

    [PostgresFact]
    public async Task Reconcile_MarksUnplugged_AndClearsHold()
    {
        _fx.EnsureAvailable();
        var (deviceId, _) = await SeedDeviceWithDefaultGroupAsync();
        var channelId = await SeedChannelAsync(deviceId, chNo: 5, hardwareState: "healthy", hold: true);

        var (svc, tracker) = NewService(deviceId);
        ObservePlug(tracker, deviceId, 5);
        tracker.Sweep(T0.AddSeconds(60));   // 静默超阈值（20s）：确认拔出——假设这条 unplug 事件也丢了

        await svc.ReconcilePresenceAsync(default);

        var (hw, hold, _) = await ReadChannelAsync(channelId);
        hw.Should().Be("no_data", "内存已确认拔出而库还挂着在位态，对账要翻正");
        hold.Should().BeFalse("离位后清 hold，与事件路径同一语义（人工删除抑制到此为止）");
    }

    [PostgresFact]
    public async Task Reconcile_SelfHeals_WhenChannelRowAppearsLate()
    {
        _fx.EnsureAvailable();
        var (deviceId, _) = await SeedDeviceWithDefaultGroupAsync();

        var (svc, tracker) = NewService(deviceId);
        ObservePlug(tracker, deviceId, 5);

        // 行还没被首测自动发现建出来：这一轮修不了，但绝不能抛
        await svc.ReconcilePresenceAsync(default);
        // 行出现了（自动发现落库）——下一轮对账把状态和建档都补上
        var channelId = await SeedChannelAsync(deviceId, chNo: 5, hardwareState: "no_data");

        await svc.ReconcilePresenceAsync(default);

        var (hw, _, _) = await ReadChannelAsync(channelId);
        hw.Should().Be("healthy");
        (await FindBoltCodeAsync(channelId)).Should().NotBeNull("这正是'等下次事件'等不来的那类漏建档");
    }

    [PostgresFact]
    public async Task Reconcile_SkipsSuspendedDevice()
    {
        _fx.EnsureAvailable();
        var (deviceId, _) = await SeedDeviceWithDefaultGroupAsync();
        var channelId = await SeedChannelAsync(deviceId, chNo: 5, hardwareState: "no_data");

        var (svc, tracker) = NewService(deviceId, markRuntimeAlive: false);   // 整机静默 → 挂起
        ObservePlug(tracker, deviceId, 5);

        await svc.ReconcilePresenceAsync(default);

        var (hw, _, _) = await ReadChannelAsync(channelId);
        hw.Should().Be("no_data", "挂起设备的内存状态是冻结旧值，不拿去覆盖库");
    }

    [PostgresFact]
    public async Task Reconcile_SkipsStaleEvidence_UntilNewEchoArrives()
    {
        _fx.EnsureAvailable();
        var (deviceId, _) = await SeedDeviceWithDefaultGroupAsync();
        var channelId = await SeedChannelAsync(deviceId, chNo: 5, hardwareState: "no_data");

        var (svc, tracker) = NewService(deviceId);
        ObservePlug(tracker, deviceId, 5);
        // 设备经历过一段挂起（离线/扫描）：地板抬高，旧回波不再是"仍在位"的证据
        tracker.Sweep(T0.AddSeconds(10), isDeviceSuspended: _ => true);

        await svc.ReconcilePresenceAsync(default);
        (await ReadChannelAsync(channelId)).HardwareState.Should().Be("no_data",
            "证据不新鲜就不修——扫描期间库可能被 probe-scan 合法改写");

        // 恢复后有了新回波，证据重新可信
        tracker.Observe(deviceId, 5, Echo, T0.AddSeconds(20));
        await svc.ReconcilePresenceAsync(default);
        (await ReadChannelAsync(channelId)).HardwareState.Should().Be("healthy");
    }

    // ---- helpers ----

    /// <summary>连击满 3 条稳定回波：状态机确认在位（事件留在队列里没人消费，等于全丢）。</summary>
    private static void ObservePlug(ChannelPresenceTracker tracker, long deviceId, ushort chNo)
    {
        tracker.Observe(deviceId, chNo, Echo, T0);
        tracker.Observe(deviceId, chNo, Echo + 10, T0.AddSeconds(1));
        tracker.Observe(deviceId, chNo, Echo + 20, T0.AddSeconds(2));
    }

    private (ChannelPresenceService Svc, ChannelPresenceTracker Tracker) NewService(
        long deviceId, bool markRuntimeAlive = true)
    {
        var connector = new FakeConnector(deviceId);
        var manager = new FakeDeviceManager(connector, channelCount: 8);
        if (markRuntimeAlive)
            manager.Primary.RuntimeStatus.MarkEnvelope(DateTimeOffset.UtcNow, isChannelResult: true);

        var tracker = new ChannelPresenceTracker(Options.Create(new ChannelPresenceOptions()));
        var enroller = new BoltAutoEnroller(
            _fx.DataSource,
            new CachedChannelResolver(
                _fx.DataSource, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<CachedChannelResolver>.Instance),
            Options.Create(new ChannelPresenceOptions()),
            NullLogger<BoltAutoEnroller>.Instance);
        var svc = new ChannelPresenceService(
            manager,
            tracker,
            new NoopPresencePublisher(),
            new ProbeScanState(),
            new AcceptanceSessionState(),
            _fx.DataSource,
            enroller,
            new BackgroundServiceHealth(),
            Options.Create(new ChannelPresenceOptions()),
            NullLogger<ChannelPresenceService>.Instance);
        return (svc, tracker);
    }

    private async Task<(long DeviceId, long GroupId)> SeedDeviceWithDefaultGroupAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..10];
        await using var db = _fx.NewDbContext();

        var project = new Project
        {
            Code = $"P-{tag}", Name = "对账测试", Industry = "风电", SlaLevel = "standard",
            CreatedAt = now, UpdatedAt = now,
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var device = new Device
        {
            ProjectId = project.Id, Code = $"D-{tag}", Model = 3, CommType = 1, CommParams = "in-process",
            Address = 1, TransferMode = 1, ChannelCount = 8, State = "online", CreatedAt = now, UpdatedAt = now,
        };
        db.Devices.Add(device);

        var group = new MonitorGroup
        {
            ProjectId = project.Id, Code = $"G-{tag}", Name = "对账组",
            StructureType = "flange", ExpectedBoltCount = 8, TargetPreloadN = 100_000, Enabled = true,
            CreatedAt = now, UpdatedAt = now,
        };
        db.MonitorGroups.Add(group);
        await db.SaveChangesAsync();

        // default_group_id 是 0026 的列，实体没映射，裸 SQL 补（自动建档的归属组解析依赖它）
        await using var cmd = _fx.DataSource.CreateCommand(
            "UPDATE devices SET default_group_id = @g WHERE id = @d");
        cmd.Parameters.AddWithValue("g", NpgsqlDbType.Bigint, group.Id);
        cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, device.Id);
        await cmd.ExecuteNonQueryAsync();

        return (device.Id, group.Id);
    }

    private async Task<long> SeedChannelAsync(long deviceId, ushort chNo, string hardwareState, bool hold = false)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.NewDbContext();
        var channel = new Channel
        {
            DeviceId = deviceId, ChannelNo = (short)chNo, ProbeFreqMhz = 2.0m, Gain = 16, MeasMode = 2, TcMode = 1,
            TofAlgorithm = "ThrSignal", HardwareState = hardwareState, BusinessState = "unbound",
            ConfigSource = "database", State = "manual", DiscoveredAt = now, CreatedAt = now, UpdatedAt = now,
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        if (hold)
        {
            await using var cmd = _fx.DataSource.CreateCommand(
                "UPDATE channels SET auto_enroll_hold = true WHERE id = @id");
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channel.Id);
            await cmd.ExecuteNonQueryAsync();
        }
        return channel.Id;
    }

    private async Task SeedBoltAsync(long groupId, long channelId, string code)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.NewDbContext();
        db.Bolts.Add(new Bolt
        {
            GroupId = groupId, ChannelId = channelId, Code = code,
            NominalDiam = "M16", Grade = "8.8", Rp02Mpa = 640, Material = "42CrMo",
            ElasticModulusGpa = 206, EffectiveLengthMm = 200, TotalLengthMm = 220,
            CrossSectionMm2 = 157, SoundVelocityMs = 5890, TargetPreloadN = 100_000,
            State = "uncalibrated", Enabled = true, GbCompliant = true,
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<(string HardwareState, bool Hold, DateTimeOffset UpdatedAt)> ReadChannelAsync(long channelId)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "SELECT hardware_state, auto_enroll_hold, updated_at FROM channels WHERE id = @id");
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
        await using var r = await cmd.ExecuteReaderAsync();
        (await r.ReadAsync()).Should().BeTrue();
        return (r.GetString(0), r.GetBoolean(1), r.GetFieldValue<DateTimeOffset>(2));
    }

    private async Task<DateTimeOffset> ReadUpdatedAtAsync(long channelId) => (await ReadChannelAsync(channelId)).UpdatedAt;

    private async Task<string?> FindBoltCodeAsync(long channelId)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "SELECT code FROM bolts WHERE channel_id = @c AND deleted_at IS NULL");
        cmd.Parameters.AddWithValue("c", NpgsqlDbType.Bigint, channelId);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private sealed class NoopPresencePublisher : IChannelPresencePublisher
    {
        public Task PublishAsync(
            ChannelPresenceChange change, long? channelId, long? boltId, string? boltCode,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
