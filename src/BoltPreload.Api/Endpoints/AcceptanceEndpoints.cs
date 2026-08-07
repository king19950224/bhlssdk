using System.Text;
using BoltPreload.Api.Auth;
using BoltPreload.Application.Devices;
using BoltPreload.Application.Devices.Acceptance;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Application.Ingestion;
using BoltPreload.Fx;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 通道接入验收：通道↔电缆↔位置对应关系的建立与确认（替代"敲击识别"的工程化流程）。
/// <para>
/// 三个入口：
/// 1. 验收会话（/acceptance-session）：把 MCR 临时缩到待验收通道集，插拔确认从数十秒缩到秒级；
///    结束/过期/进程重启后由 ChannelPresenceService 收敛循环自动恢复全通道轮询（自愈，见 AcceptanceSessionState）。
/// 2. 确认验收（/channels/{no}/accept）：落一条 channel_acceptance 记录 + 声时指纹快照写 channels，
///    方法三选一——sequential（新装顺序接入）/ loopcheck（存量接头级拔插确认）/ manual（人工登记）。
/// 3. 验收清单与报告（/acceptance、/acceptance/export.csv）：竣工资料出口。
/// </para>
/// </summary>
public static class AcceptanceEndpoints
{
    public static IEndpointRouteBuilder MapAcceptanceEndpoints(this IEndpointRouteBuilder app)
    {
        // ==================== 验收会话 ====================
        var session = app.MapGroup("/api/devices/{deviceId:long}/acceptance-session")
            .WithTags("Acceptance");

        session.MapPost("/", async (
            long deviceId,
            AcceptanceSessionRequest? req,
            HttpContext http,
            NpgsqlDataSource ds,
            IDeviceConnectorManager deviceManager,
            AcceptanceSessionState state,
            ProbeScanState probeScan,
            IOptions<ChannelPresenceOptions> presenceOpt,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var rt = deviceManager.Find(deviceId);
            if (rt is null)
                return Results.NotFound(new { error = $"设备未加载到运行时：{deviceId}", deviceId });
            if (rt.Options.Model != 3)
                return Results.BadRequest(new { error = "验收会话仅支持多通道监测型设备（model=3）", deviceId });
            if (rt.Connector.State != FxConnectorState.Connected)
                return Results.Conflict(new { error = "设备未连接，无法开启验收会话", state = rt.Connector.State.ToString() });
            if (!presenceOpt.Value.Enabled)
                // 恢复路径押在 ChannelPresenceService 收敛循环上（见 AcceptanceSessionState 注释）——它关了就没有兜底，不允许开会话
                return Results.BadRequest(new { error = "Fx:HotPlug:Enabled=false，验收会话依赖热插拔识别服务的轮询收敛兜底，请先启用" });
            if (probeScan.IsRunning(deviceId))
                return Results.Conflict(new { error = "通道自动检测（probe-scan）进行中，与验收会话互斥，请先等扫描结束或中止扫描" });

            var channelCount = (int)rt.Options.ChannelCount;
            if (channelCount <= 0) channelCount = 48;

            // 通道集：显式给了就校验；没给默认"尚未验收的活跃通道"（全部已验收时要求显式指定，避免误开空会话）
            ushort[] channels;
            if (req?.Channels is { Count: > 0 } reqChannels)
            {
                var bad = reqChannels.Where(c => c < 1 || c > channelCount).ToArray();
                if (bad.Length > 0)
                    return Results.BadRequest(new { error = $"通道号超出 1..{channelCount}", invalid = bad });
                channels = reqChannels.Select(c => (ushort)c).Distinct().OrderBy(c => c).ToArray();
            }
            else
            {
                var list = new List<ushort>();
                await using (var cmd = ds.CreateCommand(@"
                    SELECT channel_no FROM channels
                    WHERE device_id = @id AND deleted_at IS NULL AND accepted_at IS NULL
                      AND channel_no BETWEEN 1 AND @n
                    ORDER BY channel_no"))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, deviceId);
                    cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, channelCount);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) list.Add((ushort)r.GetInt16(0));
                }
                if (list.Count == 0)
                    return Results.BadRequest(new { error = "该设备全部通道均已验收；如需重新验收，请在请求里显式指定 channels" });
                channels = [.. list];
            }

            var ttl = TimeSpan.FromMinutes(Math.Clamp(req?.TtlMinutes ?? 30, 5, 120));
            var user = UserContext.Require(http);
            var s = state.TryBegin(deviceId, channels, ttl, user.UserId);
            if (s is null)
            {
                var existing = state.Get(deviceId);
                return Results.Conflict(new
                {
                    error = "该设备已有进行中的验收会话，请先结束",
                    expiresAt = existing?.ExpiresAt,
                    channels = existing?.Channels
                });
            }

            var logger = loggerFactory.CreateLogger("Acceptance");
            try
            {
                // 缩小 MCR 到待验收集：单通道刷新率 = 通道数×单次测量时长，48→N 路提速 48/N 倍
                await rt.Connector.ConfigureMultiChannelPollingAsync(channels, singleCount: 1, dailyCount: 0, cancellationToken: ct);
                await rt.Connector.SetWorkModeAsync(FxWorkMode.MultiMeas, channel: 0, address: rt.Options.Address, ct);
            }
            catch (Exception ex)
            {
                // 下发失败必须立刻释放占位——否则设备被一个"没生效"的会话锁住，probe-scan 也进不来
                state.End(deviceId);
                logger.LogError(ex, "验收会话 MCR 下发失败 device={DeviceId}", deviceId);
                return Results.Problem("验收会话轮询下发失败，详情见系统日志", statusCode: 502);
            }

            logger.LogInformation("接入验收会话开启 device={DeviceId} channels={Channels} ttl={Ttl}min by user={UserId}",
                deviceId, string.Join(',', channels), ttl.TotalMinutes, user.UserId);

            return Results.Ok(SessionView(s, presenceOpt.Value));
        })
        // 缩小轮询集属工程师级设备操作（与诊断端点同口径 reviewer+）
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithName("StartAcceptanceSession")
        .WithSummary("开启接入验收会话：MCR 临时缩到待验收通道集，插拔确认提速（reviewer+，TTL 自动过期恢复）");

