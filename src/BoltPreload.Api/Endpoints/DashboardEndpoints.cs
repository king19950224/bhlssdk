using BoltPreload.Api.Auth;
using Npgsql;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 总览聚合：以螺栓预紧力健康为中心（全局分布 + 按部位 + 松动 Top + 告警），
/// 取代原先以采集速率/设备在线为主的 IT 遥测视角。系统遥测由 /api/runtime/devices 提供，前端组合。
/// </summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dashboard/preload-health", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var sql = $@"
                WITH {AssetEndpoints.BoltStatusCte} SELECT {AssetEndpoints.StatusAgg} FROM bolt_status;

                WITH {AssetEndpoints.BoltStatusCte}
                SELECT t.name AS turbine, mg.name AS grp, {AssetEndpoints.StatusAgg}
                FROM bolt_status bs
                JOIN turbines t ON t.id = bs.turbine_id
                JOIN monitor_groups mg ON mg.id = bs.group_id
                GROUP BY t.name, mg.name
                ORDER BY t.name, mg.name;

                WITH {AssetEndpoints.BoltStatusCte}
                SELECT b.code, t.name, mg.name, bs.preload_n, bs.preload_ratio, b.min_preload_n, bs.status, b.id
                FROM bolt_status bs
                JOIN bolts b ON b.id = bs.id
                JOIN turbines t ON t.id = bs.turbine_id
                JOIN monitor_groups mg ON mg.id = bs.group_id
                WHERE bs.status IN ('loose', 'warn')
                ORDER BY bs.preload_ratio ASC NULLS LAST
                LIMIT 8;

                SELECT severity, count(*) FROM alarms WHERE cleared_at IS NULL GROUP BY severity;

                WITH {AssetEndpoints.BoltStatusCte}
                SELECT
                    count(*) FILTER (WHERE preload_ratio >= 1.0) AS b100,
                    count(*) FILTER (WHERE preload_ratio >= 0.9 AND preload_ratio < 1.0) AS b90,
                    count(*) FILTER (WHERE preload_ratio >= 0.8 AND preload_ratio < 0.9) AS b80,
                    count(*) FILTER (WHERE preload_ratio >= 0.7 AND preload_ratio < 0.8) AS b70,
                    count(*) FILTER (WHERE preload_ratio < 0.7) AS b70d
                FROM bolt_status WHERE status IN ('normal','warn','loose') AND preload_ratio IS NOT NULL;

                WITH recent AS (
                    SELECT bolt_id, regr_slope(preload_n, extract(epoch FROM ts)) AS slope,
                           regr_r2(preload_n, extract(epoch FROM ts)) AS r2, count(*) n
                    FROM measurements
                    WHERE preload_n IS NOT NULL AND ts >= now() - interval '7 days'
                    GROUP BY bolt_id HAVING count(*) >= 5
                ), {AssetEndpoints.BoltStatusCte}
                SELECT b.code, t.name, mg.name, bs.preload_n, b.min_preload_n, bs.preload_ratio, b.id,
                       (rc.slope)::double precision AS slope_sec,
                       ((bs.preload_n - b.min_preload_n) / (-rc.slope * 86400.0))::double precision AS days_to_limit,
                       (rc.r2)::double precision AS r2
                FROM bolt_status bs
                JOIN bolts b ON b.id = bs.id
                JOIN turbines t ON t.id = bs.turbine_id
                JOIN monitor_groups mg ON mg.id = bs.group_id
                JOIN recent rc ON rc.bolt_id = b.id
                WHERE bs.status IN ('normal','warn','loose') AND bs.preload_n IS NOT NULL
                  AND b.min_preload_n IS NOT NULL AND bs.preload_n > b.min_preload_n
                  AND rc.slope * 86400 < -(COALESCE(b.target_preload_n, bs.preload_n) * 0.001)
                ORDER BY days_to_limit ASC
                LIMIT 8;";

            await using var cmd = ds.CreateCommand(sql);
            await using var r = await cmd.ExecuteReaderAsync(ct);

            object global = new { total = 0L, normal = 0L, warn = 0L, loose = 0L, uncalibrated = 0L, nodata = 0L };
            if (await r.ReadAsync(ct)) global = AssetEndpoints.Health(r, 0);

            var byGroup = new List<object>();
            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct))
                byGroup.Add(new { turbine = r.GetString(0), group = r.GetString(1), health = AssetEndpoints.Health(r, 2) });

            var topLoose = new List<object>();
            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct))
                topLoose.Add(new
                {
                    boltCode = r.GetString(0),
                    turbine = r.GetString(1),
                    group = r.GetString(2),
                    preloadN = r.IsDBNull(3) ? (decimal?)null : r.GetDecimal(3),
                    preloadRatio = r.IsDBNull(4) ? (decimal?)null : r.GetDecimal(4),
                    minPreloadN = r.IsDBNull(5) ? (decimal?)null : r.GetDecimal(5),
                    status = r.GetString(6),
                    boltId = r.GetInt64(7),
                });

            var alarmsBySeverity = new List<object>();
            long openAlarms = 0;
            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var n = r.GetInt64(1);
                openAlarms += n;
                alarmsBySeverity.Add(new { severity = r.GetString(0), count = n });
            }

            // 残余率分布（按 当前/目标 分桶，仅计已标定且有近期数据的螺栓）
            object residualDist = new { b100 = 0L, b90 = 0L, b80 = 0L, b70 = 0L, b70d = 0L };
            await r.NextResultAsync(ct);
            if (await r.ReadAsync(ct))
                residualDist = new
                {
                    b100 = r.GetInt64(0),
                    b90 = r.GetInt64(1),
                    b80 = r.GetInt64(2),
                    b70 = r.GetInt64(3),
                    b70d = r.GetInt64(4),
                };

            // 即将到限：近 7 天呈下降趋势、当前仍高于下限，按预计到限天数升序
            var approachingLimit = new List<object>();
            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct))
                approachingLimit.Add(new
                {
                    boltCode = r.GetString(0),
                    turbine = r.GetString(1),
                    group = r.GetString(2),
                    preloadN = r.IsDBNull(3) ? (decimal?)null : r.GetDecimal(3),
                    minPreloadN = r.IsDBNull(4) ? (decimal?)null : r.GetDecimal(4),
                    preloadRatio = r.IsDBNull(5) ? (decimal?)null : r.GetDecimal(5),
                    boltId = r.GetInt64(6),
                    ratePerDay = r.IsDBNull(7) ? (double?)null : r.GetDouble(7) * 86400.0,
                    daysToLimit = r.IsDBNull(8) ? (double?)null : r.GetDouble(8),
                    r2 = r.IsDBNull(9) ? (double?)null : r.GetDouble(9),
                });

            return Results.Ok(new { global, byGroup, topLoose, openAlarms, alarmsBySeverity, residualDist, approachingLimit });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithTags("Dashboard")
        .WithName("PreloadHealth")
        .WithSummary("总览：螺栓预紧力健康聚合");

        return app;
    }
}
