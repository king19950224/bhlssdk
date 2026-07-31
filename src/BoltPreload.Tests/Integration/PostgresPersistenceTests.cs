using BoltPreload.Application.Ingestion;
using BoltPreload.Domain.Entities;
using BoltPreload.Fx.Models;
using BoltPreload.Infrastructure.Ingestion;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 数据库集成测试：补上"持久层 / EF 映射↔手写 schema 漂移 / WAL 重放对真库"此前完全没有的自动化覆盖。
/// 复用 CI 的 TimescaleDB（或本地 dev 库）；无库时整组自动 Skip。
/// </summary>
[Collection("postgres")]
public sealed class PostgresPersistenceTests
{
    private readonly PostgresFixture _fx;
    public PostgresPersistenceTests(PostgresFixture fx) => _fx = fx;

    // ---- EF 模型 ↔ 手写 schema 漂移守卫 ----
    [PostgresFact]
    public async Task EfModel_columns_match_handwritten_schema()
    {
        _fx.EnsureAvailable();
        await using var db = _fx.NewDbContext();

        // Take(1).ToList 会 SELECT 全部映射列：任一 HasColumnName 与 deploy/sql 不一致即抛 PostgresException。
        // 这正是"raw SQL 迁移 + 手写 EF 映射"最容易悄悄漂移、且之前零测试覆盖的点。
        var act = async () =>
        {
            await db.Projects.AsNoTracking().Take(1).ToListAsync();
            await db.MonitorGroups.AsNoTracking().Take(1).ToListAsync();
            await db.Devices.AsNoTracking().Take(1).ToListAsync();
            await db.Channels.AsNoTracking().Take(1).ToListAsync();
            await db.Bolts.AsNoTracking().Take(1).ToListAsync();
        };
        await act.Should().NotThrowAsync("EF 映射的每个列都应在 deploy/sql schema 中存在且类型兼容");
    }

    // ---- 已绑定通道：测量入 measurements，并写入测量级信号健康字段（P2 新增列） ----
    [PostgresFact]
    public async Task Sink_writes_bound_measurement_with_health_fields()
    {
        _fx.EnsureAvailable();

        var (deviceId, channelId) = await SeedBoundChannelAsync(channelNo: 9);
        var sink = NewSink();

        var env = Measurement(deviceId, channel: 9, DateTimeOffset.UtcNow, BurstWave());
        await sink.WriteAsync(env, evaluateAlarms: false);

        await using var cmd = _fx.DataSource.CreateCommand(
            "SELECT echo_time_ns, snr_db, main_freq_mhz, health_score FROM measurements WHERE channel_id = @c");
        cmd.Parameters.AddWithValue("c", NpgsqlDbType.Bigint, channelId);
        await using var r = await cmd.ExecuteReaderAsync();
        (await r.ReadAsync()).Should().BeTrue("已绑定通道的正常测量应写入 measurements");
        r.GetDecimal(0).Should().BeGreaterThan(0);                 // echo_time_ns
        r.IsDBNull(1).Should().BeFalse("带波形时 snr_db 应已计算入库");      // snr_db
        r.IsDBNull(2).Should().BeFalse("带波形时 main_freq_mhz 应已计算入库"); // main_freq_mhz
        r.IsDBNull(3).Should().BeFalse("带波形时 health_score 应已计算入库");  // health_score
    }

    // ---- 未绑定通道：自动发现 + 写 unbound_measurements ----
    [PostgresFact]
    public async Task Sink_autodiscovers_and_writes_unbound_measurement()
    {
        _fx.EnsureAvailable();

        var deviceId = await SeedDeviceAsync();   // 只建设备，不建通道/螺栓
        var sink = NewSink();

        await sink.WriteAsync(Measurement(deviceId, channel: 7, DateTimeOffset.UtcNow), evaluateAlarms: false);

        (await UnboundCountAsync(deviceId)).Should().Be(1, "新通道应被自动发现并落 unbound_measurements");
        // 自动发现应在 channels 表建出该通道
        await using var cmd = _fx.DataSource.CreateCommand(
            "SELECT count(*) FROM channels WHERE device_id=@d AND channel_no=7 AND state='auto_discovered'");
        cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
        ((long)(await cmd.ExecuteScalarAsync())!).Should().Be(1);
    }

