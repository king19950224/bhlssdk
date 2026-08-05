using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoltPreload.Application.Alarm;
using BoltPreload.Application.Notifications;
using BoltPreload.Domain.Entities;
using BoltPreload.Infrastructure.Alarm;
using BoltPreload.Infrastructure.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 告警出站通知（发件箱）的集成测试。锁定的行为：
/// 1) 入队事务性：路由命中时告警行与发件箱行同事务——发件箱插入失败（快照过期指向已删渠道）
///    告警行必须一起回滚，绝不出现"告警落了库、通知悄悄丢了"的断链；
/// 2) 规则匹配：级别下限、类型过滤、部位过滤、notify_on_clear、静默窗（critical 无视静默）；
/// 3) 派发：同渠道风暴合并成一条消息、失败退避（1 分钟起步）、连败 6 次置 dead、
///    渠道级 min_interval 只顺延不算失败；
/// 4) 管理端点：secret 永不回显、重名 409、smtp/sms 预留值不放行。
/// 每个用例开头清空三张通知表，保证用例间无顺序依赖。
/// </summary>
[Collection("postgres")]
public sealed class AlarmNotificationIntegrationTests
{
    private readonly PostgresFixture _fx;
    public AlarmNotificationIntegrationTests(PostgresFixture fx) => _fx = fx;

    // ===================== 入队事务性 =====================

    [PostgresFact]
    public async Task TriggerWithMatchingRule_WritesAlarmAndOutboxTogether()
    {
        _fx.EnsureAvailable();
        await ClearNotificationTablesAsync();

        var seed = await SeedBoltAsync();
        var channelId = await InsertChannelAsync("ch-" + Tag());
        await InsertRuleAsync(channelId, minSeverity: "warn");

        var router = NewRouter();
        await router.RefreshAsync();
        var sink = new PostgresAlarmSink(_fx.DataSource, NullLogger<PostgresAlarmSink>.Instance, router);

        await sink.PersistAsync(WarnEvent(seed.BoltId, seed.ChannelId));

        (await ScalarAsync<long>("SELECT count(*) FROM alarms WHERE bolt_id = @arg AND cleared_at IS NULL", seed.BoltId))
            .Should().Be(1);
        var payload = await ScalarAsync<string>(
            "SELECT o.payload_jsonb::text FROM notification_outbox o JOIN alarms a ON a.id = o.alarm_id WHERE a.bolt_id = @arg", seed.BoltId);
        payload.Should().Contain(seed.BoltCode, "payload 是入队时的事件快照，带螺栓编号");
        payload.Should().Contain("预紧力偏低", "评估器的中文 message 原样入快照");
    }

    [PostgresFact]
    public async Task StaleRouterSnapshot_OutboxInsertFails_AlarmRolledBack()
    {
        _fx.EnsureAvailable();
        await ClearNotificationTablesAsync();

        var seed = await SeedBoltAsync();
        var channelId = await InsertChannelAsync("ch-" + Tag());
        await InsertRuleAsync(channelId, minSeverity: "warn");

        var router = NewRouter();
        await router.RefreshAsync();
        // 快照刷新后渠道被删（规则随 CASCADE 一起没了）：入队会撞发件箱的渠道 FK
        await ExecAsync("DELETE FROM notification_channels WHERE id = @arg", channelId);

        var sink = new PostgresAlarmSink(_fx.DataSource, NullLogger<PostgresAlarmSink>.Instance, router);
        var act = async () => await sink.PersistAsync(WarnEvent(seed.BoltId, seed.ChannelId));

        await act.Should().ThrowAsync<Exception>("发件箱插入失败必须把异常抛给调用方（C2 契约，状态机据此回滚）");
        (await ScalarAsync<long>("SELECT count(*) FROM alarms WHERE bolt_id = @arg", seed.BoltId))
            .Should().Be(0, "同事务：发件箱写不进去，告警行必须一起回滚");
    }

    // ===================== 规则匹配 =====================

