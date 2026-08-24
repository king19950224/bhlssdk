using BoltPreload.Api.Auth;
using BoltPreload.Application.Ingestion;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>bind-bolt 请求：目标螺栓 + 标定处置策略 + 是否回填 unbound 历史数据。</summary>
public sealed record BindBoltRequest(long BoltId, string CalibrationAction = "invalidate", bool BackfillUnbound = false, string? Note = null);

/// <summary>move-channel 请求：目标通道 + 标定处置策略 + 是否回填 unbound 历史数据。</summary>
public sealed record MoveBoltChannelRequest(long ToChannelId, string CalibrationAction = "invalidate", bool BackfillUnbound = false, string? Note = null);

/// <summary>unbind-bolt 请求：仅可选备注。</summary>
public sealed record UnbindChannelRequest(string? Note = null);

/// <summary>
/// 通道-螺栓绑定管理端点：绑定/迁移（bind-bolt、move-channel）与解绑（unbind-bolt）。
/// 三个操作共用一套事务化实现：行级锁防并发踩踏、标定处置策略（invalidate /
/// transfer_with_validation / keep_uncomputed）、绑定轨迹 + 审计双记录、解析缓存失效。
/// </summary>
public static class BoltBindingEndpoints
{
    public static IEndpointRouteBuilder MapBoltBindingEndpoints(this IEndpointRouteBuilder app)
    {
        var chGroup = app.MapGroup("/api/channels")
            .WithTags("Channels")
            .RequireAuthorization(AuthPolicies.LoggedIn);

        chGroup.MapPost("/{channelId:long}/bind-bolt", async (
            long channelId,
            BindBoltRequest req,
            HttpContext http,
            NpgsqlDataSource ds,
            IChannelResolver resolver,
            CancellationToken ct) =>
        {
            if (req.BoltId <= 0)
                return Results.BadRequest(new { error = "boltId 必须为正数" });

            var user = UserContext.Require(http);
            var result = await MoveBoltBindingAsync(
                ds, resolver, req.BoltId, channelId, user.UserId,
                req.CalibrationAction, req.BackfillUnbound, req.Note, ct);
            return result;
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("BindBoltToChannel")
        .WithSummary("把 bolt 绑定/迁移到指定物理通道（事务 + 审计 + 缓存失效）");

        app.MapPost("/api/bolts/{boltId:long}/move-channel", async (
            long boltId,
            MoveBoltChannelRequest req,
            HttpContext http,
            NpgsqlDataSource ds,
            IChannelResolver resolver,
            CancellationToken ct) =>
        {
            if (req.ToChannelId <= 0)
                return Results.BadRequest(new { error = "toChannelId 必须为正数" });

            var user = UserContext.Require(http);
            var result = await MoveBoltBindingAsync(
                ds, resolver, boltId, req.ToChannelId, user.UserId,
                req.CalibrationAction, req.BackfillUnbound, req.Note, ct);
            return result;
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithTags("Bolts")
        .WithName("MoveBoltChannel")
        .WithSummary("迁移 bolt 到另一个物理通道");

        chGroup.MapPost("/{channelId:long}/unbind-bolt", async (
            long channelId,
            UnbindChannelRequest? req,
            HttpContext http,
            NpgsqlDataSource ds,
            IChannelResolver resolver,
            CancellationToken ct) =>
        {
            var user = UserContext.Require(http);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // reader 先读完关闭再回滚（开着 reader 回滚会抛异常，把 404 变 500）
            long deviceId = 0;
            short channelNo = 0;
            long? boltId = null;
            string? beforeJson = null;
            var channelFound = false;

            await using (var cmd = new NpgsqlCommand(@"
                SELECT c.device_id, c.channel_no, b.id, to_jsonb(b)
                FROM channels c
                LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                WHERE c.id = @channel_id AND c.deleted_at IS NULL
                FOR UPDATE OF c", conn, tx))
            {
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    channelFound = true;
                    deviceId = r.GetInt64(0);
                    channelNo = r.GetInt16(1);
                    if (!r.IsDBNull(2))
                    {
                        boltId = r.GetInt64(2);
                        beforeJson = r.GetString(3);
                    }
                }
            }
            if (!channelFound)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "通道不存在", channelId });
            }

            if (boltId is null)
            {
                await tx.RollbackAsync(ct);
                return Results.BadRequest(new { error = "该通道未绑定螺栓", channelId });
            }

            // channel_id 守卫：并发 move 可能已把该螺栓迁到别的通道（move 只锁目标通道与螺栓行，
            // 不锁源通道）——按 id 盲删会把迁走的螺栓一并软删。0 行受影响 = 绑定已变，放弃本次解绑。
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE bolts
                   SET enabled = false,
                       state = 'disabled',
                       deleted_at = now(),
                       updated_at = now()
                 WHERE id = @bolt_id AND channel_id = @channel_id AND deleted_at IS NULL", conn, tx))
            {
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId.Value);
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Conflict(new { error = "绑定关系已变更（并发迁移/解绑？），请刷新后重试", channelId, boltId });
                }
            }

            // 探头仍在位时置 auto_enroll_hold：阻止立刻自动重建；真正离位后由
            // ChannelPresenceService 清 hold。重启不清——避免“删了重启又回来”。
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE channels
                   SET business_state = 'unbound',
                       k_l = NULL,
                       k_l_uncertainty = NULL,
                       k_l_intercept_ns = NULL,
                       active_calibration_id = NULL,
                       auto_enroll_hold = CASE
                           WHEN hardware_state IN ('healthy', 'noisy', 'suspect') THEN true
                           ELSE auto_enroll_hold END,
                       updated_at = now()
                 WHERE id = @channel_id", conn, tx))
            {
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO channel_binding_history (
                    bolt_id, from_channel_id, to_channel_id, action, calibration_action,
                    moved_by, before_jsonb, note
                ) VALUES (
                    @bolt_id, @from_channel_id, NULL, 'unbind', 'invalidate',
                    @user_id, @before::jsonb, @note
                )", conn, tx))
            {
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId.Value);
                cmd.Parameters.AddWithValue("from_channel_id", NpgsqlDbType.Bigint, channelId);
                cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, user.UserId);
                cmd.Parameters.AddWithValue("before", NpgsqlDbType.Jsonb, (object?)beforeJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)req?.Note ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // audit_log 同事务补录：binding_history 是绑定专用轨迹，审计日志页只查 audit_log，
            // 缺这条会让"解绑"在统一审计视图里隐身
            await using (var audit = new NpgsqlCommand(@"
                INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at, ip_addr)
                VALUES (@uid, 'channel_unbind', 'bolt', @bid, @before::jsonb, NULL, @note, now(), @ip)", conn, tx))
            {
                audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                audit.Parameters.AddWithValue("bid", NpgsqlDbType.Bigint, boltId.Value);
                audit.Parameters.AddWithValue("before", NpgsqlDbType.Jsonb, (object?)beforeJson ?? DBNull.Value);
                audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                    $"解绑通道 CH{channelNo}（设备 {deviceId}）螺栓 #{boltId}，原因：{req?.Note ?? "（未填）"}");
                AuditClientIp.Bind(audit.Parameters);
                await audit.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            resolver.Invalidate(deviceId, (ushort)channelNo);

            return Results.Ok(new { unbound = true, channelId, boltId });
        })
        // 解绑=软删螺栓+清标定，破坏性操作与前端权限口径一致（UI 只给 reviewer 露按钮），
        // 后端策略同步收紧为 reviewer+，堵住 operator 直调 API 的旁路
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithName("UnbindChannelBolt")
        .WithSummary("解绑通道当前 bolt（事务 + 审计 + 缓存失效；reviewer+）");

        return app;
    }

    private static async Task<IResult> MoveBoltBindingAsync(
        NpgsqlDataSource ds,
        IChannelResolver resolver,
        long boltId,
        long toChannelId,
        long userId,
        string calibrationAction,
        bool backfillUnbound,
        string? note,
        CancellationToken ct)
    {
        var normalizedAction = NormalizeCalibrationAction(calibrationAction);
        if (normalizedAction is null)
        {
            return Results.BadRequest(new
            {
                error = "calibrationAction 必须是 invalidate / transfer_with_validation / keep_uncomputed"
            });
        }

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // 以下三段均遵循“reader 先读完关闭，再回滚/返回”——开着 reader 回滚会抛异常，
            // 把本应 404/409 的业务分支变成 500。
            ChannelBindingSnapshot? target = null;
            await using (var cmd = new NpgsqlCommand(@"
                SELECT id, device_id, channel_no, k_l, k_l_uncertainty, k_l_intercept_ns,
                       active_calibration_id, ref_time_ns, ref_temp_c, to_jsonb(c)
                FROM channels c
                WHERE id = @channel_id AND deleted_at IS NULL
                FOR UPDATE", conn, tx))
            {
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, toChannelId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                    target = ReadChannelBindingSnapshot(r);
            }
            if (target is null)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "目标通道不存在", toChannelId });
            }

            BoltBindingSnapshot? bolt = null;
            await using (var cmd = new NpgsqlCommand(@"
                SELECT b.id, b.channel_id, c.device_id, c.channel_no,
                       c.k_l, c.k_l_uncertainty, c.k_l_intercept_ns,
                       c.active_calibration_id, c.ref_time_ns, c.ref_temp_c,
                       to_jsonb(b), to_jsonb(c)
                FROM bolts b
                JOIN channels c ON c.id = b.channel_id
                WHERE b.id = @bolt_id AND b.deleted_at IS NULL
                FOR UPDATE OF b", conn, tx))
            {
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                    bolt = ReadBoltBindingSnapshot(r);
            }
            if (bolt is null)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "螺栓不存在", boltId });
            }

