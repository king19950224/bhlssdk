using BoltPreload.Api.Auth;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 资产层级监测：项目 → 风机 → 部位 → 螺栓，各级带预紧力健康汇总。
/// 这是操作员的主视角(资产/预紧力)，与设备/通道(硬件视角)正交。
/// </summary>
public static class AssetEndpoints
{
    // 每螺栓预紧力状态（相对安全下限 min_preload_n）：
    //   uncalibrated 未标定 / nodata 无近期数据 / loose 松动(<下限) / warn 预警(<下限×1.1) / normal 正常
    internal const string BoltStatusCte = @"
        bolt_status AS (
            SELECT b.id, b.group_id, mg.turbine_id, t.project_id,
                   l.preload_n, l.preload_ratio, l.ts AS last_ts,
                   CASE
                     WHEN b.state <> 'calibrated' OR c.k_l IS NULL THEN 'uncalibrated'
                     WHEN l.preload_n IS NULL OR l.ts < now() - interval '10 minutes' THEN 'nodata'
                     WHEN b.min_preload_n IS NOT NULL AND l.preload_n < b.min_preload_n THEN 'loose'
                     WHEN b.min_preload_n IS NOT NULL AND l.preload_n < b.min_preload_n * 1.1 THEN 'warn'
                     ELSE 'normal'
                   END AS status
            FROM bolts b
            JOIN channels c ON c.id = b.channel_id
            JOIN monitor_groups mg ON mg.id = b.group_id AND mg.deleted_at IS NULL
            JOIN turbines t ON t.id = mg.turbine_id AND t.deleted_at IS NULL
            LEFT JOIN LATERAL (
                SELECT preload_n, preload_ratio, ts FROM measurements m
                WHERE m.bolt_id = b.id ORDER BY ts DESC LIMIT 1
            ) l ON true
            WHERE b.deleted_at IS NULL
        )";

    internal const string StatusAgg = @"
        COALESCE(count(*),0) AS total,
        count(*) FILTER (WHERE status='normal') AS normal,
        count(*) FILTER (WHERE status='warn') AS warn,
        count(*) FILTER (WHERE status='loose') AS loose,
        count(*) FILTER (WHERE status='uncalibrated') AS uncalibrated,
        count(*) FILTER (WHERE status='nodata') AS nodata";

    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api").WithTags("Assets").RequireAuthorization(AuthPolicies.LoggedIn);

