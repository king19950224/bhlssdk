using BoltPreload.Api.Auth;
using BoltPreload.Fx;
using BoltPreload.Application.Devices;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Application.Ingestion;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 设备诊断与调试端点：SDK 会话/参数读回、工作模式下发、通道健康总览、
/// 点位与验收页实时数据源（channels/live）、重标定等。
/// 触发读设备参数后，响应通过 SDK 异步事件回来，要看 IngestionService 日志。
/// </summary>
public static class DiagnosticEndpoints
{
    // 仅列协议真能编码成读命令的参数（见 MultiChIotProtocolHandler.EncodeReadChannelParameter）：
    // 前 5 个是寄存器读(0x22)；RefTime 是存储读(0x42)，回包为整组基线
    // MeasMode+Threshold+RefTime+RefTemp+LEchoWave，一并落 device_parameter_history。
    // 此前列表里的 MeasMode/Threshold/RefTemp 没有对应读命令分支——编码返回空列表、什么都不发，
    // 属静默 no-op；它们的值实际一直是随 RefTime 组读回来的。
    private static readonly string[] DefaultChannelParamNames =
    [
        "ProbeFreq", "Gain", "EchoWin", "EchoWidth", "PulseConfig", "RefTime"
    ];

    public static IEndpointRouteBuilder MapDiagnosticEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices/{deviceId:long}/diagnostic")
            .WithTags("Diagnostic")
            // M2：诊断端点都是对硬件设备的直接交互（读参 / 改配置 / 改工作模式 / 下发轮询），属工程师级高危操作，
            // 从 operator 级 WriteAccess 收紧到 reviewer+（ReviewerAccess），operator 不再能直接下发设备命令。
            .RequireAuthorization(AuthPolicies.ReviewerAccess);

        // 读设备级参数
        group.MapPost("/read-device-param/{propertyName}", async (
            long deviceId, string propertyName, ushort? address,
            IDeviceConnectorManager deviceManager, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var connector = deviceManager.Find(deviceId)?.Connector;
            if (connector is null)
                return Results.NotFound(new { error = $"设备未加载到运行时：{deviceId}", deviceId });
            var logger = loggerFactory.CreateLogger("Diagnostic");
            var addr = address ?? (ushort)1;
            logger.LogInformation("触发读设备参数 device={DeviceId} param={Param} addr={Addr}",
                deviceId, propertyName, addr);

            await connector.ReadDeviceParamAsync(propertyName, addr, ct);

            return Results.Ok(new
            {
                triggered = true,
                deviceId,
                propertyName,
                address = addr,
                note = "响应通过 SDK 事件异步回来；看 API 日志的 \"收到 DeviceParameter\" 条目"
            });
        })
        .WithName("ReadDeviceParam");

        // 读通道级参数
        group.MapPost("/read-channel-param/{propertyName}", async (
            long deviceId, string propertyName, ushort channel, ushort? address,
            IDeviceConnectorManager deviceManager, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var connector = deviceManager.Find(deviceId)?.Connector;
            if (connector is null)
                return Results.NotFound(new { error = $"设备未加载到运行时：{deviceId}", deviceId });
            var logger = loggerFactory.CreateLogger("Diagnostic");
            var addr = address ?? (ushort)1;
            logger.LogInformation("触发读通道参数 device={DeviceId} ch={Ch} param={Param} addr={Addr}",
                deviceId, channel, propertyName, addr);

            await connector.ReadChannelParamAsync(propertyName, channel, addr, ct);

            return Results.Ok(new
            {
                triggered = true,
                deviceId,
                channel,
                propertyName,
                address = addr,
                note = "响应通过 SDK 事件异步回来；看 API 日志"
            });
        })
        .WithName("ReadChannelParam");

