using BoltPreload.Api.Auth;
using BoltPreload.Api.Runtime;
using BoltPreload.Application.Devices;
using BoltPreload.Application.Devices.Acceptance;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Application.Ingestion;
using BoltPreload.Fx;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 通道目录管理：设备物理通道数（devices.channel_count）与 channels 目录行的显式管理入口。
/// <para>
/// 背景：自动发现（CachedChannelResolver）对被软删的通道有"墓碑检查"——运维删掉的通道不会被
/// 数据流悄悄复活，重新启用必须走显式管理动作。此前系统缺这个入口，软删过的通道成了死角：
/// 探头插上去也永远识别不出来。本端点就是那个"显式管理动作"（reviewer+，全程审计）。
/// </para>
/// <para>
/// POST 重建目录：更新 channel_count → 复活 1..N 内被软删的通道（每通道取最近一条墓碑）→
/// 补插缺失的通道行 → 同步运行时（免重启）：路由缓存失效、SDK 会话通道补配、全通道 MCR 重下发。
/// 缩容（N 变小）不删除已有通道行，只缩小轮询范围，并在响应里提示超出范围的活跃通道数。
/// </para>
/// </summary>
public static class ChannelCatalogEndpoints
{
    // 通道数上限统一走 DeviceLimits（全项目唯一一份），这里不再自留一份 48
    private const int MaxChannelCount = DeviceLimits.MaxChannelsPerDevice;

    public static IEndpointRouteBuilder MapChannelCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices/{deviceId:long}/channel-catalog")
            .WithTags("Channels");