    [PostgresFact]
    public async Task RuleMatching_SeverityTypeGroupFilters()
    {
        _fx.EnsureAvailable();
        await ClearNotificationTablesAsync();

        var seed = await SeedBoltAsync();
        var otherGroupSeed = await SeedBoltAsync();
        var channelId = await InsertChannelAsync("ch-" + Tag());

        // 级别下限：critical 规则收不到 warn 事件
        await InsertRuleAsync(channelId, minSeverity: "critical");
        var router = NewRouter();
        await router.RefreshAsync();
        router.Match(WarnEvent(seed.BoltId, seed.ChannelId)).Should().BeNull("warn 不够 critical 门槛");
        router.Match(CriticalEvent(seed.BoltId, seed.ChannelId)).Should().NotBeNull();

        // 类型过滤：只订 signal_lost 的规则收不到 preload_low
        await ExecAsync("DELETE FROM notification_rules");
        await InsertRuleAsync(channelId, minSeverity: "warn", alarmTypes: new[] { "signal_lost" });
        await router.RefreshAsync();
        router.Match(WarnEvent(seed.BoltId, seed.ChannelId)).Should().BeNull("类型不在订阅集合里");

        // 部位过滤：规则锁在 A 组，B 组螺栓不命中
        await ExecAsync("DELETE FROM notification_rules");
        await InsertRuleAsync(channelId, minSeverity: "warn", groupId: seed.GroupId);
        await router.RefreshAsync();
        router.Match(WarnEvent(seed.BoltId, seed.ChannelId)).Should().NotBeNull("同组命中");
        router.Match(WarnEvent(otherGroupSeed.BoltId, otherGroupSeed.ChannelId)).Should().BeNull("异组不命中");
    }

    [PostgresFact]
    public async Task RuleMatching_ClearEvents_OnlyWhenOptedIn()
    {
        _fx.EnsureAvailable();
        await ClearNotificationTablesAsync();

        var seed = await SeedBoltAsync();
        var channelId = await InsertChannelAsync("ch-" + Tag());
        await InsertRuleAsync(channelId, minSeverity: "warn", notifyOnClear: false);

        var router = NewRouter();
        await router.RefreshAsync();
        var clearEv = WarnEvent(seed.BoltId, seed.ChannelId) with { Action = AlarmAction.Clear, Severity = "info" };
        router.Match(clearEv).Should().BeNull("规则没勾恢复通知");

        await ExecAsync("DELETE FROM notification_rules");
        await InsertRuleAsync(channelId, minSeverity: "warn", notifyOnClear: true);
        await router.RefreshAsync();
        router.Match(clearEv).Should().NotBeNull("勾了恢复通知就发，恢复不做级别过滤");
    }

    [PostgresFact]
    public async Task RuleMatching_QuietWindow_SuppressesWarn_ButNotCritical()
    {
        _fx.EnsureAvailable();
        await ClearNotificationTablesAsync();

        var seed = await SeedBoltAsync();
        var channelId = await InsertChannelAsync("ch-" + Tag());
        // 静默窗盖住"现在"（本地时间前后各 1 小时；跨零点由路由器的 start>end 分支处理）
        var now = DateTime.Now;
        await InsertRuleAsync(channelId, minSeverity: "warn",
            quietStart: now.AddHours(-1).TimeOfDay, quietEnd: now.AddHours(1).TimeOfDay);

        var router = NewRouter();
        await router.RefreshAsync();
        router.Match(WarnEvent(seed.BoltId, seed.ChannelId)).Should().BeNull("warn 在静默窗内被压住");
        router.Match(CriticalEvent(seed.BoltId, seed.ChannelId)).Should().NotBeNull("critical 无视静默窗");
    }

    // ===================== 派发 =====================

    [PostgresFact]
    public async Task Dispatcher_MergesSameChannelRows_IntoOneSend()
    {
        _fx.EnsureAvailable();
        await ClearNotificationTablesAsync();

        var seed = await SeedBoltAsync();
        var channelId = await InsertChannelAsync("ch-" + Tag(), minIntervalSeconds: 0);
        var alarmId = await InsertAlarmRowAsync(seed.BoltId, seed.ChannelId);
        await InsertOutboxRowAsync(alarmId, channelId, "B-01", "预紧力偏低：样例一");
        await InsertOutboxRowAsync(alarmId, channelId, "B-02", "预紧力偏低：样例二");

        var fake = new FakeTransport();
        var dispatcher = NewDispatcher(fake);
        var sent = await dispatcher.ProcessDueAsync();

        sent.Should().Be(2, "两行都随这一条合并消息置为已发");
        fake.Calls.Should().HaveCount(1, "同渠道同轮的事件合并成一条消息（风暴合并）");
        fake.Calls[0].Lines.Should().HaveCount(2);
        (await ScalarAsync<long>("SELECT count(*) FROM notification_outbox WHERE status = 'sent'")).Should().Be(2);
    }

