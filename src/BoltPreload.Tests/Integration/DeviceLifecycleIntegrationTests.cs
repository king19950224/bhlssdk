using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoltPreload.Api.Auth;
using BoltPreload.Api.Runtime;
using BoltPreload.Application.Devices;
using BoltPreload.Application.Ingestion;
using BoltPreload.Application.Realtime;
using BoltPreload.Fx;
using BoltPreload.Fx.Models;
using BoltPreload.Tests.Devices;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 设备生命周期（P1-B）的集成测试。锁定的行为：
/// 1) supervisor 对账：热加设备数秒内起消费任务且数据能进 sink；热摘后任务停、连接器被释放，
///    其它设备不受影响；
/// 2) 真机模式 manager 写侧：AddAsync 从表建运行时（只建不连）、RemoveAndStopAsync 摘除并释放、
///    primary 摘除被拒、TransferPrimary 后放行；
/// 3) 设备管理端点：新增建全通道目录、重名 409、容量预检纯函数、禁用连带闭合 signal_lost
///    （cleared_reason=device_disabled）、未禁用不许删、软删后列表可见；
/// 4) SignalHealth 不给被禁用设备的螺栓报 signal_lost（人主动停的数据停摆不是信号丢失）。
/// </summary>
[Collection("postgres")]
public sealed class DeviceLifecycleIntegrationTests
{
    private readonly PostgresFixture _fx;
    public DeviceLifecycleIntegrationTests(PostgresFixture fx) => _fx = fx;

    // ===================== supervisor 对账（全内存，不碰 TCP） =====================

    [Fact]
    public async Task Supervisor_HotAddStartsConsumption_HotRemoveStopsAndDisposes()
    {
        var mgr = new MutableFakeDeviceManager();
        var c1 = new FakeConnector(deviceId: 1);
        mgr.Add(c1);

        var registry = new IngestionTaskRegistry();
        var sink = new RecordingSink();
        var svc = new IngestionService(
            mgr, sink, new NoopPublisher(), new NoopWal(),
            new ChannelPresenceTracker(Microsoft.Extensions.Options.Options.Create(new ChannelPresenceOptions())),
            registry, new BoltPreload.Application.Diagnostics.BackgroundServiceHealth(),
            NullLogger<IngestionService>.Instance);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => registry.IsRunning(1), "启动首轮对账应拉起设备 1");

            // 热加设备 2：下一拍对账起任务，数据能进 sink
            var c2 = new FakeConnector(deviceId: 2);
            mgr.Add(c2);
            await WaitUntilAsync(() => registry.IsRunning(2), "热加的设备应在对账周期内被拉起");
            c2.Emit(Measurement(deviceId: 2, channel: 1));
            await WaitUntilAsync(() => sink.Count(2) == 1, "热加设备的数据应流进 sink");

            // 热摘设备 2：任务停、连接器释放；设备 1 不受影响
            mgr.Remove(2);
            await WaitUntilAsync(() => !registry.IsRunning(2), "摘除后消费任务应停止");
            await WaitUntilAsync(() => c2.Disposed, "摘除后连接器必须被释放（不释放就是半开 TCP 会话）");

            c1.Emit(Measurement(deviceId: 1, channel: 1));
            await WaitUntilAsync(() => sink.Count(1) == 1, "摘除设备 2 不应影响设备 1 的采集");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    // ===================== 真机模式 manager 写侧（构造连接器但不连接） =====================

