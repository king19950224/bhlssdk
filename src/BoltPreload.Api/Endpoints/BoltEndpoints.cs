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

        // 螺栓列表（含当前状态：active calibration / 最近一次测量）
        grp.MapGet("/", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            const string sql = @"
                SELECT b.id, b.code, b.nominal_diam, b.grade, b.target_preload_n, b.position_label, b.state,
                       c.id AS channel_id, c.channel_no, c.k_l, c.ref_time_ns, c.ref_temp_c,
                       last_m.ts AS last_ts, last_m.preload_n, last_m.preload_ratio, last_m.echo_time_ns, last_m.sp_temp_c,
                       b.min_preload_n,
                       b.material, b.elastic_modulus_gpa, b.sound_velocity_ms, b.effective_length_mm, b.cross_section_mm2, b.install_torque_nm
                FROM bolts b
                JOIN channels c ON c.id = b.channel_id
                LEFT JOIN LATERAL (
                    SELECT ts, preload_n, preload_ratio, echo_time_ns, sp_temp_c
                    FROM measurements m
                    WHERE m.bolt_id = b.id
                    ORDER BY ts DESC LIMIT 1
                ) last_m ON true
                WHERE b.deleted_at IS NULL
                ORDER BY b.id";

            await using var cmd = ds.CreateCommand(sql);
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
            return Results.Ok(new { count = items.Count, items });
        })
        .WithName("ListBolts")
        .WithSummary("螺栓列表（含标定状态 + 最近一次测量）");

        // 趋势查询（按时间窗，可下采样）
        grp.MapGet("/{boltId:long}/preload-trend", async (
            long boltId, NpgsqlDataSource ds,
            DateTime? since, DateTime? until, string? bucket, int? maxPoints,
            CancellationToken ct) =>
        {
            // 时间窗口默认最近 1 小时
            var sinceUtc = since?.ToUniversalTime() ?? DateTime.UtcNow.AddHours(-1);
            var untilUtc = until?.ToUniversalTime() ?? DateTime.UtcNow;
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

            // TimescaleDB time_bucket 聚合：avg/min/max preload，count 数据点
            const string sql = @"
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
                ORDER BY b ASC
                LIMIT @max";

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

        // 到限预测（权威口径）：固定窗口线性回归求下降速率 + R² 置信度 + 预计到限天数。
        // 总览"即将到限"与详情页共用同法同窗口，避免两处给出不一致的天数。
        grp.MapGet("/{boltId:long}/prediction", async (
            long boltId, int? windowDays, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var win = Math.Clamp(windowDays ?? 7, 1, 90);
            await using var cmd = ds.CreateCommand(@"
                WITH w AS (
                    SELECT preload_n, ts FROM measurements
                    WHERE bolt_id = @id AND preload_n IS NOT NULL AND ts >= now() - make_interval(days => @win)
                ),
                agg AS (
                    SELECT count(*) n,
                           regr_slope(preload_n, extract(epoch FROM ts)) slope,
                           regr_r2(preload_n, extract(epoch FROM ts)) r2
                    FROM w
                ),
                cur AS ( SELECT preload_n FROM w ORDER BY ts DESC LIMIT 1 )
                SELECT b.target_preload_n, b.min_preload_n, b.state, agg.n, agg.slope, agg.r2, cur.preload_n
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

            double? ratePerDay = slope.HasValue ? slope.Value * 86400.0 : null;
            // 平稳带：日变化幅度 < 目标的 0.1% 视为平稳（避免噪声被当趋势）
            double stableBand = (double)(target ?? cur ?? 100000m) * 0.001;
            string trend;
            if (n < 5 || !ratePerDay.HasValue) trend = "insufficient";
            else if (ratePerDay.Value < -stableBand) trend = "falling";
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
                FROM bolts b JOIN channels c ON c.id = b.channel_id
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
            if (req.MinPreloadN is > 0 && req.TargetPreloadN is > 0 && req.MinPreloadN > req.TargetPreloadN)
                return Results.BadRequest(new { error = "报警下限不能大于目标预紧力" });

            string code;
            await using (var cmd = ds.CreateCommand(@"
                UPDATE bolts SET
                    target_preload_n   = COALESCE(@target, target_preload_n),
                    min_preload_n      = COALESCE(@min, min_preload_n),
                    material           = COALESCE(@material, material),
                    elastic_modulus_gpa= COALESCE(@e, elastic_modulus_gpa),
                    sound_velocity_ms  = COALESCE(@v, sound_velocity_ms),
                    effective_length_mm= COALESCE(@len, effective_length_mm),
                    cross_section_mm2  = COALESCE(@area, cross_section_mm2),
                    install_torque_nm  = COALESCE(@torque, install_torque_nm),
                    -- 位置标签：null=不改；空串=清空(置 null)；有值=设置。便于「通道识别」纠正/清空标签。
                    position_label     = CASE WHEN @pos IS NULL THEN position_label ELSE NULLIF(@pos, '') END,
                    nominal_diam       = COALESCE(@nd, nominal_diam),
                    grade              = COALESCE(@grade, grade),
                    updated_at = now()
                WHERE id=@id AND deleted_at IS NULL
                RETURNING code"))
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

            await using (var audit = ds.CreateCommand(@"
                INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
                VALUES (@uid, 'update_bolt_spec', 'bolt', @id, NULL, @after::jsonb, @note, now())"))
            {
                audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                audit.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, boltId);
                audit.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb, System.Text.Json.JsonSerializer.Serialize(req));
                audit.Parameters.AddWithValue("note", NpgsqlDbType.Text, $"更新螺栓 {code} 预紧力设置（目标/下限/规格）");
                await audit.ExecuteNonQueryAsync(ct);
            }

            // 失效解析器缓存：目标/规格变更必须让热路径立刻重读（缓存默认 5min TTL）。
            // 否则旧值仍在用——例如旧的过小 target_preload_n 会让 preload_ratio 持续超量程。
            await using (var look = ds.CreateCommand(
                "SELECT c.device_id, c.channel_no FROM channels c JOIN bolts b ON b.channel_id=c.id WHERE b.id=@id AND c.deleted_at IS NULL"))
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
}

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