    [PostgresFact]
    public async Task Dispatcher_FailureBacksOff_ThenDeadAfterSixAttempts()
    {
        _fx.EnsureAvailable();
        await ClearNotificationTablesAsync();

        var seed = await SeedBoltAsync();
        var channelId = await InsertChannelAsync("ch-" + Tag(), minIntervalSeconds: 0);
        var alarmId = await InsertAlarmRowAsync(seed.BoltId, seed.ChannelId);
        var outboxId = await InsertOutboxRowAsync(alarmId, channelId, "B-01", "预紧力偏低：退避样例");

        var fake = new FakeTransport { FailWith = "对端 500" };
        var dispatcher = NewDispatcher(fake);

        await dispatcher.ProcessDueAsync();
        (await ScalarAsync<int>("SELECT attempts FROM notification_outbox WHERE id = @arg", outboxId)).Should().Be(1);
        (await ScalarAsync<bool>("SELECT next_attempt_at > now() + interval '30 seconds' FROM notification_outbox WHERE id = @arg", outboxId))
            .Should().BeTrue("首败退避 1 分钟起步");
        (await ScalarAsync<string>("SELECT status FROM notification_outbox WHERE id = @arg", outboxId)).Should().Be("pending");

        // 直拨到第 5 次失败后的状态并让它立即到期：下一次失败即第 6 次 → dead
        await ExecAsync("UPDATE notification_outbox SET attempts = 5, next_attempt_at = now() WHERE id = @arg", outboxId);
        await dispatcher.ProcessDueAsync();
        (await ScalarAsync<string>("SELECT status FROM notification_outbox WHERE id = @arg", outboxId))
            .Should().Be("dead", "第 6 次失败置 dead，等人工处置");
        (await ScalarAsync<string>("SELECT last_error FROM notification_outbox WHERE id = @arg", outboxId)).Should().Contain("对端 500");
    }

    [PostgresFact]
    public async Task Dispatcher_MinInterval_PostponesWithoutCountingFailure()
    {
        _fx.EnsureAvailable();
        await ClearNotificationTablesAsync();

        var seed = await SeedBoltAsync();
        var channelId = await InsertChannelAsync("ch-" + Tag(), minIntervalSeconds: 60);
        var alarmId = await InsertAlarmRowAsync(seed.BoltId, seed.ChannelId);
        await InsertOutboxRowAsync(alarmId, channelId, "B-01", "第一条");

        var fake = new FakeTransport();
        var dispatcher = NewDispatcher(fake);
        (await dispatcher.ProcessDueAsync()).Should().Be(1);

        // 间隔内的第二批：只顺延，不发送、不计失败
        var second = await InsertOutboxRowAsync(alarmId, channelId, "B-01", "第二条");
        (await dispatcher.ProcessDueAsync()).Should().Be(0);
        fake.Calls.Should().HaveCount(1, "渠道级限流：60 秒内不再发第二条");
        (await ScalarAsync<int>("SELECT attempts FROM notification_outbox WHERE id = @arg", second))
            .Should().Be(0, "限流顺延不算失败，不推进退避");
        (await ScalarAsync<bool>("SELECT next_attempt_at > now() + interval '30 seconds' FROM notification_outbox WHERE id = @arg", second))
            .Should().BeTrue("顺延到上次发送 + min_interval 之后");
    }

    // ===================== 管理端点 =====================

    [PostgresFact]
    public async Task ChannelEndpoints_SecretNeverEchoed_DuplicateName409_ReservedKind400()
    {
        _fx.EnsureAvailable();
        await ClearNotificationTablesAsync();

        var adminId = await SeedUserAsync();
        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, adminId, "admin");