    [PostgresFact]
    public async Task RealModeManager_AddRemoveRestart_PrimaryGuard()
    {
        _fx.EnsureAvailable();

        // 先有一台启用设备，用 Fx:Real:DeviceId 显式指定它为 primary——
        // 夹具库被整个集合共享，别的测试也播设备行，不锁定的话 primary 会落到 id 最小的那台上
        var (projectId, firstId) = await SeedDeviceRowAsync(code: "LIFE-A-" + Tag());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fx:Real:DeviceId"] = firstId.ToString(),
            })
            .Build();
        var registry = new IngestionTaskRegistry();
        var mgr = new DeviceConnectorManager(config, _fx.DataSource, NullLoggerFactory.Instance, registry);
        try
        {
            mgr.Find(firstId).Should().NotBeNull("构造时应装入表里的启用设备");

            // 构造之后插的行：AddAsync 热加（只建不连）
            var (_, secondId) = await SeedDeviceRowAsync(code: "LIFE-B-" + Tag(), projectId);
            (await mgr.AddAsync(secondId)).Should().BeTrue();
            mgr.Find(secondId).Should().NotBeNull();
            (await mgr.AddAsync(secondId)).Should().BeTrue("重复加同一台是幂等");

            // 禁用行加不进来
            await ExecAsync("UPDATE devices SET enabled = false WHERE id = @arg", secondId);
            (await mgr.RemoveAndStopAsync(secondId, TimeSpan.FromSeconds(1))).Should().BeTrue();
            (await mgr.AddAsync(secondId)).Should().BeFalse("未启用的行不建运行时");
            await ExecAsync("UPDATE devices SET enabled = true WHERE id = @arg", secondId);
            (await mgr.AddAsync(secondId)).Should().BeTrue();

            // primary 摘除被拒；转移后放行
            var primaryGuard = async () => await mgr.RemoveAndStopAsync(firstId, TimeSpan.FromSeconds(1));
            await primaryGuard.Should().ThrowAsync<InvalidOperationException>("primary 不许直接摘除");
            mgr.TransferPrimary(secondId).Should().BeTrue();
            mgr.Primary.DeviceId.Should().Be(secondId);
            (await mgr.RemoveAndStopAsync(firstId, TimeSpan.FromSeconds(1))).Should().BeTrue("转移后原 primary 可摘");

            // 热重启：摘了重建，primary 标记跟着回来
            (await mgr.RestartAsync(secondId, TimeSpan.FromSeconds(1))).Should().BeTrue();
            mgr.Primary.DeviceId.Should().Be(secondId, "primary 重启后自动恢复标记");
        }
        finally
        {
            await mgr.DisposeAsync();
        }
    }

    // ===================== 管理端点 =====================

    [PostgresFact]
    public async Task DeviceEndpoints_CreateCatalog_DuplicateCode_DisableCleanup_DeleteGuard()
    {
        _fx.EnsureAvailable();
        var adminId = await SeedUserAsync();
        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, adminId, "admin");

        // 新增：插行 + 建全通道目录 + 热加运行时（只建不连，连接由 IngestionService 对账拉起，
        // 而 TestApi 已把后台服务全拔了，所以这里不会真去拨号）。
        // 夹具库有一堆别的测试播的项目，多项目环境必须显式给 projectId
        long projectId;
        await using (var p = _fx.DataSource.CreateCommand(
            "INSERT INTO projects (code, name, industry, sla_level) VALUES (@c, '设备端点', '风电', 'standard') RETURNING id"))
        {
            p.Parameters.AddWithValue("c", NpgsqlDbType.Varchar, "P-" + Tag());
            projectId = (long)(await p.ExecuteScalarAsync())!;
        }
        var code = "DEV-" + Tag();
        var create = await client.PostAsJsonAsync("/api/devices/", new
        {
            code,
            commParams = "192.168.77.10:23",
            channelCount = 4,
            projectId,
        });
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var deviceId = created.GetProperty("id").GetInt64();
        created.GetProperty("runtimeAttached").GetBoolean().Should().BeTrue("新建设备应热加运行时，不必重启进程");
        (await ScalarAsync<long>("SELECT count(*) FROM channels WHERE device_id = @arg AND deleted_at IS NULL", deviceId))
            .Should().Be(4, "通道目录一次建全");

        // 项目内重名 409；参数不合法 400
        (await client.PostAsJsonAsync("/api/devices/", new { code, commParams = "192.168.77.11:23", channelCount = 4, projectId }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync("/api/devices/", new { code = "X-" + Tag(), commParams = "192.168.77.12:23", channelCount = 0, projectId }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 未禁用不许删
        (await client.DeleteAsync($"/api/devices/{deviceId}"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "删除的前置条件是已禁用");

        // 禁用连带闭合 signal_lost：给设备挂一颗绑定螺栓和一条开启告警
        var (groupId, boltId) = await SeedGroupAndBoltOnChannelAsync(deviceId, channelNo: 1);
        await ExecAsync(@"INSERT INTO alarms (bolt_id, channel_id, type, severity, triggered_at, message)
            SELECT @arg, b.channel_id, 'signal_lost', 'warn', now(), '生命周期测试' FROM bolts b WHERE b.id = @arg", boltId);

        var disable = await client.PostAsync($"/api/devices/{deviceId}/disable", null);
        disable.StatusCode.Should().Be(HttpStatusCode.OK);
        (await disable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closedSignalLost").GetInt32()
            .Should().Be(1, "禁用必须连带闭合名下 signal_lost");
        (await ScalarAsync<string>(
            "SELECT cleared_reason FROM alarms WHERE bolt_id = @arg AND type = 'signal_lost'", boltId))
            .Should().Be("device_disabled");
        (await ScalarAsync<bool>("SELECT enabled FROM devices WHERE id = @arg", deviceId)).Should().BeFalse();

        // 软删后默认列表不显示，includeDeleted=true 可见
        (await client.DeleteAsync($"/api/devices/{deviceId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var listDefault = await client.GetFromJsonAsync<JsonElement>("/api/devices/");
        listDefault.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetInt64() == deviceId)
            .Should().BeFalse("软删行默认不列");
        var listAll = await client.GetFromJsonAsync<JsonElement>("/api/devices/?includeDeleted=true");
        listAll.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetInt64() == deviceId)
            .Should().BeTrue();

        // 全程审计落了行
        (await ScalarAsync<long>(
            "SELECT count(*) FROM audit_log WHERE target_type = 'device' AND target_id = @arg " +
            "AND action IN ('device_create','device_disable','device_delete')", deviceId))
            .Should().Be(3);
    }

    [Fact]
    public void LicenseCapacity_CheckAdd_DeviceAndChannelLimits()
    {
        LicenseCapacity.CheckAdd(1, 12, 12, maxDevices: 2, maxChannels: 40).Should().BeNull("加完正好在容量内");
        LicenseCapacity.CheckAdd(2, 12, 12, maxDevices: 2, maxChannels: 40).Should().Contain("设备数");
        LicenseCapacity.CheckAdd(1, 30, 12, maxDevices: 2, maxChannels: 40).Should().Contain("通道数");
        LicenseCapacity.CheckAdd(5, 200, 48, maxDevices: null, maxChannels: null).Should().BeNull("无上限授权不拦");
    }

    // ===================== SignalHealth 跳过禁用设备 =====================

    [PostgresFact]
    public async Task SignalHealth_SkipsBoltsOfDisabledDevice()
    {
        _fx.EnsureAvailable();
        var (_, deviceId) = await SeedDeviceRowAsync(code: "SIG-" + Tag());
        var (_, boltId) = await SeedGroupAndBoltOnChannelAsync(deviceId, channelNo: 1);
        // 最后一条测量在 10 分钟前：静默超过 NoSignalSeconds，正常应报 signal_lost
        await ExecAsync(@"INSERT INTO measurements (ts, channel_id, bolt_id, gain, status_code)
            SELECT now() - interval '10 minutes', b.channel_id, b.id, 16, 0 FROM bolts b WHERE b.id = @arg", boltId);

        var svc = NewSignalHealth();

        await ExecAsync("UPDATE devices SET enabled = false WHERE id = @arg", deviceId);
        await svc.CheckAsync(default);
        (await ScalarAsync<long>("SELECT count(*) FROM alarms WHERE bolt_id = @arg AND type = 'signal_lost'", boltId))
            .Should().Be(0, "设备是人主动禁用的，数据停摆不是信号丢失");

        await ExecAsync("UPDATE devices SET enabled = true WHERE id = @arg", deviceId);
        await svc.CheckAsync(default);
        (await ScalarAsync<long>("SELECT count(*) FROM alarms WHERE bolt_id = @arg AND type = 'signal_lost' AND cleared_at IS NULL", boltId))
            .Should().Be(1, "重新启用后真静默照常报");
    }

    // ===================== helpers =====================

    private SignalHealthService NewSignalHealth() =>
        new(_fx.DataSource,
            new BoltPreload.Infrastructure.Alarm.PostgresAlarmSink(_fx.DataSource, NullLogger<BoltPreload.Infrastructure.Alarm.PostgresAlarmSink>.Instance),
            new NoopAlarmPublisher(),
            new BoltPreload.Application.Devices.ProbeScan.ProbeScanState(),
            NoopMeasurementWal.Instance,
            new BoltPreload.Application.Diagnostics.BackgroundServiceHealth(),
            Microsoft.Extensions.Options.Options.Create(new SignalHealthOptions()),
            NullLogger<SignalHealthService>.Instance);

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        condition().Should().BeTrue(because);
    }

    private static FxDecodedEnvelope Measurement(long deviceId, ushort channel)
    {
        var ts = DateTimeOffset.UtcNow;
        var cr = new FxChannelResultDto(
            Address: 1, Channel: channel, StatusCode: 0,
            EchoTimeNs: 32000m, EchoTime2Ns: null, SpTempC: 20m, DeltaTofNs: null,
            Gain: 16f, EchoWave: null, LowAi: null, HighAi: null, Angle: null, MidResults: null);
        return new FxDecodedEnvelope(deviceId, ts, ts, FxResultType.ChannelResult, ChannelResult: cr);
    }

    private async Task<(long ProjectId, long DeviceId)> SeedDeviceRowAsync(string code, long? projectId = null)
    {
        if (projectId is null)
        {
            await using var p = _fx.DataSource.CreateCommand(
                "INSERT INTO projects (code, name, industry, sla_level) VALUES (@c, '生命周期', '风电', 'standard') RETURNING id");
            p.Parameters.AddWithValue("c", NpgsqlDbType.Varchar, "P-" + Tag());
            projectId = (long)(await p.ExecuteScalarAsync())!;
        }
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO devices (project_id, code, model, comm_type, comm_params, address, transfer_mode, channel_count, state)
            VALUES (@p, @c, 3, 1, '127.0.0.9:23', 1, 1, 4, 'unknown') RETURNING id");
        cmd.Parameters.AddWithValue("p", NpgsqlDbType.Bigint, projectId.Value);
        cmd.Parameters.AddWithValue("c", NpgsqlDbType.Varchar, code);
        var deviceId = (long)(await cmd.ExecuteScalarAsync())!;

        await using var ch = _fx.DataSource.CreateCommand(@"
            INSERT INTO channels (device_id, channel_no, state, business_state, hardware_state)
            SELECT @d, gs.ch, 'manual', 'unbound', 'unknown' FROM generate_series(1, 4) AS gs(ch)
            ON CONFLICT DO NOTHING");
        ch.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
        await ch.ExecuteNonQueryAsync();
        return (projectId.Value, deviceId);
    }

    /// <summary>在设备的指定通道上建部位 + 螺栓（signal_lost 清理与 SignalHealth 过滤用）。</summary>
    private async Task<(long GroupId, long BoltId)> SeedGroupAndBoltOnChannelAsync(long deviceId, short channelNo)
    {
        await using var g = _fx.DataSource.CreateCommand(@"
            INSERT INTO monitor_groups (project_id, code, name)
            SELECT d.project_id, @c, '生命周期组' FROM devices d WHERE d.id = @d RETURNING id");
        g.Parameters.AddWithValue("c", NpgsqlDbType.Varchar, "G-" + Tag());
        g.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
        var groupId = (long)(await g.ExecuteScalarAsync())!;

        await using var b = _fx.DataSource.CreateCommand(@"
            INSERT INTO bolts (group_id, channel_id, code, nominal_diam, grade, rp02_mpa, material,
                               elastic_modulus_gpa, effective_length_mm, total_length_mm, cross_section_mm2, state)
            SELECT @g, c.id, @code, 'M20', '10.9', 940, '42CrMo', 206, 100, 120, 245, 'uncalibrated'
            FROM channels c WHERE c.device_id = @d AND c.channel_no = @n AND c.deleted_at IS NULL
            RETURNING id");
        b.Parameters.AddWithValue("g", NpgsqlDbType.Bigint, groupId);
        b.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, "B-" + Tag());
        b.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
        b.Parameters.AddWithValue("n", NpgsqlDbType.Smallint, channelNo);
        var boltId = (long)(await b.ExecuteScalarAsync())!;
        return (groupId, boltId);
    }

    private async Task<long> SeedUserAsync()
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "INSERT INTO users (username, password_hash, role) VALUES (@u, 'seed', 'admin') RETURNING id");
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, "u_" + Guid.NewGuid().ToString("N")[..12]);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task ExecAsync(string sql, long? arg = null)
    {
        await using var cmd = _fx.DataSource.CreateCommand(sql);
        if (arg is not null) cmd.Parameters.AddWithValue("arg", NpgsqlDbType.Bigint, arg.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, long? arg = null)
    {
        await using var cmd = _fx.DataSource.CreateCommand(sql);
        if (arg is not null) cmd.Parameters.AddWithValue("arg", NpgsqlDbType.Bigint, arg.Value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    // ---- supervisor 对账用的可变假管理器与轻量假件 ----

    private sealed class MutableFakeDeviceManager : IDeviceConnectorManager
    {
        private readonly object _gate = new();
        private readonly Dictionary<long, DeviceRuntimeContext> _map = new();

        public void Add(IFxConnector connector)
        {
            lock (_gate)
                _map[connector.DeviceId] = new DeviceRuntimeContext(
                    connector.DeviceId, $"fake-{connector.DeviceId}", _map.Count == 0, connector,
                    new IngestionRuntimeStatus(), new DeviceRuntimeOptions("n/a", 1, 1, 3, 3));
        }

        public void Remove(long deviceId)
        {
            lock (_gate) _map.Remove(deviceId);
        }

        public IReadOnlyCollection<DeviceRuntimeContext> Runtimes
        {
            get { lock (_gate) return _map.Values.ToArray(); }
        }

        public DeviceRuntimeContext Primary
        {
            get { lock (_gate) return _map.Values.First(); }
        }

        public DeviceRuntimeContext? Find(long deviceId)
        {
            lock (_gate) return _map.TryGetValue(deviceId, out var rt) ? rt : null;
        }

        public bool TryUpdateChannelCount(long deviceId, ushort channelCount) => false;
    }

    private sealed class RecordingSink : IMeasurementSink
    {
        private readonly List<FxDecodedEnvelope> _written = [];

        public int Count(long deviceId)
        {
            lock (_written) return _written.Count(e => e.DeviceId == deviceId);
        }

        public Task WriteAsync(FxDecodedEnvelope envelope, CancellationToken ct = default)
            => WriteAsync(envelope, evaluateAlarms: true, ct);

        public Task WriteAsync(FxDecodedEnvelope envelope, bool evaluateAlarms, CancellationToken ct = default)
        {
            lock (_written) _written.Add(envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopPublisher : IMeasurementPublisher
    {
        public Task PublishAsync(FxDecodedEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopWal : IMeasurementWal
    {
        public bool Enabled => false;
        public long PendingCount => 0;
        public ValueTask AppendAsync(FxDecodedEnvelope envelope, CancellationToken ct = default) => ValueTask.CompletedTask;
        public Task<int> ReplayAsync(Func<FxDecodedEnvelope, CancellationToken, Task> writeAsync, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}
