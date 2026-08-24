using BoltPreload.Api.Auth;
using BoltPreload.Api.Runtime;
using BoltPreload.Calibration;
using BoltPreload.Domain;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>开始一场标定试验。forceSource：manual（人工进样）| serial（串口测力仪守护进程）。</summary>
public sealed record TrialStartRequest(string? ForceSource = null, string? Note = null);

/// <summary>试验期间的一条力值进样（N）。Ts 缺省取服务端当前时刻。</summary>
public sealed record TrialForceSampleRequest(decimal ForceN, DateTimeOffset? Ts = null);

/// <summary>
/// 标定试验模式端点（force-sync-calibration-design.md §5）：连续加载、力-声时同步记录、
/// 力值平台期自动分档出点。取代"逐档拧到位→点按钮→等绿灯"的人工节奏。
/// <para>
/// 分工：本文件只管试验骨架（开始/进样/实时/结束）与"平台+声时双稳→自动出点"；
/// 档位点写 <c>torque_test_points</c>（trial_id 回链），拟合、R² 门槛、生效、会签、报告
/// 全部走 CalibrationEndpoints 既有链路，不新建任何判定逻辑。
/// </para>
/// <para>
/// 出点双判据（缺一不可）：①力值平台（<see cref="ForcePlateauDetector"/>，人工/传感器进样都适用）；
/// ②声时稳定（<see cref="EchoStabilityGuard"/> 实验室判据 0.6ns）。另有声时样本数下限——
/// 48 通道全轮询下单通道约 6s 一条，平台要保持到攒够样本才出点（前端实时提示还差几条）。
/// </para>
/// </summary>
public static class CalibrationTrialEndpoints
{
    /// <summary>出点所需的平台内最少声时样本数。全通道轮询 ~6s/条 → 约 70~80 秒的保持时间。</summary>
    private const int EchoMinSamplesPerPoint = 12;

    /// <summary>力值进样时与声时配对的时间窗（秒）：一个 48 通道轮转周期出头。</summary>
    private const int EchoPairWindowSeconds = 8;

    public static IEndpointRouteBuilder MapCalibrationTrialEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/channels/{channelId:long}/calibration-trials")
            .WithTags("CalibrationTrial")
            .RequireAuthorization(AuthPolicies.ReviewerAccess);

        // ---- 开始试验 ----
        grp.MapPost("/", async (
            long channelId, TrialStartRequest? body, HttpContext http,
            NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var userId = UserContext.Require(http).UserId;
            var source = (body?.ForceSource ?? "manual").Trim().ToLowerInvariant();
            if (source is not ("manual" or "serial"))
                return Results.BadRequest(new { error = "forceSource 仅支持 manual / serial（device_ai 为 P3，需单通道仪器）" });

            // 与阶梯采点同一前置：必须绑定螺栓且有基线（Δt 参考系），否则点的 delta_tof 无从谈起
            long boltId;
            decimal? refTime;
            await using (var cmd = ds.CreateCommand(@"
                SELECT b.id, c.ref_time_ns
                FROM channels c
                JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                WHERE c.id = @cid AND c.deleted_at IS NULL"))
            {
                cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    return Results.NotFound(new { error = $"channel_id={channelId} 不存在或未绑定螺栓", channelId });
                boltId = r.GetInt64(0);
                refTime = r.IsDBNull(1) ? null : r.GetDecimal(1);
            }
            if (refTime is null)
                return Results.BadRequest(new { error = "通道缺少基线（ref_time_ns）——请先在向导完成置零/记录基准，再开始试验" });

            long trialId;
            try
            {
                await using var cmd = ds.CreateCommand(@"
                    INSERT INTO calibration_trials (channel_id, bolt_id, force_source, operator_user_id, note)
                    VALUES (@cid, @bid, @src, @uid, @note)
                    RETURNING id");
                cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                cmd.Parameters.AddWithValue("bid", NpgsqlDbType.Bigint, boltId);
                cmd.Parameters.AddWithValue("src", NpgsqlDbType.Varchar, source);
                cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, userId);
                cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)body?.Note ?? DBNull.Value);
                trialId = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // ux_calibration_trials_running：同通道已有进行中的试验
                long? runningId = null;
                await using (var q = ds.CreateCommand(
                    "SELECT id FROM calibration_trials WHERE channel_id=@cid AND status='running' LIMIT 1"))
                {
                    q.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                    if (await q.ExecuteScalarAsync(ct) is long rid) runningId = rid;
                }
                return Results.Conflict(new { error = "该通道已有进行中的试验（可续走或先中止）", channelId, runningTrialId = runningId });
            }

            await AuditAsync(ds, userId, "trial_start", channelId,
                $"开始标定试验 #{trialId}（{source}）channel={channelId} bolt=#{boltId}", ct);
            return Results.Ok(new { trialId, channelId, boltId, forceSource = source, status = "running" });
        })
        .WithName("StartCalibrationTrial")
        .WithSummary("开始标定试验（连续加载，力值平台自动分档出点）");