        session.MapGet("/", (long deviceId, AcceptanceSessionState state, IOptions<ChannelPresenceOptions> presenceOpt) =>
        {
            var s = state.Get(deviceId);
            if (s is null || s.ExpiresAt <= DateTimeOffset.UtcNow)
                return Results.Ok(new { active = false, deviceId });
            return Results.Ok(SessionView(s, presenceOpt.Value));
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("GetAcceptanceSession")
        .WithSummary("查询当前验收会话状态");

        session.MapDelete("/", async (
            long deviceId,
            IDeviceConnectorManager deviceManager,
            AcceptanceSessionState state,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var s = state.End(deviceId);
            if (s is null)
                return Results.Ok(new { ended = false, deviceId, note = "无进行中的验收会话" });

            // 立即恢复全通道轮询（尽力而为）；失败也没关系——ChannelPresenceService 下一轮 sweep（≤5s）自动收敛
            var restored = false;
            var rt = deviceManager.Find(deviceId);
            if (rt is not null && rt.Connector.State == FxConnectorState.Connected && rt.Options.Model == 3 && rt.Options.ChannelCount > 0)
            {
                try
                {
                    var full = Enumerable.Range(1, rt.Options.ChannelCount).Select(i => (ushort)i).ToArray();
                    await rt.Connector.ConfigureMultiChannelPollingAsync(full, singleCount: 1, dailyCount: 0, cancellationToken: ct);
                    await rt.Connector.SetWorkModeAsync(FxWorkMode.MultiMeas, channel: 0, address: rt.Options.Address, ct);
                    restored = true;
                }
                catch (Exception ex)
                {
                    loggerFactory.CreateLogger("Acceptance")
                        .LogWarning(ex, "验收会话结束后立即恢复全通道轮询失败 device={DeviceId}（热插拔服务数秒内自动收敛）", deviceId);
                }
            }

            return Results.Ok(new
            {
                ended = true,
                deviceId,
                fullPollingRestored = restored,
                note = restored ? "全通道轮询已恢复" : "全通道轮询将由热插拔识别服务在数秒内自动恢复"
            });
        })
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithName("EndAcceptanceSession")
        .WithSummary("结束验收会话并恢复全通道轮询");

        // ==================== 确认验收 ====================
        app.MapPost("/api/devices/{deviceId:long}/channels/{channelNo:int}/accept", async (
            long deviceId,
            int channelNo,
            ChannelAcceptRequest? req,
            HttpContext http,
            NpgsqlDataSource ds,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var method = req?.Method?.Trim().ToLowerInvariant();
            if (method is not ("sequential" or "loopcheck" or "manual"))
                return Results.BadRequest(new { error = "method 必须是 sequential（顺序接入）/ loopcheck（拔插确认）/ manual（人工登记）" });
            if (req?.CableLabel is { Length: > 64 })
                return Results.BadRequest(new { error = "cableLabel 长度不能超过 64 字符" });
            if (req?.PositionLabel is { Length: > 64 })
                return Results.BadRequest(new { error = "positionLabel 长度不能超过 64 字符" });

            var user = UserContext.Require(http);
            var pos = string.IsNullOrWhiteSpace(req?.PositionLabel) ? null : req!.PositionLabel!.Trim();

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // 通道行 + 当前绑定 + 近 60s 最新信号快照（绑定走 measurements，未绑定走 unbound_measurements）
            long channelId = 0; long? boltId = null; string? boltCode = null, hwState = null;
            decimal? tof = null, snr = null; int? p2p = null;
            var found = false;
            await using (var cmd = new NpgsqlCommand(@"
                SELECT c.id, c.hardware_state, b.id, b.code,
                       COALESCE(le.echo_time_ns, ule.echo_time_ns) AS echo_ns,
                       wf.peak_to_peak, wf.snr_db
                FROM channels c
                LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                LEFT JOIN LATERAL (
                    SELECT echo_time_ns FROM measurements m
                    WHERE m.channel_id = c.id AND m.ts > now() - interval '60 seconds' AND m.echo_time_ns IS NOT NULL
                    ORDER BY ts DESC LIMIT 1) le ON true
                LEFT JOIN LATERAL (
                    SELECT echo_time_ns FROM unbound_measurements um
                    WHERE um.channel_id = c.id AND um.ts > now() - interval '60 seconds' AND um.echo_time_ns IS NOT NULL
                    ORDER BY ts DESC LIMIT 1) ule ON true
                LEFT JOIN LATERAL (
                    SELECT peak_to_peak, snr_db FROM wave_features w
                    WHERE w.channel_id = c.id AND w.ts > now() - interval '60 seconds'
                    ORDER BY ts DESC LIMIT 1) wf ON true
                WHERE c.device_id = @d AND c.channel_no = @ch AND c.deleted_at IS NULL
                FOR UPDATE OF c", conn, tx))
            {
                cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
                cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Smallint, (short)channelNo);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    found = true;
                    channelId = r.GetInt64(0);
                    hwState = r.GetString(1);
                    boltId = r.IsDBNull(2) ? null : r.GetInt64(2);
                    boltCode = r.IsDBNull(3) ? null : r.GetString(3);
                    tof = r.IsDBNull(4) ? null : r.GetDecimal(4);
                    p2p = r.IsDBNull(5) ? null : r.GetInt32(5);
                    snr = r.IsDBNull(6) ? null : r.GetDecimal(6);
                }
            }
            if (!found)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "通道不存在或已删除", deviceId, channelNo });
            }

            // 有信号验证方法（sequential/loopcheck）要求验收时刻有回波——没有就不是"验证过"，只能落 manual
            string? warning = null;
            if (method is "sequential" or "loopcheck" && tof is null)
            {
                await tx.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    error = "近 60 秒无回波信号，无法以信号验证方式（顺序接入/拔插确认）通过验收。请确认探头电缆已接好、信号已恢复后重试；无信号的备用通道请用 manual 登记",
                    deviceId, channelNo, hardwareState = hwState
                });
            }

            long acceptanceId;
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO channel_acceptance (
                    device_id, channel_id, channel_no, method, cable_label, position_label,
                    bolt_id, tof_ns, peak_to_peak, snr_db, hardware_state, accepted_by, note)
                VALUES (@d, @cid, @ch, @method, @cable, @pos, @bolt, @tof, @p2p, @snr, @hw, @uid, @note)
                RETURNING id", conn, tx))
            {
                cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
                cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Smallint, (short)channelNo);
                cmd.Parameters.AddWithValue("method", NpgsqlDbType.Varchar, method);
                cmd.Parameters.AddWithValue("cable", NpgsqlDbType.Varchar, (object?)req?.CableLabel?.Trim() ?? DBNull.Value);
                cmd.Parameters.AddWithValue("pos", NpgsqlDbType.Varchar, (object?)pos ?? DBNull.Value);
                cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, (object?)boltId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("tof", NpgsqlDbType.Numeric, (object?)tof ?? DBNull.Value);
                cmd.Parameters.AddWithValue("p2p", NpgsqlDbType.Integer, (object?)p2p ?? DBNull.Value);
                cmd.Parameters.AddWithValue("snr", NpgsqlDbType.Numeric, (object?)snr ?? DBNull.Value);
                cmd.Parameters.AddWithValue("hw", NpgsqlDbType.Varchar, (object?)hwState ?? DBNull.Value);
                cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)req?.Note ?? DBNull.Value);
                acceptanceId = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }

            // 指纹快照写 channels（运行期 wiring_suspect 守卫的比对基准）；位置标签同步 channels.name
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE channels
                   SET accepted_tof_ns = @tof,
                       accepted_at = now(),
                       name = COALESCE(@pos, name),
                       updated_at = now()
                 WHERE id = @cid", conn, tx))
            {
                cmd.Parameters.AddWithValue("tof", NpgsqlDbType.Numeric, (object?)tof ?? DBNull.Value);
                cmd.Parameters.AddWithValue("pos", NpgsqlDbType.Varchar, (object?)pos ?? DBNull.Value);
                cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var audit = new NpgsqlCommand(@"
                INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at, ip_addr)
                VALUES (@uid, 'channel_accept', 'channel', @cid, NULL, @after::jsonb, @note, now(), @ip)", conn, tx))
            {
                audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                audit.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                audit.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb, System.Text.Json.JsonSerializer.Serialize(new
                {
                    acceptanceId, method, cableLabel = req?.CableLabel, positionLabel = pos, tofNs = tof
                }));
                audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                    $"通道接入验收 device={deviceId} CH{channelNo} 方式={DisplayLabels.AcceptanceMethod(method)} 电缆={req?.CableLabel ?? "—"} 声时={tof?.ToString("F0") ?? "—"}ns");
                AuditClientIp.Bind(audit.Parameters);
                await audit.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            if (tof is null)
                warning = "本次为人工登记（无信号快照），接线指纹为空，运行期不受 wiring_suspect 守卫";

            loggerFactory.CreateLogger("Acceptance").LogInformation(
                "通道验收 device={DeviceId} ch={Ch} method={Method} tof={Tof} by user={UserId}",
                deviceId, channelNo, method, tof, user.UserId);

            return Results.Ok(new
            {
                acceptanceId, deviceId, channelNo, channelId, method,
                boltId, boltCode, tofNs = tof, peakToPeak = p2p, snrDb = snr,
                positionLabel = pos, warning
            });
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithTags("Acceptance")
        .WithName("AcceptChannel")
        .WithSummary("确认通道接入验收：落验收记录 + 声时指纹快照（operator+，审计）");

        // ==================== 验收清单 / 报告 ====================
        var query = app.MapGroup("/api/devices/{deviceId:long}/acceptance")
            .WithTags("Acceptance")
            .RequireAuthorization(AuthPolicies.LoggedIn);

        query.MapGet("/", async (long deviceId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var items = new List<object>();
            var accepted = 0;
            await using var cmd = ds.CreateCommand(AcceptanceListSql);
            cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var hasAcceptance = !r.IsDBNull(5);
                if (hasAcceptance) accepted++;
                items.Add(new
                {
                    channelNo = r.GetInt16(0),
                    channelId = r.GetInt64(1),
                    channelName = r.IsDBNull(2) ? null : r.GetString(2),
                    boltId = r.IsDBNull(3) ? (long?)null : r.GetInt64(3),
                    boltCode = r.IsDBNull(4) ? null : r.GetString(4),
                    acceptedAt = r.IsDBNull(5) ? (DateTime?)null : r.GetDateTime(5),
                    method = r.IsDBNull(6) ? null : r.GetString(6),
                    cableLabel = r.IsDBNull(7) ? null : r.GetString(7),
                    tofNs = r.IsDBNull(8) ? (decimal?)null : r.GetDecimal(8),
                    acceptedBy = r.IsDBNull(9) ? null : r.GetString(9),
                    note = r.IsDBNull(10) ? null : r.GetString(10),
                });
            }
            return Results.Ok(new { deviceId, total = items.Count, accepted, items });
        })
        .WithName("GetAcceptanceStatus")
        .WithSummary("验收清单：每个活跃通道的最新验收记录与覆盖率");

        query.MapGet("/export.csv", async (long deviceId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            string deviceCode;
            await using (var d = ds.CreateCommand("SELECT code FROM devices WHERE id = @id AND deleted_at IS NULL"))
            {
                d.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, deviceId);
                if (await d.ExecuteScalarAsync(ct) is not string code)
                    return Results.NotFound(new { error = "设备不存在", deviceId });
                deviceCode = code;
            }

            var csv = new StringBuilder();
            csv.Append('\uFEFF');
            // 设备编号压成一行：见 CsvExport.Inline —— 注释行上的引号不转义，带 CRLF 的编号能另起一行伪造公式
            csv.AppendLine($"# 通道接入验收报告  设备 {CsvExport.Inline(deviceCode)}  导出时间(UTC) {DateTime.UtcNow:yyyy-MM-dd HH:mm}");
            csv.AppendLine("通道,位置,电缆标签,螺栓编号,验收方式,声时ns,验收人,验收时间(UTC),备注");

            await using var cmd = ds.CreateCommand(AcceptanceListSql);
            cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                csv.Append("CH").Append(reader.GetInt16(0)).Append(',')
                   .Append(Csv(reader.IsDBNull(2) ? "" : reader.GetString(2))).Append(',')
                   .Append(Csv(reader.IsDBNull(7) ? "" : reader.GetString(7))).Append(',')
                   .Append(Csv(reader.IsDBNull(4) ? "" : reader.GetString(4))).Append(',')
                   .Append(reader.IsDBNull(6) ? "未验收" : DisplayLabels.AcceptanceMethod(reader.GetString(6))).Append(',')
                   .Append(reader.IsDBNull(8) ? "" : reader.GetDecimal(8).ToString("F0")).Append(',')
                   .Append(Csv(reader.IsDBNull(9) ? "" : reader.GetString(9))).Append(',')
                   .Append(reader.IsDBNull(5) ? "" : reader.GetDateTime(5).ToString("yyyy-MM-dd HH:mm:ss")).Append(',')
                   .Append(Csv(reader.IsDBNull(10) ? "" : reader.GetString(10))).Append('\n');
            }
            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            return Results.File(bytes, "text/csv; charset=utf-8",
                $"通道验收报告-{CsvExport.FileNameSegment(deviceCode)}-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
        })
        .WithName("ExportAcceptanceReport")
        .WithSummary("导出验收报告 CSV（竣工资料）");

        return app;
    }

    /// <summary>每个活跃通道的最新验收记录（无记录的通道也返回，报告要体现"未验收"）。</summary>
    private const string AcceptanceListSql = @"
        SELECT c.channel_no, c.id, c.name, b.id AS bolt_id, b.code,
               ca.accepted_at, ca.method, ca.cable_label, ca.tof_ns, u.username, ca.note
        FROM channels c
        LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
        LEFT JOIN LATERAL (
            SELECT accepted_at, method, cable_label, tof_ns, accepted_by, note
            FROM channel_acceptance a
            WHERE a.channel_id = c.id
            ORDER BY accepted_at DESC LIMIT 1) ca ON true
        LEFT JOIN users u ON u.id = ca.accepted_by
        WHERE c.device_id = @d AND c.deleted_at IS NULL
        ORDER BY c.channel_no";

    private static object SessionView(AcceptanceSession s, ChannelPresenceOptions opt) => new
    {
        active = true,
        deviceId = s.DeviceId,
        channels = s.Channels.Select(c => (int)c).ToArray(),
        startedAt = s.StartedAt,
        expiresAt = s.ExpiresAt,
        remainingSeconds = Math.Max(0, (int)(s.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds),
        // 会话内单通道刷新周期估算（前端提示"插入确认约 N 秒"）
        estimatedRefreshSeconds = Math.Max(1, (int)Math.Ceiling(s.Channels.Count * Math.Max(10, opt.PerChannelMeasureMs) / 1000.0)),
        startedByUserId = s.StartedByUserId
    };

    /// <summary>CSV 字段转义。实现见 <see cref="CsvExport.Field"/>（含公式注入防护）。</summary>
    internal static string Csv(string s) => CsvExport.Field(s);
}

/// <summary>POST /acceptance-session 请求体。</summary>
public sealed record AcceptanceSessionRequest(List<int>? Channels = null, int? TtlMinutes = null);

/// <summary>POST /channels/{no}/accept 请求体。</summary>
public sealed record ChannelAcceptRequest(
    string? Method = null,
    string? CableLabel = null,
    string? PositionLabel = null,
    string? Note = null);
