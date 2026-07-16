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

                -- 未闭合告警口径必须与告警中心 /api/alarms/summary 完全一致（仅按 bolts 软删过滤）。
                -- 原先额外 JOIN monitor_groups/turbines 软删过滤：机组软删但螺栓未级联时，
                -- 总览计数会吃掉这部分告警 → 「总览显示正常、告警中心却有未闭合 critical」的假安全。
                SELECT a.severity, count(*) FROM alarms a
                JOIN bolts b ON b.id = a.bolt_id AND b.deleted_at IS NULL
                WHERE a.cleared_at IS NULL GROUP BY a.severity;

                WITH {AssetEndpoints.BoltStatusCte}
                SELECT
                    -- b100 上界封到 1.10：超张力(high, >110%)不能算进「达标」，否则与健康分布的「偏高」档自相矛盾
                    count(*) FILTER (WHERE preload_ratio >= 1.0 AND preload_ratio <= 1.10) AS b100,
                    count(*) FILTER (WHERE preload_ratio >= 0.9 AND preload_ratio < 1.0) AS b90,
                    count(*) FILTER (WHERE preload_ratio >= 0.8 AND preload_ratio < 0.9) AS b80,
                    count(*) FILTER (WHERE preload_ratio >= 0.7 AND preload_ratio < 0.8) AS b70,
                    count(*) FILTER (WHERE preload_ratio < 0.7) AS b70d,
                    count(*) FILTER (WHERE preload_ratio > 1.10) AS bhigh
                FROM bolt_status WHERE status IN ('normal','warn','loose','high') AND preload_ratio IS NOT NULL;

                WITH cal AS (
                    -- 只用「当前生效」的标定（未被 superseded、且已会签 approved / 自签 self_signed）来定沉降期起点；
                    -- 否则被顶替 / 驳回 / 待复核的记录会把窗口起点错误前移，把有效回归历史饿死成 insufficient。
                    SELECT bolt_id, max(created_at) AS calibrated_at
                    FROM calibration_records
                    WHERE superseded_by IS NULL AND review_state IN ('approved','self_signed')
                    GROUP BY bolt_id
                ), recent AS (
                    SELECT m.bolt_id,
                           regr_slope(m.preload_n, extract(epoch FROM m.ts)) AS slope,
                           regr_r2(m.preload_n, extract(epoch FROM m.ts)) AS r2, count(*) n
                    FROM measurements m
                    LEFT JOIN cal ON cal.bolt_id = m.bolt_id
                    -- 拟合窗口夹到「上次标定 + 2h 沉降期」之后：避免跨标定边界（基线/kL 变化造成的台阶）
                    -- 与新标定后嵌入松弛瞬态污染斜率；刚标定不久（历史不足）自然被下面的门挡掉。
                    WHERE m.preload_n IS NOT NULL
                      AND m.ts >= GREATEST(now() - interval '7 days',
                                           COALESCE(cal.calibrated_at + interval '2 hours', now() - interval '7 days'))
                    GROUP BY m.bolt_id
                    -- 质量门：≥30 样本、跨度≥24h（按天外推才有意义）、R²≥0.5（确为线性下降而非抖动），杜绝几分钟数据外推出的垃圾天数
                    HAVING count(*) >= 30
                       AND (max(m.ts) - min(m.ts)) >= interval '24 hours'
                       AND regr_r2(m.preload_n, extract(epoch FROM m.ts)) >= 0.5
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
            object residualDist = new { b100 = 0L, b90 = 0L, b80 = 0L, b70 = 0L, b70d = 0L, bhigh = 0L };
            await r.NextResultAsync(ct);
            if (await r.ReadAsync(ct))
                residualDist = new
                {
                    b100 = r.GetInt64(0),
                    b90 = r.GetInt64(1),
                    b80 = r.GetInt64(2),
                    b70 = r.GetInt64(3),
                    b70d = r.GetInt64(4),
                    bhigh = r.GetInt64(5),
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

        // 时序趋势：驱动总览的 sparkline 与健康趋势主图。measurements 是 TimescaleDB hypertable，
        // 用 time_bucket 聚合残余率(avg/min)、采集量、在测螺栓数；告警按 triggered_at 单独分桶后并入。
        app.MapGet("/api/dashboard/trend", async (NpgsqlDataSource ds, int? hours, CancellationToken ct) =>
        {
            var h = Math.Clamp(hours ?? 24, 1, 168);
            var bucketMin = h <= 24 ? 30 : h <= 72 ? 60 : 180;
            // JOIN bolts 排除已软删螺栓的历史测点/告警：否则删螺栓后趋势线仍统计它，
            // avg_ratio / active_bolts 与当前在测资产对不上（软删口径与 bolt_status CTE 一致）
            var sql = $@"
                SELECT time_bucket(make_interval(mins => {bucketMin}), m.ts) AS bucket,
                       (avg(m.preload_ratio) FILTER (WHERE m.preload_ratio IS NOT NULL))::double precision AS avg_ratio,
                       (min(m.preload_ratio) FILTER (WHERE m.preload_ratio IS NOT NULL))::double precision AS min_ratio,
                       count(*) AS meas_count,
                       count(DISTINCT m.bolt_id) FILTER (WHERE m.preload_ratio IS NOT NULL) AS active_bolts
                FROM measurements m
                JOIN bolts b ON b.id = m.bolt_id AND b.deleted_at IS NULL
                WHERE m.ts > now() - make_interval(hours => {h})
                GROUP BY bucket ORDER BY bucket;

                SELECT time_bucket(make_interval(mins => {bucketMin}), a.triggered_at) AS bucket, count(*)
                FROM alarms a
                JOIN bolts b ON b.id = a.bolt_id AND b.deleted_at IS NULL
                WHERE a.triggered_at > now() - make_interval(hours => {h})
                GROUP BY bucket ORDER BY bucket;";

            await using var cmd = ds.CreateCommand(sql);
            await using var r = await cmd.ExecuteReaderAsync(ct);

            var rows = new List<(DateTime Ts, double? Avg, double? Min, long Count, long Bolts)>();
            while (await r.ReadAsync(ct))
                rows.Add((
                    r.GetDateTime(0),
                    r.IsDBNull(1) ? null : r.GetDouble(1),
                    r.IsDBNull(2) ? null : r.GetDouble(2),
                    r.GetInt64(3),
                    r.GetInt64(4)));

            var alarmMap = new Dictionary<DateTime, long>();
            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct))
                alarmMap[r.GetDateTime(0)] = r.GetInt64(1);

            var points = rows.Select(p => new
            {
                ts = p.Ts,
                avgRatio = p.Avg,
                minRatio = p.Min,
                count = p.Count,
                activeBolts = p.Bolts,
                alarms = alarmMap.TryGetValue(p.Ts, out var a) ? a : 0L,
            });

            return Results.Ok(new { bucketMinutes = bucketMin, hours = h, points });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithTags("Dashboard")
        .WithName("DashboardTrend")
        .WithSummary("总览：残余率/采集/告警时序趋势");

        return app;
    }
}
