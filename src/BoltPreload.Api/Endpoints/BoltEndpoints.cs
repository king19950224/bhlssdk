using System.Text;
using BoltPreload.Api.Auth;
using BoltPreload.Application.Ingestion;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 螺栓维度的查询接口：列表 / 详情 / 预紧力趋势。
/// </summary>
public static class BoltEndpoints
{
    public static IEndpointRouteBuilder MapBoltEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/bolts")
            .WithTags("Bolts")
            .RequireAuthorization(AuthPolicies.LoggedIn);

        // 螺栓列表（含当前状态：active calibration / 最近一次测量）。
        // limit 上限护栏：每行带 LATERAL 最新测量，规模上千时无界全量既慢又占内存（当前前端全量拉取，默认值给足余量）。
        grp.MapGet("/", async (NpgsqlDataSource ds, int? limit, int? offset, CancellationToken ct) =>
        {
            var lim = Math.Clamp(limit ?? 1000, 1, 5000);
            var off = Math.Max(offset ?? 0, 0);
            const string sql = @"
                SELECT b.id, b.code, b.nominal_diam, b.grade, b.target_preload_n, b.position_label, b.state,
                       c.id AS channel_id, c.channel_no, c.k_l, c.ref_time_ns, c.ref_temp_c,
                       last_m.ts AS last_ts, last_m.preload_n, last_m.preload_ratio, last_m.echo_time_ns, last_m.sp_temp_c,
                       b.min_preload_n,
                       b.material, b.elastic_modulus_gpa, b.sound_velocity_ms, b.effective_length_mm, b.cross_section_mm2, b.install_torque_nm
                FROM bolts b
                JOIN channels c ON c.id = b.channel_id AND c.deleted_at IS NULL
                LEFT JOIN LATERAL (
                    SELECT ts, preload_n, preload_ratio, echo_time_ns, sp_temp_c
                    FROM measurements m
                    WHERE m.bolt_id = b.id
                    ORDER BY ts DESC LIMIT 1
                ) last_m ON true
                WHERE b.deleted_at IS NULL
                ORDER BY b.id
                LIMIT @lim OFFSET @off";

            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("lim", NpgsqlDbType.Integer, lim);
            cmd.Parameters.AddWithValue("off", NpgsqlDbType.Integer, off);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new
                {
                    id = r.GetInt64(0),
                    code = r.GetString(1),
                    nominalDiam = r.GetString(2),
                    grade = r.GetString(3),
                    targetPreloadN = r.IsDBNull(4) ? null : (decimal?)r.GetDecimal(4),
                    positionLabel = r.IsDBNull(5) ? null : r.GetString(5),
                    state = r.GetString(6),
                    channelId = r.GetInt64(7),
                    channelNo = r.GetInt16(8),
                    calibrated = !r.IsDBNull(9),
                    kL = r.IsDBNull(9) ? null : (decimal?)r.GetDecimal(9),
                    refTimeNs = r.IsDBNull(10) ? null : (decimal?)r.GetDecimal(10),
                    refTempC = r.IsDBNull(11) ? null : (decimal?)r.GetDecimal(11),
                    lastTs = r.IsDBNull(12) ? null : (DateTime?)r.GetDateTime(12),
                    lastPreloadN = r.IsDBNull(13) ? null : (decimal?)r.GetDecimal(13),
                    lastRatio = r.IsDBNull(14) ? null : (decimal?)r.GetDecimal(14),
                    lastEchoTimeNs = r.IsDBNull(15) ? null : (decimal?)r.GetDecimal(15),
                    lastSpTempC = r.IsDBNull(16) ? null : (decimal?)r.GetDecimal(16),
                    minPreloadN = r.IsDBNull(17) ? null : (decimal?)r.GetDecimal(17),
                    material = r.IsDBNull(18) ? null : r.GetString(18),
                    elasticModulusGpa = r.IsDBNull(19) ? null : (decimal?)r.GetDecimal(19),
                    soundVelocityMs = r.IsDBNull(20) ? null : (decimal?)r.GetDecimal(20),
                    effectiveLengthMm = r.IsDBNull(21) ? null : (decimal?)r.GetDecimal(21),
                    crossSectionMm2 = r.IsDBNull(22) ? null : (decimal?)r.GetDecimal(22),
                    installTorqueNm = r.IsDBNull(23) ? null : (decimal?)r.GetDecimal(23)
                });
            }
            return Results.Ok(new { count = items.Count, hasMore = items.Count >= lim, items });
        })
        .WithName("ListBolts")
        .WithSummary("螺栓列表（含标定状态 + 最近一次测量；limit/offset 分页，默认 1000）");

        // 单螺栓详情（与列表同投影）。详情页用它替代"全量拉列表再客户端 find"——
        // 列表有 limit 护栏，规模超过默认页大小后 find 会静默找不到。
        grp.MapGet("/{boltId:long}", async (long boltId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            const string sql = @"
                SELECT b.id, b.code, b.nominal_diam, b.grade, b.target_preload_n, b.position_label, b.state,
                       c.id AS channel_id, c.channel_no, c.k_l, c.ref_time_ns, c.ref_temp_c,
                       last_m.ts AS last_ts, last_m.preload_n, last_m.preload_ratio, last_m.echo_time_ns, last_m.sp_temp_c,
                       b.min_preload_n,
                       b.material, b.elastic_modulus_gpa, b.sound_velocity_ms, b.effective_length_mm, b.cross_section_mm2, b.install_torque_nm
                FROM bolts b
                JOIN channels c ON c.id = b.channel_id AND c.deleted_at IS NULL
                LEFT JOIN LATERAL (
                    SELECT ts, preload_n, preload_ratio, echo_time_ns, sp_temp_c
                    FROM measurements m
                    WHERE m.bolt_id = b.id
                    ORDER BY ts DESC LIMIT 1
                ) last_m ON true
                WHERE b.id = @id AND b.deleted_at IS NULL";

            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, boltId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return Results.NotFound(new { error = "bolt 不存在", boltId });

            return Results.Ok(new
            {
                id = r.GetInt64(0),
                code = r.GetString(1),
                nominalDiam = r.GetString(2),
                grade = r.GetString(3),
                targetPreloadN = r.IsDBNull(4) ? null : (decimal?)r.GetDecimal(4),
                positionLabel = r.IsDBNull(5) ? null : r.GetString(5),
                state = r.GetString(6),
                channelId = r.GetInt64(7),
                channelNo = r.GetInt16(8),
                calibrated = !r.IsDBNull(9),
                kL = r.IsDBNull(9) ? null : (decimal?)r.GetDecimal(9),
                refTimeNs = r.IsDBNull(10) ? null : (decimal?)r.GetDecimal(10),
                refTempC = r.IsDBNull(11) ? null : (decimal?)r.GetDecimal(11),
                lastTs = r.IsDBNull(12) ? null : (DateTime?)r.GetDateTime(12),
                lastPreloadN = r.IsDBNull(13) ? null : (decimal?)r.GetDecimal(13),
                lastRatio = r.IsDBNull(14) ? null : (decimal?)r.GetDecimal(14),
                lastEchoTimeNs = r.IsDBNull(15) ? null : (decimal?)r.GetDecimal(15),
                lastSpTempC = r.IsDBNull(16) ? null : (decimal?)r.GetDecimal(16),
                minPreloadN = r.IsDBNull(17) ? null : (decimal?)r.GetDecimal(17),
                material = r.IsDBNull(18) ? null : r.GetString(18),
                elasticModulusGpa = r.IsDBNull(19) ? null : (decimal?)r.GetDecimal(19),
                soundVelocityMs = r.IsDBNull(20) ? null : (decimal?)r.GetDecimal(20),
                effectiveLengthMm = r.IsDBNull(21) ? null : (decimal?)r.GetDecimal(21),
                crossSectionMm2 = r.IsDBNull(22) ? null : (decimal?)r.GetDecimal(22),
                installTorqueNm = r.IsDBNull(23) ? null : (decimal?)r.GetDecimal(23)
            });
        })
        .WithName("GetBolt")
        .WithSummary("单螺栓详情（与列表同投影；不存在返回 404）");

        // 趋势查询（按时间窗，可下采样）
        grp.MapGet("/{boltId:long}/preload-trend", async (
            long boltId, NpgsqlDataSource ds,
            DateTime? since, DateTime? until, string? bucket, int? maxPoints,
            CancellationToken ct) =>
        {
            // 时间窗口默认最近 1 小时。
            // 无时区后缀的入参按 UTC 解释（API 全线 UTC 口径）：直接 ToUniversalTime() 会把
            // Unspecified 当服务器本地时区换算，同一请求的结果随部署机器时区漂移。
            var sinceUtc = AsUtc(since) ?? DateTime.UtcNow.AddHours(-1);
            var untilUtc = AsUtc(until) ?? DateTime.UtcNow;
            var maxPts = Math.Clamp(maxPoints ?? 500, 10, 5000);

            // bucket = "1s" / "10s" / "1min" / "5min" / "1hour"，或 "auto" 按窗口长度自动选
            var bucketInterval = bucket ?? "auto";
            if (bucketInterval == "auto")
            {
                var spanSeconds = (untilUtc - sinceUtc).TotalSeconds;
                bucketInterval = spanSeconds switch
                {
                    < 600 => "1s",      // < 10min → 1s
                    < 7200 => "10s",    // < 2h → 10s
                    < 86400 => "1min",  // < 24h → 1min
                    < 604800 => "5min", // < 7d → 5min
                    _ => "1hour"
                };
            }

            // bucket 白名单：非法值直接 400，避免把任意字符串塞进 time_bucket(@bucket::interval) 触发 500，
            // 或 '1 microsecond' 之类在大窗口上强制超量聚合（DoS 面）。
            if (bucketInterval is not ("1s" or "10s" or "1min" or "5min" or "1hour"))
                return Results.BadRequest(new { error = "bucket 必须是 1s/10s/1min/5min/1hour 或 auto", bucket });
            if (untilUtc <= sinceUtc)
                return Results.BadRequest(new { error = "until 必须晚于 since" });

            // 螺栓不存在与"存在但无测点"必须可区分（同模块 prediction/settings 均为 404 语义）
            await using (var exists = ds.CreateCommand("SELECT 1 FROM bolts WHERE id = @bid AND deleted_at IS NULL"))
            {
                exists.Parameters.AddWithValue("bid", NpgsqlDbType.Bigint, boltId);
                if (await exists.ExecuteScalarAsync(ct) is null)
                    return Results.NotFound(new { error = "螺栓不存在", boltId });
            }

            // TimescaleDB time_bucket 聚合：avg/min/max preload，count 数据点。
            // 桶数超过 maxPoints 时必须保留【最新】时段：内层 ORDER BY b DESC LIMIT 截最近 N 桶，
            // 外层再升序返回（原来 ASC+LIMIT 静默丢最新数据，7 天窗 + 1min 粒度即触发，趋势图显示陈旧段）。
            const string sql = @"
                SELECT * FROM (
                    SELECT
                        time_bucket(@bucket::interval, ts) AS b,
                        AVG(preload_n)::numeric(12,2) AS avg_preload,
                        MIN(preload_n)::numeric(12,2) AS min_preload,
                        MAX(preload_n)::numeric(12,2) AS max_preload,
                        AVG(preload_ratio)::numeric(6,4) AS avg_ratio,
                        AVG(sp_temp_c)::numeric(6,2) AS avg_temp,
                        AVG(echo_time_ns)::numeric(12,2) AS avg_echo,
                        COUNT(*) AS n,
                        COUNT(preload_n) AS n_valid
                    FROM measurements
                    WHERE bolt_id = @bid AND ts >= @since AND ts < @until
                    GROUP BY b
                    ORDER BY b DESC
                    LIMIT @max
                ) latest
                ORDER BY b ASC";

            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("bid", NpgsqlDbType.Bigint, boltId);
            cmd.Parameters.AddWithValue("since", NpgsqlDbType.TimestampTz, sinceUtc);
            cmd.Parameters.AddWithValue("until", NpgsqlDbType.TimestampTz, untilUtc);
            cmd.Parameters.AddWithValue("bucket", NpgsqlDbType.Varchar, bucketInterval);
            cmd.Parameters.AddWithValue("max", NpgsqlDbType.Integer, maxPts);

            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new
                {
                    ts = r.GetDateTime(0),
                    avgPreloadN = r.IsDBNull(1) ? null : (decimal?)r.GetDecimal(1),
                    minPreloadN = r.IsDBNull(2) ? null : (decimal?)r.GetDecimal(2),
                    maxPreloadN = r.IsDBNull(3) ? null : (decimal?)r.GetDecimal(3),
                    avgRatio = r.IsDBNull(4) ? null : (decimal?)r.GetDecimal(4),
                    avgTempC = r.IsDBNull(5) ? null : (decimal?)r.GetDecimal(5),
                    avgEchoNs = r.IsDBNull(6) ? null : (decimal?)r.GetDecimal(6),
                    n = r.GetInt64(7),
                    nValid = r.GetInt64(8)
                });
            }

            return Results.Ok(new
            {
                boltId,
                since = sinceUtc,
                until = untilUtc,
                bucket = bucketInterval,
                count = items.Count,
                // 数据被下采样截断时提示前端（等于 maxPoints 即可能截断，建议加大 bucket 粒度）
                truncated = items.Count >= maxPts,
                items
            });
        })
        .WithName("BoltPreloadTrend")
        .WithSummary("螺栓预紧力趋势（time_bucket 下采样）")
        .WithDescription(@"
查询参数：
- `since` / `until`：时间窗口（默认最近 1 小时，UTC）
- `bucket`：聚合粒度 `1s`/`10s`/`1min`/`5min`/`1hour`/`auto`（默认 `auto`：按窗口长度自动选）
- `maxPoints`：返回点数上限（默认 500，最大 5000）");

        // 导出趋势数据 CSV（客户做月报/年报的原始数据出口）。与趋势图同源同口径（time_bucket 聚合），
        // 上限放宽到 20000 桶（覆盖 1 年 @1hour 或 2 周 @1min）；带 UTF-8 BOM 让 Excel 正确识别中文。
        grp.MapGet("/{boltId:long}/preload-trend/export.csv", async (
            long boltId, NpgsqlDataSource ds,
            DateTime? since, DateTime? until, string? bucket,
            CancellationToken ct) =>
        {
            var sinceUtc = AsUtc(since) ?? DateTime.UtcNow.AddDays(-7);
            var untilUtc = AsUtc(until) ?? DateTime.UtcNow;
            if (untilUtc <= sinceUtc)
                return Results.BadRequest(new { error = "until 必须晚于 since" });

            var bucketInterval = bucket ?? "auto";
            if (bucketInterval == "auto")
            {
                var spanSeconds = (untilUtc - sinceUtc).TotalSeconds;
                bucketInterval = spanSeconds switch
                {
                    < 7200 => "10s", < 86400 => "1min", < 604800 => "5min", _ => "1hour"
                };
            }
            if (bucketInterval is not ("1s" or "10s" or "1min" or "5min" or "1hour"))
                return Results.BadRequest(new { error = "bucket 必须是 1s/10s/1min/5min/1hour 或 auto", bucket });

            string boltCode;
            await using (var b = ds.CreateCommand("SELECT code FROM bolts WHERE id = @bid AND deleted_at IS NULL"))
            {
                b.Parameters.AddWithValue("bid", NpgsqlDbType.Bigint, boltId);
                if (await b.ExecuteScalarAsync(ct) is not string code)
                    return Results.NotFound(new { error = "螺栓不存在", boltId });
                boltCode = code;
            }

            const string sql = @"
                SELECT time_bucket(@bucket::interval, ts) AS b,
                       AVG(preload_n)::numeric(12,2), MIN(preload_n)::numeric(12,2), MAX(preload_n)::numeric(12,2),
                       AVG(preload_ratio)::numeric(6,4), AVG(sp_temp_c)::numeric(6,2), AVG(echo_time_ns)::numeric(12,2),
                       COUNT(*), COUNT(preload_n)
                FROM measurements
                WHERE bolt_id = @bid AND ts >= @since AND ts < @until
                GROUP BY b ORDER BY b ASC LIMIT 20000";
            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("bid", NpgsqlDbType.Bigint, boltId);
            cmd.Parameters.AddWithValue("since", NpgsqlDbType.TimestampTz, sinceUtc);
            cmd.Parameters.AddWithValue("until", NpgsqlDbType.TimestampTz, untilUtc);
            cmd.Parameters.AddWithValue("bucket", NpgsqlDbType.Varchar, bucketInterval);

            var csv = new StringBuilder();
            csv.Append('\uFEFF');
            csv.AppendLine($"# 螺栓 {boltCode} 预紧力趋势导出  窗口(UTC) {sinceUtc:yyyy-MM-dd HH:mm} ~ {untilUtc:yyyy-MM-dd HH:mm}  聚合 {bucketInterval}");
            csv.AppendLine("时间(UTC),平均预紧力N,最小预紧力N,最大预紧力N,平均保持率,平均温度C,平均声时ns,样本数,有效预紧力数");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                static string D(NpgsqlDataReader rr, int i, int dec) => rr.IsDBNull(i) ? "" : Math.Round(rr.GetDecimal(i), dec).ToString();
                csv.Append(r.GetDateTime(0).ToString("yyyy-MM-dd HH:mm:ss")).Append(',')
                   .Append(D(r, 1, 0)).Append(',').Append(D(r, 2, 0)).Append(',').Append(D(r, 3, 0)).Append(',')
                   .Append(D(r, 4, 4)).Append(',').Append(D(r, 5, 1)).Append(',').Append(D(r, 6, 1)).Append(',')
                   .Append(r.GetInt64(7)).Append(',').Append(r.GetInt64(8)).Append('\n');
            }
            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            return Results.File(bytes, "text/csv; charset=utf-8", $"预紧力趋势-{boltCode}-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
        })
        .WithName("BoltPreloadTrendCsv")
        .WithSummary("导出螺栓预紧力趋势 CSV（月报/年报原始数据）");

        // 到限预测（权威口径）：固定窗口线性回归求下降速率 + R² 置信度 + 预计到限天数。
        // 总览"即将到限"与详情页共用同法同窗口，避免两处给出不一致的天数。
        grp.MapGet("/{boltId:long}/prediction", async (
            long boltId, int? windowDays, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var win = Math.Clamp(windowDays ?? 7, 1, 90);
            await using var cmd = ds.CreateCommand(@"
                WITH cal AS (
                    -- 只用“当前生效”的标定（未被 superseded、且 approved/self_signed 生效）定沉降期起点，
                    -- 排除被顶替 / 驳回 / 待复核记录把窗口错误前移。
                    SELECT max(created_at) AS calibrated_at FROM calibration_records
                    WHERE bolt_id = @id AND superseded_by IS NULL AND review_state IN ('approved','self_signed')
                ),
                w AS (
                    -- 拟合窗口夹到“上次标定 + 2h 沉降期”之后：不跨标定边界、不含嵌入松弛瞬态
                    SELECT preload_n, ts FROM measurements
                    WHERE bolt_id = @id AND preload_n IS NOT NULL
                      AND ts >= GREATEST(now() - make_interval(days => @win),
                                         COALESCE((SELECT calibrated_at FROM cal) + interval '2 hours',
                                                  now() - make_interval(days => @win)))
                ),
                agg AS (
                    SELECT count(*) n,
                           regr_slope(preload_n, extract(epoch FROM ts)) slope,
                           regr_r2(preload_n, extract(epoch FROM ts)) r2,
                           EXTRACT(epoch FROM (max(ts) - min(ts)))::double precision AS span_sec
                    FROM w
                ),
                cur AS ( SELECT preload_n FROM w ORDER BY ts DESC LIMIT 1 )
                SELECT b.target_preload_n, b.min_preload_n, b.state, agg.n, agg.slope, agg.r2, cur.preload_n, agg.span_sec
                FROM bolts b CROSS JOIN agg LEFT JOIN cur ON true
                WHERE b.id = @id AND b.deleted_at IS NULL");
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, boltId);
            cmd.Parameters.AddWithValue("win", NpgsqlDbType.Integer, win);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound(new { error = "螺栓不存在", boltId });

            decimal? target = r.IsDBNull(0) ? null : r.GetDecimal(0);
            decimal? min = r.IsDBNull(1) ? null : r.GetDecimal(1);
            long n = r.GetInt64(3);
            double? slope = r.IsDBNull(4) ? null : r.GetDouble(4);
            double? r2 = r.IsDBNull(5) ? null : r.GetDouble(5);
            decimal? cur = r.IsDBNull(6) ? null : r.GetDecimal(6);
            double spanSec = r.IsDBNull(7) ? 0.0 : r.GetDouble(7);

            double? ratePerDay = slope.HasValue ? slope.Value * 86400.0 : null;
            // 平稳带：日变化幅度 < 目标的 0.1% 视为平稳（避免噪声被当趋势）
            double stableBand = (double)(target ?? cur ?? 100000m) * 0.001;
            // 可信度门（与总览“即将到限”同口径）：样本≥30、跨度≥24h、R²≥0.5，任一不足即 insufficient，
            // 不外推。否则几分钟/几十分钟的标定抖动会算出 -19万 N/天、0 天到限这类垃圾值。
            const long minSamples = 30;
            const double minSpanSec = 24 * 3600;
            const double minR2 = 0.5;
            bool reliable = n >= minSamples && spanSec >= minSpanSec && r2.HasValue && r2.Value >= minR2 && ratePerDay.HasValue;
            string trend;
            if (!reliable) trend = "insufficient";
            else if (ratePerDay!.Value < -stableBand) trend = "falling";
            else if (ratePerDay.Value > stableBand) trend = "rising";
            else trend = "stable";

            double? daysToLimit = null;
            if (trend == "falling" && cur.HasValue && min.HasValue && cur.Value > min.Value && ratePerDay!.Value < 0)
                daysToLimit = (double)(cur.Value - min.Value) / (-ratePerDay.Value);

            return Results.Ok(new
            {
                boltId,
                windowDays = win,
                sampleCount = n,
                current = cur,
                target,
                min,
                ratePerDay,
                r2,
                trend,
                daysToLimit,
            });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("BoltPrediction")
        .WithSummary("到限预测（固定窗口线性回归，权威口径）");

        // 螺栓所在通道的超声测量配置 + 标定基线（驱动 EchoTime 提取与预紧力换算）
        grp.MapGet("/{boltId:long}/channel-config", async (long boltId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand(@"
                SELECT c.device_id, c.channel_no, c.probe_freq_mhz, c.gain, c.threshold, c.meas_mode,
                       c.echo_win_ns, c.tc_mode, c.ref_time_ns, c.ref_temp_c, c.k_l, c.k_l_uncertainty,
                       c.config_source, c.config_synced_at
                FROM bolts b JOIN channels c ON c.id = b.channel_id AND c.deleted_at IS NULL
                WHERE b.id = @id AND b.deleted_at IS NULL");
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, boltId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound(new { error = "螺栓不存在", boltId });
            return Results.Ok(new
            {
                deviceId = r.GetInt64(0),
                channelNo = r.GetInt16(1),
                probeFreqMhz = r.GetDecimal(2),
                gain = r.GetFloat(3),
                threshold = r.GetInt16(4),
                measMode = r.GetInt16(5),
                echoWinNs = r.IsDBNull(6) ? (decimal?)null : r.GetDecimal(6),
                tcMode = r.GetInt16(7),
                refTimeNs = r.IsDBNull(8) ? (decimal?)null : r.GetDecimal(8),
                refTempC = r.IsDBNull(9) ? (decimal?)null : r.GetDecimal(9),
                kL = r.IsDBNull(10) ? (decimal?)null : r.GetDecimal(10),
                kLUncertainty = r.IsDBNull(11) ? (decimal?)null : r.GetDecimal(11),
                configSource = r.IsDBNull(12) ? null : r.GetString(12),
                configSyncedAt = r.IsDBNull(13) ? (DateTime?)null : r.GetDateTime(13),
            });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("BoltChannelConfig")
        .WithSummary("螺栓通道的超声测量配置 + 标定基线");

        // 螺栓预紧力设置：目标预紧力 / 报警下限 / 材料几何（写库元数据,审计;阈值变更可追溯）
        grp.MapPost("/{boltId:long}/settings", async (
            long boltId, BoltSettingsRequest req, HttpContext http, NpgsqlDataSource ds,
            IChannelResolver resolver, CancellationToken ct) =>
        {
            var user = UserContext.Require(http);
            if (req.TargetPreloadN is < 0 || req.MinPreloadN is < 0)
                return Results.BadRequest(new { error = "目标预紧力 / 报警下限不能为负" });
            if (req.MinPreloadN is > 0 && req.TargetPreloadN is > 0 && req.MinPreloadN > req.TargetPreloadN)
                return Results.BadRequest(new { error = "报警下限不能大于目标预紧力" });

            // §0 责任链：阈值变更与审计同事务。报警下限/目标预紧力直接决定何时报警，
            // 改了却没留痕，事后就说不清"当时的阈值是谁设的"。
            string code;
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE bolts SET
                    target_preload_n   = COALESCE(@target, target_preload_n),
                    min_preload_n      = COALESCE(@min, min_preload_n),
                    material           = COALESCE(@material, material),
                    elastic_modulus_gpa= COALESCE(@e, elastic_modulus_gpa),
                    sound_velocity_ms  = COALESCE(@v, sound_velocity_ms),
                    effective_length_mm= COALESCE(@len, effective_length_mm),
                    cross_section_mm2  = COALESCE(@area, cross_section_mm2),
                    install_torque_nm  = COALESCE(@torque, install_torque_nm),
                    -- 位置标签：null=不改；空串=清空(置 null)；有值=设置。便于“通道识别”纠正/清空标签。
                    position_label     = CASE WHEN @pos IS NULL THEN position_label ELSE NULLIF(@pos, '') END,
                    nominal_diam       = COALESCE(@nd, nominal_diam),
                    grade              = COALESCE(@grade, grade),
                    updated_at = now()
                WHERE id=@id AND deleted_at IS NULL
                RETURNING code", conn, tx))
            {
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, boltId);
                cmd.Parameters.AddWithValue("target", NpgsqlDbType.Numeric, (object?)req.TargetPreloadN ?? DBNull.Value);
                cmd.Parameters.AddWithValue("min", NpgsqlDbType.Numeric, (object?)req.MinPreloadN ?? DBNull.Value);
                cmd.Parameters.AddWithValue("material", NpgsqlDbType.Varchar, (object?)req.Material ?? DBNull.Value);
                cmd.Parameters.AddWithValue("e", NpgsqlDbType.Numeric, (object?)req.ElasticModulusGpa ?? DBNull.Value);
                cmd.Parameters.AddWithValue("v", NpgsqlDbType.Numeric, (object?)req.SoundVelocityMs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("len", NpgsqlDbType.Numeric, (object?)req.EffectiveLengthMm ?? DBNull.Value);
                cmd.Parameters.AddWithValue("area", NpgsqlDbType.Numeric, (object?)req.CrossSectionMm2 ?? DBNull.Value);
                cmd.Parameters.AddWithValue("torque", NpgsqlDbType.Numeric, (object?)req.InstallTorqueNm ?? DBNull.Value);
                cmd.Parameters.AddWithValue("pos", NpgsqlDbType.Varchar, (object?)req.PositionLabel ?? DBNull.Value);
                cmd.Parameters.AddWithValue("nd", NpgsqlDbType.Varchar, (object?)req.NominalDiam ?? DBNull.Value);
                cmd.Parameters.AddWithValue("grade", NpgsqlDbType.Varchar, (object?)req.Grade ?? DBNull.Value);
                var scalar = await cmd.ExecuteScalarAsync(ct);
                if (scalar is null) return Results.NotFound(new { error = "螺栓不存在", boltId });
                code = (string)scalar;
            }

            await using (var audit = new NpgsqlCommand(@"
                INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
                VALUES (@uid, 'update_bolt_spec', 'bolt', @id, NULL, @after::jsonb, @note, now())", conn, tx))
            {
                audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                audit.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, boltId);
                audit.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb, System.Text.Json.JsonSerializer.Serialize(req));
                audit.Parameters.AddWithValue("note", NpgsqlDbType.Text, $"更新螺栓 {code} 预紧力设置（目标/下限/规格）");
                await audit.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);

            // 失效解析器缓存：目标/规格变更必须让热路径立刻重读（缓存默认 5min TTL）。
            // 否则旧值仍在用——例如旧的过小 target_preload_n 会让 preload_ratio 持续超量程。
            // 提交后再查，复用同一条连接，别为一次只读再去连接池要一条。
            await using (var look = new NpgsqlCommand(
                "SELECT c.device_id, c.channel_no FROM channels c JOIN bolts b ON b.channel_id=c.id WHERE b.id=@id AND c.deleted_at IS NULL", conn))
            {
                look.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, boltId);
                await using var lr = await look.ExecuteReaderAsync(ct);
                if (await lr.ReadAsync(ct))
                    resolver.Invalidate(lr.GetInt64(0), (ushort)lr.GetInt16(1));
            }

            return Results.Ok(new { boltId, code, updated = true });
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("UpdateBoltSettings")
        .WithSummary("更新螺栓预紧力设置（目标/下限/材料几何,审计）");

        return app;
    }

    /// <summary>
    /// 查询参数时间统一按 UTC 口径：带时区后缀的正常换算；无后缀（Kind=Unspecified）
    /// 视为 UTC，而不是随服务器本地时区解释。
    /// </summary>
    private static DateTime? AsUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Unspecified } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        { } v => v.ToUniversalTime(),
    };
}

/// <summary>改螺栓参数。字段全可空——给了哪个改哪个，没给的不动。</summary>
public sealed record BoltSettingsRequest(
    decimal? TargetPreloadN = null,
    decimal? MinPreloadN = null,
    string? Material = null,
    decimal? ElasticModulusGpa = null,
    decimal? SoundVelocityMs = null,
    decimal? EffectiveLengthMm = null,
    decimal? CrossSectionMm2 = null,
    decimal? InstallTorqueNm = null,
    string? PositionLabel = null,
    string? NominalDiam = null,
    string? Grade = null);
