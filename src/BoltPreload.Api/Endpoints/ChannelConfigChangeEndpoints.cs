using System.Text.Json;
using BoltPreload.Api.Auth;
using BoltPreload.Application.Devices;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Application.Ingestion;
using BoltPreload.Fx;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 通道参数变更审批工作流：<c>requested → approved（异人审批）→ executed → verified</c>，
/// 另含终态 <c>rejected</c>（驳回）/ <c>failed</c>（执行失败）。
/// <para>
/// 关闭"操作员单方面把安全攸关测量参数直接下发设备"的安全漏洞：提交不碰设备，必须经【另一名】
/// reviewer 审批后才能执行下发；每步写 audit_log；执行/验证按 deviceId 路由到对应设备连接器。
/// </para>
/// 提交入口见 <c>POST /api/devices/{id}/diagnostic/configure-channel/{ch}</c>（仅建 requested）。
/// </summary>
public static class ChannelConfigChangeEndpoints
{
    public static IEndpointRouteBuilder MapChannelConfigChangeEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- 队列 / 历史 ----
        app.MapGet("/api/config-changes", async (string? status, int? limit, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var lim = Math.Clamp(limit ?? 100, 1, 500);
            var (where, statusParam) = status switch
            {
                "pending" => ("c.status IN ('requested','approved')", (string?)null),
                null or "" => ("TRUE", null),
                _ => ("c.status = @status", status)
            };
            // 联表取用户名：审批流展示"谁提交/谁审批/谁执行"必须是人名，不能让客户对着"用户1"猜
            await using var cmd = ds.CreateCommand($@"
                SELECT c.id, c.device_id, c.channel_id, c.channel_no, c.status, c.requested_by, c.requested_at,
                       c.approved_by, c.approved_at, c.executed_by, c.executed_at, c.verified_at, c.failure_reason, c.note, c.requested_jsonb,
                       ru.username, au.username, eu.username
                FROM channel_config_change_requests c
                LEFT JOIN users ru ON ru.id = c.requested_by
                LEFT JOIN users au ON au.id = c.approved_by
                LEFT JOIN users eu ON eu.id = c.executed_by
                WHERE {where}
                ORDER BY c.requested_at DESC
                LIMIT @lim");
            if (statusParam is not null) cmd.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, statusParam);
            cmd.Parameters.AddWithValue("lim", NpgsqlDbType.Integer, lim);
            var items = await ReadListAsync(cmd, ct);
            return Results.Ok(new { count = items.Count, items });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithTags("ConfigChange")
        .WithName("ListConfigChanges")
        .WithSummary("参数变更请求列表（status=pending 看待审批/待执行队列）");

        app.MapGet("/api/config-changes/{id:long}", async (long id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand(@"
                SELECT c.id, c.device_id, c.channel_id, c.channel_no, c.status, c.requested_by, c.requested_at,
                       c.approved_by, c.approved_at, c.executed_by, c.executed_at, c.verified_at, c.failure_reason, c.note, c.requested_jsonb,
                       ru.username, au.username, eu.username
                FROM channel_config_change_requests c
                LEFT JOIN users ru ON ru.id = c.requested_by
                LEFT JOIN users au ON au.id = c.approved_by
                LEFT JOIN users eu ON eu.id = c.executed_by
                WHERE c.id=@id");
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
            var items = await ReadListAsync(cmd, ct);
            return items.Count == 0 ? Results.NotFound(new { error = "请求不存在", id }) : Results.Ok(items[0]);
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithTags("ConfigChange")
        .WithName("GetConfigChange");

        // ---- 审批（异人，职责分离）----
        app.MapPost("/api/config-changes/{id:long}/approve", async (
            long id, HttpContext http, NpgsqlDataSource ds, ILoggerFactory lf, CancellationToken ct) =>
        {
            var reviewerId = UserContext.Require(http).UserId;
            var row = await LoadAsync(ds, id, ct);
            if (row is null) return Results.NotFound(new { error = "请求不存在", id });
            if (row.Status != "requested") return Results.Conflict(new { error = $"当前状态 {row.Status}，仅 requested 可审批", id });
            if (row.RequestedBy == reviewerId)
                return Results.BadRequest(new { error = "审批人必须与提交人不同（两人复核底线）。", requestedBy = row.RequestedBy });

            // 审批与审计同事务（责任链不可断）
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE channel_config_change_requests
                SET status='approved', approved_by=@rev, approved_at=now()
                WHERE id=@id AND status='requested'", conn, tx))
            {
                cmd.Parameters.AddWithValue("rev", NpgsqlDbType.Bigint, reviewerId);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Conflict(new { error = "状态已变更，审批未生效（并发？）", id });
                }
            }

            await WriteAuditAsync(conn, tx, reviewerId, "config_change_approve", row.ChannelId,
                JsonSerializer.Serialize(new { requestId = id, requestedBy = row.RequestedBy }),
                $"审批通过通道参数变更 #{id}", ct);
            await tx.CommitAsync(ct);
            lf.CreateLogger("ConfigChange").LogWarning("参数变更已审批 #{Id} reviewer={Rev}", id, reviewerId);
            return Results.Ok(new { id, status = "approved", approvedBy = reviewerId });
        })
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithTags("ConfigChange")
        .WithName("ApproveConfigChange")
        .WithSummary("审批参数变更（必须由不同于提交人的 reviewer）");

        // ---- 驳回 ----
        app.MapPost("/api/config-changes/{id:long}/reject", async (
            long id, RejectBody? body, HttpContext http, NpgsqlDataSource ds, ILoggerFactory lf, CancellationToken ct) =>
        {
            var reviewerId = UserContext.Require(http).UserId;
            var row = await LoadAsync(ds, id, ct);
            if (row is null) return Results.NotFound(new { error = "请求不存在", id });
            if (row.Status != "requested")
                return Results.Conflict(new { error = $"当前状态 {row.Status}，仅 requested 可驳回", id });

            var reason = string.IsNullOrWhiteSpace(body?.Reason) ? "（未填写驳回原因）" : body!.Reason!.Trim();
            // 驳回与审计同事务
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE channel_config_change_requests
                SET status='rejected', approved_by=@rev, approved_at=now(), failure_reason=@reason
                WHERE id=@id AND status='requested'", conn, tx))
            {
                cmd.Parameters.AddWithValue("rev", NpgsqlDbType.Bigint, reviewerId);
                cmd.Parameters.AddWithValue("reason", NpgsqlDbType.Text, reason);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Conflict(new { error = "状态已变更，驳回未生效（并发？）", id });
                }
            }

            await WriteAuditAsync(conn, tx, reviewerId, "config_change_reject", row.ChannelId,
                JsonSerializer.Serialize(new { requestId = id, reason }), $"驳回通道参数变更 #{id}：{reason}", ct);
            await tx.CommitAsync(ct);
            lf.CreateLogger("ConfigChange").LogWarning("参数变更被驳回 #{Id} reviewer={Rev} reason={Reason}", id, reviewerId, reason);
            return Results.Ok(new { id, status = "rejected", rejectedBy = reviewerId, reason });
        })
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithTags("ConfigChange")
        .WithName("RejectConfigChange")
        .WithSummary("驳回参数变更");

        // ---- 执行（仅 approved；下发到对应设备）----
        app.MapPost("/api/config-changes/{id:long}/execute", async (
            long id, HttpContext http, IDeviceConnectorManager deviceManager,
            NpgsqlDataSource ds, IChannelResolver resolver, ProbeScanState probeScanState,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("ConfigChange");
            var userId = UserContext.Require(http).UserId;

            var row = await LoadAsync(ds, id, ct);
            if (row is null) return Results.NotFound(new { error = "请求不存在", id });
            if (row.Status != "approved")
                return Results.Conflict(new { error = $"当前状态 {row.Status}，仅 approved 可执行（必须先审批）", id });

            var runtime = deviceManager.Find(row.DeviceId);
            if (runtime is null)
                return Results.NotFound(new { error = $"设备未加载到运行时：{row.DeviceId}", deviceId = row.DeviceId });
            // 扫描互斥：扫描期间下发通道配置会与扫描器抢设备（set-mode/MCR/commission 同一约束）
            if (probeScanState.IsRunning(row.DeviceId))
                return Results.Conflict(new { error = "通道自动检测进行中，暂缓执行下发", deviceId = row.DeviceId, id });

            ChannelConfigUpdate? updates;
            try { updates = JsonSerializer.Deserialize<ChannelConfigUpdate>(row.RequestedJson); }
            catch (Exception ex)
            {
                logger.LogError(ex, "参数变更请求体反序列化失败 #{Id}", id);
                return Results.BadRequest(new { error = "请求参数格式不正确，无法解析", id });
            }
            if (updates is null) return Results.BadRequest(new { error = "请求参数为空", id });

            try
            {
                await runtime.Connector.ConfigureChannelAsync((ushort)row.ChannelNo, updates, runtime.Options.Address, ct);

                await using (var cmd = ds.CreateCommand(@"
                    UPDATE channel_config_change_requests
                    SET status='executed', executed_by=@user, executed_at=now(), failure_reason=NULL
                    WHERE id=@id AND status='approved'"))
                {
                    cmd.Parameters.AddWithValue("user", NpgsqlDbType.Bigint, userId);
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                    if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                        return Results.Conflict(new { error = "状态已变更，执行未记录（并发？）；设备可能已下发，请核对", id });
                }

                // 同步 channels 冗余配置 + 失效解析器缓存——否则热路径/前端长期按旧 DB 配置换算，
                // 设备与库漂移（历史缺口：执行只改设备和审批单，不 sync 库）。
                await SyncChannelRowAsync(ds, row.ChannelId, updates, ct);
                resolver.Invalidate(row.DeviceId, (ushort)row.ChannelNo);

                await WriteAuditAsync(ds, userId, "config_change_execute", row.ChannelId,
                    JsonSerializer.Serialize(new { requestId = id, deviceId = row.DeviceId, channelNo = row.ChannelNo, updates }),
                    $"执行下发通道参数变更 #{id}", ct);
                logger.LogWarning("参数变更已执行下发 #{Id} device={Dev} ch={Ch} by user={User}",
                    id, row.DeviceId, row.ChannelNo, userId);

                // 触发读回，便于随后 verify 闭环（响应经 SDK 事件异步落 device_parameter_history）
                await TriggerReadbackAsync(runtime.Connector, (ushort)row.ChannelNo, runtime.Options.Address, updates, ct);

                return Results.Ok(new
                {
                    id, status = "executed", deviceId = row.DeviceId, channelNo = row.ChannelNo,
                    note = "已下发设备并同步通道配置。请执行“验证”触发读回比对确认实际生效。"
                });
            }
            catch (Exception ex)
            {
                await using var cmd = ds.CreateCommand(@"
                    UPDATE channel_config_change_requests
                    SET status='failed', failure_reason=@reason
                    WHERE id=@id AND status='approved'");
                cmd.Parameters.AddWithValue("reason", NpgsqlDbType.Text, ex.Message);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                await cmd.ExecuteNonQueryAsync(ct);
                logger.LogError(ex, "参数变更执行失败 #{Id} device={Dev} ch={Ch}", id, row.DeviceId, row.ChannelNo);
                return Results.BadRequest(new { error = ex.Message, id, status = "failed" });
            }
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithTags("ConfigChange")
        .WithName("ExecuteConfigChange")
        .WithSummary("执行已审批的参数变更（下发设备）");

        // ---- 验证（读回比对，仅 executed）----
        app.MapPost("/api/config-changes/{id:long}/verify", async (
            long id, HttpContext http, IDeviceConnectorManager deviceManager,
            NpgsqlDataSource ds, ILoggerFactory lf, CancellationToken ct) =>
        {
            var userId = UserContext.Require(http).UserId;
            var row = await LoadAsync(ds, id, ct);
            if (row is null) return Results.NotFound(new { error = "请求不存在", id });
            if (row.Status != "executed")
                return Results.Conflict(new { error = $"当前状态 {row.Status}，仅 executed 可验证", id });

            var updates = JsonSerializer.Deserialize<ChannelConfigUpdate>(row.RequestedJson) ?? new ChannelConfigUpdate();
            var expected = ExpectedParams(updates);

            // 读回最近的设备实际参数（device_parameter_history），与请求值比对
            var actual = await LoadLatestReadbackAsync(ds, row.DeviceId, row.ChannelNo, expected.Select(e => e.Param), ct);
            var comparisons = expected.Select(e =>
            {
                var has = actual.TryGetValue(e.Param, out var act);
                var match = has && Math.Abs(act - e.Value) <= 0.001m;
                return new { param = e.Param, requested = e.Value, actual = has ? act : (decimal?)null, match };
            }).ToArray();

            var allMatched = comparisons.Length > 0 && comparisons.All(c => c.match);
            var verifyJson = JsonSerializer.Serialize(new { verifiedBy = userId, comparisons });

            if (allMatched)
            {
                await using var cmd = ds.CreateCommand(@"
                    UPDATE channel_config_change_requests
                    SET status='verified', verify_started_at=COALESCE(verify_started_at, now()),
                        verified_at=now(), verify_jsonb=@vj::jsonb
                    WHERE id=@id AND status='executed'");
                cmd.Parameters.AddWithValue("vj", NpgsqlDbType.Jsonb, verifyJson);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                await cmd.ExecuteNonQueryAsync(ct);
                await WriteAuditAsync(ds, userId, "config_change_verify", row.ChannelId, verifyJson,
                    $"读回验证通过通道参数变更 #{id}", ct);
                lf.CreateLogger("ConfigChange").LogInformation("参数变更读回验证通过 #{Id}", id);
                return Results.Ok(new { id, status = "verified", comparisons });
            }

            // 未全部匹配：记录本次比对，保持 executed，等设备读回到位后再验
            await using (var cmd = ds.CreateCommand(@"
                UPDATE channel_config_change_requests
                SET verify_started_at=COALESCE(verify_started_at, now()), verify_jsonb=@vj::jsonb
                WHERE id=@id AND status='executed'"))
            {
                cmd.Parameters.AddWithValue("vj", NpgsqlDbType.Jsonb, verifyJson);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            return Results.Ok(new
            {
                id, status = "executed", verified = false, comparisons,
                note = "尚未全部匹配（设备读回未到位或不一致），请稍后重试验证。"
            });
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithTags("ConfigChange")
        .WithName("VerifyConfigChange")
        .WithSummary("读回比对验证已执行的参数变更");

        return app;
    }

    private sealed record ChangeRow(
        long Id, long DeviceId, long ChannelId, short ChannelNo,
        string Status, long? RequestedBy, long? ApprovedBy, string RequestedJson);

    private static async Task<ChangeRow?> LoadAsync(NpgsqlDataSource ds, long id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(@"
            SELECT id, device_id, channel_id, channel_no, status, requested_by, approved_by, requested_jsonb
            FROM channel_config_change_requests WHERE id=@id");
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new ChangeRow(
            r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt16(3),
            r.GetString(4),
            r.IsDBNull(5) ? null : r.GetInt64(5),
            r.IsDBNull(6) ? null : r.GetInt64(6),
            r.GetString(7));
    }

    private static async Task<List<object>> ReadListAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var items = new List<object>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            items.Add(new
            {
                id = r.GetInt64(0),
                deviceId = r.GetInt64(1),
                channelId = r.GetInt64(2),
                channelNo = r.GetInt16(3),
                status = r.GetString(4),
                requestedBy = r.IsDBNull(5) ? (long?)null : r.GetInt64(5),
                requestedAt = r.GetDateTime(6),
                approvedBy = r.IsDBNull(7) ? (long?)null : r.GetInt64(7),
                approvedAt = r.IsDBNull(8) ? (DateTime?)null : r.GetDateTime(8),
                executedBy = r.IsDBNull(9) ? (long?)null : r.GetInt64(9),
                executedAt = r.IsDBNull(10) ? (DateTime?)null : r.GetDateTime(10),
                verifiedAt = r.IsDBNull(11) ? (DateTime?)null : r.GetDateTime(11),
                failureReason = r.IsDBNull(12) ? null : r.GetString(12),
                note = r.IsDBNull(13) ? null : r.GetString(13),
                requested = r.IsDBNull(14) ? null : r.GetString(14),
                requestedByName = r.FieldCount > 15 && !r.IsDBNull(15) ? r.GetString(15) : null,
                approvedByName = r.FieldCount > 16 && !r.IsDBNull(16) ? r.GetString(16) : null,
                executedByName = r.FieldCount > 17 && !r.IsDBNull(17) ? r.GetString(17) : null
            });
        }
        return items;
    }

    private static List<(string Param, decimal Value)> ExpectedParams(ChannelConfigUpdate u)
    {
        var list = new List<(string, decimal)>();
        if (u.ProbeFreqMhz is { } a) list.Add(("ProbeFreq", a));
        if (u.EchoWinNs is { } b) list.Add(("EchoWin", b));
        if (u.EchoWidthUs is { } c) list.Add(("EchoWidth", c));
        if (u.Gain is { } d) list.Add(("Gain", d));
        if (u.Threshold is { } e) list.Add(("Threshold", e));
        if (u.MeasMode is { } f) list.Add(("MeasMode", f));
        if (u.TcMode is { } g) list.Add(("TcMode", g));
        if (u.TempCoefficient is { } h) list.Add(("TempCoefficient", h));
        if (u.RefTimeNs is { } i) list.Add(("RefTime", i));
        if (u.RefTempC is { } j) list.Add(("RefTemp", j));
        return list;
    }

    private static async Task<Dictionary<string, decimal>> LoadLatestReadbackAsync(
        NpgsqlDataSource ds, long deviceId, short channelNo, IEnumerable<string> paramNames, CancellationToken ct)
    {
        var result = new Dictionary<string, decimal>();
        var names = paramNames.ToArray();
        if (names.Length == 0) return result;

        await using var cmd = ds.CreateCommand(@"
            SELECT DISTINCT ON (param_name) param_name, param_value_text
            FROM device_parameter_history
            WHERE device_id=@device_id AND channel_no=@channel_no
              AND param_name = ANY(@names) AND param_value_text IS NOT NULL
            ORDER BY param_name, received_at DESC");
        cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
        cmd.Parameters.AddWithValue("channel_no", NpgsqlDbType.Smallint, channelNo);
        cmd.Parameters.AddWithValue("names", NpgsqlDbType.Array | NpgsqlDbType.Text, names);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (decimal.TryParse(r.GetString(1), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
                result[r.GetString(0)] = v;
        }
        return result;
    }

    private static async Task TriggerReadbackAsync(
        IFxConnector connector, ushort channel, ushort address, ChannelConfigUpdate updates, CancellationToken ct)
    {
        foreach (var (param, _) in ExpectedParams(updates))
        {
            try { await connector.ReadChannelParamAsync(param, channel, address, ct); }
            catch { /* 读回是尽力而为；失败不影响执行已完成的事实 */ }
        }
    }

    /// <summary>
    /// 执行下发成功后，把请求里的字段同步进 channels 冗余配置（只覆盖请求携带的字段）。
    /// 与 DiagnosticEndpoints 的 sync-channel-config（device_readback）区分 config_source，便于追溯来源。
    /// </summary>
    private static async Task SyncChannelRowAsync(
        NpgsqlDataSource ds, long channelId, ChannelConfigUpdate u, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(@"
            UPDATE channels SET
                probe_freq_mhz   = COALESCE(@pf, probe_freq_mhz),
                echo_win_ns      = COALESCE(@ew, echo_win_ns),
                echo_width_us    = COALESCE(@ewd, echo_width_us),
                gain             = COALESCE(@gain, gain),
                threshold        = COALESCE(@thr, threshold),
                meas_mode        = COALESCE(@mm, meas_mode),
                tc_mode          = COALESCE(@tc, tc_mode),
                temp_coefficient = COALESCE(@tcoef, temp_coefficient),
                ref_time_ns      = COALESCE(@rt, ref_time_ns),
                ref_temp_c       = COALESCE(@rtemp, ref_temp_c),
                config_source    = 'config_change',
                config_synced_at = now(),
                updated_at       = now()
            WHERE id = @id AND deleted_at IS NULL");
        cmd.Parameters.AddWithValue("pf", NpgsqlDbType.Numeric, (object?)u.ProbeFreqMhz ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ew", NpgsqlDbType.Numeric, (object?)u.EchoWinNs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ewd", NpgsqlDbType.Smallint, (object?)(short?)u.EchoWidthUs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("gain", NpgsqlDbType.Real, (object?)(float?)u.Gain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("thr", NpgsqlDbType.Smallint, (object?)u.Threshold ?? DBNull.Value);
        cmd.Parameters.AddWithValue("mm", NpgsqlDbType.Smallint, (object?)(short?)u.MeasMode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tc", NpgsqlDbType.Smallint, (object?)(short?)u.TcMode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tcoef", NpgsqlDbType.Numeric, (object?)u.TempCoefficient ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rt", NpgsqlDbType.Numeric, (object?)u.RefTimeNs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rtemp", NpgsqlDbType.Numeric, (object?)u.RefTempC ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task WriteAuditAsync(
        NpgsqlDataSource ds, long userId, string action, long channelId, string afterJson, string note, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(AuditSql);
        FillAuditParams(cmd, userId, action, channelId, afterJson, note);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>与状态更新共享事务的审计写入（approve/reject 用，责任链不可断）。</summary>
    private static async Task WriteAuditAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        long userId, string action, long channelId, string afterJson, string note, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(AuditSql, conn, tx);
        FillAuditParams(cmd, userId, action, channelId, afterJson, note);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private const string AuditSql = @"
        INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
        VALUES (@user_id, @action, 'channel', @target_id, NULL, @after::jsonb, @note, now())";

    private static void FillAuditParams(NpgsqlCommand cmd, long userId, string action, long channelId, string afterJson, string note)
    {
        cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, userId);
        cmd.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action);
        cmd.Parameters.AddWithValue("target_id", NpgsqlDbType.Bigint, channelId);
        cmd.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb, afterJson);
        cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, note);
    }

    /// <summary>驳回配置变更请求：可选驳回原因。</summary>
    public sealed record RejectBody(string? Reason);
}
