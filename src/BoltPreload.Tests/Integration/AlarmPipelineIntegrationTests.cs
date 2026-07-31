using System.Net;
using System.Net.Http.Json;
using BoltPreload.Api.Runtime;
using BoltPreload.Application.Alarm;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Application.Diagnostics;
using BoltPreload.Application.Ingestion;
using BoltPreload.Application.Realtime;
using BoltPreload.Domain.Entities;
using BoltPreload.Fx.Models;
using BoltPreload.Infrastructure.Alarm;
using BoltPreload.Infrastructure.Ingestion;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 告警管道对真库的集成测试（无 DB 时自动 Skip）。锁定的修复：
/// 1) 重复投递（ON CONFLICT DO NOTHING, rowsAffected==0）不再推进告警状态机的连续计数；
/// 2) 生效的维护抑制(shelve)吞掉触发；已过期的抑制不生效；
/// 3) signal_lost 触发也走抑制判定（此前 SignalHealthService 绕过 shelve）；
/// 4) 手动闭合复位评估器状态机：条件仍越限时下一条样本立即重新触发；
/// 5) 闭合原因放宽为 text（>64 字不再 500），API 层限 500 字。
/// 标定基线设计：KL=0.001 ns/N、RefTime=30000ns、echo=30100ns（温度=参考温度→温补中性）
/// → deltaTof=100 → preloadN=100000N；螺栓 target=172000/min=120000 → ratio≈0.58(&lt;0.70 触发 preload_low)，
/// 应力=408MPa ∈ [30%,90%]×940 → 落在 GB 适用范围内（保证真出 preload_n / ratio）。
/// </summary>
[Collection("postgres")]
public sealed class AlarmPipelineIntegrationTests
{
    private readonly PostgresFixture _fx;
    public AlarmPipelineIntegrationTests(PostgresFixture fx) => _fx = fx;

    private const decimal Kl = 0.001m;
    private const decimal RefTimeNs = 30000m;
    private const decimal RefTempC = 20m;
    private const decimal LowEchoNs = 30100m;   // → preloadN 100kN → ratio ≈ 0.58

    [PostgresFact]
    public async Task FiveLowSamples_OpenPreloadLowAlarm()
    {
        _fx.EnsureAvailable();

        var (deviceId, _, boltId, _) = await SeedCalibratedChannelAsync(channelNo: 21);
        var sink = NewSinkWithAlarms();

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 5; i++)
            await sink.WriteAsync(LowMeasurement(deviceId, 21, t0.AddSeconds(i)), evaluateAlarms: true);

