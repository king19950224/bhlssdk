using BoltPreload.Api.Auth;
using BoltPreload.Api.Runtime;
using BoltPreload.Application.Devices;
using BoltPreload.Application.Ingestion;
using BoltPreload.Application.Licensing;
using BoltPreload.Fx;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 设备生命周期管理（/api/devices，Admin）：列表、新增、试连、改参、启停、软删。
/// 此前加一台设备只能手工 SQL 插行再重启进程；现在全链路不重启：
/// 插行建目录 → 运行时热加（IngestionService 对账 2 秒内拉起采集）→ 授权容量热重估。
/// 禁用/删除做连带清理：停采集断 TCP、闭合名下 signal_lost（cleared_reason=device_disabled，
/// 不清会因监测窗滑出而永久挂开）、按设备失效路由缓存。preload 类告警有意不动——
/// 螺栓还在法兰上，只是数据停了，保持最后已知状态比伪装成"已恢复"诚实。
/// 硬删不提供：channels.device_id 外键无级联，历史测量按通道挂着，删行只会爆 FK。
/// </summary>
public static class DeviceEndpoints
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TestConnectTimeout = TimeSpan.FromSeconds(5);

    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/devices")
            .WithTags("Devices")
            .RequireAuthorization(AuthPolicies.AdminAccess);

        // ---------- 表视角列表（含禁用/软删筛选）+ 运行时叠加 ----------
        grp.MapGet("/", async (
            NpgsqlDataSource ds, IDeviceConnectorManager manager, IngestionTaskRegistry registry,
            bool? includeDeleted, CancellationToken ct) =>
        {
            var sql = @"
                SELECT d.id, d.code, d.model, d.comm_type, d.comm_params, d.address, d.transfer_mode,
                       d.channel_count, d.enabled, d.state, d.deleted_at, d.created_at, p.code,
                       (SELECT count(*) FROM channels c WHERE c.device_id = d.id AND c.deleted_at IS NULL)
                FROM devices d
                JOIN projects p ON p.id = d.project_id"
                + (includeDeleted == true ? "" : " WHERE d.deleted_at IS NULL")
                + " ORDER BY d.id";
            await using var cmd = ds.CreateCommand(sql);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt64(0);
                var rt = manager.Find(id);
                items.Add(new
                {
                    id,
                    code = r.GetString(1),
                    model = r.GetInt16(2),
                    commType = r.GetInt16(3),
                    commParams = r.GetString(4),
                    address = r.GetInt16(5),
                    transferMode = r.GetInt16(6),
                    channelCount = r.GetInt16(7),
                    enabled = r.GetBoolean(8),
                    state = r.GetString(9),
                    deletedAt = r.IsDBNull(10) ? null : (DateTime?)r.GetDateTime(10),
                    createdAt = r.GetDateTime(11),
                    projectCode = r.GetString(12),
                    activeChannels = r.GetInt64(13),
                    inRuntime = rt is not null,
                    isPrimary = rt?.IsPrimary ?? false,
                    connectorState = rt?.Connector.State.ToString(),
                    isIngesting = registry.IsRunning(id),
                });
            }
            return Results.Ok(new { count = items.Count, items });
        })
        .WithName("ListDevicesAdmin")
        .WithSummary("设备表视角列表（含禁用；includeDeleted=true 看软删行）+ 运行时叠加");

        // ---------- 新增：插行 + 建通道目录 + 热加运行时 + 容量重估 ----------
        grp.MapPost("/", async (
            HttpContext http, DeviceCreateRequest req, NpgsqlDataSource ds,
            IDeviceConnectorManager manager, ILicenseService license, LicenseEnforcementState enforcement,
            CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            var err = ValidateDeviceParams(req.Code, req.CommParams, req.ChannelCount, req.Model, req.CommType, req.Address);
            if (err is not null) return Results.BadRequest(new { error = err });

            // 容量预检：超限直接 409，不让配置先落库再被 LicenseGate 把所有写请求 403 掉
            var status = license.GetStatus();
            var capacity = LicenseCapacity.CheckAdd(
                manager.Runtimes.Count, manager.CountActiveChannels(), req.ChannelCount, status.MaxDevices, status.MaxChannels);
            if (capacity is not null)
                return Results.Conflict(new { error = $"license_capacity_exceeded：{capacity}" });

            // 项目归属：单项目环境不必指定；多项目必须显式给
            long projectId;
            if (req.ProjectId is { } pid)
            {
                projectId = pid;
            }
            else
            {
                var ids = new List<long>();
                await using (var q = ds.CreateCommand("SELECT id FROM projects WHERE deleted_at IS NULL ORDER BY id"))
                await using (var pr = await q.ExecuteReaderAsync(ct))
                    while (await pr.ReadAsync(ct)) ids.Add(pr.GetInt64(0));
                if (ids.Count != 1)
                    return Results.BadRequest(new { error = $"当前有 {ids.Count} 个项目，必须显式指定 projectId" });
                projectId = ids[0];
            }

            long deviceId;
            await using (var conn = await ds.OpenConnectionAsync(ct))
            await using (var tx = await conn.BeginTransactionAsync(ct))
            {
                try
                {
                    await using var cmd = new NpgsqlCommand(@"
                        INSERT INTO devices (project_id, code, model, comm_type, comm_params, address, transfer_mode, channel_count, state)
                        VALUES (@p, @code, @model, @ctype, @comm, @addr, @tm, @n, 'unknown')
                        RETURNING id", conn, tx);
                    cmd.Parameters.AddWithValue("p", NpgsqlDbType.Bigint, projectId);
                    cmd.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, req.Code.Trim());
                    cmd.Parameters.AddWithValue("model", NpgsqlDbType.Smallint, (short)req.Model);
                    cmd.Parameters.AddWithValue("ctype", NpgsqlDbType.Smallint, (short)req.CommType);
                    cmd.Parameters.AddWithValue("comm", NpgsqlDbType.Varchar, req.CommParams.Trim());
                    cmd.Parameters.AddWithValue("addr", NpgsqlDbType.Smallint, (short)req.Address);
                    cmd.Parameters.AddWithValue("tm", NpgsqlDbType.Smallint, (short)req.TransferMode);
                    cmd.Parameters.AddWithValue("n", NpgsqlDbType.Smallint, (short)req.ChannelCount);
                    deviceId = (long)(await cmd.ExecuteScalarAsync(ct))!;
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Conflict(new { error = $"设备编码 '{req.Code}' 在该项目下已存在" });
                }

                // 通道目录 1..N 一次建全（与 channel-catalog 重建端点的补插同一套语义）
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO channels (device_id, channel_no, state, business_state, hardware_state)
                    SELECT @id, gs.ch, 'manual', 'unbound', 'unknown'
                    FROM generate_series(1, @n) AS gs(ch)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, deviceId);
                    cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, req.ChannelCount);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await WriteDeviceAuditAsync(conn, tx, u.UserId, "device_create", deviceId,
                    $"新建设备 '{req.Code.Trim()}'（{req.CommParams.Trim()}，{req.ChannelCount} 通道）{req.Note ?? ""}".Trim(), ct);
                await tx.CommitAsync(ct);
            }

            var (attached, runtimeNote) = await TryAddRuntimeAsync(manager, deviceId, ct);
            LicenseCapacity.Reevaluate(manager, license, enforcement);

            return Results.Ok(new
            {
                id = deviceId,
                channelsCreated = req.ChannelCount,
                runtimeAttached = attached,
                note = runtimeNote ?? (attached ? "采集将在数秒内自动拉起（对账循环）" : null),
            });
        })
        .WithName("CreateDevice")
        .WithSummary("新增设备：插行 + 建通道目录 + 热加运行时（不重启进程），前置授权容量预检");

        // ---------- 试连：临时连接器，用完即弃 ----------
        grp.MapPost("/{id:long}/test-connection", async (
            long id, NpgsqlDataSource ds, IDeviceConnectorManager manager, ILoggerFactory lf, CancellationToken ct) =>
        {
            short model, commType, address, transferMode, channelCount;
            string commParams;
            await using (var cmd = ds.CreateCommand(@"
                SELECT model, comm_type, comm_params, address, transfer_mode, channel_count
                FROM devices WHERE id = @id AND deleted_at IS NULL"))
            {
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    return Results.NotFound(new { error = "设备不存在", id });
                model = r.GetInt16(0);
                commType = r.GetInt16(1);
                commParams = r.GetString(2);
                address = r.GetInt16(3);
                transferMode = r.GetInt16(4);
                channelCount = r.GetInt16(5);
            }

            // 试连另开一条 TCP：串口服务器多是单连接位，正在采集的设备会被踢线——运行中慎用
            var warning = manager.Find(id) is not null
                ? "该设备正在采集中，试连会抢占其 TCP 连接位，可能造成短暂断线重连"
                : null;

            var spec = new FxRealDeviceSpec(id, (ushort)model, (byte)commType, commParams,
                (ushort)address, (ushort)transferMode, (ushort)Math.Clamp((int)channelCount, 1, DeviceLimits.MaxChannelsPerDevice));
            var connector = FxConnectorFactory.CreateReal(spec, [], lf.CreateLogger<FxSdkConnector>());
            var ok = false;
            string? error = null;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TestConnectTimeout);
                await connector.ConnectAsync(timeoutCts.Token);
                ok = await FxConnectorWait.UntilConnectedAsync(connector, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                error = $"连接超时（{TestConnectTimeout.TotalSeconds:0} 秒）";
            }
            catch (Exception ex)
            {
                // 试连失败可能含 Socket/DNS 细节，不回传给前端
                error = "连接失败，详情见系统日志";
                lf.CreateLogger("Device").LogWarning(ex, "设备试连失败 id={Id}", id);
            }
            finally
            {
                await connector.DisposeAsync();
            }
            return Results.Ok(new { id, ok, state = connector.State.ToString(), error, warning });
        })
        .WithName("TestDeviceConnection")
        .WithSummary("试连（临时连接器 5 秒超时，不入运行时，用完即弃）");

        // ---------- 改参：落表 + 在跑则热重启；可转移 primary ----------
        grp.MapPatch("/{id:long}", async (
            long id, HttpContext http, DeviceUpdateRequest req, NpgsqlDataSource ds,
            IDeviceConnectorManager manager, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            if (req.CommParams is not null && string.IsNullOrWhiteSpace(req.CommParams))
                return Results.BadRequest(new { error = "CommParams 不能为空白" });
            if (req.Address is < 1 or > 255)
                return Results.BadRequest(new { error = "Address 必须在 1..255" });

            var connChanged = false;
            await using (var conn = await ds.OpenConnectionAsync(ct))
            await using (var tx = await conn.BeginTransactionAsync(ct))
            {
                string? oldComm = null;
                short oldAddr = 0, oldTm = 0;
                await using (var q = new NpgsqlCommand(
                    "SELECT comm_params, address, transfer_mode FROM devices WHERE id = @id AND deleted_at IS NULL FOR UPDATE", conn, tx))
                {
                    q.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                    await using var r = await q.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct))
                    {
                        oldComm = r.GetString(0);
                        oldAddr = r.GetInt16(1);
                        oldTm = r.GetInt16(2);
                    }
                }
                if (oldComm is null)
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "设备不存在", id });
                }

                var newComm = req.CommParams?.Trim() ?? oldComm;
                var newAddr = (short)(req.Address ?? oldAddr);
                var newTm = (short)(req.TransferMode ?? oldTm);
                connChanged = newComm != oldComm || newAddr != oldAddr || newTm != oldTm;

                await using (var cmd = new NpgsqlCommand(@"
                    UPDATE devices SET comm_params = @comm, address = @addr, transfer_mode = @tm, updated_at = now()
                    WHERE id = @id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("comm", NpgsqlDbType.Varchar, newComm);
                    cmd.Parameters.AddWithValue("addr", NpgsqlDbType.Smallint, newAddr);
                    cmd.Parameters.AddWithValue("tm", NpgsqlDbType.Smallint, newTm);
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await WriteDeviceAuditAsync(conn, tx, u.UserId, "device_update", id,
                    $"更新设备连接参数：{oldComm}/{oldAddr} → {newComm}/{newAddr}{(req.MakePrimary ? "，转为主设备" : "")}", ct);
                await tx.CommitAsync(ct);
            }

            var primaryTransferred = false;
            if (req.MakePrimary)
            {
                primaryTransferred = manager.TransferPrimary(id);
                if (!primaryTransferred)
                    return Results.Conflict(new { error = "设备不在运行时中（禁用或 Simulated 模式），无法转移主设备", id });
            }

            var restarted = false;
            string? note = null;
            if (connChanged && manager.Find(id) is not null)
            {
                try
                {
                    restarted = await manager.RestartAsync(id, StopTimeout, ct);
                }
                catch (NotSupportedException)
                {
                    note = "Simulated 模式：连接参数已落库，运行时不变";
                }
            }
            return Results.Ok(new { id, updated = true, connChanged, restarted, primaryTransferred, note });
        })
        .WithName("UpdateDevice")
        .WithSummary("改连接参数（在跑则热重启连接）；makePrimary=true 转移主设备（运行时概念，重启进程后按配置重选）");

        // ---------- 启用 ----------
        grp.MapPost("/{id:long}/enable", async (
            long id, HttpContext http, NpgsqlDataSource ds,
            IDeviceConnectorManager manager, ILicenseService license, LicenseEnforcementState enforcement,
            CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            var status = license.GetStatus();

            await using (var conn = await ds.OpenConnectionAsync(ct))
            await using (var tx = await conn.BeginTransactionAsync(ct))
            {
                short? channelCount = null;
                var alreadyEnabled = false;
                await using (var q = new NpgsqlCommand(
                    "SELECT channel_count, enabled FROM devices WHERE id = @id AND deleted_at IS NULL FOR UPDATE", conn, tx))
                {
                    q.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                    await using var r = await q.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct))
                    {
                        channelCount = r.GetInt16(0);
                        alreadyEnabled = r.GetBoolean(1);
                    }
                }
                if (channelCount is null)
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "设备不存在或已删除", id });
                }
                if (alreadyEnabled)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Ok(new { id, enabled = true, note = "设备本就处于启用状态" });
                }

                var capacity = LicenseCapacity.CheckAdd(
                    manager.Runtimes.Count, manager.CountActiveChannels(), channelCount.Value, status.MaxDevices, status.MaxChannels);
                if (capacity is not null)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Conflict(new { error = $"license_capacity_exceeded：{capacity}" });
                }

                await using (var cmd = new NpgsqlCommand(
                    "UPDATE devices SET enabled = true, updated_at = now() WHERE id = @id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await WriteDeviceAuditAsync(conn, tx, u.UserId, "device_enable", id, $"启用设备 #{id}", ct);
                await tx.CommitAsync(ct);
            }

            var (attached, runtimeNote) = await TryAddRuntimeAsync(manager, id, ct);
            LicenseCapacity.Reevaluate(manager, license, enforcement);
            return Results.Ok(new { id, enabled = true, runtimeAttached = attached, note = runtimeNote });
        })
        .WithName("EnableDevice")
        .WithSummary("启用设备（容量预检 + 热加运行时，采集数秒内自动拉起）");

        // ---------- 禁用：停采集断 TCP + 连带清理 ----------
        grp.MapPost("/{id:long}/disable", async (
            long id, HttpContext http, NpgsqlDataSource ds,
            IDeviceConnectorManager manager, IngestionTaskRegistry registry, IChannelResolver resolver,
            ILicenseService license, LicenseEnforcementState enforcement, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            if (manager.Find(id) is { IsPrimary: true })
                return Results.Conflict(new { error = "primary 设备不许禁用：先把主设备转移给另一台启用设备（PATCH makePrimary）", id });

            int closedSignalLost;
            await using (var conn = await ds.OpenConnectionAsync(ct))
            await using (var tx = await conn.BeginTransactionAsync(ct))
            {
                var found = false;
                var alreadyDisabled = false;
                await using (var q = new NpgsqlCommand(
                    "SELECT enabled FROM devices WHERE id = @id AND deleted_at IS NULL FOR UPDATE", conn, tx))
                {
                    q.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                    await using var r = await q.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct))
                    {
                        found = true;
                        alreadyDisabled = !r.GetBoolean(0);
                    }
                }
                if (!found)
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "设备不存在或已删除", id });
                }
                if (alreadyDisabled)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Ok(new { id, disabled = true, note = "设备本就处于禁用状态" });
                }

                await using (var cmd = new NpgsqlCommand(
                    "UPDATE devices SET enabled = false, updated_at = now() WHERE id = @id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // 闭合名下开启的 signal_lost：数据停摆是人主动禁用的预期结果，不闭合的话
                // 螺栓滑出监测窗后这些告警永久挂开。preload 类告警有意不动（真实状态未知，保持最后已知）。
                await using (var cmd = new NpgsqlCommand(@"
                    UPDATE alarms SET cleared_at = now(), cleared_by = @uid, cleared_reason = 'device_disabled'
                    WHERE type = 'signal_lost' AND cleared_at IS NULL
                      AND bolt_id IN (
                          SELECT b.id FROM bolts b
                          JOIN channels c ON c.id = b.channel_id
                          WHERE c.device_id = @id)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, u.UserId);
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                    closedSignalLost = await cmd.ExecuteNonQueryAsync(ct);
                }

                await WriteDeviceAuditAsync(conn, tx, u.UserId, "device_disable", id,
                    $"禁用设备 #{id}，连带闭合 {closedSignalLost} 条 signal_lost", ct);
                await tx.CommitAsync(ct);
            }

            var taskStopped = true;
            string? note = null;
            try
            {
                await manager.RemoveAndStopAsync(id, StopTimeout, ct);
                taskStopped = !registry.IsRunning(id);
            }
            catch (NotSupportedException)
            {
                note = "Simulated 模式：仅落库，运行时不变";
            }

            resolver.InvalidateDevice(id);
            LicenseCapacity.Reevaluate(manager, license, enforcement);
            return Results.Ok(new { id, disabled = true, closedSignalLost, taskStopped, note });
        })
        .WithName("DisableDevice")
        .WithSummary("禁用设备：停采集断 TCP、闭合名下 signal_lost、失效路由缓存、容量重估");

        // ---------- 软删（前置条件：已禁用） ----------
        grp.MapDelete("/{id:long}", async (
            long id, HttpContext http, NpgsqlDataSource ds, IChannelResolver resolver, CancellationToken ct) =>
        {
            var u = UserContext.Require(http);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            bool? enabled = null;
            string? code = null;
            await using (var q = new NpgsqlCommand(
                "SELECT enabled, code FROM devices WHERE id = @id AND deleted_at IS NULL FOR UPDATE", conn, tx))
            {
                q.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                await using var r = await q.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    enabled = r.GetBoolean(0);
                    code = r.GetString(1);
                }
            }
            if (enabled is null)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "设备不存在或已删除", id });
            }
            if (enabled.Value)
            {
                await tx.RollbackAsync(ct);
                return Results.Conflict(new { error = "先禁用再删除：删除是终态动作，必须先经过停采集的禁用步骤", id });
            }

            await using (var cmd = new NpgsqlCommand(
                "UPDATE devices SET deleted_at = now(), updated_at = now() WHERE id = @id", conn, tx))
            {
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await WriteDeviceAuditAsync(conn, tx, u.UserId, "device_delete", id, $"软删设备 '{code}'（#{id}）", ct);
            await tx.CommitAsync(ct);

            resolver.InvalidateDevice(id);
            return Results.Ok(new { id, deleted = true });
        })
        .WithName("DeleteDevice")
        .WithSummary("软删设备（必须先禁用；历史测量按通道保留，不提供硬删）");

        return app;
    }

    // ==================================================================== helpers

    private static async Task<(bool Attached, string? Note)> TryAddRuntimeAsync(
        IDeviceConnectorManager manager, long deviceId, CancellationToken ct)
    {
        try
        {
            return (await manager.AddAsync(deviceId, ct), null);
        }
        catch (NotSupportedException)
        {
            return (false, "Simulated 模式：仅落库，运行时不变（模拟设备数量由 Fx:Simulator:DeviceCount 决定）");
        }
    }

    private static string? ValidateDeviceParams(string code, string commParams, int channelCount, int model, int commType, int address)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 64)
            return "Code 必填且不超过 64 字符";
        if (string.IsNullOrWhiteSpace(commParams) || commParams.Trim().Length > 128)
            return "CommParams 必填且不超过 128 字符（如 192.168.1.50:23）";
        if (channelCount is < 1 or > DeviceLimits.MaxChannelsPerDevice)
            return $"ChannelCount 必须在 1..{DeviceLimits.MaxChannelsPerDevice}";
        if (model is < 0 or > 3)
            return "Model 必须在 0..3（SDK 设备型号）";
        if (commType is < 0 or > 2)
            return "CommType 必须是 0 串口 / 1 TCPClient / 2 TCPServer";
        if (address is < 1 or > 255)
            return "Address 必须在 1..255";
        return null;
    }

    private static async Task WriteDeviceAuditAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        long userId, string action, long deviceId, string note, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at, ip_addr)
            VALUES (@user_id, @action, 'device', @target_id, NULL, NULL, @note, now(), @ip)", conn, tx);
        cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, userId);
        cmd.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action);
        cmd.Parameters.AddWithValue("target_id", NpgsqlDbType.Bigint, deviceId);
        cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, note);
        AuditClientIp.Bind(cmd.Parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// 新建设备。ProjectId 单项目环境可不给（自动取唯一活跃项目）；
/// 通道目录按 ChannelCount 一次建全，后续扩容走 channel-catalog 重建端点。
/// </summary>
public sealed record DeviceCreateRequest(
    string Code,
    string CommParams,
    int ChannelCount,
    int Model = 3,
    int CommType = 1,
    int Address = 1,
    int TransferMode = 1,
    long? ProjectId = null,
    string? Note = null);

/// <summary>
/// 更新设备连接参数（给了的字段才改）。MakePrimary=true 把主设备转移到本设备——
/// 运行时概念不落库，进程重启后按 Fx:Real:DeviceId 配置或 id 最小行重选。
/// </summary>
public sealed record DeviceUpdateRequest(
    string? CommParams = null,
    int? Address = null,
    int? TransferMode = null,
    bool MakePrimary = false);
