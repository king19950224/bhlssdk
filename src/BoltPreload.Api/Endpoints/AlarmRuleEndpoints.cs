using BoltPreload.Api.Auth;
using BoltPreload.Application.Alarm;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 告警阈值规则端点（/api/alarm-rules）：三级 scope（global/group/bolt）的提案、异人复核与生效链查询。
/// 解析顺序 bolt &gt; group &gt; global &gt; appsettings 出厂兜底，评估侧见 IAlarmRuleProvider。
/// <para>
/// 复核流程是两步异人（提案 pending → 批准 active / 驳回 rejected；新 active 落地时同目标旧 active
/// 置 superseded），语义照标定会签。有意不复用通道参数那套四步流：那套含设备执行、读回验证，
/// 纯库内变更用不上。
/// </para>
/// </summary>
public static class AlarmRuleEndpoints
{
    private static readonly HashSet<string> KnownScopes = new(StringComparer.Ordinal) { "global", "group", "bolt" };

    public static IEndpointRouteBuilder MapAlarmRuleEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/alarm-rules")
            .WithTags("AlarmRules")
            .RequireAuthorization(AuthPolicies.LoggedIn);

        // 规则列表（全部状态）：待审在前，其余按时间倒序
        grp.MapGet("/", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            const string sql = @"
                SELECT r.id, r.scope, r.group_id, mg.code, r.bolt_id, b.code,
                       r.trigger_low_ratio, r.clear_low_ratio, r.trigger_high_ratio, r.clear_high_ratio,
                       r.trigger_consecutive, r.clear_consecutive, r.critical_deviation, r.preload_loss_ratio,
                       r.reason, r.review_state, r.created_by, cu.username, r.created_at,
                       au.username, r.approved_at
                FROM alarm_rules r
                LEFT JOIN monitor_groups mg ON mg.id = r.group_id
                LEFT JOIN bolts b ON b.id = r.bolt_id
                LEFT JOIN users cu ON cu.id = r.created_by
                LEFT JOIN users au ON au.id = r.approved_by
                ORDER BY (r.review_state = 'pending') DESC, r.created_at DESC
                LIMIT 500";
            await using var cmd = ds.CreateCommand(sql);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new
                {
                    id = r.GetInt64(0),
                    scope = r.GetString(1),
                    groupId = r.IsDBNull(2) ? (long?)null : r.GetInt64(2),
                    groupCode = r.IsDBNull(3) ? null : r.GetString(3),
                    boltId = r.IsDBNull(4) ? (long?)null : r.GetInt64(4),
                    boltCode = r.IsDBNull(5) ? null : r.GetString(5),
                    triggerLowRatio = r.GetDecimal(6),
                    clearLowRatio = r.GetDecimal(7),
                    triggerHighRatio = r.GetDecimal(8),
                    clearHighRatio = r.GetDecimal(9),
                    triggerConsecutive = r.GetInt32(10),
                    clearConsecutive = r.GetInt32(11),
                    criticalDeviation = r.GetDecimal(12),
                    preloadLossRatio = r.GetDecimal(13),
                    reason = r.GetString(14),
                    reviewState = r.GetString(15),
                    createdBy = r.GetInt64(16),
                    createdByName = r.IsDBNull(17) ? null : r.GetString(17),
                    createdAt = r.GetDateTime(18),
                    reviewedByName = r.IsDBNull(19) ? null : r.GetString(19),
                    reviewedAt = r.IsDBNull(20) ? null : (DateTime?)r.GetDateTime(20),
                });
            }
            // truncated：让前端能提示"历史记录超出显示上限"（待审规则排最前永不丢，截掉的只会是最老的历史）
            return Results.Ok(new { count = items.Count, truncated = items.Count >= 500, items });
        })
        .WithName("ListAlarmRules")
        .WithSummary("告警阈值规则列表（含待审/生效/驳回/已替代；最多 500 条，待审优先）");

        // 提案表单的目标下拉：部位与螺栓两张平铺表（资产端点是 项目→机组→部位 三级钻取，表单用不顺手）
        grp.MapGet("/targets", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var groups = new List<object>();
            await using (var cmd = ds.CreateCommand(
                "SELECT id, code, name FROM monitor_groups WHERE deleted_at IS NULL ORDER BY code"))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                    groups.Add(new { id = r.GetInt64(0), code = r.GetString(1), name = r.GetString(2) });
            }

            var bolts = new List<object>();
            await using (var cmd = ds.CreateCommand(@"
                SELECT b.id, b.code, mg.code
                FROM bolts b
                JOIN monitor_groups mg ON mg.id = b.group_id
                WHERE b.deleted_at IS NULL
                ORDER BY b.code"))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                    bolts.Add(new { id = r.GetInt64(0), code = r.GetString(1), groupCode = r.GetString(2) });
            }

            return Results.Ok(new { groups, bolts });
        })
        .WithName("ListAlarmRuleTargets")
        .WithSummary("规则提案的目标候选（平铺的部位/螺栓清单）");

        // 规则设定（reviewer+）：2026-08-19 取消审批环节——提交即生效（同目标旧规则自动置换，
        // 签署人/时间/依据全部落档）。参数链校验挡在入口，DB 的 CHECK 是最后防线。
        grp.MapPost("/", async (
            HttpContext http, AlarmRuleProposeRequest req, NpgsqlDataSource ds,
            IAlarmRuleProvider rules, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);

            var err = ValidateThresholds(req);
            if (err is not null) return Results.BadRequest(new { error = err });

            if (!KnownScopes.Contains(req.Scope))
                return Results.BadRequest(new { error = $"scope 必须是 global / group / bolt，收到 '{req.Scope}'" });
            var scopeOk = req.Scope switch
            {
                "global" => req.GroupId is null && req.BoltId is null,
                "group" => req.GroupId is > 0 && req.BoltId is null,
                _ => req.BoltId is > 0 && req.GroupId is null,
            };
            if (!scopeOk)
                return Results.BadRequest(new { error = "scope 与目标不成套：global 不带目标，group 只带 groupId，bolt 只带 boltId" });

            string targetLabel;
            switch (req.Scope)
            {
                case "group":
                {
                    await using var q = ds.CreateCommand("SELECT code FROM monitor_groups WHERE id = @id");
                    q.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, req.GroupId!.Value);
                    if (await q.ExecuteScalarAsync(ct) is not string code)
                        return Results.NotFound(new { error = "监测部位不存在", groupId = req.GroupId });
                    targetLabel = $"部位 {code}";
                    break;
                }
                case "bolt":
                {
                    await using var q = ds.CreateCommand("SELECT code FROM bolts WHERE id = @id AND deleted_at IS NULL");
                    q.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, req.BoltId!.Value);
                    if (await q.ExecuteScalarAsync(ct) is not string code)
                        return Results.NotFound(new { error = "螺栓不存在或已删除", boltId = req.BoltId });
                    targetLabel = $"螺栓 {code}";
                    break;
                }
                default:
                    targetLabel = "全站";
                    break;
            }

            var reason = req.Reason?.Trim();
            if (string.IsNullOrEmpty(reason) || reason.Length < 5)
                return Results.BadRequest(new { error = "Reason 至少 5 个有效字符（国标条款/合同条款/标定记录编号）" });

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            long ruleId;
            try
            {
                // 同目标只留一条 active：旧规则置 superseded（历史可查），新规则直接生效
                var supersede = req.Scope switch
                {
                    "global" => "UPDATE alarm_rules SET review_state = 'superseded' WHERE review_state = 'active' AND scope = 'global'",
                    "group" => "UPDATE alarm_rules SET review_state = 'superseded' WHERE review_state = 'active' AND scope = 'group' AND group_id = @target",
                    _ => "UPDATE alarm_rules SET review_state = 'superseded' WHERE review_state = 'active' AND scope = 'bolt' AND bolt_id = @target",
                };
                await using (var s = new NpgsqlCommand(supersede, conn, tx))
                {
                    if (req.Scope != "global")
                        s.Parameters.AddWithValue("target", NpgsqlDbType.Bigint, req.Scope == "group" ? req.GroupId!.Value : req.BoltId!.Value);
                    await s.ExecuteNonQueryAsync(ct);
                }

                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO alarm_rules
                        (scope, group_id, bolt_id,
                         trigger_low_ratio, clear_low_ratio, trigger_high_ratio, clear_high_ratio,
                         trigger_consecutive, clear_consecutive, critical_deviation, preload_loss_ratio,
                         reason, created_by, review_state, approved_by, approved_at)
                    VALUES (@scope, @gid, @bid, @tl, @cl, @th, @ch, @tc, @cc, @cd, @pl, @reason, @uid, 'active', @uid, now())
                    RETURNING id", conn, tx);
                cmd.Parameters.AddWithValue("scope", NpgsqlDbType.Varchar, req.Scope);
                cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, (object?)req.GroupId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("bid", NpgsqlDbType.Bigint, (object?)req.BoltId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("tl", NpgsqlDbType.Numeric, req.TriggerLowRatio);
                cmd.Parameters.AddWithValue("cl", NpgsqlDbType.Numeric, req.ClearLowRatio);
                cmd.Parameters.AddWithValue("th", NpgsqlDbType.Numeric, req.TriggerHighRatio);
                cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Numeric, req.ClearHighRatio);
                cmd.Parameters.AddWithValue("tc", NpgsqlDbType.Integer, req.TriggerConsecutive);
                cmd.Parameters.AddWithValue("cc", NpgsqlDbType.Integer, req.ClearConsecutive);
                cmd.Parameters.AddWithValue("cd", NpgsqlDbType.Numeric, req.CriticalDeviation);
                cmd.Parameters.AddWithValue("pl", NpgsqlDbType.Numeric, req.PreloadLossRatio);
                cmd.Parameters.AddWithValue("reason", NpgsqlDbType.Text, reason);
                cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, u.UserId);
                ruleId = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await tx.RollbackAsync(ct);
                return Results.Conflict(new { error = "同一目标已有待处理的历史提案，请先在规则列表处理它" });
            }

            await WriteRuleAuditAsync(conn, tx, u.UserId, "alarm_rule_propose", ruleId,
                $"设定告警阈值规则 #{ruleId}（{targetLabel}）并生效，依据：{reason}", ct);
            await tx.CommitAsync(ct);

            // 提交即生效：显式刷新快照，不等 30 秒定时轮。刷新失败不回滚（下一轮定时刷新会补上）
            try
            {
                await rules.RefreshAsync(ct);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger(nameof(AlarmRuleEndpoints))
                    .LogWarning(ex, "规则 #{Id} 生效后快照刷新失败，最迟 30 秒后由定时刷新生效", ruleId);
            }
            return Results.Ok(new { id = ruleId, reviewState = "active" });
        })
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithName("ProposeAlarmRule")
        .WithSummary("设定告警阈值规则（提交即生效，同目标旧规则自动置换；签署与依据落档）");

        // 批准存量 pending（reviewer+）：同目标旧 active 置 superseded，本条置 active 并刷新快照
        grp.MapPost("/{id:long}/approve", async (
            long id, HttpContext http, NpgsqlDataSource ds, IAlarmRuleProvider rules,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            string? scope = null, state = null;
            long? groupId = null, boltId = null;
            long createdBy = 0;
            await using (var q = new NpgsqlCommand(
                "SELECT scope, group_id, bolt_id, created_by, review_state FROM alarm_rules WHERE id = @id FOR UPDATE", conn, tx))
            {
                q.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                await using var r = await q.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    scope = r.GetString(0);
                    groupId = r.IsDBNull(1) ? null : r.GetInt64(1);
                    boltId = r.IsDBNull(2) ? null : r.GetInt64(2);
                    createdBy = r.GetInt64(3);
                    state = r.GetString(4);
                }
            }
            if (scope is null)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "规则不存在", id });
            }
            if (state != "pending")
            {
                await tx.RollbackAsync(ct);
                return Results.Conflict(new { error = $"规则不在待审状态（当前 {state}）", id });
            }
            // 2026-08-19 取消审批环节：异人限制移除。本端点只服务存量 pending（新规则提交即生效），
            // 谁批准都记入 approved_by 留痕
            _ = createdBy;

            // 同目标只留一条 active：旧规则置 superseded，历史可查
            var supersede = scope switch
            {
                "global" => "UPDATE alarm_rules SET review_state = 'superseded' WHERE review_state = 'active' AND scope = 'global'",
                "group" => "UPDATE alarm_rules SET review_state = 'superseded' WHERE review_state = 'active' AND scope = 'group' AND group_id = @target",
                _ => "UPDATE alarm_rules SET review_state = 'superseded' WHERE review_state = 'active' AND scope = 'bolt' AND bolt_id = @target",
            };
            await using (var s = new NpgsqlCommand(supersede, conn, tx))
            {
                if (scope != "global")
                    s.Parameters.AddWithValue("target", NpgsqlDbType.Bigint, scope == "group" ? groupId!.Value : boltId!.Value);
                await s.ExecuteNonQueryAsync(ct);
            }

            await using (var a = new NpgsqlCommand(
                "UPDATE alarm_rules SET review_state = 'active', approved_by = @uid, approved_at = now() WHERE id = @id", conn, tx))
            {
                a.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, u.UserId);
                a.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                await a.ExecuteNonQueryAsync(ct);
            }

            await WriteRuleAuditAsync(conn, tx, u.UserId, "alarm_rule_approve", id,
                $"批准告警阈值规则 #{id}（{ScopeLabel(scope)}），提案人 #{createdBy}", ct);
            await tx.CommitAsync(ct);

            // 批准即生效：显式刷新快照，不等 30 秒定时轮。刷新失败不回滚批准（下一轮定时刷新会补上）
            try
            {
                await rules.RefreshAsync(ct);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger(nameof(AlarmRuleEndpoints))
                    .LogWarning(ex, "规则 #{Id} 批准后快照刷新失败，最迟 30 秒后由定时刷新生效", id);
            }
            return Results.Ok(new { id, reviewState = "active" });
        })
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithName("ApproveAlarmRule")
        .WithSummary("批准存量待审的告警规则（新规则提交即生效，本端点处理历史记录）");

        // 驳回（reviewer+）：提案人驳回自己的提案 = 撤回，允许
        grp.MapPost("/{id:long}/reject", async (
            long id, HttpContext http, AlarmRuleRejectRequest? body, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            var note = body?.Note?.Trim();

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            string? scope = null;
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE alarm_rules SET review_state = 'rejected', approved_by = @uid, approved_at = now()
                 WHERE id = @id AND review_state = 'pending'
                RETURNING scope", conn, tx))
            {
                cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, u.UserId);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                if (await cmd.ExecuteScalarAsync(ct) is string s) scope = s;
            }
            if (scope is null)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "规则不存在或不在待审状态", id });
            }

            await WriteRuleAuditAsync(conn, tx, u.UserId, "alarm_rule_reject", id,
                $"驳回告警阈值规则 #{id}（{ScopeLabel(scope)}）{(string.IsNullOrEmpty(note) ? "" : $"，理由：{note}")}", ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { id, reviewState = "rejected" });
        })
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithName("RejectAlarmRule")
        .WithSummary("驳回告警阈值规则（提案人自驳 = 撤回）");

        // 某颗螺栓的生效链：页面展示"当前用哪套阈值、来自哪级"。挂在 bolts 路径下更顺手
        app.MapGet("/api/bolts/{boltId:long}/effective-alarm-rule", async (
            long boltId, NpgsqlDataSource ds, IOptions<StaticThresholdAlarmOptions> factoryDefault,
            CancellationToken ct) =>
        {
            long groupId;
            await using (var q = ds.CreateCommand("SELECT group_id FROM bolts WHERE id = @id AND deleted_at IS NULL"))
            {
                q.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, boltId);
                if (await q.ExecuteScalarAsync(ct) is not long g)
                    return Results.NotFound(new { error = "螺栓不存在或已删除", boltId });
                groupId = g;
            }

            const string sql = @"
                SELECT r.id, r.scope,
                       r.trigger_low_ratio, r.clear_low_ratio, r.trigger_high_ratio, r.clear_high_ratio,
                       r.trigger_consecutive, r.clear_consecutive, r.critical_deviation, r.preload_loss_ratio,
                       r.reason, r.approved_at
                FROM alarm_rules r
                WHERE r.review_state = 'active'
                  AND ((r.scope = 'bolt' AND r.bolt_id = @b)
                    OR (r.scope = 'group' AND r.group_id = @g)
                    OR  r.scope = 'global')
                ORDER BY CASE r.scope WHEN 'bolt' THEN 0 WHEN 'group' THEN 1 ELSE 2 END
                LIMIT 1";
            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
            cmd.Parameters.AddWithValue("g", NpgsqlDbType.Bigint, groupId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                return Results.Ok(new
                {
                    boltId,
                    source = r.GetString(1),
                    ruleId = (long?)r.GetInt64(0),
                    triggerLowRatio = r.GetDecimal(2),
                    clearLowRatio = r.GetDecimal(3),
                    triggerHighRatio = r.GetDecimal(4),
                    clearHighRatio = r.GetDecimal(5),
                    triggerConsecutive = r.GetInt32(6),
                    clearConsecutive = r.GetInt32(7),
                    criticalDeviation = r.GetDecimal(8),
                    preloadLossRatio = r.GetDecimal(9),
                    reason = r.GetString(10),
                    approvedAt = r.IsDBNull(11) ? null : (DateTime?)r.GetDateTime(11),
                });
            }

            // 库里没有可用规则 → appsettings 出厂兜底，与评估器的回退路径一致
            var d = factoryDefault.Value;
            return Results.Ok(new
            {
                boltId,
                source = "default",
                ruleId = (long?)null,
                triggerLowRatio = d.TriggerLowRatio,
                clearLowRatio = d.ClearLowRatio,
                triggerHighRatio = d.TriggerHighRatio,
                clearHighRatio = d.ClearHighRatio,
                triggerConsecutive = d.TriggerConsecutive,
                clearConsecutive = d.ClearConsecutive,
                criticalDeviation = d.CriticalDeviation,
                preloadLossRatio = d.PreloadLossRatio,
                reason = (string?)null,
                approvedAt = (DateTime?)null,
            });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("GetEffectiveAlarmRule")
        .WithSummary("某颗螺栓生效的告警阈值及其来源级别（bolt/group/global/default）");

        return app;
    }

    /// <summary>
    /// 阈值参数链校验。回差方向（clear 在 trigger 的安全侧）由 DB CHECK 兜底，
    /// 这里连完整顺序一起管：loss &lt; 触发下限 &lt; 恢复下限 &lt; 恢复上限 &lt; 触发上限。
    /// </summary>
    private static string? ValidateThresholds(AlarmRuleProposeRequest req)
    {
        if (req.PreloadLossRatio < 0) return "PreloadLossRatio 不得为负";
        if (req.TriggerLowRatio <= req.PreloadLossRatio) return "TriggerLowRatio 必须大于 PreloadLossRatio（脱落档在触发下限之下）";
        if (req.ClearLowRatio <= req.TriggerLowRatio) return "ClearLowRatio 必须大于 TriggerLowRatio（回差方向）";
        if (req.ClearHighRatio <= req.ClearLowRatio) return "ClearHighRatio 必须大于 ClearLowRatio";
        if (req.TriggerHighRatio <= req.ClearHighRatio) return "TriggerHighRatio 必须大于 ClearHighRatio（回差方向）";
        if (req.TriggerHighRatio >= 10) return "TriggerHighRatio 超出合理范围（ratio 是相对目标预紧力的比值）";
        if (req.TriggerConsecutive is < 1 or > 1000) return "TriggerConsecutive 必须在 1..1000";
        if (req.ClearConsecutive is < 1 or > 1000) return "ClearConsecutive 必须在 1..1000";
        if (req.CriticalDeviation is <= 0 or > 1) return "CriticalDeviation 必须在 (0, 1]";
        return null;
    }

    private static string ScopeLabel(string scope) => scope switch
    {
        "global" => "全站",
        "group" => "部位级",
        _ => "螺栓级",
    };

    /// <summary>规则提案/批准/驳回写 audit_log，与状态变更同事务（与告警处置的审计口径一致）。</summary>
    private static async Task WriteRuleAuditAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        long userId, string action, long targetId, string note, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at, ip_addr)
            VALUES (@user_id, @action, 'alarm_rule', @target_id, NULL, NULL, @note, now(), @ip)", conn, tx);
        cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, userId);
        cmd.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action);
        cmd.Parameters.AddWithValue("target_id", NpgsqlDbType.Bigint, targetId);
        cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, note);
        AuditClientIp.Bind(cmd.Parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// 告警阈值规则提案。八个阈值参数与 <see cref="BoltPreload.Application.Alarm.StaticThresholdAlarmOptions"/>
/// 一一对应；Reason 强制填修改依据（国标条款/合同条款/标定记录编号），跟着规则归档。
/// </summary>
public sealed record AlarmRuleProposeRequest(
    string Scope,
    long? GroupId,
    long? BoltId,
    decimal TriggerLowRatio,
    decimal ClearLowRatio,
    decimal TriggerHighRatio,
    decimal ClearHighRatio,
    int TriggerConsecutive,
    int ClearConsecutive,
    decimal CriticalDeviation,
    decimal PreloadLossRatio,
    string Reason);

/// <summary>驳回告警阈值规则。理由可不填，填了记进审计备注。</summary>
public sealed record AlarmRuleRejectRequest(string? Note = null);