        (await OpenAlarmCountAsync(boltId, "preload_low")).Should().Be(1, "连续 5 条越限样本应触发 preload_low");
    }

    [PostgresFact]
    public async Task DuplicateTimestamp_DoesNotAdvanceAlarmStreak()
    {
        _fx.EnsureAvailable();

        var (deviceId, _, boltId, _) = await SeedCalibratedChannelAsync(channelNo: 22);
        var sink = NewSinkWithAlarms();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);

        // 4 条不同 ts 的越限样本 → 连续计数 4（默认触发阈值 5，尚未触发）
        for (var i = 0; i < 4; i++)
            await sink.WriteAsync(LowMeasurement(deviceId, 22, t0.AddSeconds(i)), evaluateAlarms: true);

        // 重复第 4 条 ts（ON CONFLICT DO NOTHING）→ 不得推进连续计数
        await sink.WriteAsync(LowMeasurement(deviceId, 22, t0.AddSeconds(3)), evaluateAlarms: true);
        (await OpenAlarmCountAsync(boltId, "preload_low"))
            .Should().Be(0, "重复投递未写入新行，不应把连续计数推到阈值");

        // 第 5 条新 ts → 连续 5 → 触发
        await sink.WriteAsync(LowMeasurement(deviceId, 22, t0.AddSeconds(4)), evaluateAlarms: true);
        (await OpenAlarmCountAsync(boltId, "preload_low")).Should().Be(1);
    }

    [PostgresFact]
    public async Task ActiveShelve_SuppressesAlarm()
    {
        _fx.EnsureAvailable();

        var (deviceId, _, boltId, userId) = await SeedCalibratedChannelAsync(channelNo: 23);
        await InsertShelveAsync(boltId, "preload_low", userId, shelvedAt: null,
            expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        var sink = NewSinkWithAlarms();

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 6; i++)   // 多写一条，确保跨过阈值
            await sink.WriteAsync(LowMeasurement(deviceId, 23, t0.AddSeconds(i)), evaluateAlarms: true);

        (await OpenAlarmCountAsync(boltId, "preload_low"))
            .Should().Be(0, "生效的维护抑制应吞掉 preload_low 触发");
    }

    [PostgresFact]
    public async Task ExpiredShelve_DoesNotSuppress()
    {
        _fx.EnsureAvailable();

        var (deviceId, _, boltId, userId) = await SeedCalibratedChannelAsync(channelNo: 24);
        await InsertShelveAsync(boltId, "preload_low", userId,
            shelvedAt: DateTimeOffset.UtcNow.AddHours(-2), expiresAt: DateTimeOffset.UtcNow.AddHours(-1));
        var sink = NewSinkWithAlarms();

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 5; i++)
            await sink.WriteAsync(LowMeasurement(deviceId, 24, t0.AddSeconds(i)), evaluateAlarms: true);

        (await OpenAlarmCountAsync(boltId, "preload_low"))
            .Should().Be(1, "已过期的抑制不应生效");
    }

    // ===================== signal_lost 走抑制判定 =====================

    [PostgresFact]
    public async Task SignalLost_ShelvedBolt_NotTriggered_UnshelvedBolt_Triggered()
    {
        _fx.EnsureAvailable();

        // 两颗螺栓都"曾有测量、现已静默 10 分钟"；A 整颗抑制（alarm_type=NULL 通配），B 不抑制
        var (devA, _, boltA, userId) = await SeedCalibratedChannelAsync(channelNo: 25);
        var (devB, _, boltB, _) = await SeedCalibratedChannelAsync(channelNo: 26);
        await InsertShelveAsync(boltA, type: null, userId, shelvedAt: null,
            expiresAt: DateTimeOffset.UtcNow.AddHours(1));

        // 各写一条测量确立 last_ts（单条不会推进到 on-delay 阈值，不产生预紧力告警噪声）
        var sink = NewSinkWithAlarms();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        await sink.WriteAsync(LowMeasurement(devA, 25, t0), evaluateAlarms: true);
        await sink.WriteAsync(LowMeasurement(devB, 26, t0), evaluateAlarms: true);

        var svc = new SignalHealthService(
            _fx.DataSource,
            new PostgresAlarmSink(_fx.DataSource, NullLogger<PostgresAlarmSink>.Instance),
            new NoopAlarmPublisher(),
            new ProbeScanState(),
            new BackgroundServiceHealth(),
            Options.Create(new SignalHealthOptions()),   // 默认 120s 无回波判丢失，10 分钟静默必命中
            NullLogger<SignalHealthService>.Instance);
        await svc.CheckAsync(default);

        (await OpenAlarmCountAsync(boltA, "signal_lost"))
            .Should().Be(0, "整颗抑制生效期间 signal_lost 不得触发（此前 SignalHealthService 绕过 shelve）");
        (await OpenAlarmCountAsync(boltB, "signal_lost"))
            .Should().Be(1, "未抑制的静默螺栓应正常触发 signal_lost");
    }

    // ===================== 手动闭合复位状态机（重臂） =====================

    [PostgresFact]
    public async Task ManualClear_WhileStillViolating_RetriggersOnNextSample()
    {
        _fx.EnsureAvailable();

        var (deviceId, _, boltId, _) = await SeedCalibratedChannelAsync(channelNo: 27);
        var operatorId = await SeedUserAsync();

        // 用 API 工厂内的同一 sink/evaluator：手动闭合端点必须复位的正是这套内存状态机
        using var factory = TestApi.CreateFactory(_fx);
        var sink = factory.Services.GetRequiredService<IMeasurementSink>();

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 5; i++)
            await sink.WriteAsync(LowMeasurement(deviceId, 27, t0.AddSeconds(i)), evaluateAlarms: true);
        (await OpenAlarmCountAsync(boltId, "preload_low")).Should().Be(1);

        long alarmId;
        await using (var q = _fx.DataSource.CreateCommand(
            "SELECT id FROM alarms WHERE bolt_id=@b AND type='preload_low' AND cleared_at IS NULL"))
        {
            q.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
            alarmId = (long)(await q.ExecuteScalarAsync())!;
        }

        // 条件仍越限时手动闭合 → 状态机复位 → 下一条越限样本立即重新触发
        using var client = TestApi.AuthedClient(factory, operatorId, "operator");
        var resp = await client.PostAsJsonAsync($"/api/alarms/{alarmId}/clear", new { reason = "误闭合演练：条件未消除" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await OpenAlarmCountAsync(boltId, "preload_low")).Should().Be(0, "闭合后暂无开启告警");

        await sink.WriteAsync(LowMeasurement(deviceId, 27, t0.AddSeconds(5)), evaluateAlarms: true);
        (await OpenAlarmCountAsync(boltId, "preload_low"))
            .Should().Be(1, "条件仍越限：闭合复位状态机后，一条样本即应重新触发（不再要求完整恢复周期或重启）");
    }

    // ===================== 闭合原因长度（text 迁移 + API 上限） =====================

    [PostgresFact]
    public async Task ManualClear_LongReason_StoredFully_And501Rejected()
    {
        _fx.EnsureAvailable();

        var (_, channelId, boltId, _) = await SeedCalibratedChannelAsync(channelNo: 28);
        var operatorId = await SeedUserAsync();
        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, operatorId, "operator");

        // >64 字符（旧 varchar(64) 会 500）应完整入库
        var longReason = new string('处', 200);
        var a1 = await InsertOpenAlarmAsync(boltId, channelId);
        var ok = await client.PostAsJsonAsync($"/api/alarms/{a1}/clear", new { reason = longReason });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, "闭合原因 200 字须可入库（cleared_reason 已放宽为 text）");
        await using (var q = _fx.DataSource.CreateCommand("SELECT cleared_reason FROM alarms WHERE id=@id"))
        {
            q.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, a1);
            (await q.ExecuteScalarAsync() as string).Should().Be(longReason, "闭合原因不得截断");
        }

        // 超 500 字符 → 400（API 层上限）
        var a2 = await InsertOpenAlarmAsync(boltId, channelId);
        var bad = await client.PostAsJsonAsync($"/api/alarms/{a2}/clear", new { reason = new string('长', 501) });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ===================== shelve alarmType 白名单 =====================

    [PostgresFact]
    public async Task Shelve_UnknownType_Rejected_NullType_Allowed()
    {
        _fx.EnsureAvailable();

        var (_, _, boltId, _) = await SeedCalibratedChannelAsync(channelNo: 29);
        var operatorId = await SeedUserAsync();
        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, operatorId, "operator");

        var bad = await client.PostAsJsonAsync("/api/alarms/shelve",
            new { boltId, alarmType = "preload_lwo", hours = 24, reason = "拼错类型的抑制必须被拒" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest, "未知 type 会生成永远匹配不上的抑制，必须 400");

        var ok = await client.PostAsJsonAsync("/api/alarms/shelve",
            new { boltId, hours = 24, reason = "整颗维护：更换探头窗口" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, "不传 alarmType = 抑制该螺栓全部类型，合法");
    }

    // ===================== helpers =====================

    private PostgresMeasurementSink NewSinkWithAlarms()
    {
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10_000 });
        var resolver = new CachedChannelResolver(_fx.DataSource, cache, NullLogger<CachedChannelResolver>.Instance);
        var evaluator = new StaticThresholdAlarmEvaluator(
            Options.Create(new StaticThresholdAlarmOptions()), NullLogger<StaticThresholdAlarmEvaluator>.Instance);
        var alarmSink = new PostgresAlarmSink(_fx.DataSource, NullLogger<PostgresAlarmSink>.Instance);
        return new PostgresMeasurementSink(
            _fx.DataSource, resolver, NullLogger<PostgresMeasurementSink>.Instance,
            waveStore: null, alarmEvaluator: evaluator, alarmSink: alarmSink, alarmPublisher: null);
    }

    private async Task<long> SeedUserAsync()
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "INSERT INTO users (username, password_hash, role) VALUES (@u, 'seed', 'admin') RETURNING id");
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, "u_" + Guid.NewGuid().ToString("N")[..12]);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(long deviceId, long channelId, long boltId, long userId)> SeedCalibratedChannelAsync(short channelNo)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = await SeedUserAsync();
        var tag = Guid.NewGuid().ToString("N")[..10];

        long channelId, boltId, deviceId;
        await using (var db = _fx.NewDbContext())
        {
            var project = new Project { Code = $"P-{tag}", Name = "告警集成", Industry = "风电", SlaLevel = "standard", CreatedAt = now, UpdatedAt = now };
            db.Projects.Add(project);
            await db.SaveChangesAsync();

            var device = new Device
            {
                ProjectId = project.Id, Code = $"D-{tag}", Model = 3, CommType = 1, CommParams = "in-process",
                Address = 1, TransferMode = 1, ChannelCount = 40, State = "online", CreatedAt = now, UpdatedAt = now,
            };
            db.Devices.Add(device);
            await db.SaveChangesAsync();
            deviceId = device.Id;

            var group = new MonitorGroup { ProjectId = project.Id, Code = $"G-{tag}", Name = "测试组", CreatedAt = now, UpdatedAt = now };
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
            channelId = channel.Id;

            var bolt = new Bolt
            {
                GroupId = group.Id, ChannelId = channel.Id, Code = $"B-{tag}", NominalDiam = "M20", Grade = "10.9",
                Rp02Mpa = 940m, Material = "42CrMo", ElasticModulusGpa = 206m, EffectiveLengthMm = 100m, TotalLengthMm = 120m,
                CrossSectionMm2 = 245m, SoundVelocityMs = 5890m, TargetPreloadN = 172_000m, MinPreloadN = 120_000m,
                State = "calibrated", CreatedAt = now, UpdatedAt = now,
            };
            db.Bolts.Add(bolt);
            await db.SaveChangesAsync();
            boltId = bolt.Id;
        }

        // 标定档案（镜像 SimulatedCalibrationProvisioner 的列集，保证与 schema 一致）+ 通道指向该标定
        Guid calibId;
        await using (var ins = _fx.DataSource.CreateCommand(@"
            INSERT INTO calibration_records
              (bolt_id, method, operator_user_id, reviewer_user_id, points_jsonb,
               k_l, k_l_uncertainty, r_squared, outliers_removed, fitter_version,
               ref_time_ns, ref_temp_c, passed, operator_signature_at, reviewer_signature_at, expires_at, notes)
            VALUES (@b,'quick',@u,@u,'[]'::jsonb,
               @kl,0.00001,0.999,0,'test:1.0',
               @rt,@rtemp,true,now(),now(),(now()+interval '365 days')::date,'集成测试标定')
            RETURNING id"))
        {
            ins.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
            ins.Parameters.AddWithValue("u", NpgsqlDbType.Bigint, userId);
            ins.Parameters.AddWithValue("kl", NpgsqlDbType.Numeric, Kl);
            ins.Parameters.AddWithValue("rt", NpgsqlDbType.Numeric, RefTimeNs);
            ins.Parameters.AddWithValue("rtemp", NpgsqlDbType.Numeric, RefTempC);
            calibId = (Guid)(await ins.ExecuteScalarAsync())!;
        }

        await using (var uc = _fx.DataSource.CreateCommand(@"
            UPDATE channels SET k_l=@kl, k_l_uncertainty=0.00001, ref_time_ns=@rt, ref_temp_c=@rtemp,
                k_l_intercept_ns=NULL, active_calibration_id=@cid WHERE id=@id"))
        {
            uc.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
            uc.Parameters.AddWithValue("kl", NpgsqlDbType.Numeric, Kl);
            uc.Parameters.AddWithValue("rt", NpgsqlDbType.Numeric, RefTimeNs);
            uc.Parameters.AddWithValue("rtemp", NpgsqlDbType.Numeric, RefTempC);
            uc.Parameters.AddWithValue("cid", NpgsqlDbType.Uuid, calibId);
            await uc.ExecuteNonQueryAsync();
        }

        return (deviceId, channelId, boltId, userId);
    }

    /// <summary>type=null 表示整颗螺栓通配抑制（alarm_type IS NULL）。</summary>
    private async Task InsertShelveAsync(long boltId, string? type, long userId, DateTimeOffset? shelvedAt, DateTimeOffset expiresAt)
    {
        var sql = shelvedAt is null
            ? "INSERT INTO alarm_shelvings (bolt_id, alarm_type, shelved_by, expires_at, reason) VALUES (@b,@t,@u,@exp,'集成测试维护抑制')"
            : "INSERT INTO alarm_shelvings (bolt_id, alarm_type, shelved_by, shelved_at, expires_at, reason) VALUES (@b,@t,@u,@sh,@exp,'集成测试维护抑制')";
        await using var cmd = _fx.DataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
        cmd.Parameters.AddWithValue("t", NpgsqlDbType.Varchar, (object?)type ?? DBNull.Value);
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Bigint, userId);
        cmd.Parameters.AddWithValue("exp", NpgsqlDbType.TimestampTz, expiresAt);
        if (shelvedAt is not null) cmd.Parameters.AddWithValue("sh", NpgsqlDbType.TimestampTz, shelvedAt.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>直插一条开启中的 preload_low 告警（绕过状态机，供闭合端点行为测试用）。</summary>
    private async Task<long> InsertOpenAlarmAsync(long boltId, long channelId)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO alarms (bolt_id, channel_id, type, severity, triggered_at, message)
            VALUES (@b, @c, 'preload_low', 'warn', now() - interval '5 minutes', '闭合端点测试')
            RETURNING id");
        cmd.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
        cmd.Parameters.AddWithValue("c", NpgsqlDbType.Bigint, channelId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> OpenAlarmCountAsync(long boltId, string type)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "SELECT count(*) FROM alarms WHERE bolt_id=@b AND type=@t AND cleared_at IS NULL");
        cmd.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
        cmd.Parameters.AddWithValue("t", NpgsqlDbType.Varchar, type);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static FxDecodedEnvelope LowMeasurement(long deviceId, ushort channel, DateTimeOffset ts)
    {
        var cr = new FxChannelResultDto(
            Address: 1, Channel: channel, StatusCode: 0,
            EchoTimeNs: LowEchoNs, EchoTime2Ns: null, SpTempC: RefTempC, DeltaTofNs: null,
            Gain: 16f, EchoWave: null, LowAi: null, HighAi: null, Angle: null, MidResults: null);
        return new FxDecodedEnvelope(deviceId, ts, ts, FxResultType.ChannelResult, ChannelResult: cr);
    }
}