        // ---------- 目录现状（供 UI 显示"目录缺多少通道"并引导重建） ----------
        group.MapGet("/", async (long deviceId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            const string sql = @"
                SELECT d.channel_count,
                       (SELECT count(*) FROM channels c
                         WHERE c.device_id = d.id AND c.deleted_at IS NULL) AS active_count,
                       (SELECT COALESCE(array_agg(gs.ch ORDER BY gs.ch), '{}')
                          FROM generate_series(1, GREATEST(d.channel_count, 1)) AS gs(ch)
                         WHERE NOT EXISTS (
                             SELECT 1 FROM channels c
                             WHERE c.device_id = d.id AND c.channel_no = gs.ch AND c.deleted_at IS NULL)) AS missing,
                       (SELECT count(*) FROM channels c
                         WHERE c.device_id = d.id AND c.deleted_at IS NULL AND c.channel_no > d.channel_count) AS extra_count
                FROM devices d
                WHERE d.id = @id AND d.deleted_at IS NULL";

            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, deviceId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return Results.NotFound(new { error = "设备不存在", deviceId });

            var missing = r.IsDBNull(2) ? [] : (int[])r.GetValue(2);
            return Results.Ok(new
            {
                deviceId,
                channelCount = r.GetInt16(0),
                activeChannels = r.GetInt64(1),
                missingChannels = missing,
                extraActiveChannels = r.GetInt64(3),
                maxChannelCount = MaxChannelCount
            });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("GetChannelCatalog")
        .WithSummary("通道目录现状：channel_count、活跃通道数、目录缺失的通道号");

        // ---------- 重建目录 ----------
        group.MapPost("/", async (
            long deviceId,
            ChannelCatalogRequest req,
            HttpContext http,
            NpgsqlDataSource ds,
            IDeviceConnectorManager deviceManager,
            IChannelResolver resolver,
            ProbeScanState probeScan,
            AcceptanceSessionState acceptance,
            IConfiguration configuration,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (req.ChannelCount is < 1 or > MaxChannelCount)
                return Results.BadRequest(new { error = $"channelCount 必须在 1..{MaxChannelCount} 之间（SDK 多通道协议单板上限 {MaxChannelCount}）" });

            var logger = loggerFactory.CreateLogger("ChannelCatalog");
            var user = UserContext.Require(http);
            var n = (short)req.ChannelCount;

            short oldCount;
            List<short> resurrected = [];
            List<short> created = [];
            long extraActive;

            await using (var conn = await ds.OpenConnectionAsync(ct))
            await using (var tx = await conn.BeginTransactionAsync(ct))
            {
                // 设备行锁：串行化并发的目录重建（复活/补插都以"当前活跃行"为前提）
                await using (var cmd = new NpgsqlCommand(
                    "SELECT channel_count FROM devices WHERE id = @id AND deleted_at IS NULL FOR UPDATE", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, deviceId);
                    var scalar = await cmd.ExecuteScalarAsync(ct);
                    if (scalar is null)
                    {
                        await tx.RollbackAsync(ct);
                        return Results.NotFound(new { error = "设备不存在", deviceId });
                    }
                    oldCount = (short)scalar;
                }

                await using (var cmd = new NpgsqlCommand(
                    "UPDATE devices SET channel_count = @n, updated_at = now() WHERE id = @id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("n", NpgsqlDbType.Smallint, n);
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, deviceId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // 复活 1..N 内被软删的通道：每个 channel_no 只取最近一条墓碑（历史上可能反复删过留下多条），
                // 保留原有参数（探头频率/增益/基线快照），硬件状态归零为 unknown 由在位判定重新确认。
                await using (var cmd = new NpgsqlCommand(@"
                    WITH pick AS (
                        SELECT DISTINCT ON (channel_no) id, channel_no
                        FROM channels
                        WHERE device_id = @id
                          AND deleted_at IS NOT NULL
                          AND channel_no BETWEEN 1 AND @n
                          AND NOT EXISTS (
                              SELECT 1 FROM channels a
                              WHERE a.device_id = @id AND a.channel_no = channels.channel_no AND a.deleted_at IS NULL)
                        ORDER BY channel_no, deleted_at DESC, id DESC
                    )
                    UPDATE channels c
                       SET deleted_at = NULL,
                           hardware_state = 'unknown',
                           business_state = CASE WHEN EXISTS (
                               SELECT 1 FROM bolts b WHERE b.channel_id = c.id AND b.deleted_at IS NULL)
                               THEN 'bound' ELSE 'unbound' END,
                           updated_at = now()
                      FROM pick
                     WHERE c.id = pick.id
                    RETURNING c.channel_no", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, deviceId);
                    cmd.Parameters.AddWithValue("n", NpgsqlDbType.Smallint, n);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) resurrected.Add(r.GetInt16(0));
                }

                // 补插目录里从未有过的通道号（复活后仍无任何行的）
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO channels (device_id, channel_no, state, business_state, hardware_state)
                    SELECT @id, gs.ch, 'manual', 'unbound', 'unknown'
                    FROM generate_series(1, @n) AS gs(ch)
                    WHERE NOT EXISTS (
                        SELECT 1 FROM channels c WHERE c.device_id = @id AND c.channel_no = gs.ch)
                    RETURNING channel_no", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, deviceId);
                    cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, (int)n);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) created.Add(r.GetInt16(0));
                }

                // 缩容不删行：超出 N 的活跃通道保留（可能还绑着螺栓），只是不再进全通道轮询
                await using (var cmd = new NpgsqlCommand(
                    "SELECT count(*) FROM channels WHERE device_id = @id AND deleted_at IS NULL AND channel_no > @n", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, deviceId);
                    cmd.Parameters.AddWithValue("n", NpgsqlDbType.Smallint, n);
                    extraActive = (long)(await cmd.ExecuteScalarAsync(ct))!;
                }

                // §0 责任链：状态变更与审计同事务
                await using (var audit = new NpgsqlCommand(@"
                    INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at, ip_addr)
                    VALUES (@uid, 'device_channel_catalog_update', 'device', @did, @before::jsonb, @after::jsonb, @note, now(), @ip)", conn, tx))
                {
                    audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                    audit.Parameters.AddWithValue("did", NpgsqlDbType.Bigint, deviceId);
                    audit.Parameters.AddWithValue("before", NpgsqlDbType.Jsonb,
                        System.Text.Json.JsonSerializer.Serialize(new { channelCount = oldCount }));
                    audit.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb,
                        System.Text.Json.JsonSerializer.Serialize(new { channelCount = n, resurrected, created }));
                    audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                        $"通道目录重建：channel_count {oldCount}→{n}，复活 {resurrected.Count} 个，新建 {created.Count} 个。{req.Note ?? ""}".Trim());
                    AuditClientIp.Bind(audit.Parameters);
                    await audit.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
            }

            // ---------- 事务外的运行时同步（尽力而为，失败不回滚目录——重启进程也能收敛） ----------
            resolver.InvalidateAll();

            var rt = deviceManager.Find(deviceId);
            var runtimeUpdated = rt is not null && deviceManager.TryUpdateChannelCount(deviceId, (ushort)n);

            var sessionProvisioned = false;
            var mcrReconfigured = false;
            string? runtimeWarning = rt is null
                ? "设备不在运行时中（Fx:Mode 或 devices.enabled 检查），目录已更新，重启服务后生效"
                : null;

            if (rt is not null)
            {
                // SDK 只对 SessionContext 里有 ChannelConfig 的通道算 EchoTime——把目录（含新通道）的
                // 配置补进会话，新探头插上立刻能出回波，不必等重启。
                try
                {
                    var inits = FxInitialChannelConfigLoader.Load(
                        ds, configuration, rt.Options.Address, deviceId, logger, includeAppSettingsFallback: false);
                    await rt.Connector.ProvisionSessionChannelsAsync(inits, ct);
                    sessionProvisioned = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "在线补配会话通道失败 device={DeviceId}（重启后自动装载）", deviceId);
                    runtimeWarning = "会话通道补配失败，新通道需重启服务后才能计算回波";
                }

                // 全通道轮询立即收敛（HotPlug 关闭时 ChannelPresenceService 不会代劳，这里显式下发一次）。
                // 扫描/验收会话期间不动 MCR（与两者的独占契约一致），结束后由 ChannelPresenceService 收敛。
                if (probeScan.IsRunning(deviceId))
                {
                    runtimeWarning = "通道自动检测进行中，全通道轮询将在扫描结束后收敛";
                }
                else if (acceptance.IsActive(deviceId))
                {
                    runtimeWarning = "接入验收会话进行中，全通道轮询将在会话结束后收敛";
                }
                else if (rt.Connector.State == FxConnectorState.Connected && rt.Options.Model == 3)
                {
                    try
                    {
                        var target = Enumerable.Range(1, n).Select(i => (ushort)i).ToArray();
                        await rt.Connector.ConfigureMultiChannelPollingAsync(target, singleCount: 1, dailyCount: 0, cancellationToken: ct);
                        await rt.Connector.SetWorkModeAsync(FxWorkMode.MultiMeas, channel: 0, address: rt.Options.Address, ct);
                        mcrReconfigured = true;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "全通道轮询下发失败 device={DeviceId}（热插拔服务下轮 sweep 会重试）", deviceId);
                        runtimeWarning = "全通道轮询下发失败，热插拔识别服务会自动重试";
                    }
                }
                else if (rt.Connector.State != FxConnectorState.Connected)
                {
                    runtimeWarning = "设备当前离线，连接恢复后自动下发全通道轮询";
                }
            }

            logger.LogInformation(
                "通道目录重建完成 device={DeviceId} channelCount={Old}→{New} 复活={Resurrected} 新建={Created} by user={UserId}",
                deviceId, oldCount, n, resurrected.Count, created.Count, user.UserId);

            return Results.Ok(new
            {
                deviceId,
                channelCount = (int)n,
                previousChannelCount = (int)oldCount,
                resurrectedChannels = resurrected.Select(x => (int)x).OrderBy(x => x).ToArray(),
                createdChannels = created.Select(x => (int)x).OrderBy(x => x).ToArray(),
                extraActiveChannels = extraActive,
                runtimeUpdated,
                sessionProvisioned,
                mcrReconfigured,
                warning = runtimeWarning
            });
        })
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithName("RebuildChannelCatalog")
        .WithSummary("重建通道目录：更新 channel_count、复活软删通道、补插缺失行（reviewer+，审计）");

        return app;
    }
}

/// <summary>POST /channel-catalog 请求体。</summary>
public sealed record ChannelCatalogRequest(int ChannelCount, string? Note = null);
