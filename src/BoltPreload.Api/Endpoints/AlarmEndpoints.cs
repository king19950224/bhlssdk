using BoltPreload.Api.Auth;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

public static class AlarmEndpoints
{
    public static IEndpointRouteBuilder MapAlarmEndpoints(this IEndpointRouteBuilder app)
    {
        // 读告警 logged_in 即可；ack / shelve 需 write_access（operator+）
        var grp = app.MapGroup("/api/alarms")
            .WithTags("Alarms")
            .RequireAuthorization(AuthPolicies.LoggedIn);

        // 列告警（默认只列开启的；可选 includeCleared=true 看历史）
        grp.MapGet("/", async (
            NpgsqlDataSource ds, bool? includeCleared, string? severity, int? limit,
            CancellationToken ct) =>
        {
            var n = Math.Clamp(limit ?? 100, 1, 1000);
            // 带确认/闭合责任人（用户名）——告警处置责任链在列表可见（可追溯）
            var sql = @"
                SELECT a.id, a.bolt_id, b.code AS bolt_code, a.channel_id, a.type, a.severity,
                       a.triggered_at, a.acknowledged_at, a.cleared_at,
                       a.preload_n_at_trigger, a.preload_ratio_at_trigger, a.target_preload_n,
                       a.threshold_low_ratio, a.threshold_high_ratio,
                       a.consecutive_count, a.message, a.cleared_reason,
                       au.username AS acked_by, cu.username AS cleared_by, a.acknowledged_note
                FROM alarms a
                JOIN bolts b ON b.id = a.bolt_id AND b.deleted_at IS NULL
                LEFT JOIN users au ON au.id = a.acknowledged_by
                LEFT JOIN users cu ON cu.id = a.cleared_by
                WHERE 1=1"
                + (includeCleared == true ? "" : " AND a.cleared_at IS NULL")
                + (severity is null ? "" : " AND a.severity = @sev")
                + " ORDER BY a.triggered_at DESC LIMIT @n";

            await using var cmd = ds.CreateCommand(sql);
            if (severity is not null) cmd.Parameters.AddWithValue("sev", NpgsqlDbType.Varchar, severity);
            cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, n);

            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new
                {
                    id = r.GetInt64(0),
                    boltId = r.GetInt64(1),
                    boltCode = r.GetString(2),
                    channelId = r.GetInt64(3),
                    type = r.GetString(4),
                    severity = r.GetString(5),
                    triggeredAt = r.GetDateTime(6),
                    acknowledgedAt = r.IsDBNull(7) ? null : (DateTime?)r.GetDateTime(7),
                    clearedAt = r.IsDBNull(8) ? null : (DateTime?)r.GetDateTime(8),
                    preloadN = r.IsDBNull(9) ? null : (decimal?)r.GetDecimal(9),
                    preloadRatio = r.IsDBNull(10) ? null : (decimal?)r.GetDecimal(10),
                    targetPreloadN = r.IsDBNull(11) ? null : (decimal?)r.GetDecimal(11),
                    thresholdLow = r.IsDBNull(12) ? null : (decimal?)r.GetDecimal(12),
                    thresholdHigh = r.IsDBNull(13) ? null : (decimal?)r.GetDecimal(13),
                    consecutive = r.GetInt32(14),
                    message = r.IsDBNull(15) ? null : r.GetString(15),
                    clearedReason = r.IsDBNull(16) ? null : r.GetString(16),
                    ackedBy = r.IsDBNull(17) ? null : r.GetString(17),
                    clearedBy = r.IsDBNull(18) ? null : r.GetString(18),
                    ackedNote = r.IsDBNull(19) ? null : r.GetString(19),
                    isOpen = r.IsDBNull(8),
                    isAcked = !r.IsDBNull(7)
                });
            }
            return Results.Ok(new { count = items.Count, items });
        })
        .WithName("ListAlarms")
        .WithSummary("告警列表（默认只列开启的）");

        // 确认告警（operator+，user_id 强制取 HttpContext，不接客户端伪造）
        grp.MapPost("/{id:long}/ack", async (
            long id, HttpContext http, AckRequest? body, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var currentUser = UserContext.Require(http);
            const string sql = @"
                UPDATE alarms SET acknowledged_at = now(), acknowledged_by = @uid, acknowledged_note = @note
                WHERE id = @id AND acknowledged_at IS NULL
                RETURNING id, type, severity, triggered_at";

            long ackId; string ackType, ackSeverity; DateTime ackTriggeredAt;
            await using (var cmd = ds.CreateCommand(sql))
            {
                cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, currentUser.UserId);
                cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)body?.Note ?? DBNull.Value);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);

                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    return Results.NotFound(new { error = "告警不存在或已 ack", id });
                ackId = r.GetInt64(0);
                ackType = r.GetString(1);
                ackSeverity = r.GetString(2);
                ackTriggeredAt = r.GetDateTime(3);
            }

            await WriteAlarmAuditAsync(ds, currentUser.UserId, "alarm_ack", ackId,
                $"确认告警 #{ackId}（{ackType}）", ct);

            return Results.Ok(new
            {
                id = ackId,
                type = ackType,
                severity = ackSeverity,
                triggeredAt = ackTriggeredAt,
                acknowledged = true,
                acknowledgedBy = currentUser.UserId
            });
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("AckAlarm")
        .WithSummary("确认告警（不闭合，仅标记已读）");

        // 手动闭合告警（operator+）— 配合 0011_safety_fixes.sql 的 partial unique 索引
        grp.MapPost("/{id:long}/clear", async (
            long id, HttpContext http, ClearRequest? body, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            // cleared_by 持久化到库（安全动作可追责）；NULL 才是系统自动闭合
            const string sql = @"
                UPDATE alarms
                   SET cleared_at = now(),
                       cleared_by = @uid,
                       cleared_reason = COALESCE(@reason, 'manual_reset')
                 WHERE id = @id AND cleared_at IS NULL
                 RETURNING type, cleared_reason";
            string alarmType, clearedReason;
            await using (var cmd = ds.CreateCommand(sql))
            {
                cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, u.UserId);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                cmd.Parameters.AddWithValue("reason", NpgsqlDbType.Varchar, (object?)body?.Reason ?? DBNull.Value);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    return Results.NotFound(new { error = "告警不存在或已闭合", id });
                alarmType = r.GetString(0);
                clearedReason = r.GetString(1);
            }

            await WriteAlarmAuditAsync(ds, u.UserId, "alarm_clear", id,
                $"手动闭合告警 #{id}（{alarmType}）原因：{clearedReason}", ct);
            return Results.Ok(new { id, type = alarmType, cleared = true, by = u.UserId });
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("ClearAlarm")
        .WithSummary("手动闭合告警");

        // 抑制 / shelve（维护时段屏蔽某 bolt+type，带过期时间）
        grp.MapPost("/shelve", async (
            HttpContext http, ShelveRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            if (req.Hours <= 0 || req.Hours > 168)
                return Results.BadRequest(new { error = "Hours 必须在 1..168（即 1 小时 ~ 7 天）" });
            if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length < 5)
                return Results.BadRequest(new { error = "Reason 至少 5 字" });

            const string sql = @"
                INSERT INTO alarm_shelvings (bolt_id, alarm_type, shelved_by, expires_at, reason)
                VALUES (@bolt, @type, @by, @exp, @reason)
                RETURNING id, expires_at";
            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, (object?)req.BoltId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, (object?)req.AlarmType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("by", NpgsqlDbType.Bigint, u.UserId);
            cmd.Parameters.AddWithValue("exp", NpgsqlDbType.TimestampTz, DateTimeOffset.UtcNow.AddHours(req.Hours));
            cmd.Parameters.AddWithValue("reason", NpgsqlDbType.Text, req.Reason);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return Results.Ok(new { shelvingId = r.GetInt64(0), expiresAt = r.GetDateTime(1) });
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("ShelveAlarm")
        .WithSummary("抑制某 bolt+type 的告警直到 expires_at");

        // 生效中的抑制列表：抑制必须可见可管理（否则"被静默的告警"成为治理盲区，EEMUA 191 反模式）
        grp.MapGet("/shelvings", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            const string sql = @"
                SELECT s.id, s.bolt_id, b.code, s.alarm_type, s.shelved_at, s.expires_at, s.reason,
                       s.shelved_by, u.username
                FROM alarm_shelvings s
                LEFT JOIN bolts b ON b.id = s.bolt_id
                LEFT JOIN users u ON u.id = s.shelved_by
                WHERE s.cancelled_at IS NULL AND s.expires_at > now()
                ORDER BY s.expires_at";
            await using var cmd = ds.CreateCommand(sql);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new
                {
                    id = r.GetInt64(0),
                    boltId = r.IsDBNull(1) ? (long?)null : r.GetInt64(1),
                    boltCode = r.IsDBNull(2) ? null : r.GetString(2),
                    alarmType = r.IsDBNull(3) ? null : r.GetString(3),
                    shelvedAt = r.GetDateTime(4),
                    expiresAt = r.GetDateTime(5),
                    reason = r.GetString(6),
                    shelvedBy = r.IsDBNull(7) ? (long?)null : r.GetInt64(7),
                    shelvedByName = r.IsDBNull(8) ? null : r.GetString(8),
                });
            return Results.Ok(new { count = items.Count, items });
        })
        .WithName("ListActiveShelvings")
        .WithSummary("生效中的告警抑制（未取消且未过期）");

        // 取消抑制
        grp.MapPost("/shelve/{id:long}/cancel", async (long id, HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            await using var cmd = ds.CreateCommand(@"
                UPDATE alarm_shelvings SET cancelled_at = now(), cancelled_by = @uid
                 WHERE id = @id AND cancelled_at IS NULL");
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
            cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, u.UserId);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            return n == 0 ? Results.NotFound(new { error = "抑制不存在或已取消", id }) : Results.Ok(new { id, cancelled = true });
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("CancelShelve");

        // 汇总统计
        grp.MapGet("/summary", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            const string sql = @"
                SELECT severity, type,
                       COUNT(*) FILTER (WHERE cleared_at IS NULL) AS open_count,
                       COUNT(*) FILTER (WHERE cleared_at IS NULL AND acknowledged_at IS NULL) AS open_unacked,
                       COUNT(*) AS total_count
                FROM alarms
                GROUP BY severity, type
                ORDER BY severity DESC, type";
            await using var cmd = ds.CreateCommand(sql);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new
                {
                    severity = r.GetString(0),
                    type = r.GetString(1),
                    open = r.GetInt64(2),
                    openUnacked = r.GetInt64(3),
                    total = r.GetInt64(4)
                });
            }
            return Results.Ok(new { items });
        })
        .WithName("AlarmSummary")
        .WithSummary("告警汇总（按 severity × type）");

        return app;
    }

    /// <summary>
    /// 把告警确认/闭合动作写入 audit_log（append-only 轨迹）。
    /// alarms 行本身的 acknowledged_by/cleared_by 是当前态；audit_log 是不可篡改的历史，满足「可追责」。
    /// </summary>
    private static async Task WriteAlarmAuditAsync(
        NpgsqlDataSource ds, long userId, string action, long alarmId, string note, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(@"
            INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
            VALUES (@user_id, @action, 'alarm', @target_id, NULL, NULL, @note, now())");
        cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, userId);
        cmd.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action);
        cmd.Parameters.AddWithValue("target_id", NpgsqlDbType.Bigint, alarmId);
        cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, note);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed record AckRequest(string? Note = null);
public sealed record ClearRequest(string? Reason = null);
public sealed record ShelveRequest(long? BoltId, string? AlarmType, int Hours, string Reason);