        // ---- 进行中的试验（向导断点续走用）----
        grp.MapGet("/running", async (long channelId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand(@"
                SELECT id, force_source, started_at FROM calibration_trials
                WHERE channel_id=@cid AND status='running' LIMIT 1");
            cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound(new { error = "无进行中的试验", channelId });
            return Results.Ok(new { trialId = r.GetInt64(0), forceSource = r.GetString(1), startedAt = r.GetDateTime(2) });
        })
        .WithName("GetRunningCalibrationTrial")
        .WithSummary("查询该通道进行中的标定试验");

        // ---- 力值进样（manual：前端定时上报表读数；serial：守护进程转发）----
        grp.MapPost("/{trialId:long}/force-samples", async (
            long channelId, long trialId, TrialForceSampleRequest? body, HttpContext http,
            NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (body is null || body.ForceN <= 0)
                return Results.BadRequest(new { error = "forceN 必填且必须大于 0（单位 N）" });
            var userId = UserContext.Require(http).UserId;

            var trial = await LoadTrialAsync(ds, channelId, trialId, ct);
            if (trial is null) return Results.NotFound(new { error = "试验不存在或不属于该通道", trialId, channelId });
            if (trial.Status != "running")
                return Results.Conflict(new { error = $"试验已{(trial.Status == "done" ? "结束" : "中止")}，不能继续进样", trialId });

            var ts = body.Ts ?? DateTimeOffset.UtcNow;

            // 声时配对：取该螺栓最近一个轮转周期内的最新测量（全通道轮询 ~6s/条，8s 窗口够一条）
            decimal? echoNs = null, tempC = null;
            await using (var cmd = ds.CreateCommand(@"
                SELECT echo_time_ns, sp_temp_c FROM measurements
                WHERE bolt_id=@bid AND echo_time_ns IS NOT NULL
                  AND ts > @ts - make_interval(secs => @win) AND ts <= @ts + make_interval(secs => 1)
                ORDER BY ts DESC LIMIT 1"))
            {
                cmd.Parameters.AddWithValue("bid", NpgsqlDbType.Bigint, trial.BoltId);
                cmd.Parameters.AddWithValue("ts", NpgsqlDbType.TimestampTz, ts);
                cmd.Parameters.AddWithValue("win", NpgsqlDbType.Double, (double)EchoPairWindowSeconds);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    echoNs = r.IsDBNull(0) ? null : r.GetDecimal(0);
                    tempC = r.IsDBNull(1) ? null : r.GetDecimal(1);
                }
            }

            await using (var cmd = ds.CreateCommand(@"
                INSERT INTO calibration_trial_samples (trial_id, ts, force_n, echo_time_ns, sp_temp_c)
                VALUES (@t, @ts, @f, @echo, @temp)
                ON CONFLICT (trial_id, ts) DO NOTHING"))
            {
                cmd.Parameters.AddWithValue("t", NpgsqlDbType.Bigint, trialId);
                cmd.Parameters.AddWithValue("ts", NpgsqlDbType.TimestampTz, ts);
                cmd.Parameters.AddWithValue("f", NpgsqlDbType.Numeric, body.ForceN);
                cmd.Parameters.AddWithValue("echo", NpgsqlDbType.Numeric, (object?)echoNs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("temp", NpgsqlDbType.Numeric, (object?)tempC ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 平台评估 + 条件出点（幂等：同档不会出第二个点）
            var eval = await EvaluatePlateauAsync(ds, trial, userId, ct);
            return Results.Ok(new { trialId, paired = echoNs is not null, eval.Plateau, eval.EchoCount, eval.PointCreated, eval.PointId, eval.Hint });
        })
        .WithName("PostTrialForceSample")
        .WithSummary("试验力值进样（自动配对声时；平台+声时双稳自动出点）");

        // ---- 实时视图（向导 2s 轮询）----
        grp.MapGet("/{trialId:long}/live", async (
            long channelId, long trialId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var trial = await LoadTrialAsync(ds, channelId, trialId, ct);
            if (trial is null) return Results.NotFound(new { error = "试验不存在或不属于该通道", trialId, channelId });

            var samples = new List<object>();
            await using (var cmd = ds.CreateCommand(@"
                SELECT ts, force_n, echo_time_ns FROM (
                    SELECT ts, force_n, echo_time_ns FROM calibration_trial_samples
                    WHERE trial_id=@t ORDER BY ts DESC LIMIT 300
                ) x ORDER BY ts"))
            {
                cmd.Parameters.AddWithValue("t", NpgsqlDbType.Bigint, trialId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    samples.Add(new
                    {
                        ts = r.GetDateTime(0),
                        forceN = r.GetDecimal(1),
                        echoTimeNs = r.IsDBNull(2) ? (decimal?)null : r.GetDecimal(2),
                    });
            }

            var points = new List<object>();
            await using (var cmd = ds.CreateCommand(@"
                SELECT id, estimated_preload_n, avg_echo_time_ns, delta_tof_ns, sample_count, window_end, excluded
                FROM torque_test_points WHERE trial_id=@t ORDER BY id"))
            {
                cmd.Parameters.AddWithValue("t", NpgsqlDbType.Bigint, trialId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    points.Add(new
                    {
                        id = r.GetInt64(0),
                        forceN = r.GetDecimal(1),
                        avgEchoTimeNs = r.GetDecimal(2),
                        deltaTofNs = r.GetDecimal(3),
                        sampleCount = r.GetInt32(4),
                        at = r.GetDateTime(5),
                        excluded = r.GetBoolean(6),
                    });
            }

            // 当前平台态（不出点，只给前端指引）
            var eval = await EvaluatePlateauAsync(ds, trial, actorUserId: null, ct, dryRun: true);
            return Results.Ok(new
            {
                trialId,
                trial.Status,
                trial.ForceSource,
                startedAt = trial.StartedAt,
                samples,
                points,
                currentPlateau = eval.Plateau,
                echoCountInPlateau = eval.EchoCount,
                hint = eval.Hint,
            });
        })
        .WithName("GetTrialLive")
        .WithSummary("试验实时视图：样本流 / 已生成点 / 当前平台与指引");

        // ---- 结束 / 中止 ----
        grp.MapPost("/{trialId:long}/finish", async (
            long channelId, long trialId, HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var userId = UserContext.Require(http).UserId;
            var trial = await LoadTrialAsync(ds, channelId, trialId, ct);
            if (trial is null) return Results.NotFound(new { error = "试验不存在或不属于该通道", trialId, channelId });
            if (trial.Status != "running") return Results.Conflict(new { error = "试验已不在进行中", trialId, trial.Status });

            await using (var cmd = ds.CreateCommand(
                "UPDATE calibration_trials SET status='done', ended_at=now() WHERE id=@t"))
            {
                cmd.Parameters.AddWithValue("t", NpgsqlDbType.Bigint, trialId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            long pointCount = 0;
            await using (var cmd = ds.CreateCommand(
                "SELECT count(*) FROM torque_test_points WHERE trial_id=@t AND excluded=false"))
            {
                cmd.Parameters.AddWithValue("t", NpgsqlDbType.Bigint, trialId);
                pointCount = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
            }
            await AuditAsync(ds, userId, "trial_finish", channelId,
                $"结束标定试验 #{trialId}：有效档位点 {pointCount} 个", ct);
            return Results.Ok(new
            {
                trialId, status = "done", pointCount,
                note = pointCount >= 3 ? "点数已够拟合，请到拟合页应用" : "有效点不足 3 个，拟合需要至少 3 档（可再次开始试验补采）",
            });
        })
        .WithName("FinishCalibrationTrial")
        .WithSummary("结束试验（点集进既有拟合/生效链路）");

        grp.MapPost("/{trialId:long}/abort", async (
            long channelId, long trialId, HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var userId = UserContext.Require(http).UserId;
            var trial = await LoadTrialAsync(ds, channelId, trialId, ct);
            if (trial is null) return Results.NotFound(new { error = "试验不存在或不属于该通道", trialId, channelId });
            if (trial.Status != "running") return Results.Conflict(new { error = "试验已不在进行中", trialId, trial.Status });

            // 中止 = 本场试验作废：已出的点一并剔除（可逆，PATCH 可恢复），拟合不受污染
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var cmd = new NpgsqlCommand(
                "UPDATE calibration_trials SET status='aborted', ended_at=now() WHERE id=@t", conn, tx))
            {
                cmd.Parameters.AddWithValue("t", NpgsqlDbType.Bigint, trialId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            int excluded;
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE torque_test_points SET excluded=true, exclude_reason='试验中止'
                WHERE trial_id=@t AND excluded=false", conn, tx))
            {
                cmd.Parameters.AddWithValue("t", NpgsqlDbType.Bigint, trialId);
                excluded = await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            await AuditAsync(ds, userId, "trial_abort", channelId,
                $"中止标定试验 #{trialId}，剔除其 {excluded} 个档位点", ct);
            return Results.Ok(new { trialId, status = "aborted", excludedPoints = excluded });
        })
        .WithName("AbortCalibrationTrial")
        .WithSummary("中止试验（其档位点标剔除，拟合不受污染）");

        return app;
    }

    // ===================== 内部 =====================

    private sealed record TrialRow(long Id, long ChannelId, long BoltId, string ForceSource, string Status, DateTime StartedAt);

    private static async Task<TrialRow?> LoadTrialAsync(NpgsqlDataSource ds, long channelId, long trialId, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(@"
            SELECT id, channel_id, bolt_id, force_source, status, started_at
            FROM calibration_trials WHERE id=@t AND channel_id=@cid");
        cmd.Parameters.AddWithValue("t", NpgsqlDbType.Bigint, trialId);
        cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new TrialRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetDateTime(5));
    }

    private sealed record PlateauEval(object? Plateau, long EchoCount, bool PointCreated, long? PointId, string Hint);

    /// <summary>
    /// 平台评估与条件出点。出点三门（顺序即失败提示的优先级）：
    /// ①力值平台成立；②与上一档步距足够（防同档重复出点）；③平台窗内声时样本够且稳。
    /// dryRun=true 只评估不出点（live 视图用）。
    /// </summary>
    private static async Task<PlateauEval> EvaluatePlateauAsync(
        NpgsqlDataSource ds, TrialRow trial, long? actorUserId, CancellationToken ct, bool dryRun = false)
    {
        // 最近 10 分钟力值样本（平台检测的输入；更早的样本属于已出点的历史档位）
        var samples = new List<ForcePlateauDetector.Sample>();
        await using (var cmd = ds.CreateCommand(@"
            SELECT ts, force_n FROM calibration_trial_samples
            WHERE trial_id=@t AND ts > now() - interval '10 minutes' ORDER BY ts"))
        {
            cmd.Parameters.AddWithValue("t", NpgsqlDbType.Bigint, trial.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                samples.Add(new ForcePlateauDetector.Sample(r.GetDateTime(0), r.GetDecimal(1)));
        }

        var plateau = ForcePlateauDetector.Detect(samples);
        if (plateau is null)
            return new PlateauEval(null, 0, false, null,
                samples.Count < ForcePlateauDetector.MinSamples
                    ? "等待力值进样…（保持载荷不动即形成平台）"
                    : "力值未稳（正在加载或抖动过大），保持当前载荷等待平台形成");

        object plateauDto = new
        {
            start = plateau.Start, end = plateau.End,
            meanForceN = plateau.MeanForceN, sampleCount = plateau.SampleCount,
            durationSeconds = Math.Round(plateau.DurationSeconds, 1),
        };

        // 步距守卫：与本试验最近一个点比力值（同一平台反复评估只会出一个点）
        await using (var cmd = ds.CreateCommand(@"
            SELECT estimated_preload_n FROM torque_test_points
            WHERE trial_id=@t ORDER BY id DESC LIMIT 1"))
        {
            cmd.Parameters.AddWithValue("t", NpgsqlDbType.Bigint, trial.Id);
            if (await cmd.ExecuteScalarAsync(ct) is decimal lastForce
                && Math.Abs(plateau.MeanForceN - lastForce) < ForcePlateauDetector.MinStepN)
            {
                return new PlateauEval(plateauDto, 0, false, null,
                    $"当前档（{plateau.MeanForceN / 1000m:0.0} kN）已出过点——加载到下一档（步距 ≥ {ForcePlateauDetector.MinStepN / 1000m:0} kN）");
            }
        }

        // 平台窗内声时样本
        decimal avgEcho = 0, avgTemp = 0;
        decimal? stddevEcho = null, minEcho = null, maxEcho = null;
        long echoCount = 0;
        DateTime winStart = default, winEnd = default;
        await using (var cmd = ds.CreateCommand(@"
            SELECT ROUND(AVG(echo_time_ns),6), ROUND(STDDEV_POP(echo_time_ns),6),
                   ROUND(MIN(echo_time_ns),6), ROUND(MAX(echo_time_ns),6),
                   ROUND(AVG(sp_temp_c),6), COUNT(*), MIN(ts), MAX(ts)
            FROM measurements
            WHERE bolt_id=@bid AND echo_time_ns IS NOT NULL AND ts >= @start AND ts <= @end"))
        {
            cmd.Parameters.AddWithValue("bid", NpgsqlDbType.Bigint, trial.BoltId);
            cmd.Parameters.AddWithValue("start", NpgsqlDbType.TimestampTz, plateau.Start);
            cmd.Parameters.AddWithValue("end", NpgsqlDbType.TimestampTz, plateau.End);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            echoCount = r.GetInt64(5);
            if (!r.IsDBNull(0) && echoCount > 0)
            {
                avgEcho = r.GetDecimal(0);
                stddevEcho = r.IsDBNull(1) ? null : r.GetDecimal(1);
                minEcho = r.IsDBNull(2) ? null : r.GetDecimal(2);
                maxEcho = r.IsDBNull(3) ? null : r.GetDecimal(3);
                avgTemp = r.IsDBNull(4) ? 20m : r.GetDecimal(4);
                winStart = r.GetDateTime(6);
                winEnd = r.GetDateTime(7);
            }
        }
        if (echoCount < EchoMinSamplesPerPoint)
            return new PlateauEval(plateauDto, echoCount, false, null,
                $"平台已稳（{plateau.MeanForceN / 1000m:0.0} kN），保持载荷攒声时样本：{echoCount}/{EchoMinSamplesPerPoint}");

        // 声时稳定性（实验室判据）：平台期内还在松弛/爬坡的声时不能出点
        var stab = await EchoStabilityGuard.MeasureAsync(
            ds, trial.ChannelId, (int)Math.Ceiling(plateau.DurationSeconds), ct);
        if (!stab.Stable)
            return new PlateauEval(plateauDto, echoCount, false, null,
                $"力值已稳但声时未稳（波动 {stab.StddevNs:0.00} ns / 趋势 {stab.HalfWindowDriftNs:0.00} ns，限 0.6）——通常是松弛未完，继续保持");

        if (dryRun)
            return new PlateauEval(plateauDto, echoCount, false, null, "双稳达标（只读视图不出点）");

        // 出点：与人工采点同一张表同一套列（拟合/报告零改动）。基线快照按当前通道值。
        decimal refTime = 0, refTemp = 20, tc = BoltPhysicsDefaults.SdkTempCoefficientPerC;
        string nominalDiam = "";
        await using (var cmd = ds.CreateCommand(@"
            SELECT c.ref_time_ns, c.ref_temp_c, COALESCE(b.temp_coefficient_per_c, @tcDefault), b.nominal_diam
            FROM channels c JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
            WHERE c.id=@cid"))
        {
            cmd.Parameters.AddWithValue("tcDefault", NpgsqlDbType.Numeric, BoltPhysicsDefaults.SdkTempCoefficientPerC);
            cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, trial.ChannelId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct) || r.IsDBNull(0) || r.IsDBNull(1))
                return new PlateauEval(plateauDto, echoCount, false, null, "通道基线缺失（试验中被重置？），请中止试验重新置零");
            refTime = r.GetDecimal(0);
            refTemp = r.GetDecimal(1);
            tc = r.GetDecimal(2);
            nominalDiam = r.IsDBNull(3) ? "" : r.GetString(3);
        }

        var normalizedEcho = avgEcho / (1m + tc * (avgTemp - refTemp));
        var deltaTof = normalizedEcho - refTime;
        var diameterMm = CalibrationEndpoints.ParseNominalDiameterMm(nominalDiam);

        long pointId;
        await using (var conn = await ds.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO torque_test_points (
                    bolt_id, channel_id, torque_nm, nut_factor, nominal_diameter_mm,
                    estimated_preload_n, sample_window_seconds, sample_count,
                    window_start, window_end,
                    avg_echo_time_ns, stddev_echo_time_ns, min_echo_time_ns, max_echo_time_ns,
                    avg_temp_c, ref_time_ns, ref_temp_c, temp_coefficient,
                    normalized_echo_time_ns, delta_tof_ns,
                    operator_user_id, note, trial_id
                ) VALUES (
                    @bolt_id, @channel_id, 0, 0.20, @diameter,
                    @preload, @seconds, @count,
                    @window_start, @window_end,
                    @avg_echo, @stddev_echo, @min_echo, @max_echo,
                    @avg_temp, @ref_time, @ref_temp, @tc,
                    @normalized_echo, @delta_tof,
                    @user_id, @note, @trial_id
                ) RETURNING id", conn, tx))
            {
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, trial.BoltId);
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, trial.ChannelId);
                cmd.Parameters.AddWithValue("diameter", NpgsqlDbType.Numeric, diameterMm);
                cmd.Parameters.AddWithValue("preload", NpgsqlDbType.Numeric, plateau.MeanForceN);
                cmd.Parameters.AddWithValue("seconds", NpgsqlDbType.Integer, (int)Math.Ceiling(plateau.DurationSeconds));
                cmd.Parameters.AddWithValue("count", NpgsqlDbType.Integer, (int)echoCount);
                cmd.Parameters.AddWithValue("window_start", NpgsqlDbType.TimestampTz, winStart);
                cmd.Parameters.AddWithValue("window_end", NpgsqlDbType.TimestampTz, winEnd);
                cmd.Parameters.AddWithValue("avg_echo", NpgsqlDbType.Numeric, avgEcho);
                cmd.Parameters.AddWithValue("stddev_echo", NpgsqlDbType.Numeric, (object?)stddevEcho ?? DBNull.Value);
                cmd.Parameters.AddWithValue("min_echo", NpgsqlDbType.Numeric, (object?)minEcho ?? DBNull.Value);
                cmd.Parameters.AddWithValue("max_echo", NpgsqlDbType.Numeric, (object?)maxEcho ?? DBNull.Value);
                cmd.Parameters.AddWithValue("avg_temp", NpgsqlDbType.Numeric, avgTemp);
                cmd.Parameters.AddWithValue("ref_time", NpgsqlDbType.Numeric, refTime);
                cmd.Parameters.AddWithValue("ref_temp", NpgsqlDbType.Numeric, refTemp);
                cmd.Parameters.AddWithValue("tc", NpgsqlDbType.Numeric, tc);
                cmd.Parameters.AddWithValue("normalized_echo", NpgsqlDbType.Numeric, normalizedEcho);
                cmd.Parameters.AddWithValue("delta_tof", NpgsqlDbType.Numeric, deltaTof);
                cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, (object?)actorUserId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                    $"[试验模式 #{trial.Id}] 平台 {plateau.DurationSeconds:0}s 实测 {plateau.MeanForceN:0} N（{trial.ForceSource}）");
                cmd.Parameters.AddWithValue("trial_id", NpgsqlDbType.Bigint, trial.Id);
                pointId = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
            }

            // 与人工采点同一审计口径：档位点是标定曲线的原始证据
            await using (var audit = new NpgsqlCommand(@"
                INSERT INTO audit_log (user_id, action, target_type, target_id, after_jsonb, note, at)
                VALUES (@uid, 'torque_point_capture', 'channel', @cid, @after::jsonb, @note, now())", conn, tx))
            {
                audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, (object?)actorUserId ?? DBNull.Value);
                audit.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, trial.ChannelId);
                audit.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb, System.Text.Json.JsonSerializer.Serialize(new
                {
                    pointId, trialId = trial.Id, forceMode = true,
                    measuredForceN = plateau.MeanForceN, plateauSeconds = plateau.DurationSeconds,
                    echoSampleCount = echoCount, deltaTofNs = deltaTof,
                }));
                audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                    $"试验 #{trial.Id} 平台自动出点 #{pointId}：实测 {plateau.MeanForceN:0} N，Δt={deltaTof:0.0} ns");
                await audit.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }

        return new PlateauEval(plateauDto, echoCount, true, pointId,
            $"已出点：{plateau.MeanForceN / 1000m:0.0} kN——加载到下一档（步距 ≥ {ForcePlateauDetector.MinStepN / 1000m:0} kN）");
    }

    private static async Task AuditAsync(
        NpgsqlDataSource ds, long userId, string action, long channelId, string note, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(@"
            INSERT INTO audit_log (user_id, action, target_type, target_id, note, at)
            VALUES (@uid, @action, 'channel', @cid, @note, now())");
        cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, userId);
        cmd.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action);
        cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
        cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, note);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
