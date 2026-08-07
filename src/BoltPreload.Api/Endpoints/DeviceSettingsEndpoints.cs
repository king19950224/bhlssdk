using BoltPreload.Api.Auth;
using BoltPreload.Api.Runtime;
using BoltPreload.Application.Ingestion;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 设备设置：默认监测组（探头在位自动建档的归属组）。
/// <para>
/// “检测即螺栓”的前提是系统知道新螺栓归到哪个组（bolts.group_id NOT NULL）：
/// 本端点提供设置入口；设置成功后立即对该设备当前【在位且未建档】的通道补建档——
/// 用户不必重新插拔探头，设置完列表秒级出现螺栓。
/// </para>
/// </summary>
public static class DeviceSettingsEndpoints
{
    public static IEndpointRouteBuilder MapDeviceSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/devices/{deviceId:long}/settings")
            .WithTags("Devices");

        // ---------- 读设置（含同项目可选组列表，直接驱动下拉） ----------
        grp.MapGet("/", async (long deviceId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            long projectId;
            long? defaultGroupId;
            await using (var cmd = ds.CreateCommand(
                "SELECT project_id, default_group_id FROM devices WHERE id = @id AND deleted_at IS NULL"))
            {
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, deviceId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    return Results.NotFound(new { error = "设备不存在", deviceId });
                projectId = r.GetInt64(0);
                defaultGroupId = r.IsDBNull(1) ? null : r.GetInt64(1);
            }

            var groups = new List<object>();
            string? defaultGroupCode = null;
            await using (var cmd = ds.CreateCommand(
                "SELECT id, code, name FROM monitor_groups WHERE project_id = @p AND deleted_at IS NULL ORDER BY code"))
            {
                cmd.Parameters.AddWithValue("p", NpgsqlDbType.Bigint, projectId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var id = r.GetInt64(0);
                    var code = r.GetString(1);
                    if (id == defaultGroupId) defaultGroupCode = code;
                    groups.Add(new { id, code, name = r.GetString(2) });
                }
            }

            return Results.Ok(new { deviceId, projectId, defaultGroupId, defaultGroupCode, availableGroups = groups });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("GetDeviceSettings")
        .WithSummary("设备设置：默认监测组 + 同项目可选组列表");

        // ---------- 设置默认监测组 ----------
        grp.MapPatch("/", async (
            long deviceId,
            DeviceSettingsPatch? req,
            HttpContext http,
            NpgsqlDataSource ds,
            BoltAutoEnroller enroller,
            IOptions<ChannelPresenceOptions> presenceOpt,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (req is null) return Results.BadRequest(new { error = "请求体必填" });
            var user = UserContext.Require(http);
            var logger = loggerFactory.CreateLogger("DeviceSettings");

            long? oldGroupId;
            string? newGroupCode = null;
            await using (var conn = await ds.OpenConnectionAsync(ct))
            await using (var tx = await conn.BeginTransactionAsync(ct))
            {
                long projectId;
                await using (var cmd = new NpgsqlCommand(
                    "SELECT project_id, default_group_id FROM devices WHERE id = @id AND deleted_at IS NULL FOR UPDATE", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, deviceId);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    if (!await r.ReadAsync(ct))
                        return Results.NotFound(new { error = "设备不存在", deviceId });
                    projectId = r.GetInt64(0);
                    oldGroupId = r.IsDBNull(1) ? null : r.GetInt64(1);
                }

                if (req.DefaultGroupId is long gid)
                {
                    await using var cmd = new NpgsqlCommand(
                        "SELECT code, project_id FROM monitor_groups WHERE id = @gid AND deleted_at IS NULL", conn, tx);
                    cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, gid);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    if (!await r.ReadAsync(ct))
                        return Results.NotFound(new { error = "监测组不存在", groupId = gid });
                    if (r.GetInt64(1) != projectId)
                        return Results.BadRequest(new { error = "监测组与设备不在同一项目，禁止跨项目建档", groupId = gid });
                    newGroupCode = r.GetString(0);
                }

                await using (var cmd = new NpgsqlCommand(
                    "UPDATE devices SET default_group_id = @gid, updated_at = now() WHERE id = @id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, (object?)req.DefaultGroupId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, deviceId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await using (var audit = new NpgsqlCommand(@"
                    INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at, ip_addr)
                    VALUES (@uid, 'device_default_group_update', 'device', @did, @before::jsonb, @after::jsonb, @note, now(), @ip)", conn, tx))
                {
                    audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                    audit.Parameters.AddWithValue("did", NpgsqlDbType.Bigint, deviceId);
                    audit.Parameters.AddWithValue("before", NpgsqlDbType.Jsonb,
                        System.Text.Json.JsonSerializer.Serialize(new { defaultGroupId = oldGroupId }));
                    audit.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb,
                        System.Text.Json.JsonSerializer.Serialize(new { defaultGroupId = req.DefaultGroupId, code = newGroupCode }));
                    audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                        $"设备默认监测组：{oldGroupId?.ToString() ?? "未设置"} → {(req.DefaultGroupId is null ? "未设置" : $"{req.DefaultGroupId}({newGroupCode})")}（自动建档归属组）");
                    AuditClientIp.Bind(audit.Parameters);
                    await audit.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
            }

            // 事务外补建档：把当前已在位但没建档的通道立刻补上（不必重新插拔触发新事件）。
            // 失败不影响设置结果——enroller 自带重试队列，且下次插拔事件也会建。
            var enrolled = new List<object>();
            if (req.DefaultGroupId is not null && presenceOpt.Value.AutoEnroll)
            {
                var presentUnbound = new List<short>();
                await using (var cmd = ds.CreateCommand(@"
                    SELECT c.channel_no FROM channels c
                    WHERE c.device_id = @d AND c.deleted_at IS NULL
                      AND c.hardware_state = ANY(@states)
                      AND NOT EXISTS (SELECT 1 FROM bolts b WHERE b.channel_id = c.id AND b.deleted_at IS NULL)
                    ORDER BY c.channel_no"))
                {
                    cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
                    cmd.Parameters.AddWithValue("states", NpgsqlDbType.Array | NpgsqlDbType.Varchar, BoltAutoEnroller.PresentStates);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) presentUnbound.Add(r.GetInt16(0));
                }
                foreach (var chNo in presentUnbound)
                {
                    var e = await enroller.TryEnrollAsync(deviceId, (ushort)chNo, ct);
                    if (e is not null) enrolled.Add(new { channelNo = (int)chNo, boltId = e.BoltId, code = e.Code });
                }
            }

            logger.LogInformation("设备默认监测组更新 device={DeviceId} group={GroupId}({Code}) 补建档 {Count} 颗 by user={UserId}",
                deviceId, req.DefaultGroupId, newGroupCode ?? "-", enrolled.Count, user.UserId);

            return Results.Ok(new
            {
                deviceId,
                defaultGroupId = req.DefaultGroupId,
                defaultGroupCode = newGroupCode,
                enrolledBolts = enrolled
            });
        })
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithName("PatchDeviceSettings")
        .WithSummary("设置设备默认监测组（自动建档归属组；设置后对在位未建档通道立即补建档）");

        return app;
    }
}

/// <summary>PATCH /settings 请求体。defaultGroupId=null 表示清除默认组（自动建档将暂停并提示设置）。</summary>
public sealed record DeviceSettingsPatch(long? DefaultGroupId);
