using BoltPreload.Api.Auth;
using BoltPreload.Api.Runtime;
using BoltPreload.Application.Ingestion;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 通道维度的管理接口：列表 + 改名。
/// 改名变更走 audit_log，绑定/解绑走 channel_binding_history（不在本类，另有 endpoint）。
/// </summary>
public static class ChannelEndpoints
{
    public static IEndpointRouteBuilder MapChannelEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/devices/{deviceId:long}/channels")
            .WithTags("Channels");

        grp.MapGet("/", async (
            long deviceId,
            NpgsqlDataSource ds,
            CancellationToken ct) =>
        {
            const string sql = @"
                SELECT c.id, c.channel_no, c.name, c.state, c.sampling_enabled,
                       c.hardware_state, c.business_state, c.last_seen_at,
                       b.id AS bolt_id, b.code AS bolt_code
                FROM channels c
                LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                WHERE c.device_id = @device_id AND c.deleted_at IS NULL
                ORDER BY c.channel_no";
            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new
                {
                    channelId = r.GetInt64(0),
                    channelNo = r.GetInt16(1),
                    name = r.IsDBNull(2) ? null : r.GetString(2),
                    state = r.GetString(3),
                    samplingEnabled = r.GetBoolean(4),
                    hardwareState = r.GetString(5),
                    businessState = r.GetString(6),
                    lastSeenAt = r.IsDBNull(7) ? null : (DateTime?)r.GetDateTime(7),
                    bolt = r.IsDBNull(8) ? null : (object)new
                    {
                        id = r.GetInt64(8),
                        code = r.IsDBNull(9) ? null : r.GetString(9)
                    }
                });
            }
            return Results.Ok(new { deviceId, count = items.Count, items });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("ListDeviceChannels")
        .WithSummary("设备通道列表（含位置名称、硬件状态、当前绑定）");

        grp.MapPatch("/{channelNo:int}/name", async (
            long deviceId, int channelNo,
            ChannelNamePatchRequest? body,
            HttpContext http,
            NpgsqlDataSource ds,
            IChannelResolver resolver,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (channelNo < 1 || channelNo > DeviceLimits.MaxChannelsPerDevice)
                return Results.BadRequest(new { error = $"channelNo 超出 1..{DeviceLimits.MaxChannelsPerDevice} 范围" });

            var raw = body?.Name?.Trim();
            // 空字符串 = 清除名称（恢复成 NULL）
            string? newName = string.IsNullOrEmpty(raw) ? null : raw;
            if (newName is not null && newName.Length > 64)
                return Results.BadRequest(new { error = "name 长度不能超过 64 字符" });

            // 改名 + 审计放同一事务（类注释承诺"改名走 audit_log"，此前只打了进程日志，UI 无法追溯）
            long updatedId;
            string? oldName = null;
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            const string sql = @"
                UPDATE channels c
                   SET name = @name, updated_at = now()
                  FROM (SELECT id, name AS old_name FROM channels
                        WHERE device_id = @device_id AND channel_no = @ch AND deleted_at IS NULL) prev
                 WHERE c.id = prev.id
                RETURNING c.id, prev.old_name";
            await using (var cmd = new NpgsqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, (object?)newName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
                cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Smallint, (short)channelNo);
                var found = false;
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    if (await r.ReadAsync(ct))
                    {
                        found = true;
                        updatedId = r.GetInt64(0);
                        oldName = r.IsDBNull(1) ? null : r.GetString(1);
                    }
                    else updatedId = 0;
                }
                if (!found)
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "通道不存在或已删除", deviceId, channelNo });
                }
            }

            var user = UserContext.Require(http);
            await using (var audit = new NpgsqlCommand(@"
                INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
                VALUES (@uid, 'channel_rename', 'channel', @cid,
                        jsonb_build_object('name', @old::text), jsonb_build_object('name', @new::text),
                        @note, now())", conn, tx))
            {
                audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                audit.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, updatedId);
                audit.Parameters.AddWithValue("old", NpgsqlDbType.Text, (object?)oldName ?? DBNull.Value);
                audit.Parameters.AddWithValue("new", NpgsqlDbType.Text, (object?)newName ?? DBNull.Value);
                audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                    $"通道改名 device={deviceId} CH{channelNo}：'{oldName ?? "(空)"}' → '{newName ?? "(空)"}'");
                await audit.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);

            // resolver 缓存按 (deviceId, channelNo) 失效一下，下次解析就拿到新 name
            resolver.Invalidate(deviceId, (ushort)channelNo);

            var logger = loggerFactory.CreateLogger("ChannelName");
            logger.LogInformation(
                "通道改名 device={DeviceId} ch={Ch} name='{Name}'",
                deviceId, channelNo, newName ?? "(null)");

            return Results.Ok(new { channelId = updatedId, deviceId, channelNo, name = newName });
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("PatchChannelName")
        .WithSummary("修改通道可读名称（物理位置标签）");

        return app;
    }
}

/// <summary>改通道显示名，就是现场用来认物理位置的那个标签。</summary>
public sealed class ChannelNamePatchRequest
{
    public string? Name { get; set; }
}
