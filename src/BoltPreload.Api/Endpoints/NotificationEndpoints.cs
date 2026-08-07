using System.Text.Json;
using BoltPreload.Api.Auth;
using BoltPreload.Application.Notifications;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 出站通知管理端点：渠道（/api/notification-channels）、规则（/api/notification-rules）、
/// 发件箱（/api/notification-outbox）。渠道与规则是 Admin 的事；发件箱查询/重发放宽到 Reviewer——
/// 值班复核的人得能看见"通知到底发出去没有"并把 dead 行救活。
/// 配置变更全部写审计并显式刷新路由快照（不等 30 秒定时轮）。
/// config 里的 secret 永不回显（返回 hasSecret 布尔），诊断包脱敏之外 API 面也不漏。
/// </summary>
public static class NotificationEndpoints
{
    private static readonly HashSet<string> KnownFormats = new(StringComparer.Ordinal) { "raw", "wecom", "dingtalk" };
    private static readonly HashSet<string> KnownSeverities = new(StringComparer.Ordinal) { "info", "warn", "critical" };
    private static readonly HashSet<string> KnownOutboxStatuses = new(StringComparer.Ordinal) { "pending", "sent", "dead" };

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        MapChannelEndpoints(app);
        MapRuleEndpoints(app);
        MapOutboxEndpoints(app);
        return app;
    }

    // ==================================================================== 渠道

    private static void MapChannelEndpoints(IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/notification-channels")
            .WithTags("Notifications")
            .RequireAuthorization(AuthPolicies.AdminAccess);

        grp.MapGet("/", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            const string sql = @"
                SELECT c.id, c.name, c.kind, c.config_jsonb::text, c.min_interval_seconds, c.enabled,
                       c.created_at, c.updated_at,
                       (SELECT count(*) FROM notification_rules r WHERE r.channel_id = c.id) AS rule_count
                FROM notification_channels c
                ORDER BY c.name";
            await using var cmd = ds.CreateCommand(sql);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var (url, format, hasSecret) = ParseChannelConfig(r.GetString(3));
                items.Add(new
                {
                    id = r.GetInt64(0),
                    name = r.GetString(1),
                    kind = r.GetString(2),
                    url,
                    format,
                    hasSecret,
                    minIntervalSeconds = r.GetInt32(4),
                    enabled = r.GetBoolean(5),
                    createdAt = r.GetDateTime(6),
                    updatedAt = r.GetDateTime(7),
                    ruleCount = r.GetInt64(8),
                });
            }
            return Results.Ok(new { count = items.Count, items });
        })
        .WithName("ListNotificationChannels")
        .WithSummary("通知渠道列表（secret 不回显）");

        grp.MapPost("/", async (
            HttpContext http, ChannelSaveRequest req, NpgsqlDataSource ds, INotificationRouter router,
            IConfiguration cfg, ILoggerFactory lf, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            var err = ValidateChannel(req, AllowPrivateTargets(cfg));
            if (err is not null) return Results.BadRequest(new { error = err });

            var config = BuildChannelConfig(req.Url!, req.Format!, req.Secret);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            long id;
            try
            {
                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO notification_channels (name, kind, config_jsonb, min_interval_seconds, enabled)
                    VALUES (@name, @kind, @cfg, @interval, @enabled)
                    RETURNING id", conn, tx);
                cmd.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, req.Name.Trim());
                cmd.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, req.Kind);
                cmd.Parameters.AddWithValue("cfg", NpgsqlDbType.Jsonb, config);
                cmd.Parameters.AddWithValue("interval", NpgsqlDbType.Integer, req.MinIntervalSeconds);
                cmd.Parameters.AddWithValue("enabled", NpgsqlDbType.Boolean, req.Enabled);
                id = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await tx.RollbackAsync(ct);
                return Results.Conflict(new { error = $"渠道名 '{req.Name}' 已存在" });
            }

            await WriteNotifyAuditAsync(conn, tx, u.UserId, "notification_channel_create", "notification_channel", id,
                $"新建通知渠道 '{req.Name.Trim()}'（{req.Kind}/{req.Format}）", ct);
            await tx.CommitAsync(ct);
            await RefreshRouterSafeAsync(router, lf, ct);
            return Results.Ok(new { id });
        })
        .WithName("CreateNotificationChannel")
        .WithSummary("新建通知渠道（本轮仅 webhook）");

        grp.MapPatch("/{id:long}", async (
            long id, HttpContext http, ChannelSaveRequest req, NpgsqlDataSource ds, INotificationRouter router,
            IConfiguration cfg, ILoggerFactory lf, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            var err = ValidateChannel(req, AllowPrivateTargets(cfg));
            if (err is not null) return Results.BadRequest(new { error = err });

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // secret 语义：null=保留现有，空串=清除，有值=替换（回显永远只给 hasSecret）
            string? existingSecret = null;
            await using (var q = new NpgsqlCommand(
                "SELECT config_jsonb::text FROM notification_channels WHERE id = @id FOR UPDATE", conn, tx))
            {
                q.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                if (await q.ExecuteScalarAsync(ct) is not string existingCfg)
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "渠道不存在", id });
                }
                using var doc = JsonDocument.Parse(existingCfg);
                if (doc.RootElement.TryGetProperty("secret", out var s))
                    existingSecret = s.GetString();
            }

            var secret = req.Secret is null ? existingSecret : (req.Secret == "" ? null : req.Secret);
            var config = BuildChannelConfig(req.Url!, req.Format!, secret);

            await using (var cmd = new NpgsqlCommand(@"
                UPDATE notification_channels
                   SET name = @name, kind = @kind, config_jsonb = @cfg,
                       min_interval_seconds = @interval, enabled = @enabled, updated_at = now()
                 WHERE id = @id", conn, tx))
            {
                cmd.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, req.Name.Trim());
                cmd.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, req.Kind);
                cmd.Parameters.AddWithValue("cfg", NpgsqlDbType.Jsonb, config);
                cmd.Parameters.AddWithValue("interval", NpgsqlDbType.Integer, req.MinIntervalSeconds);
                cmd.Parameters.AddWithValue("enabled", NpgsqlDbType.Boolean, req.Enabled);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await WriteNotifyAuditAsync(conn, tx, u.UserId, "notification_channel_update", "notification_channel", id,
                $"更新通知渠道 '{req.Name.Trim()}'（enabled={req.Enabled}）", ct);
            await tx.CommitAsync(ct);
            await RefreshRouterSafeAsync(router, lf, ct);
            return Results.Ok(new { id, updated = true });
        })
        .WithName("UpdateNotificationChannel")
        .WithSummary("更新通知渠道（secret：null 保留 / 空串清除 / 有值替换）");

        grp.MapDelete("/{id:long}", async (
            long id, HttpContext http, NpgsqlDataSource ds, INotificationRouter router,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            string? name;
            try
            {
                await using var cmd = new NpgsqlCommand(
                    "DELETE FROM notification_channels WHERE id = @id RETURNING name", conn, tx);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                name = await cmd.ExecuteScalarAsync(ct) as string;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                // 发件箱历史行还挂在这个渠道上：历史要留（发送轨迹可追溯），渠道只能禁用不能删
                await tx.RollbackAsync(ct);
                return Results.Conflict(new { error = "该渠道已有发送记录，历史不可删——请改用禁用" });
            }
            if (name is null)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "渠道不存在", id });
            }

            await WriteNotifyAuditAsync(conn, tx, u.UserId, "notification_channel_delete", "notification_channel", id,
                $"删除通知渠道 '{name}'", ct);
            await tx.CommitAsync(ct);
            await RefreshRouterSafeAsync(router, lf, ct);
            return Results.Ok(new { id, deleted = true });
        })
        .WithName("DeleteNotificationChannel")
        .WithSummary("删除通知渠道（有发送历史时拒绝，改用禁用）");

        // 当场发一条测试消息：不进发件箱、不重试，通不通立见分晓
        grp.MapPost("/{id:long}/test", async (
            long id, HttpContext http, NpgsqlDataSource ds, IEnumerable<INotificationTransport> transports,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            string? name = null, kind = null, config = null;
            await using (var cmd = ds.CreateCommand(
                "SELECT name, kind, config_jsonb::text FROM notification_channels WHERE id = @id"))
            {
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    name = r.GetString(0);
                    kind = r.GetString(1);
                    config = r.GetString(2);
                }
            }
            if (name is null)
                return Results.NotFound(new { error = "渠道不存在", id });

            var transport = transports.FirstOrDefault(t => t.Kind == kind);
            if (transport is null)
                return Results.BadRequest(new { error = $"渠道类型 '{kind}' 没有可用适配器" });

            try
            {
                await transport.SendAsync(
                    new NotificationChannelInfo(id, name, kind!, config!),
                    "螺栓预紧力监测：通知渠道连通性测试",
                    new[] { $"渠道 '{name}' 配置有效，此消息由测试按钮触发（{DateTimeOffset.Now:MM-dd HH:mm:ss}）" }, ct);
            }
            catch (Exception ex)
            {
                // 不回传底层异常细节（可能含内网 URL / DNS / TLS），详情见日志
                lf.CreateLogger("Notification").LogWarning(ex, "通知渠道测试发送失败 channel={Id}", id);
                return Results.Json(new { ok = false, error = "测试发送失败，详情见系统日志" },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            await WriteStandaloneAuditAsync(ds, u.UserId, "notification_test_send", "notification_channel", id,
                $"测试发送通知渠道 '{name}'：成功", ct);
            return Results.Ok(new { ok = true });
        })
        .WithName("TestNotificationChannel")
        .WithSummary("发送测试消息（直发不入发件箱）");
    }

    // ==================================================================== 规则

    private static void MapRuleEndpoints(IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/notification-rules")
            .WithTags("Notifications")
            .RequireAuthorization(AuthPolicies.AdminAccess);

        grp.MapGet("/", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            const string sql = @"
                SELECT r.id, r.channel_id, c.name, r.min_severity, r.alarm_types, r.group_id, mg.code,
                       r.notify_on_clear, r.quiet_start, r.quiet_end, r.enabled
                FROM notification_rules r
                JOIN notification_channels c ON c.id = r.channel_id
                LEFT JOIN monitor_groups mg ON mg.id = r.group_id
                ORDER BY c.name, r.id";
            await using var cmd = ds.CreateCommand(sql);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new
                {
                    id = r.GetInt64(0),
                    channelId = r.GetInt64(1),
                    channelName = r.GetString(2),
                    minSeverity = r.GetString(3),
                    alarmTypes = r.IsDBNull(4) ? null : r.GetFieldValue<string[]>(4),
                    groupId = r.IsDBNull(5) ? (long?)null : r.GetInt64(5),
                    groupCode = r.IsDBNull(6) ? null : r.GetString(6),
                    notifyOnClear = r.GetBoolean(7),
                    quietStart = r.IsDBNull(8) ? null : r.GetFieldValue<TimeSpan>(8).ToString(@"hh\:mm"),
                    quietEnd = r.IsDBNull(9) ? null : r.GetFieldValue<TimeSpan>(9).ToString(@"hh\:mm"),
                    enabled = r.GetBoolean(10),
                });
            }
            return Results.Ok(new { count = items.Count, items });
        })
        .WithName("ListNotificationRules")
        .WithSummary("通知规则列表");

        grp.MapPost("/", async (
            HttpContext http, RuleSaveRequest req, NpgsqlDataSource ds, INotificationRouter router,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            var (err, quietStart, quietEnd) = await ValidateRuleAsync(req, ds, ct);
            if (err is not null) return Results.BadRequest(new { error = err });

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            long id;
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO notification_rules
                    (channel_id, min_severity, alarm_types, group_id, notify_on_clear, quiet_start, quiet_end, enabled)
                VALUES (@chan, @sev, @types, @grp, @onclear, @qs, @qe, @enabled)
                RETURNING id", conn, tx))
            {
                BindRuleParams(cmd, req, quietStart, quietEnd);
                id = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }

            await WriteNotifyAuditAsync(conn, tx, u.UserId, "notification_rule_create", "notification_rule", id,
                $"新建通知规则 #{id}（渠道 #{req.ChannelId}，级别 ≥{req.MinSeverity}）", ct);
            await tx.CommitAsync(ct);
            await RefreshRouterSafeAsync(router, lf, ct);
            return Results.Ok(new { id });
        })
        .WithName("CreateNotificationRule")
        .WithSummary("新建通知规则");

        grp.MapPatch("/{id:long}", async (
            long id, HttpContext http, RuleSaveRequest req, NpgsqlDataSource ds, INotificationRouter router,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            var (err, quietStart, quietEnd) = await ValidateRuleAsync(req, ds, ct);
            if (err is not null) return Results.BadRequest(new { error = err });

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            int n;
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE notification_rules
                   SET channel_id = @chan, min_severity = @sev, alarm_types = @types, group_id = @grp,
                       notify_on_clear = @onclear, quiet_start = @qs, quiet_end = @qe, enabled = @enabled,
                       updated_at = now()
                 WHERE id = @id", conn, tx))
            {
                BindRuleParams(cmd, req, quietStart, quietEnd);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                n = await cmd.ExecuteNonQueryAsync(ct);
            }
            if (n == 0)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "规则不存在", id });
            }

            await WriteNotifyAuditAsync(conn, tx, u.UserId, "notification_rule_update", "notification_rule", id,
                $"更新通知规则 #{id}（enabled={req.Enabled}）", ct);
            await tx.CommitAsync(ct);
            await RefreshRouterSafeAsync(router, lf, ct);
            return Results.Ok(new { id, updated = true });
        })
        .WithName("UpdateNotificationRule")
        .WithSummary("更新通知规则");

        grp.MapDelete("/{id:long}", async (
            long id, HttpContext http, NpgsqlDataSource ds, INotificationRouter router,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            int n;
            await using (var cmd = new NpgsqlCommand("DELETE FROM notification_rules WHERE id = @id", conn, tx))
            {
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                n = await cmd.ExecuteNonQueryAsync(ct);
            }
            if (n == 0)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "规则不存在", id });
            }

            await WriteNotifyAuditAsync(conn, tx, u.UserId, "notification_rule_delete", "notification_rule", id,
                $"删除通知规则 #{id}", ct);
            await tx.CommitAsync(ct);
            await RefreshRouterSafeAsync(router, lf, ct);
            return Results.Ok(new { id, deleted = true });
        })
        .WithName("DeleteNotificationRule")
        .WithSummary("删除通知规则");
    }

    // ==================================================================== 发件箱

    private static void MapOutboxEndpoints(IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/notification-outbox")
            .WithTags("Notifications")
            .RequireAuthorization(AuthPolicies.ReviewerAccess);

        grp.MapGet("/", async (NpgsqlDataSource ds, int? limit, string? status, CancellationToken ct) =>
        {
            if (status is not null && !KnownOutboxStatuses.Contains(status))
                return Results.BadRequest(new { error = $"status 只能是 {string.Join(" / ", KnownOutboxStatuses)}" });
            var n = Math.Clamp(limit ?? 50, 1, 500);

            var sql = @"
                SELECT o.id, o.alarm_id, o.channel_id, c.name, o.event_kind, o.status,
                       o.attempts, o.next_attempt_at, o.last_error, o.created_at, o.sent_at,
                       o.payload_jsonb::text
                FROM notification_outbox o
                JOIN notification_channels c ON c.id = o.channel_id
                WHERE 1=1" + (status is null ? "" : " AND o.status = @status") + @"
                ORDER BY o.created_at DESC
                LIMIT @n";
            await using var cmd = ds.CreateCommand(sql);
            if (status is not null) cmd.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, status);
            cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, n);

            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                // 列表只带摘要字段，payload 全文对排查够用又不至于把页面撑爆
                string? boltCode = null, message = null;
                using (var doc = JsonDocument.Parse(r.GetString(11)))
                {
                    if (doc.RootElement.TryGetProperty("boltCode", out var bc)) boltCode = bc.GetString();
                    if (doc.RootElement.TryGetProperty("message", out var msg)) message = msg.GetString();
                }
                items.Add(new
                {
                    id = r.GetInt64(0),
                    alarmId = r.GetInt64(1),
                    channelId = r.GetInt64(2),
                    channelName = r.GetString(3),
                    eventKind = r.GetString(4),
                    status = r.GetString(5),
                    attempts = r.GetInt32(6),
                    nextAttemptAt = r.GetDateTime(7),
                    lastError = r.IsDBNull(8) ? null : r.GetString(8),
                    createdAt = r.GetDateTime(9),
                    sentAt = r.IsDBNull(10) ? null : (DateTime?)r.GetDateTime(10),
                    boltCode,
                    message,
                });
            }
            return Results.Ok(new { count = items.Count, items });
        })
        .WithName("ListNotificationOutbox")
        .WithSummary("发送记录（含失败原因，默认最近 50 条）");

        // dead 行救活：清零退避从头再试（渠道修好之后用）
        grp.MapPost("/{id:long}/retry", async (long id, HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            int n;
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE notification_outbox
                   SET status = 'pending', attempts = 0, next_attempt_at = now()
                 WHERE id = @id AND status = 'dead'", conn, tx))
            {
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                n = await cmd.ExecuteNonQueryAsync(ct);
            }
            if (n == 0)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "记录不存在或不是 dead 状态", id });
            }

            await WriteNotifyAuditAsync(conn, tx, u.UserId, "notification_retry", "notification_outbox", id,
                $"手工重发通知 outbox #{id}", ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { id, requeued = true });
        })
        .WithName("RetryNotificationOutbox")
        .WithSummary("重发 dead 行（清零退避）");
    }

    // ==================================================================== helpers

    private static (string? Url, string? Format, bool HasSecret) ParseChannelConfig(string configJson)
    {
        using var doc = JsonDocument.Parse(configJson);
        var root = doc.RootElement;
        return (
            root.TryGetProperty("url", out var u) ? u.GetString() : null,
            root.TryGetProperty("format", out var f) ? f.GetString() : null,
            root.TryGetProperty("secret", out var s) && !string.IsNullOrEmpty(s.GetString()));
    }

    private static string BuildChannelConfig(string url, string format, string? secret) =>
        JsonSerializer.Serialize(secret is null or ""
            ? new Dictionary<string, string> { ["url"] = url.Trim(), ["format"] = format }
            : new Dictionary<string, string> { ["url"] = url.Trim(), ["format"] = format, ["secret"] = secret });

    /// <summary>风场内网接收器（SCADA 网内消息桥）确有其事，业主要用时在配置里显式放行并留痕。</summary>
    private static bool AllowPrivateTargets(IConfiguration cfg) =>
        cfg.GetValue("Notifications:AllowPrivateWebhookTargets", false);

    private static string? ValidateChannel(ChannelSaveRequest req, bool allowPrivateTargets)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > 64)
            return "Name 必填且不超过 64 字符";
        // kind 的 smtp/sms 是库里预留的枚举，本轮没有适配器，配了也发不出去，端点直接不放行
        if (req.Kind != "webhook")
            return "本轮只支持 kind=webhook（smtp/sms 预留，适配器未实现）";
        // SSRF：不仅校验 scheme，还禁本机/私网（见 WebhookUrlGuard）
        var urlErr = WebhookUrlGuard.Validate(req.Url, allowPrivateTargets);
        if (urlErr is not null) return urlErr;
        if (req.Format is null || !KnownFormats.Contains(req.Format))
            return $"Format 只能是 {string.Join(" / ", KnownFormats)}";
        if (req.MinIntervalSeconds is < 0 or > 3600)
            return "MinIntervalSeconds 必须在 0..3600";
        return null;
    }

    private static async Task<(string? Error, TimeSpan? QuietStart, TimeSpan? QuietEnd)> ValidateRuleAsync(
        RuleSaveRequest req, NpgsqlDataSource ds, CancellationToken ct)
    {
        if (!KnownSeverities.Contains(req.MinSeverity))
            return ($"MinSeverity 只能是 {string.Join(" / ", KnownSeverities)}", null, null);

        if (req.AlarmTypes is { Length: > 0 })
        {
            foreach (var t in req.AlarmTypes)
            {
                if (!DisplayLabels.KnownAlarmTypes.Contains(t))
                    return ($"未知告警类型 '{t}'（可选：{string.Join(" / ", DisplayLabels.KnownAlarmTypes)}）", null, null);
            }
        }

        TimeSpan? qs = null, qe = null;
        if ((req.QuietStart is null) != (req.QuietEnd is null))
            return ("静默窗必须同时给 QuietStart 和 QuietEnd（或都不给）", null, null);
        if (req.QuietStart is not null)
        {
            if (!TimeSpan.TryParseExact(req.QuietStart, @"hh\:mm", null, out var start)
                || !TimeSpan.TryParseExact(req.QuietEnd!, @"hh\:mm", null, out var end))
                return ("静默窗时间格式为 HH:mm（如 22:00）", null, null);
            qs = start;
            qe = end;
        }

        await using (var cmd = ds.CreateCommand("SELECT 1 FROM notification_channels WHERE id = @id"))
        {
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, req.ChannelId);
            if (await cmd.ExecuteScalarAsync(ct) is null)
                return ($"渠道 #{req.ChannelId} 不存在", null, null);
        }
        if (req.GroupId is { } gid)
        {
            await using var cmd = ds.CreateCommand("SELECT 1 FROM monitor_groups WHERE id = @id AND deleted_at IS NULL");
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, gid);
            if (await cmd.ExecuteScalarAsync(ct) is null)
                return ($"监测部位 #{gid} 不存在", null, null);
        }
        return (null, qs, qe);
    }

    private static void BindRuleParams(NpgsqlCommand cmd, RuleSaveRequest req, TimeSpan? quietStart, TimeSpan? quietEnd)
    {
        cmd.Parameters.AddWithValue("chan", NpgsqlDbType.Bigint, req.ChannelId);
        cmd.Parameters.AddWithValue("sev", NpgsqlDbType.Varchar, req.MinSeverity);
        cmd.Parameters.AddWithValue("types", NpgsqlDbType.Array | NpgsqlDbType.Text,
            (object?)(req.AlarmTypes is { Length: > 0 } ? req.AlarmTypes : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("grp", NpgsqlDbType.Bigint, (object?)req.GroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("onclear", NpgsqlDbType.Boolean, req.NotifyOnClear);
        cmd.Parameters.AddWithValue("qs", NpgsqlDbType.Time, (object?)quietStart ?? DBNull.Value);
        cmd.Parameters.AddWithValue("qe", NpgsqlDbType.Time, (object?)quietEnd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("enabled", NpgsqlDbType.Boolean, req.Enabled);
    }

    private static async Task RefreshRouterSafeAsync(INotificationRouter router, ILoggerFactory lf, CancellationToken ct)
    {
        // 配置已提交，刷新失败只影响生效时点（最迟 30 秒后定时轮补上），不让请求 500
        try
        {
            await router.RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            lf.CreateLogger(nameof(NotificationEndpoints))
                .LogWarning(ex, "通知路由快照刷新失败，最迟 30 秒后由定时刷新生效");
        }
    }

    private static async Task WriteNotifyAuditAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        long userId, string action, string targetType, long targetId, string note, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at, ip_addr)
            VALUES (@user_id, @action, @target_type, @target_id, NULL, NULL, @note, now(), @ip)", conn, tx);
        cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, userId);
        cmd.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action);
        cmd.Parameters.AddWithValue("target_type", NpgsqlDbType.Varchar, targetType);
        cmd.Parameters.AddWithValue("target_id", NpgsqlDbType.Bigint, targetId);
        cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, note);
        AuditClientIp.Bind(cmd.Parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>测试发送这类"发完才知道结果"的动作没有业务事务可挂，审计单独一条写入。</summary>
    private static async Task WriteStandaloneAuditAsync(
        NpgsqlDataSource ds, long userId, string action, string targetType, long targetId, string note, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(@"
            INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at, ip_addr)
            VALUES (@user_id, @action, @target_type, @target_id, NULL, NULL, @note, now(), @ip)");
        cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, userId);
        cmd.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action);
        cmd.Parameters.AddWithValue("target_type", NpgsqlDbType.Varchar, targetType);
        cmd.Parameters.AddWithValue("target_id", NpgsqlDbType.Bigint, targetId);
        cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, note);
        AuditClientIp.Bind(cmd.Parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// 通知渠道保存请求（新建与更新同一形状）。更新时 Secret 的语义：null=保留现有，空串=清除，有值=替换。
/// </summary>
public sealed record ChannelSaveRequest(
    string Name,
    string Kind,
    string? Url,
    string? Format,
    string? Secret,
    int MinIntervalSeconds = 60,
    bool Enabled = true);

/// <summary>
/// 通知规则保存请求。AlarmTypes 空 = 全部类型；GroupId 空 = 全站；
/// 静默窗为服务器本地时间 HH:mm，跨零点合法（22:00–06:00），critical 无视静默。
/// </summary>
public sealed record RuleSaveRequest(
    long ChannelId,
    string MinSeverity = "warn",
    string[]? AlarmTypes = null,
    long? GroupId = null,
    bool NotifyOnClear = false,
    string? QuietStart = null,
    string? QuietEnd = null,
    bool Enabled = true);