            long existingBoltId = 0;
            string? existingBoltCode = null;
            var existingIsPlaceholder = false;
            await using (var cmd = new NpgsqlCommand(@"
                SELECT id, code, (confirmed_at IS NULL AND state = 'uncalibrated')
                FROM bolts
                WHERE channel_id = @channel_id
                  AND id <> @bolt_id
                  AND deleted_at IS NULL
                LIMIT 1
                FOR UPDATE", conn, tx))
            {
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, toChannelId);
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    existingBoltId = r.GetInt64(0);
                    existingBoltCode = r.GetString(1);
                    existingIsPlaceholder = r.GetBoolean(2);
                }
            }
            string? absorbedBoltCode = null;
            if (existingBoltCode is not null && !existingIsPlaceholder)
            {
                // 资产档占位：不吸收——绑定是人工声明，覆盖别人的声明必须先显式解绑
                await tx.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    error = "目标通道已绑定其他螺栓",
                    channelId = toChannelId,
                    existingBoltId,
                    existingBoltCode
                });
            }
            if (existingBoltCode is not null)
            {
                // 占位档吸收（两级模型 R3）：目标通道上是自动建档且无人工痕迹的占位档
                // （典型场景：探头挪线后 auto-enroll 抢先建档），迁移时同事务软删占位档并接管，
                // 免去"先手工删 ghost 再迁移"的两步操作。审计与绑定轨迹双留痕。
                absorbedBoltCode = existingBoltCode;
                await using (var cmd = new NpgsqlCommand(@"
                    UPDATE bolts
                       SET deleted_at = now(), enabled = false, updated_at = now()
                     WHERE id = @id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, existingBoltId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO channel_binding_history (
                        bolt_id, from_channel_id, to_channel_id, action, calibration_action,
                        moved_by, before_jsonb, note)
                    VALUES (@id, @cid, NULL, 'unbind', 'keep_uncomputed', @uid, NULL, @note)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, existingBoltId);
                    cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, toChannelId);
                    cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, userId);
                    cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                        $"[占位档吸收] 迁移螺栓 #{boltId} 至本通道时吸收自动建档的占位档 {existingBoltCode}（未标定、无人工痕迹）");
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await using (var audit = new NpgsqlCommand(@"
                    INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at, ip_addr)
                    VALUES (@uid, 'bolt_absorb_placeholder', 'bolt', @id, NULL, NULL, @note, now(), @ip)", conn, tx))
                {
                    audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, userId);
                    audit.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, existingBoltId);
                    audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                        $"迁移吸收占位档 {existingBoltCode}（通道 {toChannelId}）：由螺栓 #{boltId} 接管该通道");
                    AuditClientIp.Bind(audit.Parameters);
                    await audit.ExecuteNonQueryAsync(ct);
                }
            }

            // 安全：禁止跨设备绑定/迁移——通道与螺栓必须在同一物理设备，否则测量/标定上下文错乱
            if (target.DeviceId != bolt.DeviceId)
            {
                await tx.RollbackAsync(ct);
                return Results.BadRequest(new
                {
                    error = "不允许跨设备绑定或迁移螺栓",
                    boltDeviceId = bolt.DeviceId,
                    targetDeviceId = target.DeviceId
                });
            }

            var isMove = bolt.ChannelId != toChannelId;
            var action = isMove ? "move" : "bind";
            // 同通道"再绑一次"(!isMove) 不动标定：默认 invalidate 会把在产标定清掉（UI 双击 / 幂等重绑的坑）
            if (isMove)
                await ApplyCalibrationPolicyAsync(conn, tx, target.ChannelId, bolt, normalizedAction, ct);

            await using (var cmd = new NpgsqlCommand(@"
                UPDATE bolts
                   SET channel_id = @channel_id,
                       enabled = true,
                       deleted_at = NULL,
                       -- confirmed_at：人工绑定/迁移 = 人工声明归属，占位档升级为资产档（两级模型盖章点之一）
                       confirmed_at = COALESCE(confirmed_at, now()),
                       updated_at = now()
                 WHERE id = @bolt_id", conn, tx))
            {
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, toChannelId);
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (isMove)
            {
                // 源通道螺栓已迁走：镜像 unbind 清理标定,避免空通道仍显示 calibrated / 下次绑定误用旧 K_L
                await using var fromCmd = new NpgsqlCommand(@"
                    UPDATE channels
                       SET business_state = 'unbound',
                           k_l = NULL,
                           k_l_uncertainty = NULL,
                           k_l_intercept_ns = NULL,
                           active_calibration_id = NULL,
                           updated_at = now()
                     WHERE id = @from_channel_id", conn, tx);
                fromCmd.Parameters.AddWithValue("from_channel_id", NpgsqlDbType.Bigint, bolt.ChannelId);
                await fromCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new NpgsqlCommand(@"
                UPDATE channels
                   SET business_state = 'bound',
                       updated_at = now()
                 WHERE id = @channel_id", conn, tx))
            {
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, toChannelId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            long backfilled = 0;
            if (backfillUnbound)
            {
                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO measurements (
                        bolt_id, channel_id, ts, ingested_at,
                        echo_time_ns, delta_tof_ns, sp_temp_c, gain, status_code,
                        wave_blob_url, algo_version
                    )
                    SELECT @bolt_id, channel_id, ts, ingested_at,
                           echo_time_ns, delta_tof_ns, sp_temp_c, COALESCE(gain, 0), status_code,
                           wave_blob_url, 'phase1:raw:backfilled'
                    FROM unbound_measurements
                    -- 负状态码异常帧不进 measurements（四处回填同构，表约定见 0001）
                    WHERE channel_id = @channel_id AND status_code >= 0
                    ON CONFLICT (channel_id, ts) DO NOTHING", conn, tx);
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, toChannelId);
                backfilled = await cmd.ExecuteNonQueryAsync(ct);

                await using var wfCmd = new NpgsqlCommand(@"
                    UPDATE wave_features
                       SET bolt_id = @bolt_id
                     WHERE channel_id = @channel_id
                       AND bolt_id IS NULL", conn, tx);
                wfCmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                wfCmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, toChannelId);
                await wfCmd.ExecuteNonQueryAsync(ct);

                // 回填后清理该通道的 unbound 暂存（已迁入 measurements 或与既有行重复）：避免 /unbound 报表持续报陈旧数据、避免重复回填
                await using var delCmd = new NpgsqlCommand(
                    "DELETE FROM unbound_measurements WHERE channel_id = @channel_id", conn, tx);
                delCmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, toChannelId);
                await delCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO channel_binding_history (
                    bolt_id, from_channel_id, to_channel_id, action, calibration_action,
                    moved_by, before_jsonb, after_jsonb, note
                )
                SELECT @bolt_id, @from_channel_id, @to_channel_id, @action, @calibration_action,
                       @user_id, @before::jsonb, to_jsonb(b), @note
                FROM bolts b
                WHERE b.id = @bolt_id", conn, tx))
            {
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                cmd.Parameters.AddWithValue("from_channel_id", NpgsqlDbType.Bigint, bolt.ChannelId);
                cmd.Parameters.AddWithValue("to_channel_id", NpgsqlDbType.Bigint, toChannelId);
                cmd.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action);
                cmd.Parameters.AddWithValue("calibration_action", NpgsqlDbType.Varchar, normalizedAction);
                cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, userId);
                cmd.Parameters.AddWithValue("before", NpgsqlDbType.Jsonb, bolt.BoltJson);
                cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)note ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // audit_log 同事务补录（统一审计视图只查 audit_log，见解绑处说明）
            await using (var audit = new NpgsqlCommand(@"
                INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at, ip_addr)
                SELECT @uid, @action, 'bolt', @bid, @before::jsonb, to_jsonb(b), @note, now(), @ip
                FROM bolts b WHERE b.id = @bid", conn, tx))
            {
                audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, userId);
                audit.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, isMove ? "bolt_move_channel" : "channel_bind");
                audit.Parameters.AddWithValue("bid", NpgsqlDbType.Bigint, boltId);
                audit.Parameters.AddWithValue("before", NpgsqlDbType.Jsonb, bolt.BoltJson);
                audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                    (isMove
                        ? $"迁移螺栓 #{boltId} 通道 {bolt.ChannelId} → {toChannelId}（标定策略 {normalizedAction}，回填 {backfilled} 条）"
                        : $"绑定螺栓 #{boltId} 到通道 {toChannelId}（回填 {backfilled} 条）")
                    + (string.IsNullOrWhiteSpace(note) ? "" : $"，备注：{note}"));
                AuditClientIp.Bind(audit.Parameters);
                await audit.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            resolver.Invalidate(bolt.DeviceId, (ushort)bolt.ChannelNo);
            resolver.Invalidate(target.DeviceId, (ushort)target.ChannelNo);

            return Results.Ok(new
            {
                moved = isMove,
                boltId,
                fromChannelId = bolt.ChannelId,
                toChannelId,
                calibrationAction = normalizedAction,
                backfilled,
                // 非空 = 目标通道上自动建档的占位档已被本次迁移吸收（软删 + 审计留痕）
                absorbedBoltCode
            });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static string? NormalizeCalibrationAction(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "invalidate"
            : value.Trim().ToLowerInvariant();
        return normalized is "invalidate" or "transfer_with_validation" or "keep_uncomputed"
            ? normalized
            : null;
    }

    private static async Task ApplyCalibrationPolicyAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long targetChannelId,
        BoltBindingSnapshot source,
        string calibrationAction,
        CancellationToken ct)
    {
        if (calibrationAction == "transfer_with_validation")
        {
            await using var cmd = new NpgsqlCommand(@"
                UPDATE channels
                   SET k_l = @kl,
                       k_l_uncertainty = @klu,
                       k_l_intercept_ns = @intercept,
                       active_calibration_id = @calib_id,
                       ref_time_ns = COALESCE(@ref_time, ref_time_ns),
                       ref_temp_c = COALESCE(@ref_temp, ref_temp_c),
                       updated_at = now()
                 WHERE id = @channel_id", conn, tx);
            cmd.Parameters.AddWithValue("kl", NpgsqlDbType.Numeric, (object?)source.KL ?? DBNull.Value);
            cmd.Parameters.AddWithValue("klu", NpgsqlDbType.Numeric, (object?)source.KLUncertainty ?? DBNull.Value);
            cmd.Parameters.AddWithValue("intercept", NpgsqlDbType.Numeric, (object?)source.KLInterceptNs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("calib_id", NpgsqlDbType.Uuid, (object?)source.ActiveCalibrationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ref_time", NpgsqlDbType.Numeric, (object?)source.RefTimeNs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ref_temp", NpgsqlDbType.Numeric, (object?)source.RefTempC ?? DBNull.Value);
            cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, targetChannelId);
            await cmd.ExecuteNonQueryAsync(ct);
            return;
        }

        if (calibrationAction == "keep_uncomputed")
        {
            // 保留目标通道自身已有标定不动（no-op）。操作员显式选择"沿用目标通道现有标定"——
            // 典型场景：把螺栓重新绑回它原来的通道，标定本就对应该螺栓，不该被清空或被源通道覆盖。
            // 安全提示：若目标通道原是为别的螺栓标定的，沿用其 K_L 会给新螺栓算出错误预紧力；需要重标定时应选 invalidate。
            // calibration_action 已写入 channel_binding_history，可审计追溯。
            return;
        }

        // invalidate（默认）：清空目标通道标定，重标定通过前不出正式预紧力
        await using (var cmd = new NpgsqlCommand(@"
            UPDATE channels
               SET k_l = NULL,
                   k_l_uncertainty = NULL,
                   k_l_intercept_ns = NULL,
                   active_calibration_id = NULL,
                   updated_at = now()
             WHERE id = @channel_id", conn, tx))
        {
            cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, targetChannelId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static ChannelBindingSnapshot ReadChannelBindingSnapshot(NpgsqlDataReader r) => new(
        ChannelId: r.GetInt64(0),
        DeviceId: r.GetInt64(1),
        ChannelNo: r.GetInt16(2),
        KL: r.IsDBNull(3) ? null : r.GetDecimal(3),
        KLUncertainty: r.IsDBNull(4) ? null : r.GetDecimal(4),
        KLInterceptNs: r.IsDBNull(5) ? null : r.GetDecimal(5),
        ActiveCalibrationId: r.IsDBNull(6) ? null : r.GetGuid(6),
        RefTimeNs: r.IsDBNull(7) ? null : r.GetDecimal(7),
        RefTempC: r.IsDBNull(8) ? null : r.GetDecimal(8),
        ChannelJson: r.GetString(9));

    private static BoltBindingSnapshot ReadBoltBindingSnapshot(NpgsqlDataReader r) => new(
        BoltId: r.GetInt64(0),
        ChannelId: r.GetInt64(1),
        DeviceId: r.GetInt64(2),
        ChannelNo: r.GetInt16(3),
        KL: r.IsDBNull(4) ? null : r.GetDecimal(4),
        KLUncertainty: r.IsDBNull(5) ? null : r.GetDecimal(5),
        KLInterceptNs: r.IsDBNull(6) ? null : r.GetDecimal(6),
        ActiveCalibrationId: r.IsDBNull(7) ? null : r.GetGuid(7),
        RefTimeNs: r.IsDBNull(8) ? null : r.GetDecimal(8),
        RefTempC: r.IsDBNull(9) ? null : r.GetDecimal(9),
        BoltJson: r.GetString(10),
        ChannelJson: r.GetString(11));
}

internal sealed record ChannelBindingSnapshot(
    long ChannelId,
    long DeviceId,
    short ChannelNo,
    decimal? KL,
    decimal? KLUncertainty,
    decimal? KLInterceptNs,
    Guid? ActiveCalibrationId,
    decimal? RefTimeNs,
    decimal? RefTempC,
    string ChannelJson);

internal sealed record BoltBindingSnapshot(
    long BoltId,
    long ChannelId,
    long DeviceId,
    short ChannelNo,
    decimal? KL,
    decimal? KLUncertainty,
    decimal? KLInterceptNs,
    Guid? ActiveCalibrationId,
    decimal? RefTimeNs,
    decimal? RefTempC,
    string BoltJson,
    string ChannelJson);