    // ---- 自动发现墓碑：被显式软删的通道不得被数据流"悄悄"复活（否则运维的删除操作永远不生效）----
    // 这正是 dev 库里观察到的行为：设备周期上报全部物理通道，软删通道若被自动重建，删除就形同虚设。
    [PostgresFact]
    public async Task Sink_does_not_revive_soft_deleted_channel()
    {
        _fx.EnsureAvailable();

        var deviceId = await SeedDeviceAsync();
        // 显式软删一个通道（模拟运维删除空通道：deleted_at 置位）
        await using (var del = _fx.DataSource.CreateCommand(
            @"INSERT INTO channels (device_id, channel_no, state, first_seen_at, last_seen_at, deleted_at)
              VALUES (@d, 11, 'manual', now(), now(), now())"))
        {
            del.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
            await del.ExecuteNonQueryAsync();
        }

        var sink = NewSink();
        await sink.WriteAsync(Measurement(deviceId, channel: 11, DateTimeOffset.UtcNow), evaluateAlarms: false);

        // ① 不得新建/复活 live 通道（deleted_at IS NULL 的行数应为 0）
        await using (var live = _fx.DataSource.CreateCommand(
            "SELECT count(*) FROM channels WHERE device_id=@d AND channel_no=11 AND deleted_at IS NULL"))
        {
            live.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
            ((long)(await live.ExecuteScalarAsync())!).Should().Be(0, "软删通道不得被数据流自动复活");
        }
        // ② 无法解析的软删通道测量应被直接丢弃，不落 unbound_measurements
        (await UnboundCountAsync(deviceId)).Should().Be(0, "解析不出通道的测量应丢弃，不得落 unbound");
    }

    // ---- WAL 重放对真库：入库失败兜底链路的端到端验证 ----
    [PostgresFact]
    public async Task WalReplay_persists_to_real_db()
    {
        _fx.EnsureAvailable();

        var deviceId = await SeedDeviceAsync();
        var sink = NewSink();
        var tempRoot = Path.Combine(Path.GetTempPath(), "walint_" + Guid.NewGuid().ToString("N"));
        try
        {
            var wal = new LocalFileMeasurementWal(
                Options.Create(new MeasurementWalOptions { RootPath = tempRoot, MaxEntries = 100 }),
                NullLogger<LocalFileMeasurementWal>.Instance);

            await wal.AppendAsync(Measurement(deviceId, channel: 3, DateTimeOffset.UtcNow));
            var replayed = await wal.ReplayAsync((e, ct) => sink.WriteAsync(e, evaluateAlarms: false, ct));

            replayed.Should().Be(1);
            wal.PendingCount.Should().Be(0);
            (await UnboundCountAsync(deviceId)).Should().Be(1, "WAL 重放应把测量补写进真库");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    // ===================== helpers =====================

    private PostgresMeasurementSink NewSink()
    {
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10_000 });
        var resolver = new CachedChannelResolver(_fx.DataSource, cache, NullLogger<CachedChannelResolver>.Instance);
        // waveStore=null：不落波形文件；measurements 的 snr_db/main_freq/health_score 仍由 cr.EchoWave 直接计算入库
        return new PostgresMeasurementSink(_fx.DataSource, resolver, NullLogger<PostgresMeasurementSink>.Instance);
    }

    private async Task<long> SeedDeviceAsync()
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.NewDbContext();
        var tag = Guid.NewGuid().ToString("N")[..10];
        var project = new Project { Code = $"P-{tag}", Name = "集成测试项目", Industry = "风电", SlaLevel = "standard", CreatedAt = now, UpdatedAt = now };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var device = new Device
        {
            ProjectId = project.Id, Code = $"D-{tag}", Model = 3, CommType = 1, CommParams = "in-process",
            Address = 1, TransferMode = 1, ChannelCount = 40, State = "online", CreatedAt = now, UpdatedAt = now,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<(long deviceId, long channelId)> SeedBoundChannelAsync(short channelNo)
    {
        var now = DateTimeOffset.UtcNow;
        var deviceId = await SeedDeviceAsync();
        await using var db = _fx.NewDbContext();

        // 监测组（bolts.group_id FK）
        var pid = await db.Devices.Where(d => d.Id == deviceId).Select(d => d.ProjectId).SingleAsync();
        var group = new MonitorGroup { ProjectId = pid, Code = $"G-{deviceId}", Name = "测试组", CreatedAt = now, UpdatedAt = now };
        db.MonitorGroups.Add(group);
        await db.SaveChangesAsync();

        var channel = new Channel
        {
            DeviceId = deviceId, ChannelNo = channelNo, ProbeFreqMhz = 2.0m, Gain = 16, MeasMode = 2, TcMode = 1,
            TofAlgorithm = "ThrSignal", HardwareState = "healthy", BusinessState = "bound", ConfigSource = "database",
            State = "manual", DiscoveredAt = now, CreatedAt = now, UpdatedAt = now,
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var bolt = new Bolt
        {
            GroupId = group.Id, ChannelId = channel.Id, Code = $"B-{deviceId}", NominalDiam = "M20", Grade = "10.9",
            Rp02Mpa = 940m, Material = "42CrMo", ElasticModulusGpa = 206m, EffectiveLengthMm = 100m, TotalLengthMm = 120m,
            CrossSectionMm2 = 245m, SoundVelocityMs = 5890m, TargetPreloadN = 172_000m, MinPreloadN = 120_000m,
            State = "uncalibrated", CreatedAt = now, UpdatedAt = now,
        };
        db.Bolts.Add(bolt);
        await db.SaveChangesAsync();

        return (deviceId, channel.Id);
    }

    private async Task<long> UnboundCountAsync(long deviceId)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "SELECT count(*) FROM unbound_measurements um JOIN channels c ON c.id = um.channel_id WHERE c.device_id = @d");
        cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static FxDecodedEnvelope Measurement(long deviceId, ushort channel, DateTimeOffset ts, short[]? wave = null)
    {
        FxEchoWaveDto? echo = wave is null ? null : new FxEchoWaveDto(0, 23000, 25, wave, -490);
        var cr = new FxChannelResultDto(
            Address: 1, Channel: channel, StatusCode: 0,
            EchoTimeNs: 32100m, EchoTime2Ns: null, SpTempC: 20.0m, DeltaTofNs: 100m,
            Gain: 16f, EchoWave: echo, LowAi: null, HighAi: null, Angle: null, MidResults: null);
        return new FxDecodedEnvelope(deviceId, ts, ts, FxResultType.ChannelResult, ChannelResult: cr);
    }

    // 安静噪声段 + 衰减正弦回波，保证 snr_db/main_freq/health_score 都能算出非空值
    private static short[] BurstWave()
    {
        const int n = 256;
        var data = new short[n];
        var rnd = new Random(20260615);
        for (int i = 0; i < n; i++)
        {
            double noise = (rnd.NextDouble() - 0.5) * 4;     // ±2 噪声底
            double sig = 0;
            if (i >= 64)
            {
                double env = Math.Exp(-(i - 64) / 40.0);     // 衰减包络
                sig = 120 * env * Math.Sin(2 * Math.PI * (i - 64) / 16.0);  // ~周期16点
            }
            data[i] = (short)Math.Round(noise + sig);
        }
        return data;
    }
}
