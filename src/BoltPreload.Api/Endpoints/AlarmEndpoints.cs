using BoltPreload.Api.Auth;
using BoltPreload.Application.Alarm;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 告警端点（/api/alarms）：查询、确认（ack）、闭合（clear）、抑制（shelve）。
/// 告警的产生与自动恢复在 StaticThresholdAlarmEvaluator，本组只做人工处置与检索。
/// </summary>
public static class AlarmEndpoints
{
    /// <summary>可抑制/可闭合的告警类型白名单。拼错的 type 会生成一条永远匹配不上的抑制（操作者误以为已静默），必须挡在入口。</summary>
    private static readonly HashSet<string> KnownAlarmTypes =
        new(StringComparer.Ordinal) { "preload_low", "preload_high", "preload_loss", "signal_lost" };

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
                       au.username AS acked_by, cu.username AS cleared_by, a.acknowledged_note,
                       a.last_active_at
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
                    // 条件最近一次命中时刻（NULL=旧数据或触发后再未确认命中）——区分"仍在越限"与"已缓解未闭合"
                    lastActiveAt = r.IsDBNull(20) ? null : (DateTime?)r.GetDateTime(20),
                    isOpen = r.IsDBNull(8),
                    isAcked = !r.IsDBNull(7)
                });
            }
            return Results.Ok(new { count = items.Count, items });
        })
        .WithName("ListAlarms")
        .WithSummary("告警列表（默认只列开启的）");

        // 确认告警（operator+，user_id 强制取 HttpContext，不接客户端伪造）。
        // 状态更新与审计同事务：不允许出现"已 ack 但 audit_log 缺条"的断链。
        grp.MapPost("/{id:long}/ack", async (
            long id, HttpContext http, AckRequest? body, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var currentUser = UserContext.Require(http);
            const string sql = @"
                UPDATE alarms SET acknowledged_at = now(), acknowledged_by = @uid, acknowledged_note = @note
                WHERE id = @id AND acknowledged_at IS NULL
                RETURNING id, type, severity, triggered_at";

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            long ackId = 0; string? ackType = null, ackSeverity = null; DateTime ackTriggeredAt = default;
            await using (var cmd = new NpgsqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, currentUser.UserId);
                cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)body?.Note ?? DBNull.Value);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);

                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    ackId = r.GetInt64(0);
                    ackType = r.GetString(1);
                    ackSeverity = r.GetString(2);
                    ackTriggeredAt = r.GetDateTime(3);
                }
            }
            if (ackType is null)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "告警不存在或已 ack", id });
            }

            await WriteAlarmAuditAsync(conn, tx, currentUser.UserId, "alarm_ack", ackId,
                $"确认告警 #{ackId}（{DisplayLabels.AlarmType(ackType)}）", ct);
            await tx.CommitAsync(ct);

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

        // 手动闭合告警（operator+）— 配合 0011_safety_fixes.sql 的 partial unique 索引。闭合与审计同事务。
        grp.MapPost("/{id:long}/clear", async (
            long id, HttpContext http, ClearRequest? body, NpgsqlDataSource ds,
            IAlarmEvaluator evaluator, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            var reason = body?.Reason?.Trim();
            if (string.IsNullOrEmpty(reason)) reason = null;
            if (reason is { Length: > 500 })
                return Results.BadRequest(new { error = "闭合原因最多 500 字符", length = reason.Length });

            // cleared_by 持久化到库（安全动作可追责）；NULL 才是系统自动闭合
            const string sql = @"
                UPDATE alarms
                   SET cleared_at = now(),
                       cleared_by = @uid,
                       cleared_reason = COALESCE(@reason, 'manual_reset')
                 WHERE id = @id AND cleared_at IS NULL
                 RETURNING type, cleared_reason, bolt_id, channel_id";

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            string? alarmType = null, clearedReason = null;
            long boltId = 0, channelId = 0;
            await using (var cmd = new NpgsqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, u.UserId);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                cmd.Parameters.AddWithValue("reason", NpgsqlDbType.Text, (object?)reason ?? DBNull.Value);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    alarmType = r.GetString(0);
                    clearedReason = r.GetString(1);
                    boltId = r.GetInt64(2);
                    channelId = r.GetInt64(3);
                }
            }
            if (alarmType is null)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "告警不存在或已闭合", id });
            }

            await WriteAlarmAuditAsync(conn, tx, u.UserId, "alarm_clear", id,
                $"手动闭合告警 #{id}（{DisplayLabels.AlarmType(alarmType)}）原因：{clearedReason}", ct);
            await tx.CommitAsync(ct);

            // 复位评估器状态机（重臂）：手动闭合时条件可能仍在越限——若不复位，内存槽位停在 active，
            // 后续持续越限样本不会再触发任何新告警（要等完整恢复周期或进程重启），告警中心长期"无告警"
            // 而螺栓实际仍越限。RollbackTransition(Trigger) 恰好把槽位退到"差一条样本即可重触发"：
            // 条件仍在 → 下一条样本立即重报；条件已缓解 → 计数自然归零，不产生误报。
            // signal_lost 无内存状态机（按 DB open 集合判定），RollbackTransition 对其为 no-op。
            evaluator.RollbackTransition(new AlarmEvent(
                AlarmAction.Trigger, boltId, channelId, alarmType, "info", DateTimeOffset.UtcNow,
                PreloadN: null, PreloadRatio: null, TargetPreloadN: null,
                ThresholdLowRatio: 0m, ThresholdHighRatio: 0m, ConsecutiveCount: 0, Message: ""));

            return Results.Ok(new { id, type = alarmType, cleared = true, by = u.UserId });
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("ClearAlarm")
        .WithSummary("手动闭合告警（闭合后复位状态机：条件仍越限会立即重新触发）");

        // 抑制 / shelve（维护时段屏蔽某 bolt+type，带过期时间）。
        // boltId 必填：入库判定把 NULL 当通配符（PostgresMeasurementSink），若放行 bolt/type 双空，
        // operator 一次请求即可静默全站告警 7 天——监测系统不允许存在这种"全局静音键"。
        // alarmType 仍可空 = 抑制该螺栓全部类型（更换探头等整颗维护是合理场景）。
        grp.MapPost("/shelve", async (
            HttpContext http, ShelveRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            if (req.Hours <= 0 || req.Hours > 168)
                return Results.BadRequest(new { error = "Hours 必须在 1..168（即 1 小时 ~ 7 天）" });
            var reason = req.Reason?.Trim();
            if (string.IsNullOrEmpty(reason) || reason.Length < 5)
                return Results.BadRequest(new { error = "Reason 至少 5 个有效字符" });
            if (req.BoltId is not > 0)
                return Results.BadRequest(new { error = "boltId 必填：抑制必须锁定到具体螺栓，不允许全局抑制" });
            // type 白名单：拼错的 type 会生成一条永远匹配不上的抑制，操作者却以为已静默（null=抑制全部类型，合法）
            if (req.AlarmType is not null && !KnownAlarmTypes.Contains(req.AlarmType))
                return Results.BadRequest(new
                {
                    error = $"未知告警类型 '{req.AlarmType}'，可选：{string.Join(" / ", KnownAlarmTypes)}；或不传 = 抑制该螺栓全部类型"
                });

            string boltCode;
            await using (var check = ds.CreateCommand(
                "SELECT code FROM bolts WHERE id = @id AND deleted_at IS NULL"))
            {
                check.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, req.BoltId.Value);
                var scalar = await check.ExecuteScalarAsync(ct);
                if (scalar is not string code)
                    return Results.NotFound(new { error = "螺栓不存在或已删除", boltId = req.BoltId });
                boltCode = code;
            }

            const string sql = @"
                INSERT INTO alarm_shelvings (bolt_id, alarm_type, shelved_by, expires_at, reason)
                VALUES (@bolt, @type, @by, @exp, @reason)
                RETURNING id, expires_at";
            long shelvingId;
            DateTime expiresAt;
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var cmd = new NpgsqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, req.BoltId.Value);
                cmd.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, (object?)req.AlarmType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("by", NpgsqlDbType.Bigint, u.UserId);
                cmd.Parameters.AddWithValue("exp", NpgsqlDbType.TimestampTz, DateTimeOffset.UtcNow.AddHours(req.Hours));
                cmd.Parameters.AddWithValue("reason", NpgsqlDbType.Text, reason);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                shelvingId = r.GetInt64(0);
                expiresAt = r.GetDateTime(1);
            }

            await WriteAlarmAuditAsync(conn, tx, u.UserId, "alarm_shelve", shelvingId,
                $"抑制告警 {boltCode} · {(req.AlarmType is null ? "全部类型" : DisplayLabels.AlarmType(req.AlarmType))} {req.Hours} 小时，原因：{reason}",
                ct, targetType: "alarm_shelving");
            await tx.CommitAsync(ct);

            return Results.Ok(new { shelvingId, expiresAt });
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("ShelveAlarm")
        .WithSummary("抑制某 bolt+type 的告警直到 expires_at（boltId 必填，写审计）");

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

        // 取消抑制（取消与审计同事务）
        grp.MapPost("/shelve/{id:long}/cancel", async (long id, HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            var found = false;
            string? boltCode = null;
            string? alarmType = null;
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE alarm_shelvings s SET cancelled_at = now(), cancelled_by = @uid
                 WHERE s.id = @id AND s.cancelled_at IS NULL
                RETURNING (SELECT code FROM bolts WHERE id = s.bolt_id), s.alarm_type", conn, tx))
            {
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, u.UserId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    found = true;
                    boltCode = r.IsDBNull(0) ? null : r.GetString(0);
                    alarmType = r.IsDBNull(1) ? null : r.GetString(1);
                }
            }
            if (!found)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "抑制不存在或已取消", id });
            }

            await WriteAlarmAuditAsync(conn, tx, u.UserId, "alarm_shelve_cancel", id,
                $"取消抑制 #{id}（{boltCode ?? "全部螺栓"} · {(alarmType is null ? "全部类型" : DisplayLabels.AlarmType(alarmType))}）",
                ct, targetType: "alarm_shelving");
            await tx.CommitAsync(ct);
            return Results.Ok(new { id, cancelled = true });
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("CancelShelve");

        // 汇总统计（排除已软删螺栓的告警，与列表/总览同一软删口径）
        grp.MapGet("/summary", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            const string sql = @"
                SELECT a.severity, a.type,
                       COUNT(*) FILTER (WHERE a.cleared_at IS NULL) AS open_count,
                       COUNT(*) FILTER (WHERE a.cleared_at IS NULL AND a.acknowledged_at IS NULL) AS open_unacked,
                       COUNT(*) AS total_count
                FROM alarms a
                JOIN bolts b ON b.id = a.bolt_id AND b.deleted_at IS NULL
                GROUP BY a.severity, a.type
                ORDER BY a.severity DESC, a.type";
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
    /// 把告警确认/闭合/抑制动作写入 audit_log（append-only 轨迹），与状态更新共享同一事务。
    /// alarms 行本身的 acknowledged_by/cleared_by 是当前态；audit_log 是不可篡改的历史，满足“可追责”。
    /// </summary>
    private static async Task WriteAlarmAuditAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        long userId, string action, long targetId, string note, CancellationToken ct,
        string targetType = "alarm")
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
            VALUES (@user_id, @action, @target_type, @target_id, NULL, NULL, @note, now())", conn, tx);
        cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, userId);
        cmd.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action);
        cmd.Parameters.AddWithValue("target_type", NpgsqlDbType.Varchar, targetType);
        cmd.Parameters.AddWithValue("target_id", NpgsqlDbType.Bigint, targetId);
        cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, note);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>确认告警——值班的人看见了。备注可不填。</summary>
public sealed record AckRequest(string? Note = null);

/// <summary>人工闭合告警。原因可不填，但闭合人和原因都会落库，事后要靠它追责。</summary>
public sealed record ClearRequest(string? Reason = null);

/// <summary>
/// 把某颗螺栓的告警静音几小时（检修期间用）。BoltId 必须给：不给就成了全场静默，端点直接 400 拒掉。
/// 原因也必填，trim 完不足 5 个字同样拒——不然事后没人说得清当时为什么把告警关了。
/// </summary>
public sealed record ShelveRequest(long? BoltId, string? AlarmType, int Hours, string Reason);