        // 项目列表
        grp.MapGet("/projects", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand($@"
                WITH {BoltStatusCte}
                SELECT p.id, p.code, p.name, p.industry, p.location,
                       (SELECT count(*) FROM turbines t WHERE t.project_id=p.id AND t.deleted_at IS NULL) AS turbines,
                       COALESCE(bs.total,0), COALESCE(bs.normal,0), COALESCE(bs.warn,0), COALESCE(bs.loose,0),
                       COALESCE(bs.uncalibrated,0), COALESCE(bs.nodata,0),
                       (SELECT count(*) FROM alarms a JOIN bolts b2 ON b2.id=a.bolt_id
                          JOIN monitor_groups mg2 ON mg2.id=b2.group_id JOIN turbines t2 ON t2.id=mg2.turbine_id
                        WHERE t2.project_id=p.id AND a.cleared_at IS NULL) AS open_alarms
                FROM projects p
                LEFT JOIN (SELECT project_id, {StatusAgg} FROM bolt_status GROUP BY project_id) bs ON bs.project_id=p.id
                WHERE p.deleted_at IS NULL AND p.enabled
                ORDER BY p.name");
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new
                {
                    id = r.GetInt64(0), code = r.GetString(1), name = r.GetString(2),
                    industry = r.GetString(3), location = r.IsDBNull(4) ? null : r.GetString(4),
                    turbines = r.GetInt64(5),
                    health = Health(r, 6),
                    openAlarms = r.GetInt64(12),
                });
            return Results.Ok(new { count = items.Count, items });
        }).WithName("ListProjects");

        // 项目下风机
        grp.MapGet("/projects/{projectId:long}/turbines", async (long projectId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand($@"
                WITH {BoltStatusCte}
                SELECT t.id, t.code, t.name, t.model, t.capacity_kw, t.location, t.state,
                       (SELECT count(*) FROM monitor_groups mg WHERE mg.turbine_id=t.id AND mg.deleted_at IS NULL) AS groups,
                       COALESCE(bs.total,0), COALESCE(bs.normal,0), COALESCE(bs.warn,0), COALESCE(bs.loose,0),
                       COALESCE(bs.uncalibrated,0), COALESCE(bs.nodata,0),
                       (SELECT count(*) FROM alarms a JOIN bolts b2 ON b2.id=a.bolt_id JOIN monitor_groups mg2 ON mg2.id=b2.group_id
                        WHERE mg2.turbine_id=t.id AND a.cleared_at IS NULL) AS open_alarms
                FROM turbines t
                LEFT JOIN (SELECT turbine_id, {StatusAgg} FROM bolt_status GROUP BY turbine_id) bs ON bs.turbine_id=t.id
                WHERE t.project_id=@pid AND t.deleted_at IS NULL
                ORDER BY t.code");
            cmd.Parameters.AddWithValue("pid", NpgsqlDbType.Bigint, projectId);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new
                {
                    id = r.GetInt64(0), code = r.GetString(1), name = r.GetString(2),
                    model = r.IsDBNull(3) ? null : r.GetString(3),
                    capacityKw = r.IsDBNull(4) ? (int?)null : r.GetInt32(4),
                    location = r.IsDBNull(5) ? null : r.GetString(5),
                    state = r.GetString(6),
                    groups = r.GetInt64(7),
                    health = Health(r, 8),
                    openAlarms = r.GetInt64(14),
                });
            return Results.Ok(new { count = items.Count, items });
        }).WithName("ListTurbines");

        // 风机下部位
        grp.MapGet("/turbines/{turbineId:long}/groups", async (long turbineId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand($@"
                WITH {BoltStatusCte}
                SELECT mg.id, mg.code, mg.name, mg.structure_type, mg.expected_bolt_count, mg.target_preload_n,
                       COALESCE(bs.total,0), COALESCE(bs.normal,0), COALESCE(bs.warn,0), COALESCE(bs.loose,0),
                       COALESCE(bs.uncalibrated,0), COALESCE(bs.nodata,0),
                       (SELECT count(*) FROM alarms a JOIN bolts b2 ON b2.id=a.bolt_id
                        WHERE b2.group_id=mg.id AND a.cleared_at IS NULL) AS open_alarms
                FROM monitor_groups mg
                LEFT JOIN (SELECT group_id, {StatusAgg} FROM bolt_status GROUP BY group_id) bs ON bs.group_id=mg.id
                WHERE mg.turbine_id=@tid AND mg.deleted_at IS NULL
                ORDER BY mg.code");
            cmd.Parameters.AddWithValue("tid", NpgsqlDbType.Bigint, turbineId);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new
                {
                    id = r.GetInt64(0), code = r.GetString(1), name = r.GetString(2),
                    structureType = r.IsDBNull(3) ? null : r.GetString(3),
                    expectedBoltCount = r.IsDBNull(4) ? (int?)null : r.GetInt16(4),
                    targetPreloadN = r.IsDBNull(5) ? (decimal?)null : r.GetDecimal(5),
                    health = Health(r, 6),
                    openAlarms = r.GetInt64(12),
                });
            return Results.Ok(new { count = items.Count, items });
        }).WithName("ListGroups");

        // 部位下螺栓（监测网格）
        grp.MapGet("/monitor-groups/{groupId:long}/bolts", async (long groupId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand($@"
                WITH {BoltStatusCte}
                SELECT b.id, b.code, b.nominal_diam, b.grade, b.position_label, b.state,
                       b.target_preload_n, b.min_preload_n,
                       bs.preload_n, bs.preload_ratio, bs.last_ts, bs.status,
                       ch.id, ch.channel_no, d.id, d.code,
                       (SELECT count(*) FROM alarms a WHERE a.bolt_id=b.id AND a.cleared_at IS NULL) AS open_alarms
                FROM bolts b
                JOIN bolt_status bs ON bs.id=b.id
                JOIN channels ch ON ch.id=b.channel_id
                JOIN devices d ON d.id=ch.device_id
                WHERE b.group_id=@gid AND b.deleted_at IS NULL
                ORDER BY b.code");
            cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, groupId);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                items.Add(new
                {
                    id = r.GetInt64(0), code = r.GetString(1),
                    nominalDiam = r.GetString(2), grade = r.GetString(3),
                    positionLabel = r.IsDBNull(4) ? null : r.GetString(4),
                    boltState = r.GetString(5),
                    targetPreloadN = r.IsDBNull(6) ? (decimal?)null : r.GetDecimal(6),
                    minPreloadN = r.IsDBNull(7) ? (decimal?)null : r.GetDecimal(7),
                    preloadN = r.IsDBNull(8) ? (decimal?)null : r.GetDecimal(8),
                    preloadRatio = r.IsDBNull(9) ? (decimal?)null : r.GetDecimal(9),
                    lastTs = r.IsDBNull(10) ? (DateTime?)null : r.GetDateTime(10),
                    status = r.GetString(11),
                    channelId = r.GetInt64(12), channelNo = r.GetInt16(13),
                    deviceId = r.GetInt64(14), deviceCode = r.GetString(15),
                    openAlarms = r.GetInt64(16),
                });
            return Results.Ok(new { count = items.Count, items });
        }).WithName("ListGroupBolts");

        return app;
    }

    // 读取连续 6 列 total/normal/warn/loose/uncalibrated/nodata 为 health 对象
    internal static object Health(NpgsqlDataReader r, int offset) => new
    {
        total = r.GetInt64(offset),
        normal = r.GetInt64(offset + 1),
        warn = r.GetInt64(offset + 2),
        loose = r.GetInt64(offset + 3),
        uncalibrated = r.GetInt64(offset + 4),
        nodata = r.GetInt64(offset + 5),
    };
}