        var name = "值班群-" + Tag();
        var create = await client.PostAsJsonAsync("/api/notification-channels/", new
        {
            name,
            kind = "webhook",
            url = "https://example.invalid/hook",
            format = "wecom",
            secret = "top-secret-token",
        });
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/notification-channels/");
        var item = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == name);
        item.GetProperty("hasSecret").GetBoolean().Should().BeTrue();
        item.TryGetProperty("secret", out _).Should().BeFalse("secret 永不回显");
        list.ToString().Should().NotContain("top-secret-token");

        (await client.PostAsJsonAsync("/api/notification-channels/", new
        {
            name,
            kind = "webhook",
            url = "https://example.invalid/hook2",
            format = "raw",
        })).StatusCode.Should().Be(HttpStatusCode.Conflict, "渠道重名 409");

        (await client.PostAsJsonAsync("/api/notification-channels/", new
        {
            name = name + "-smtp",
            kind = "smtp",
            url = "https://example.invalid/x",
            format = "raw",
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "smtp/sms 是预留枚举，适配器没实现前不放行");
    }

    // ===================== helpers =====================

    private sealed class FakeTransport : INotificationTransport
    {
        public string Kind => "webhook";
        public string? FailWith { get; set; }
        public List<(NotificationChannelInfo Channel, string Title, IReadOnlyList<string> Lines)> Calls { get; } = new();

        public Task SendAsync(NotificationChannelInfo channel, string title, IReadOnlyList<string> lines, CancellationToken ct = default)
        {
            if (FailWith is not null)
                throw new HttpRequestException(FailWith);
            Calls.Add((channel, title, lines));
            return Task.CompletedTask;
        }
    }

    private PostgresNotificationRouter NewRouter() =>
        new(_fx.DataSource, NullLogger<PostgresNotificationRouter>.Instance);

    private NotificationDispatcher NewDispatcher(INotificationTransport transport) =>
        new(_fx.DataSource, new[] { transport }, NullLogger<NotificationDispatcher>.Instance);

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static AlarmEvent WarnEvent(long boltId, long channelId) => new(
        AlarmAction.Trigger, boltId, channelId, "preload_low", "warn", DateTimeOffset.UtcNow,
        PreloadN: 100_000m, PreloadRatio: 0.58m, TargetPreloadN: 172_000m,
        ThresholdLowRatio: 0.70m, ThresholdHighRatio: 1.10m, ConsecutiveCount: 5,
        Message: "预紧力偏低：当前 100000 N，为目标 172000 N 的 58.1%");

    private static AlarmEvent CriticalEvent(long boltId, long channelId) =>
        WarnEvent(boltId, channelId) with { Type = "preload_loss", Severity = "critical", ConsecutiveCount = 1 };

    private async Task ClearNotificationTablesAsync()
    {
        await ExecAsync("DELETE FROM notification_outbox");
        await ExecAsync("DELETE FROM notification_rules");
        await ExecAsync("DELETE FROM notification_channels");
    }

    private async Task<long> InsertChannelAsync(string name, int minIntervalSeconds = 0)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO notification_channels (name, kind, config_jsonb, min_interval_seconds)
            VALUES (@name, 'webhook', '{""url"":""https://example.invalid/hook"",""format"":""raw""}', @interval)
            RETURNING id");
        cmd.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, name);
        cmd.Parameters.AddWithValue("interval", NpgsqlDbType.Integer, minIntervalSeconds);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task InsertRuleAsync(
        long channelId, string minSeverity, string[]? alarmTypes = null, long? groupId = null,
        bool notifyOnClear = false, TimeSpan? quietStart = null, TimeSpan? quietEnd = null)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO notification_rules
                (channel_id, min_severity, alarm_types, group_id, notify_on_clear, quiet_start, quiet_end)
            VALUES (@chan, @sev, @types, @grp, @onclear, @qs, @qe)");
        cmd.Parameters.AddWithValue("chan", NpgsqlDbType.Bigint, channelId);
        cmd.Parameters.AddWithValue("sev", NpgsqlDbType.Varchar, minSeverity);
        cmd.Parameters.AddWithValue("types", NpgsqlDbType.Array | NpgsqlDbType.Text, (object?)alarmTypes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("grp", NpgsqlDbType.Bigint, (object?)groupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("onclear", NpgsqlDbType.Boolean, notifyOnClear);
        cmd.Parameters.AddWithValue("qs", NpgsqlDbType.Time, (object?)quietStart ?? DBNull.Value);
        cmd.Parameters.AddWithValue("qe", NpgsqlDbType.Time, (object?)quietEnd ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> InsertAlarmRowAsync(long boltId, long channelId)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO alarms (bolt_id, channel_id, type, severity, triggered_at, message)
            VALUES (@b, @c, 'preload_low', 'warn', now(), '派发测试') RETURNING id");
        cmd.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
        cmd.Parameters.AddWithValue("c", NpgsqlDbType.Bigint, channelId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> InsertOutboxRowAsync(long alarmId, long channelId, string boltCode, string message)
    {
        var payload = JsonSerializer.Serialize(new
        {
            action = "trigger",
            type = "preload_low",
            severity = "warn",
            boltCode,
            groupName = "测试组",
            message,
            ts = DateTimeOffset.UtcNow,
        });
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO notification_outbox (alarm_id, channel_id, event_kind, payload_jsonb)
            VALUES (@a, @c, 'trigger', @p) RETURNING id");
        cmd.Parameters.AddWithValue("a", NpgsqlDbType.Bigint, alarmId);
        cmd.Parameters.AddWithValue("c", NpgsqlDbType.Bigint, channelId);
        cmd.Parameters.AddWithValue("p", NpgsqlDbType.Jsonb, payload);
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

    private async Task<long> SeedUserAsync()
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "INSERT INTO users (username, password_hash, role) VALUES (@u, 'seed', 'admin') RETURNING id");
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, "u_" + Guid.NewGuid().ToString("N")[..12]);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private sealed record SeedResult(long BoltId, long ChannelId, long GroupId, string BoltCode);

    /// <summary>项目→设备→部位→通道→螺栓一条龙（每次调用独立部位，部位过滤用例靠这一点区分组）。</summary>
    private async Task<SeedResult> SeedBoltAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Tag();
        await using var db = _fx.NewDbContext();

        var project = new Project { Code = $"P-{tag}", Name = "通知集成", Industry = "风电", SlaLevel = "standard", CreatedAt = now, UpdatedAt = now };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var device = new Device
        {
            ProjectId = project.Id, Code = $"D-{tag}", Model = 3, CommType = 1, CommParams = "in-process",
            Address = 1, TransferMode = 1, ChannelCount = 40, State = "online", CreatedAt = now, UpdatedAt = now,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        var group = new MonitorGroup { ProjectId = project.Id, Code = $"G-{tag}", Name = $"通知组-{tag}", CreatedAt = now, UpdatedAt = now };
        db.MonitorGroups.Add(group);
        await db.SaveChangesAsync();

        var channel = new Channel
        {
            DeviceId = device.Id, ChannelNo = 1, ProbeFreqMhz = 2.0m, Gain = 16, MeasMode = 2, TcMode = 1,
            TofAlgorithm = "ThrSignal", HardwareState = "healthy", BusinessState = "bound", ConfigSource = "database",
            State = "manual", DiscoveredAt = now, CreatedAt = now, UpdatedAt = now,
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var bolt = new Bolt
        {
            GroupId = group.Id, ChannelId = channel.Id, Code = $"B-{tag}", NominalDiam = "M20", Grade = "10.9",
            Rp02Mpa = 940m, Material = "42CrMo", ElasticModulusGpa = 206m, EffectiveLengthMm = 100m, TotalLengthMm = 120m,
            CrossSectionMm2 = 245m, SoundVelocityMs = 5890m, TargetPreloadN = 172_000m, MinPreloadN = 120_000m,
            State = "calibrated", CreatedAt = now, UpdatedAt = now,
        };
        db.Bolts.Add(bolt);
        await db.SaveChangesAsync();

        return new SeedResult(bolt.Id, channel.Id, group.Id, bolt.Code);
    }
}