        group.MapPost("/configure-multichannel-polling", async (
            long deviceId,
            ConfigureMultiChannelPollingRequest? body,
            IDeviceConnectorManager deviceManager,
            NpgsqlDataSource ds,
            ProbeScanState probeScanState,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var connector = deviceManager.Find(deviceId)?.Connector;
            if (connector is null)
                return Results.NotFound(new { error = $"设备未加载到运行时：{deviceId}", deviceId });
            // 扫描期间不允许下发 MCR — 否则会和扫描器抢工作模式
            if (probeScanState.IsRunning(deviceId))
                return Results.Conflict(new { error = "通道自动检测进行中，不能并发下发 MCR 配置" });

            ushort[] channels;
            if (body?.Channels is { Length: > 0 })
            {
                channels = body.Channels
                    .Where(ch => ch > 0)
                    .Distinct()
                    .OrderBy(ch => ch)
                    .ToArray();
            }
            else
            {
                await using var cmd = ds.CreateCommand(@"
                    SELECT channel_no
                    FROM channels
                    WHERE device_id = @device_id
                      AND deleted_at IS NULL
                      AND sampling_enabled = true
                    ORDER BY channel_no");
                cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
                var list = new List<ushort>();
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    list.Add((ushort)r.GetInt16(0));
                channels = list.ToArray();
            }

            if (channels.Length == 0)
                return Results.BadRequest(new { error = "没有可启用的通道" });

            await connector.ConfigureMultiChannelPollingAsync(
                channels,
                body?.SingleCount,
                body?.DailyCount,
                body?.StartSeconds,
                ct);

            var logger = loggerFactory.CreateLogger("Diagnostic");
            logger.LogWarning("已下发多通道轮询配置 device={DeviceId} channels={Channels}",
                deviceId, string.Join(",", channels));

            return Results.Ok(new
            {
                triggered = true,
                deviceId,
                enabledChannels = channels,
                channels.Length,
                note = "已按 SDK DeviceConfig.MCRConfig 下发设备端多通道轮询配置。"
            });
        })
        .WithName("ConfigureMultiChannelPolling")
        .WithSummary("按 SDK MCRConfig 配置设备端多通道轮询启用通道");

        group.MapPost("/read-channel-params/batch", async (
            long deviceId,
            BatchReadChannelParamsRequest? body,
            IDeviceConnectorManager deviceManager,
            NpgsqlDataSource ds,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var connector = deviceManager.Find(deviceId)?.Connector;
            if (connector is null)
                return Results.NotFound(new { error = $"设备未加载到运行时：{deviceId}", deviceId });
            var logger = loggerFactory.CreateLogger("Diagnostic");
            var addr = body?.Address ?? (ushort)1;
            var propertyNames = (body?.PropertyNames is { Length: > 0 }
                    ? body.PropertyNames
                    : DefaultChannelParamNames)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            ushort[] channels;
            if (body?.Channels is { Length: > 0 })
            {
                // 与默认分支同一 1..48 硬件边界：否则可对任意大通道号发起海量设备读，阻塞请求线程/冲击设备
                channels = body.Channels.Where(x => x is >= 1 and <= 48).Distinct().OrderBy(x => x).ToArray();
                if (channels.Length == 0)
                    return Results.BadRequest(new { error = "channels 必须在 1..48 范围内" });
            }
            else
            {
                await using var cmd = ds.CreateCommand(@"
                    SELECT COALESCE(channel_count, 40)
                    FROM devices
                    WHERE id = @device_id AND deleted_at IS NULL");
                cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
                var n = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 40);
                channels = Enumerable.Range(1, Math.Clamp(n, 1, 48)).Select(x => (ushort)x).ToArray();
            }

            foreach (var ch in channels)
            {
                foreach (var propertyName in propertyNames)
                {
                    await connector.ReadChannelParamAsync(propertyName, ch, addr, ct);
                }
            }

            logger.LogInformation("批量触发读通道参数 device={DeviceId} channels={Channels} params={Params}",
                deviceId, string.Join(",", channels), string.Join(",", propertyNames));

            return Results.Ok(new
            {
                triggered = true,
                deviceId,
                address = addr,
                channels,
                propertyNames,
                requestCount = channels.Length * propertyNames.Length,
                note = "响应通过 SDK 事件异步回来，并写入 device_parameter_history。"
            });
        })
        .WithName("BatchReadChannelParams")
        .WithSummary("批量读取 1-40 通道参数（异步回包）");

        // 把最近一次读回的通道参数同步到 channels 表。
        // 用途：设备参数以厂家真实读回为准，避免长期依赖 appsettings 兜底。
        group.MapPost("/sync-channel-config/{channel}", async (
            long deviceId,
            ushort channel,
            NpgsqlDataSource ds,
            IChannelResolver resolver,
            HttpContext http,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Diagnostic");
            var userId = UserContext.Require(http).UserId;

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            try
            {
                string? beforeJson;
                await using (var beforeCmd = new NpgsqlCommand(@"
                    SELECT to_jsonb(c) FROM channels c
                    WHERE c.device_id = @device_id AND c.channel_no = @channel_no AND c.deleted_at IS NULL
                    LIMIT 1", conn, tx))
                {
                    beforeCmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
                    beforeCmd.Parameters.AddWithValue("channel_no", NpgsqlDbType.Smallint, (short)channel);
                    beforeJson = await beforeCmd.ExecuteScalarAsync(ct) as string;
                }

                if (beforeJson is null)
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "通道不存在，不能同步配置", deviceId, channel });
                }

                await using var cmd = new NpgsqlCommand(@"
                    WITH latest AS (
                        SELECT DISTINCT ON (param_name)
                               param_name, param_value_text
                        FROM device_parameter_history
                        WHERE device_id = @device_id
                          AND channel_no = @channel_no
                          AND param_value_text IS NOT NULL
                          AND param_name IN ('ProbeFreq','Gain','Threshold','MeasMode','RefTime','RefTemp','EchoWin','EchoWidth','TcMode','TempCoefficient')
                        ORDER BY param_name, received_at DESC
                    ),
                    p AS (
                        SELECT
                            MAX(param_value_text) FILTER (WHERE param_name = 'ProbeFreq') AS probe_freq,
                            MAX(param_value_text) FILTER (WHERE param_name = 'Gain') AS gain,
                            MAX(param_value_text) FILTER (WHERE param_name = 'Threshold') AS threshold,
                            MAX(param_value_text) FILTER (WHERE param_name = 'MeasMode') AS meas_mode,
                            MAX(param_value_text) FILTER (WHERE param_name = 'RefTime') AS ref_time,
                            MAX(param_value_text) FILTER (WHERE param_name = 'RefTemp') AS ref_temp,
                            MAX(param_value_text) FILTER (WHERE param_name = 'EchoWin') AS echo_win,
                            MAX(param_value_text) FILTER (WHERE param_name = 'EchoWidth') AS echo_width,
                            MAX(param_value_text) FILTER (WHERE param_name = 'TcMode') AS tc_mode,
                            MAX(param_value_text) FILTER (WHERE param_name = 'TempCoefficient') AS temp_coeff
                        FROM latest
                    )
                    UPDATE channels c
                    SET
                        probe_freq_mhz = COALESCE(NULLIF(p.probe_freq, '')::numeric, c.probe_freq_mhz),
                        gain = COALESCE(NULLIF(p.gain, '')::real, c.gain),
                        threshold = COALESCE(NULLIF(p.threshold, '')::smallint, c.threshold),
                        meas_mode = COALESCE(NULLIF(p.meas_mode, '')::smallint, c.meas_mode),
                        ref_time_ns = COALESCE(NULLIF(p.ref_time, '')::numeric, c.ref_time_ns),
                        ref_temp_c = COALESCE(NULLIF(p.ref_temp, '')::numeric, c.ref_temp_c),
                        echo_win_ns = COALESCE(NULLIF(p.echo_win, '')::numeric, c.echo_win_ns),
                        echo_width_us = COALESCE(NULLIF(p.echo_width, '')::smallint, c.echo_width_us),
                        tc_mode = COALESCE(NULLIF(p.tc_mode, '')::smallint, c.tc_mode),
                        temp_coefficient = COALESCE(NULLIF(p.temp_coeff, '')::numeric, c.temp_coefficient),
                        config_source = 'device_readback',
                        config_synced_at = now(),
                        updated_at = now()
                    FROM p
                    WHERE c.device_id = @device_id
                      AND c.channel_no = @channel_no
                      AND c.deleted_at IS NULL
                    RETURNING c.id, c.device_id, c.channel_no, c.probe_freq_mhz, c.gain, c.threshold,
                              c.meas_mode, c.ref_time_ns, c.ref_temp_c, c.tc_mode, to_jsonb(c)", conn, tx);

                cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
                cmd.Parameters.AddWithValue("channel_no", NpgsqlDbType.Smallint, (short)channel);

                // reader 先读完关闭再回滚（开着 reader 回滚会抛异常，把 400 变 500）
                long channelId = 0;
                object? synced = null;
                string afterJson = "";
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    if (await reader.ReadAsync(ct))
                    {
                        channelId = reader.GetInt64(0);
                        synced = new
                        {
                            id = channelId,
                            deviceId = reader.GetInt64(1),
                            channelNo = reader.GetInt16(2),
                            probeFreqMhz = reader.GetDecimal(3),
                            gain = reader.GetFloat(4),
                            threshold = reader.GetInt16(5),
                            measMode = reader.GetInt16(6),
                            refTimeNs = reader.IsDBNull(7) ? null : (decimal?)reader.GetDecimal(7),
                            refTempC = reader.IsDBNull(8) ? null : (decimal?)reader.GetDecimal(8),
                            tcMode = reader.GetInt16(9)
                        };
                        afterJson = reader.GetString(10);
                    }
                }
                if (synced is null)
                {
                    await tx.RollbackAsync(ct);
                    return Results.BadRequest(new { error = "没有可同步的通道参数历史", deviceId, channel });
                }

                await using (var audit = new NpgsqlCommand(@"
                    INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
                    VALUES (@user_id, 'sync_channel_config', 'channel', @channel_id, @before::jsonb, @after::jsonb, @note, now())", conn, tx))
                {
                    audit.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, userId);
                    audit.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                    audit.Parameters.AddWithValue("before", NpgsqlDbType.Jsonb, beforeJson);
                    audit.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb, afterJson);
                    audit.Parameters.AddWithValue("note", NpgsqlDbType.Text, "从 device_parameter_history 最新读回值同步通道配置");
                    await audit.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                resolver.Invalidate(deviceId, channel);

                logger.LogWarning("已同步通道配置 device={DeviceId} ch={Channel}: {@Config}", deviceId, channel, synced);

                return Results.Ok(new
                {
                    synced = true,
                    syncedAt = DateTimeOffset.UtcNow,
                    channel = synced,
                    note = "已同步 channels 表并失效通道路由缓存；重启或重连后 SDK SessionContext 会直接从 DB 加载这些值。"
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                logger.LogError(ex, "同步通道配置失败 device={DeviceId} ch={Channel}", deviceId, channel);
                return Results.Problem(detail: "服务器内部错误，请稍后重试；详情见系统日志。", statusCode: 500);
            }
        })
        .WithName("SyncChannelConfigFromHistory")
        .WithSummary("把设备最新读回参数同步到 channels 表");

        app.MapGet("/api/channels/{channelId:long}/config", async (
            long channelId,
            IChannelConfigService service,
            CancellationToken ct) =>
        {
            var config = await service.GetAsync(channelId, ct);
            return config is null
                ? Results.NotFound(new { error = "通道不存在", channelId })
                : Results.Ok(config);
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithTags("Channels")
        .WithName("GetChannelConfigView")
        .WithSummary("查看通道配置主数据（DB）");

        app.MapGet("/api/devices/{deviceId:long}/channels/{channelNo:int}/config-diff", async (
            long deviceId,
            ushort channelNo,
            IChannelConfigService service,
            CancellationToken ct) =>
        {
            var items = await service.CompareAsync(deviceId, channelNo, ct);
            return Results.Ok(new
            {
                deviceId,
                channelNo,
                hasDifferences = items.Any(x => x.IsDifferent),
                items
            });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithTags("Channels")
        .WithName("CompareChannelConfig")
        .WithSummary("对比 DB 通道配置和设备最近读回参数");

        // 提交通道参数变更【请求】（安全红线：不再裸写设备）。
        // 必须经【另一名】reviewer 审批后，由 /api/config-changes/{id}/execute 下发设备。
        group.MapPost("/configure-channel/{channel}", async (
            long deviceId, ushort channel,
            ChannelConfigUpdate updates,
            NpgsqlDataSource ds,
            HttpContext http,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Diagnostic");
            var actor = UserContext.Require(http);
            var userId = actor.UserId;
            // 管理员修改免审批：admin 提交的变更直接落 approved（approved_by=本人），跳过异人复核，
            // 可直接 /execute 下发；operator/reviewer 仍走 requested→approved(异人)→executed→verified。
            // approved_by 落库 + 下方审计条目让"管理员免审批"可追溯（不因免审批而丢失责任链）。
            var isAdmin = actor.Role == AuthRoles.Admin;
            var status = isAdmin ? "approved" : "requested";

            long requestId = 0;
            long channelId = 0;
            // 请求写入 + 管理员免审批的审计条目放在同一事务：避免"已自动批准的变更"落库但审计丢失（§0 责任链不可断）
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO channel_config_change_requests (
                        device_id, channel_id, channel_no, requested_by, requested_jsonb, status, note,
                        approved_by, approved_at
                    )
                    SELECT @device_id, c.id, @channel_no, @user_id, @updates::jsonb, @status, @note,
                           @approved_by, CASE WHEN @approved_by IS NULL THEN NULL ELSE now() END
                    FROM channels c
                    WHERE c.device_id = @device_id AND c.channel_no = @channel_no AND c.deleted_at IS NULL
                    RETURNING id, channel_id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
                    cmd.Parameters.AddWithValue("channel_no", NpgsqlDbType.Smallint, (short)channel);
                    cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, userId);
                    cmd.Parameters.AddWithValue("updates", NpgsqlDbType.Jsonb, System.Text.Json.JsonSerializer.Serialize(updates));
                    cmd.Parameters.AddWithValue("status", NpgsqlDbType.Varchar, status);
                    cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, isAdmin ? "configure-channel 提交（管理员免审批，自动批准）" : "configure-channel 提交");
                    cmd.Parameters.AddWithValue("approved_by", NpgsqlDbType.Bigint, isAdmin ? userId : (object)DBNull.Value);
                    // reader 先读完关闭再回滚（开着 reader 回滚会抛异常，把 404 变 500）
                    var inserted = false;
                    await using (var r = await cmd.ExecuteReaderAsync(ct))
                    {
                        if (await r.ReadAsync(ct))
                        {
                            inserted = true;
                            requestId = r.GetInt64(0);
                            channelId = r.GetInt64(1);
                        }
                    }
                    if (!inserted)
                    {
                        await tx.RollbackAsync(ct);
                        return Results.NotFound(new { error = "通道不存在", deviceId, channel });
                    }
                }

                if (isAdmin)
                {
                    // 责任链：管理员免审批也要留痕（谁、何时、批了哪条）
                    await using var audit = new NpgsqlCommand(@"
                        INSERT INTO audit_log (user_id, action, target_type, target_id, after_jsonb, note, at)
                        VALUES (@uid, 'config_change_approve', 'channel', @cid, @after::jsonb, @note, now())", conn, tx);
                    audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, userId);
                    audit.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                    audit.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb,
                        System.Text.Json.JsonSerializer.Serialize(new { requestId, autoApproved = true, by = userId, role = actor.Role }));
                    audit.Parameters.AddWithValue("note", NpgsqlDbType.Text, $"管理员免审批：变更 #{requestId} 提交即自动批准");
                    await audit.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                logger.LogError(ex, "提交通道参数变更失败 device={DeviceId} ch={Ch}", deviceId, channel);
                return Results.Problem(detail: "服务器内部错误，请稍后重试；详情见系统日志。", statusCode: 500);
            }

            logger.LogWarning(
                "已提交通道参数变更请求 #{ReqId} device={DeviceId} ch={Ch} status={Status} updates={Updates} by user={UserId}",
                requestId, deviceId, channel, status, updates, userId);

            return Results.Ok(new
            {
                submitted = true,
                requestId,
                status,
                deviceId,
                channel,
                applied = updates,
                note = isAdmin
                    ? $"管理员免审批：变更【请求】#{requestId} 已自动批准，可直接 POST /api/config-changes/{requestId}/execute 下发，再 /verify 读回验证。"
                    : $"已提交变更【请求】#{requestId}，未下发设备。需另一名 reviewer 调 POST /api/config-changes/{requestId}/approve 审批，再 /execute 下发、/verify 读回验证。"
            });
        })
        .WithName("ConfigureChannel")
        .WithSummary("提交通道参数变更请求（需审批后执行，不再直接下发设备）")
        .WithDescription(@"
行为变更（安全红线）：本接口【不再直接下发设备】，仅创建参数变更【请求】(status=requested)。
安全攸关的测量参数变更必须两人复核：
1. 本接口提交请求；
2. 另一名 reviewer 调 POST /api/config-changes/{id}/approve 审批（不可自审）；
3. POST /api/config-changes/{id}/execute 下发到设备；
4. POST /api/config-changes/{id}/verify 读回比对确认生效。

请求体示例：
```json
{ ""gain"": 20, ""threshold"": -480 }
```
仅传需要改的字段。注意 MeasMode/Threshold/RefTime/RefTemp 等 SDK 协议不允许单独写，执行时会失败并引导改用 /recalibrate。");

        // 调试期直发（仅未标定通道）：现场对回波/增益时，逐次审批不现实。
        // 安全模型：未标定通道（未出具正式预紧力）允许工程师直发非安全基线参数（增益/探头频率/回波窗/回波宽度）；
        // 已标定（在产）通道一律走 configure-channel 审批工作流。基线类（阈值/测量模式/RefTime/RefTemp）一律走 /recalibrate。
        // M3 拍板：随组策略走 reviewer+（见组级 ReviewerAccess），不再单独放宽到 WriteAccess——
        // 初调工程师本来就需要本组其他端点（读参/对波形/set-mode，均 reviewer+）才能完成初调闭环，
        // 单留一个 operator 可直写设备的口子只剩绕过审批的风险，没有独立价值。
        group.MapPost("/configure-channel/{channel}/commission", async (
            long deviceId, ushort channel,
            ChannelConfigUpdate updates,
            IDeviceConnectorManager deviceManager,
            NpgsqlDataSource ds,
            IChannelResolver resolver,
            ProbeScanState probeScanState,
            HttpContext http,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Diagnostic");
            var userId = UserContext.Require(http).UserId;

            if (updates.Threshold.HasValue || updates.MeasMode.HasValue || updates.RefTimeNs.HasValue
                || updates.RefTempC.HasValue || updates.TcMode.HasValue || updates.TempCoefficient.HasValue)
                return Results.BadRequest(new { error = "阈值/测量模式/RefTime/RefTemp/温补 属标定基线，请走 /recalibrate；调试直发仅支持 增益/探头频率/回波窗/回波宽度。" });
            // 与 set-mode/MCR 同一互斥：扫描期间不下发通道配置，避免与扫描器抢设备
            if (probeScanState.IsRunning(deviceId))
                return Results.Conflict(new { error = "通道自动检测进行中，不能并发下发通道配置", deviceId });

            long channelId;
            bool calibrated;
            await using (var q = ds.CreateCommand(@"
                SELECT id, (active_calibration_id IS NOT NULL OR k_l IS NOT NULL) AS calibrated
                FROM channels WHERE device_id=@d AND channel_no=@ch AND deleted_at IS NULL"))
            {
                q.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
                q.Parameters.AddWithValue("ch", NpgsqlDbType.Smallint, (short)channel);
                await using var r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Results.NotFound(new { error = "通道不存在", deviceId, channel });
                channelId = r.GetInt64(0);
                calibrated = r.GetBoolean(1);
            }
            if (calibrated)
                return Results.Conflict(new { error = "通道已标定（在产），参数变更须走审批工作流：configure-channel → approve → execute → verify。", deviceId, channel });

            var runtime = deviceManager.Find(deviceId);
            if (runtime is null)
                return Results.NotFound(new { error = $"设备未加载到运行时：{deviceId}", deviceId });

            try
            {
                await runtime.Connector.ConfigureChannelAsync(channel, updates, runtime.Options.Address, ct);

                // DB 同步与审计同事务；echo_width_us 也随下发同步（此前允许下发却不落库，设备与库漂移）
                await using var conn = await ds.OpenConnectionAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var u = new NpgsqlCommand(@"
                    UPDATE channels SET
                        gain = COALESCE(@gain, gain),
                        probe_freq_mhz = COALESCE(@pf, probe_freq_mhz),
                        echo_win_ns = COALESCE(@ew, echo_win_ns),
                        echo_width_us = COALESCE(@ewd, echo_width_us),
                        updated_at = now()
                    WHERE id=@id", conn, tx))
                {
                    u.Parameters.AddWithValue("gain", NpgsqlDbType.Real, (object?)(updates.Gain.HasValue ? (float)updates.Gain.Value : (float?)null) ?? DBNull.Value);
                    u.Parameters.AddWithValue("pf", NpgsqlDbType.Numeric, (object?)updates.ProbeFreqMhz ?? DBNull.Value);
                    u.Parameters.AddWithValue("ew", NpgsqlDbType.Numeric, (object?)updates.EchoWinNs ?? DBNull.Value);
                    u.Parameters.AddWithValue("ewd", NpgsqlDbType.Smallint, (object?)(short?)updates.EchoWidthUs ?? DBNull.Value);
                    u.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
                    await u.ExecuteNonQueryAsync(ct);
                }

                await using (var audit = new NpgsqlCommand(@"
                    INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
                    VALUES (@uid, 'channel_commission', 'channel', @cid, NULL, @after::jsonb, @note, now())", conn, tx))
                {
                    audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, userId);
                    audit.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                    audit.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb, System.Text.Json.JsonSerializer.Serialize(updates));
                    audit.Parameters.AddWithValue("note", NpgsqlDbType.Text, $"调试直发通道参数 device={deviceId} ch={channel}（未标定）");
                    await audit.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);

                // 失效路由缓存：热路径/前端立刻读到新配置（此前漏调，缓存最长滞后 5 分钟）
                resolver.Invalidate(deviceId, channel);

                logger.LogWarning("调试直发通道参数 device={Dev} ch={Ch} updates={Updates} by user={User}",
                    deviceId, channel, updates, userId);
                return Results.Ok(new { commissioned = true, deviceId, channel, applied = updates, note = "已直发设备（未标定通道，调试期）。观察波形/声时确认锁到真实回波后，请执行重标定设置基线。" });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "调试直发失败 device={Dev} ch={Ch}", deviceId, channel);
                return Results.BadRequest(new { error = "参数下发失败（设备离线或通信超时），详情见系统日志。", deviceId, channel });
            }
        })
        .WithName("CommissionChannel")
        .WithSummary("调试期直发通道参数（仅未标定通道：增益/探头频率/回波窗；已标定走审批）；随组策略 reviewer+");

        // 设备工作模式控制（Standby/MultiMeas/...）
        group.MapPost("/set-mode/{mode}", async (
            long deviceId, string mode, ushort? channel, ushort? address,
            IDeviceConnectorManager deviceManager, ProbeScanState probeScanState,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var connector = deviceManager.Find(deviceId)?.Connector;
            if (connector is null)
                return Results.NotFound(new { error = $"设备未加载到运行时：{deviceId}", deviceId });
            // 扫描期间不允许手工改 WorkMode
            if (probeScanState.IsRunning(deviceId))
                return Results.Conflict(new { error = "通道自动检测进行中，不能并发设置 WorkMode" });

            var logger = loggerFactory.CreateLogger("Diagnostic");
            if (!Enum.TryParse<FxWorkMode>(mode, ignoreCase: true, out var workMode))
            {
                return Results.BadRequest(new
                {
                    error = "无效的工作模式",
                    validModes = Enum.GetNames<FxWorkMode>()
                });
            }

            await connector.SetWorkModeAsync(workMode,
                channel ?? (ushort)0,
                address ?? (ushort)1,
                ct);
            logger.LogInformation("已下发 SetMode device={DeviceId} mode={Mode}", deviceId, workMode);

            return Results.Ok(new { triggered = true, deviceId, mode = workMode.ToString() });
        })
        .WithName("SetWorkMode");

        // ========== 缓存效率诊断 ==========
        app.MapGet("/api/diagnostics/cache-stats", (BoltPreload.Application.Ingestion.IChannelResolver r) =>
        {
            if (r is BoltPreload.Infrastructure.Ingestion.CachedChannelResolver cached)
            {
                var s = cached.Stats;
                return Results.Ok(new
                {
                    s.Hits, s.Misses, s.AutoDiscoveries, s.Invalidations,
                    hitRate = Math.Round(s.HitRate * 100, 2),
                    note = "热路径通道解析缓存命中率。预期长期 > 99%（通道变更很少）。"
                });
            }
            return Results.Ok(new { note = "当前 IChannelResolver 不是 CachedChannelResolver" });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithTags("Diagnostic")
        .WithName("CacheStats")
        .WithSummary("通道路由缓存效率");

        app.MapPost("/api/diagnostics/cache-stats/reset", (BoltPreload.Application.Ingestion.IChannelResolver r) =>
        {
            r.InvalidateAll();
            return Results.Ok(new { reset = true });
        })
        // 清空路由缓存会引发短时全量回源查库,属运维干预动作,与其他诊断操作同级(reviewer+)
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithTags("Diagnostic")
        .WithName("CacheReset")
        .WithSummary("清空通道路由缓存（强制下次重新查库）");

        app.MapGet("/api/devices/{deviceId:long}/diagnostic/real-health", async (
            long deviceId,
            ushort? channel,
            int? minutes,
            IDeviceConnectorManager deviceManager,
            NpgsqlDataSource ds,
            CancellationToken ct) =>
        {
            var connector = deviceManager.Find(deviceId)?.Connector;
            if (connector is null)
                return Results.NotFound(new { error = $"设备未加载到运行时：{deviceId}", deviceId });
            if (channel is null)
                return Results.BadRequest(new { error = "必须指定 channel 参数" });

            var windowMinutes = Math.Clamp(minutes ?? 1, 1, 60);
            var ch = channel.Value;

            const string sql = @"
                WITH m AS (
                    SELECT m.*
                    FROM measurements m
                    JOIN channels c ON c.id = m.channel_id
                    WHERE c.device_id = @device_id
                      AND c.channel_no = @channel_no
                      AND m.ts > now() - (@minutes || ' minutes')::interval
                ),
                latest_param AS (
                    SELECT DISTINCT ON (param_name)
                           param_name, param_value_text, received_at
                    FROM device_parameter_history
                    WHERE device_id = @device_id
                      AND channel_no = @channel_no
                      AND param_name IN ('ProbeFreq','Gain','MeasMode','Threshold','RefTime','RefTemp')
                    ORDER BY param_name, received_at DESC
                )
                SELECT
                    (SELECT COUNT(*) FROM m) AS total,
                    (SELECT COUNT(echo_time_ns) FROM m) AS with_echo,
                    (SELECT ROUND(AVG(sp_temp_c), 2) FROM m) AS avg_temp,
                    (SELECT MAX(ts) FROM m) AS last_ts,
                    (SELECT jsonb_object_agg(param_name, jsonb_build_object('value', param_value_text, 'receivedAt', received_at)) FROM latest_param) AS params_json";

            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
            cmd.Parameters.AddWithValue("channel_no", NpgsqlDbType.Smallint, (short)ch);
            cmd.Parameters.AddWithValue("minutes", NpgsqlDbType.Integer, windowMinutes);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var total = r.GetInt64(0);
            var withEcho = r.GetInt64(1);
            var echoRate = total == 0 ? 0 : Math.Round(100.0 * withEcho / total, 1);
            var avgTemp = r.IsDBNull(2) ? null : (decimal?)r.GetDecimal(2);
            var lastTs = r.IsDBNull(3) ? null : (DateTime?)r.GetDateTime(3);
            var paramsJson = r.IsDBNull(4) ? null : r.GetString(4);

            var sessionConfigs = connector.GetSessionChannelConfigs();
            var sessionConfig = sessionConfigs.FirstOrDefault(c => c.Channel == ch);

            return Results.Ok(new
            {
                deviceId,
                channel = ch,
                connectorState = connector.State.ToString(),
                windowMinutes,
                measurement = new
                {
                    total,
                    withEcho,
                    echoRatePct = echoRate,
                    avgTempC = avgTemp,
                    lastTs,
                    healthy = total > 0 && echoRate >= 95
                },
                sdkSession = new
                {
                    hasChannelConfig = sessionConfig is not null,
                    sessionConfig,
                    allConfiguredChannels = sessionConfigs.Select(c => c.Channel).ToArray(),
                    note = "EchoTime 由 SDK 根据波形计算；必须有 ChannelConfig 写入 SessionContext。"
                },
                latestDeviceParamsJson = paramsJson
            });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithTags("Diagnostic")
        .WithName("RealDeviceHealth")
        .WithSummary("真实设备健康检查：EchoTime 接受率 + SDK SessionContext 通道配置");

        app.MapGet("/api/devices/{deviceId:long}/channels/health", async (
            long deviceId,
            int? minutes,
            IDeviceConnectorManager deviceManager,
            NpgsqlDataSource ds,
            CancellationToken ct) =>
        {
            var connector = deviceManager.Find(deviceId)?.Connector;
            if (connector is null)
                return Results.NotFound(new { error = $"设备未加载到运行时：{deviceId}", deviceId });
            var windowMinutes = Math.Clamp(minutes ?? 1, 1, 60);
            const string sql = @"
                WITH last_param AS (
                    SELECT channel_no,
                           jsonb_object_agg(param_name, jsonb_build_object('value', param_value_text, 'receivedAt', received_at)) AS params_json
                    FROM (
                        SELECT DISTINCT ON (channel_no, param_name)
                               channel_no, param_name, param_value_text, received_at
                        FROM device_parameter_history
                        WHERE device_id = @device_id
                          AND channel_no IS NOT NULL
                          AND param_name IN ('ProbeFreq','Gain','MeasMode','Threshold','RefTime','RefTemp','EchoWin','EchoWidth')
                        ORDER BY channel_no, param_name, received_at DESC
                    ) p
                    GROUP BY channel_no
                )
                SELECT
                    c.id, c.channel_no, c.state, c.sampling_enabled, c.hardware_state, c.business_state,
                    c.probe_freq_mhz, c.gain, c.threshold, c.meas_mode, c.ref_time_ns, c.ref_temp_c,
                    b.id AS bolt_id, b.code AS bolt_code,
                    COALESCE(m.total, 0) AS total,
                    COALESCE(m.with_echo, 0) AS with_echo,
                    m.avg_echo, m.echo_std, m.avg_temp, m.last_ts,
                    wf.snr_db, wf.peak_to_peak, wf.noise_rms,
                    lp.params_json
                FROM channels c
                LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                LEFT JOIN LATERAL (
                    SELECT COUNT(*) AS total,
                           COUNT(echo_time_ns) AS with_echo,
                           ROUND(AVG(echo_time_ns), 2) AS avg_echo,
                           ROUND(STDDEV_SAMP(echo_time_ns), 3) AS echo_std,
                           ROUND(AVG(sp_temp_c), 2) AS avg_temp,
                           MAX(ts) AS last_ts
                    FROM measurements m
                    WHERE m.channel_id = c.id
                      AND m.ts > now() - (@minutes || ' minutes')::interval
                ) m ON true
                LEFT JOIN LATERAL (
                    SELECT snr_db, peak_to_peak, noise_rms
                    FROM wave_features wf
                    WHERE wf.channel_id = c.id
                    ORDER BY ts DESC LIMIT 1
                ) wf ON true
                LEFT JOIN last_param lp ON lp.channel_no = c.channel_no
                WHERE c.device_id = @device_id
                  AND c.deleted_at IS NULL
                ORDER BY c.channel_no";

            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
            cmd.Parameters.AddWithValue("minutes", NpgsqlDbType.Integer, windowMinutes);

            var sessionChannels = connector.GetSessionChannelConfigs()
                .ToDictionary(c => c.Channel);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var channelNo = (ushort)r.GetInt16(1);
                sessionChannels.TryGetValue(channelNo, out var sessionConfig);
                var total = r.GetInt64(14);
                var withEcho = r.GetInt64(15);
                var echoRate = total == 0 ? 0 : Math.Round(100.0 * withEcho / total, 1);
                items.Add(new
                {
                    channelId = r.GetInt64(0),
                    channelNo,
                    state = r.GetString(2),
                    samplingEnabled = r.GetBoolean(3),
                    hardwareState = r.GetString(4),
                    businessState = r.GetString(5),
                    desired = new
                    {
                        probeFreqMhz = r.GetDecimal(6),
                        gain = r.GetFloat(7),
                        threshold = r.GetInt16(8),
                        measMode = r.GetInt16(9),
                        refTimeNs = r.IsDBNull(10) ? null : (decimal?)r.GetDecimal(10),
                        refTempC = r.IsDBNull(11) ? null : (decimal?)r.GetDecimal(11)
                    },
                    bolt = r.IsDBNull(12) ? null : new { id = r.GetInt64(12), code = r.GetString(13) },
                    measurement = new
                    {
                        total,
                        withEcho,
                        echoRatePct = echoRate,
                        avgEchoNs = r.IsDBNull(16) ? null : (decimal?)r.GetDecimal(16),
                        echoStdNs = r.IsDBNull(17) ? null : (decimal?)r.GetDecimal(17),
                        avgTempC = r.IsDBNull(18) ? null : (decimal?)r.GetDecimal(18),
                        lastTs = r.IsDBNull(19) ? null : (DateTime?)r.GetDateTime(19),
                        healthy = total > 0 && echoRate >= 95
                    },
                    latestWave = new
                    {
                        snrDb = r.IsDBNull(20) ? null : (decimal?)r.GetDecimal(20),
                        peakToPeak = r.IsDBNull(21) ? null : (int?)r.GetInt32(21),
                        noiseRms = r.IsDBNull(22) ? null : (decimal?)r.GetDecimal(22)
                    },
                    actualParamsJson = r.IsDBNull(23) ? null : r.GetString(23),
                    sdkSession = new
                    {
                        configured = sessionConfig is not null,
                        sessionConfig
                    }
                });
            }

            return Results.Ok(new
            {
                deviceId,
                connectorState = connector.State.ToString(),
                windowMinutes,
                count = items.Count,
                sessionChannels = sessionChannels.Keys.OrderBy(x => x).ToArray(),
                items
            });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithTags("Channels")
        .WithName("DeviceChannelsHealth")
        .WithSummary("多通道健康总览：每通道 Session、绑定、测量、波形与设备读回参数");

        // 点位与验收页数据源：返回【全部活跃通道】的最新回波声时 + 信号强度(峰峰值/SNR) + 验收状态。
        // 未绑定通道的回波来自 unbound_measurements（绑定后才进 measurements）——这样新插探头、
        // 还没建档的通道也能在页面看到实时信号并就地建档；无数据通道也返回（前端要能显示整个通道面板）。
        app.MapGet("/api/devices/{deviceId:long}/channels/live", async (
            long deviceId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            const string sql = @"
                SELECT c.channel_no, b.id, b.code, b.position_label,
                       COALESCE(le.echo_time_ns, ule.echo_time_ns) AS echo_ns,
                       COALESCE(le.sp_temp_c, ule.sp_temp_c) AS temp_c,
                       COALESCE(le.ts, ule.ts) AS last_ts,
                       wf.peak_to_peak, wf.snr_db,
                       c.id AS channel_id, c.name AS channel_name,
                       c.hardware_state, c.last_seen_at, c.accepted_at, c.last_present_at,
                       b.target_preload_n
                FROM channels c
                LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                LEFT JOIN LATERAL (
                    SELECT echo_time_ns, sp_temp_c, ts FROM measurements m
                    WHERE m.channel_id = c.id AND m.ts > now() - interval '20 seconds'
                    ORDER BY ts DESC LIMIT 1) le ON true
                LEFT JOIN LATERAL (
                    SELECT echo_time_ns, sp_temp_c, ts FROM unbound_measurements um
                    WHERE um.channel_id = c.id AND um.ts > now() - interval '20 seconds'
                    ORDER BY ts DESC LIMIT 1) ule ON true
                LEFT JOIN LATERAL (
                    SELECT peak_to_peak, snr_db FROM wave_features w
                    WHERE w.channel_id = c.id AND w.ts > now() - interval '20 seconds'
                    ORDER BY ts DESC LIMIT 1) wf ON true
                WHERE c.device_id = @d AND c.deleted_at IS NULL
                ORDER BY c.channel_no";
            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new
                {
                    channelNo = r.GetInt16(0),
                    boltId = r.IsDBNull(1) ? (long?)null : r.GetInt64(1),
                    boltCode = r.IsDBNull(2) ? null : r.GetString(2),
                    positionLabel = r.IsDBNull(3) ? null : r.GetString(3),
                    echoNs = r.IsDBNull(4) ? (decimal?)null : r.GetDecimal(4),
                    tempC = r.IsDBNull(5) ? (decimal?)null : r.GetDecimal(5),
                    lastTs = r.IsDBNull(6) ? (DateTime?)null : r.GetDateTime(6),
                    peakToPeak = r.IsDBNull(7) ? (int?)null : r.GetInt32(7),
                    snrDb = r.IsDBNull(8) ? (decimal?)null : r.GetDecimal(8),
                    channelId = r.GetInt64(9),
                    channelName = r.IsDBNull(10) ? null : r.GetString(10),
                    hardwareState = r.GetString(11),
                    lastSeenAt = r.IsDBNull(12) ? (DateTime?)null : r.GetDateTime(12),
                    acceptedAt = r.IsDBNull(13) ? (DateTime?)null : r.GetDateTime(13),
                    // 曾在位标记：识别页据此区分"失联"（曾在位，红色警示）与"待接入"（从未在位，中性）
                    lastPresentAt = r.IsDBNull(14) ? (DateTime?)null : r.GetDateTime(14),
                    // 自动建档缺省无目标值 → 识别页「待补规格」引导
                    targetPreloadN = r.IsDBNull(15) ? (decimal?)null : r.GetDecimal(15),
                });
            return Results.Ok(new { deviceId, count = items.Count, items });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithTags("Channels")
        .WithName("DeviceChannelsLive")
        .WithSummary("点位与验收：全部通道的最新回波声时 + 信号强度 + 验收状态（含未绑定通道）");

        // ========== 重标定（用当前 TOF/Temp 写成新基线） ==========
        //
        // SDK 协议层一次写一整组：MeasMode + Threshold + RefTime + RefTemp + LEchoWave
        // 这是设备厂家协议的硬约束（见 MultiChIotProtocolHandler:269-294），不能拆。
        // FxSdkConnector.RecalibrateChannelAsync 内部自动取最近 ChannelResult 的 EchoTime/SpTemp
        // 和最近 LEchoWave 作为新基线整组写入。
        //
        // 调用前必须确认：螺栓处于"已知预紧力"状态（拧紧到目标或零预紧），否则基线无效。
        // 重标定会覆盖设备 RefTime/RefTemp/LEchoWave 标定基线（安全攸关），要求 reviewer+，且必须登录。
        var calibGroup = app.MapGroup("/api/devices/{deviceId:long}/channels/{channel:int}")
            .WithTags("Calibration")
            .RequireAuthorization(AuthPolicies.ReviewerAccess);

        calibGroup.MapPost("/recalibrate", async (
            long deviceId, ushort channel, ushort? address,
            RecalibrateRequest? body,
            IDeviceConnectorManager deviceManager, ProbeScanState probeScanState,
            NpgsqlDataSource ds, IChannelResolver resolver, HttpContext http,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var connector = deviceManager.Find(deviceId)?.Connector;
            if (connector is null)
                return Results.NotFound(new { error = $"设备未加载到运行时：{deviceId}", deviceId });
            // 扫描期间不允许重标定：会与扫描器抢工作模式，且重标定基线必须在已知预紧状态下取，
            // 扫描中通道频繁切换不可能满足"已知预紧"前提
            if (probeScanState.IsRunning(deviceId))
                return Results.Conflict(new { error = "通道自动检测进行中，不能并发执行重标定" });

            var logger = loggerFactory.CreateLogger("Recalibrate");
            var addr = address ?? (ushort)1;
            logger.LogWarning(
                "触发重标定 device={DeviceId} ch={Ch} addr={Addr} explicit RefTime={Rt} RefTemp={Rtemp}",
                deviceId, channel, addr, body?.RefTimeNs, body?.RefTempC);

            try
            {
                var result = await connector.RecalibrateChannelAsync(
                    channel, addr,
                    explicitRefTimeNs: body?.RefTimeNs,
                    explicitRefTempC: body?.RefTempC,
                    cancellationToken: ct);
                logger.LogInformation(
                    "重标定完成 device={DeviceId} ch={Ch} 新 RefTime={Rt}ns RefTemp={Rtemp}°C 波形 {Pts} 点",
                    deviceId, channel, result.NewRefTimeNs, result.NewRefTempC, result.EchoWaveBytes);

                // 同步基线到库（否则前端/换算仍按旧 RefTime 算 ΔTOF），并失效路由缓存让热路径立刻读到新基线。
                long channelId = 0;
                await using (var upd = ds.CreateCommand(@"
                    UPDATE channels SET ref_time_ns=@rt, ref_temp_c=@rtemp, updated_at=now()
                    WHERE device_id=@d AND channel_no=@ch AND deleted_at IS NULL
                    RETURNING id"))
                {
                    upd.Parameters.AddWithValue("rt", NpgsqlDbType.Numeric, result.NewRefTimeNs);
                    upd.Parameters.AddWithValue("rtemp", NpgsqlDbType.Numeric, result.NewRefTempC);
                    upd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
                    upd.Parameters.AddWithValue("ch", NpgsqlDbType.Smallint, (short)channel);
                    if (await upd.ExecuteScalarAsync(ct) is long id) channelId = id;
                }
                resolver.Invalidate(deviceId, channel);

                if (channelId > 0)
                {
                    var uid = UserContext.Require(http).UserId;
                    await using var audit = ds.CreateCommand(@"
                        INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
                        VALUES (@uid, 'channel_recalibrate', 'channel', @cid, NULL, @after::jsonb, @note, now())");
                    audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, uid);
                    audit.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                    audit.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb, System.Text.Json.JsonSerializer.Serialize(new { result.NewRefTimeNs, result.NewRefTempC, explicit_ = body?.RefTimeNs != null }));
                    audit.Parameters.AddWithValue("note", NpgsqlDbType.Text, $"重标定基线 device={deviceId} ch={channel} → RefTime={result.NewRefTimeNs}ns");
                    await audit.ExecuteNonQueryAsync(ct);
                }

                return Results.Ok(new
                {
                    deviceId,
                    channel,
                    address = addr,
                    newRefTimeNs = result.NewRefTimeNs,
                    newRefTempC = result.NewRefTempC,
                    echoWavePoints = result.EchoWaveBytes,
                    timestamp = result.Timestamp,
                    note = "基线已写入设备并同步到库（缓存已失效）。"
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (TimeoutException ex)
            {
                logger.LogError(ex, "重标定超时 device={DeviceId} ch={Ch}", deviceId, channel);
                return Results.Json(new { error = ex.Message }, statusCode: 504);
            }
        })
        .WithName("RecalibrateChannel")
        .WithSummary("通道重标定：用当前 TOF/Temp 写成新基线")
        .WithDescription(@"
**危险操作**：会覆盖设备里的 RefTime/RefTemp/LEchoWave 整组标定基线。

前置条件：
1. 螺栓处于已知预紧力状态（零预紧或额定预紧）。
2. 设备已在 MultiMeas 模式且能稳定测出 EchoTime（即 measurements.echo_time_ns 非空）。

流程：
- 取本通道最近一次有效的 EchoTime / SpTemp 作为新 RefTime / RefTemp；
- 如果本地缓存没有 LEchoWave，自动触发 ReadChannelParam(RefTime) 存储读，整组带回设备基线（含参考波形）；
- 把 MeasMode+Threshold+RefTime+RefTemp+LEchoWave 一次性写到设备（SDK 协议约束）。

操作后建议：
- 在 calibration_records 表里登记本次重标定（当前需人工，标定接口会自动登记）；
- 等 10s 后调 ReadChannelParam(RefTime) 验证设备实际生效；
- 持续观察 EchoTime 在新 RefTemp 下的接受率是否提升。");

        return app;
    }
}

/// <summary>
/// 重标定请求体（全部可选）：
/// 留空 → 自动用最近一次 ChannelResult.EchoTime / SpTemp 作为新基线（标准路径）；
/// 显式给值 → 强制写入（设备测不出 TOF 时的兜底）。
/// </summary>
/// <param name="RefTimeNs">显式 RefTime (ns)</param>
/// <param name="RefTempC">显式 RefTemp (°C)</param>
public sealed record RecalibrateRequest(decimal? RefTimeNs = null, decimal? RefTempC = null);
/// <summary>批量读取通道参数请求：不传 Channels/PropertyNames 时按默认通道集与默认参数集读。</summary>
public sealed record BatchReadChannelParamsRequest(
    ushort[]? Channels = null,
    string[]? PropertyNames = null,
    ushort? Address = null);

/// <summary>下发 MCR 多通道轮询配置的请求：通道集与单轮/每日采集次数、起始秒。</summary>
public sealed record ConfigureMultiChannelPollingRequest(
    ushort[]? Channels = null,
    ushort? SingleCount = null,
    ushort? DailyCount = null,
    uint? StartSeconds = null);
