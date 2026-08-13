using BoltPreload.Api.Auth;
using BoltPreload.Api.Runtime;
using BoltPreload.Application.Ingestion;
using BoltPreload.Calibration;
using BoltPreload.Domain;
using BoltPreload.Fx;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 标定相关接口。三条标定路径：
/// <para>
/// 1. “快速标定”（quick-calibrate）：运维直接传入 k_l / RefTime / RefTemp，
///    等价于"我知道这颗螺栓在已知预紧力下的 TOF"，用于恢复/迁移已知参数；
/// 2. “扭矩法临时标定”（torque-calibrate）：由扭矩+摩擦系数估算载荷，临时性质，正式验收需替换；
/// 3. “正式多点载荷标定”：GB/T 43232 路径 B/C，真实载荷点最小二乘拟合，
///    强制 R²≥0.98 + JJF 1059.1 不确定度评定 + 两人会签（见本文件后半部分）。
/// </para>
/// <para>
/// 调用流程：
/// 1. 通过 channel_id 查到 bolt_id（通道必须已绑定螺栓）；
/// 2. 把当前生效的 calibration_record 标记 superseded_by = new_id；
/// 3. 插入新 calibration_record（method='quick'）；
/// 4. UPDATE channels 把 k_l / ref_time_ns / ref_temp_c / k_l_uncertainty / active_calibration_id 同步过去
///    （热路径冗余字段，避免预紧力计算时再 JOIN）；
/// 5. 写 audit_log；
/// 6. 失效 IChannelResolver 缓存（让下一次解析拿到新参数）。
/// </para>
/// </summary>
public static class CalibrationEndpoints
{
    public static IEndpointRouteBuilder MapCalibrationEndpoints(this IEndpointRouteBuilder app)
    {
        // 标定签发要 reviewer+ 权限（涉及商用底线 §0：任何对算法、阈值、报警规则的修改都要可追溯）
        var grp = app.MapGroup("/api/channels/{channelId:long}")
            .WithTags("Calibration")
            .RequireAuthorization(AuthPolicies.ReviewerAccess);

        app.MapGet("/api/calibration-center/summary", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            // JOIN bolts 排除已软删螺栓的档案：删掉的资产不应再撑大标定中心 KPI
            const string sql = @"
                SELECT
                    cr.method,
                    COUNT(*) AS total,
                    COUNT(*) FILTER (WHERE cr.superseded_by IS NULL AND cr.review_state IN ('approved','self_signed')) AS active_count,
                    COUNT(*) FILTER (WHERE cr.expires_at < CURRENT_DATE) AS expired_count,
                    ROUND(AVG(cr.r_squared), 4) AS avg_r2
                FROM calibration_records cr
                JOIN bolts b ON b.id = cr.bolt_id AND b.deleted_at IS NULL
                GROUP BY cr.method
                ORDER BY cr.method";

            await using var cmd = ds.CreateCommand(sql);
            var methods = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var method = r.GetString(0);
                methods.Add(new
                {
                    method,
                    trustLevel = DisplayLabels.TrustLevel(method),
                    // 精度口径与验收报告同源（DisplayLabels.PrecisionSpec），避免页面与导出件各写一套
                    precision = DisplayLabels.PrecisionSpec(method),
                    total = r.GetInt64(1),
                    active = r.GetInt64(2),
                    expired = r.GetInt64(3),
                    avgR2 = r.IsDBNull(4) ? null : (decimal?)r.GetDecimal(4)
                });
            }

            return Results.Ok(new
            {
                generatedAt = DateTimeOffset.UtcNow,
                methods,
                policy = new
                {
                    formal = "可用于正式报告和告警阈值依据",
                    engineering_estimate = "可用于试运行和趋势验证，报告必须标明非载荷标定",
                    temporary = "仅用于调试或临时演示，不应用于安全结论"
                }
            });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("CalibrationCenterSummary")
        .WithSummary("标定中心摘要：按方法和可信等级统计");

        // 快速标定
        grp.MapPost("/quick-calibrate", async (
            long channelId, QuickCalibrateRequest req,
            NpgsqlDataSource ds, IChannelResolver resolver,
            HttpContext http,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("Calibration");
            var userId = UserContext.Require(http).UserId;  // 操作人/审核人/审计一律取自 JWT，不信任客户端 user_id

            // 1. 找 bolt_id + device_id + channel_no（一并拿出来便于失效缓存）
            //    顺带取屈服强度/截面/目标预紧力，用于下面的 K_L 量纲护栏
            long boltId, deviceId;
            short channelNo;
            decimal? targetPreloadN, rp02Mpa, crossSectionMm2;
            await using (var cmd = ds.CreateCommand(@"
                SELECT b.id, c.device_id, c.channel_no,
                       b.target_preload_n, b.rp02_mpa, b.cross_section_mm2
                FROM channels c
                JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                WHERE c.id = @cid AND c.deleted_at IS NULL"))
            {
                cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                {
                    return Results.NotFound(new { error = $"channel_id={channelId} 不存在或未绑定 bolt", channelId });
                }
                boltId = r.GetInt64(0);
                deviceId = r.GetInt64(1);
                channelNo = r.GetInt16(2);
                targetPreloadN = r.IsDBNull(3) ? null : r.GetDecimal(3);
                rp02Mpa = r.IsDBNull(4) ? null : r.GetDecimal(4);
                crossSectionMm2 = r.IsDBNull(5) ? null : r.GetDecimal(5);
            }

            // 1b. K_L 量纲/量级护栏（grounded，纯拒绝非法输入，不改变合法标定的计算）。
            //     本系统约定 K_L = Δt/F（单位 ns/N，量级 ~1e-4，用法 F = Δt/K_L）；
            //     最常见事故是误照标定证书 / plan 附录 C 的 1/N 约定（~1e-11，差一个 t₀）填进来。
            if (ValidateKLMagnitude(req.KL, req.RefTimeNs, YieldForceN(rp02Mpa, crossSectionMm2), targetPreloadN) is { } klReject)
                return klReject;

            // 2. 一个事务里：锁通道行 + superseded 旧记录 + INSERT 新 + UPDATE channels + audit_log
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            try
            {
                if (!await LockChannelForCalibrationAsync(conn, tx, channelId, ct))
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "通道不存在或已删除", channelId });
                }

                var newCalibId = Guid.NewGuid();

                // 先 INSERT 新记录（superseded_by=NULL），再 UPDATE 旧记录指向它
                // 注意：FK calibration_records.superseded_by → calibration_records.id 是 immediate，
                // 所以必须新记录先存在，旧记录的 superseded_by 才能成立
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO calibration_records (
                        id, bolt_id, method,
                        operator_user_id, reviewer_user_id,
                        loading_device, loading_device_cert,
                        env_temp_c, env_humidity,
                        points_jsonb, k_l, k_l_uncertainty, r_squared, outliers_removed,
                        uncertainty_breakdown, fitter_version,
                        ref_time_ns, ref_temp_c,
                        passed, notes, review_state,
                        operator_signature_at, reviewer_signature_at, expires_at, created_at, sdk_version
                    ) VALUES (
                        @id, @bolt_id, 'quick',
                        @op, @rev,
                        @ld, @ldc,
                        @et, @eh,
                        @pts::jsonb, @kl, @klu, 0, 0,
                        NULL, 'quick:v1',
                        @rt, @rtemp,
                        true, @note, 'self_signed',
                        now(), now(), @exp, now(), @sdkver
                    )", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, newCalibId);
                    cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                    cmd.Parameters.AddWithValue("op", NpgsqlDbType.Bigint, userId);
                    cmd.Parameters.AddWithValue("rev", NpgsqlDbType.Bigint, userId);  // quick 模式自签（review_state='self_signed'，非两人会签）
                    cmd.Parameters.AddWithValue("ld", NpgsqlDbType.Varchar, (object?)req.LoadingDevice ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("ldc", NpgsqlDbType.Varchar, (object?)req.LoadingDeviceCert ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("et", NpgsqlDbType.Numeric, (object?)req.EnvTempC ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("eh", NpgsqlDbType.Numeric, (object?)req.EnvHumidity ?? DBNull.Value);
                    // points_jsonb 必填，quick 模式给一个空数组 + 说明
                    var pointsJson = req.Points is null
                        ? """[{"_note":"快速标定，无多点数据"}]"""
                        : System.Text.Json.JsonSerializer.Serialize(req.Points);
                    cmd.Parameters.AddWithValue("pts", NpgsqlDbType.Jsonb, pointsJson);
                    cmd.Parameters.AddWithValue("kl", NpgsqlDbType.Numeric, req.KL);
                    cmd.Parameters.AddWithValue("klu", NpgsqlDbType.Numeric, req.KLUncertainty ?? 0m);
                    cmd.Parameters.AddWithValue("rt", NpgsqlDbType.Numeric, req.RefTimeNs);
                    cmd.Parameters.AddWithValue("rtemp", NpgsqlDbType.Numeric, req.RefTempC);
                    cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                        (object?)(req.Note ?? "快速标定（接口直调）") ?? DBNull.Value);
                    // 默认 30 天过期（GB/T 43232 推荐≤180 天，quick 模式更保守）
                    cmd.Parameters.AddWithValue("exp", NpgsqlDbType.Date,
                        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(req.ExpiresDays ?? 30)));
                    // 标定系数与 TOF 提取算法版本绑定（0031）：1.4/1.6 的自动法算法不同源，系数禁止混用
                    cmd.Parameters.AddWithValue("sdkver", NpgsqlDbType.Text, FxConnectorFactory.SdkVersion);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // 新记录已存在 → 把同 bolt 的旧 active 记录标 superseded_by=新 id
                await SupersedeActiveCalibrationsAsync(conn, tx, boltId, newCalibId, ct);

                await using (var cmd = new NpgsqlCommand(@"
                    UPDATE channels SET
                        k_l = @kl,
                        k_l_uncertainty = @klu,
                        k_l_intercept_ns = NULL,
                        ref_time_ns = @rt,
                        ref_temp_c = @rtemp,
                        active_calibration_id = @cid,
                        updated_at = now()
                    WHERE id = @id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("kl", NpgsqlDbType.Numeric, req.KL);
                    cmd.Parameters.AddWithValue("klu", NpgsqlDbType.Numeric, req.KLUncertainty ?? 0m);
                    cmd.Parameters.AddWithValue("rt", NpgsqlDbType.Numeric, req.RefTimeNs);
                    cmd.Parameters.AddWithValue("rtemp", NpgsqlDbType.Numeric, req.RefTempC);
                    cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Uuid, newCalibId);
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // 标定激活后回写 bolts.state（历史遗漏点：API 标定只更新 channels，漏写 state → 已标定却显示"未标定"）
                await MarkBoltCalibratedAsync(conn, tx, boltId, ct);

                // audit_log
                var afterJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    channelId,
                    boltId,
                    calibrationId = newCalibId,
                    req.KL,
                    req.RefTimeNs,
                    req.RefTempC
                });
                await WriteChannelAuditAsync(conn, tx, userId, "quick_calibrate", channelId,
                    afterJson, $"快速标定 螺栓 {boltId}", ct);

                await tx.CommitAsync(ct);

                // 失效缓存（让下一次 ChannelResult 解析走新参数）
                resolver.Invalidate(deviceId, (ushort)channelNo);

                logger.LogWarning("通道 {Ch} 快速标定完成 KL={KL} RefTime={Rt} RefTemp={Rtemp} calibId={Id}",
                    channelId, req.KL, req.RefTimeNs, req.RefTempC, newCalibId);

                return Results.Ok(new
                {
                    channelId,
                    boltId,
                    calibrationId = newCalibId,
                    appliedAt = DateTime.UtcNow,
                    activeCalibration = new { req.KL, req.RefTimeNs, req.RefTempC, req.KLUncertainty },
                    note = "已生效，下一条 ChannelResult 将基于新基线计算预紧力"
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                logger.LogError(ex, "快速标定失败 channelId={Ch}", channelId);
                return Results.Problem(detail: "服务器内部错误，请稍后重试；详情见系统日志。", statusCode: 500);
            }
        })
        .WithName("QuickCalibrate")
        .WithSummary("快速标定（写 k_l / RefTime / RefTemp）")
        .WithDescription(@"
**场景**：现场知道螺栓在某一预紧力下的 TOF，直接写参数让系统能开始计算。

请求示例：
```json
{
  ""kL"": 0.000123,
  ""refTimeNs"": 32240,
  ""refTempC"": 23.4,
  ""kLUncertainty"": 0.0000045,
  ""note"": ""现场快速标定，目标预紧力 80 kN""
}
```

注意：
- **kL 量纲约定**：本系统用 `K_L = Δt/F`（单位 **ns/N**，量级约 **1e-4**，用法 `F = Δt/K_L`）。
  标定证书 / plan 附录 C 用的是 `K_L = Δt/(F·t₀)`（单位 1/N，量级约 1e-11），二者相差一个 t₀；
  **切勿直接把证书上的 1e-11 填进来**。入参会做量级护栏校验（屈服力/目标预紧力下的声时变化合理性），明显不符会被拒绝。
- 严格按 GB/T 43232-2023 / JJF 1059.1-2012 的多点最小二乘标定走标定工作站流程；
- quick 模式 expires_at 默认 30 天（短）；
- 触发后旧 calibration_record 自动 superseded，channels 表冗余字段同步更新，缓存自动失效。");

        // 扭矩法临时标定：输入扭矩，按 T = K F d 估算预紧力，再用最近真实 EchoTime 反推 K_L
        grp.MapPost("/torque-calibrate", async (
            long channelId, TorqueCalibrateRequest req,
            NpgsqlDataSource ds, IChannelResolver resolver,
            HttpContext http,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("Calibration");
            var userId = UserContext.Require(http).UserId;  // 操作人/审核人/审计一律取自 JWT，不信任客户端 user_id

            long boltId, deviceId;
            short channelNo;
            string nominalDiam;
            decimal? existingRefTime, existingRefTemp;
            decimal? currentTarget, rp02Mpa, crossSectionMm2, boltTempCoeff;
            await using (var cmd = ds.CreateCommand(@"
                SELECT b.id, c.device_id, c.channel_no, b.nominal_diam,
                       c.ref_time_ns, c.ref_temp_c, b.target_preload_n,
                       b.rp02_mpa, b.cross_section_mm2, b.temp_coefficient_per_c
                FROM channels c
                JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                WHERE c.id = @cid AND c.deleted_at IS NULL"))
            {
                cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    return Results.NotFound(new { error = $"channel_id={channelId} 不存在或未绑定 bolt", channelId });

                boltId = r.GetInt64(0);
                deviceId = r.GetInt64(1);
                channelNo = r.GetInt16(2);
                nominalDiam = r.GetString(3);
                existingRefTime = r.IsDBNull(4) ? null : r.GetDecimal(4);
                existingRefTemp = r.IsDBNull(5) ? null : r.GetDecimal(5);
                currentTarget = r.IsDBNull(6) ? null : r.GetDecimal(6);
                rp02Mpa = r.IsDBNull(7) ? null : r.GetDecimal(7);
                crossSectionMm2 = r.IsDBNull(8) ? null : r.GetDecimal(8);
                boltTempCoeff = r.IsDBNull(9) ? null : r.GetDecimal(9);
            }

            var diameterMm = req.NominalDiameterMm ?? ParseNominalDiameterMm(nominalDiam);
            if (diameterMm <= 0)
                return Results.BadRequest(new { error = "无法确定螺栓公称直径，请传 nominalDiameterMm，例如 M20 传 20" });

            var nutFactor = req.NutFactor ?? 0.20m;
            if (nutFactor <= 0)
                return Results.BadRequest(new { error = "nutFactor 必须大于 0" });

            // T = K F d，其中 d 用 m
            var estimatedPreloadN = req.TorqueNm / (nutFactor * (diameterMm / 1000m));
            if (estimatedPreloadN <= 0)
                return Results.BadRequest(new { error = "估算预紧力 <= 0，请检查 torqueNm / nutFactor / diameter" });

            var sampleWindowSeconds = Math.Clamp(req.SampleWindowSeconds ?? 60, 5, 600);

            // 采样稳定性护栏（hardening §2）：窗口里混着拧紧过程/松弛尾巴时拒绝。
            // 不拦的话平均声时落在两个受力状态之间，K_L 错一个数量级还毫无征兆——
            // 08-12 真机上 CH1/CH2/CH8 就是这么标废的（设完基线一分钟就提交，窗口 3/4 是松开状态）。
            var stability = await EchoStabilityGuard.MeasureAsync(ds, channelId, sampleWindowSeconds, ct);
            if (!stability.Stable && req.Force != true)
                return Results.BadRequest(new
                {
                    error = $"采样窗口内声时未达稳定判据（波动 {Fmt(stability.StddevNs)} ns，前后半窗趋势 {Fmt(stability.HalfWindowDriftNs)} ns）。"
                        + "多半是拧紧后还没等满采样窗口、或应力松弛未结束——请保持螺栓静置，等稳后重试；"
                        + "确认要用当前数据可传 force=true（会记入审计）。",
                    stability = stability.ToDiagnostics(),
                });

            decimal avgEcho, avgTemp;
            long sampleCount;
            await using (var cmd = ds.CreateCommand(@"
                SELECT ROUND(AVG(echo_time_ns), 6), ROUND(AVG(sp_temp_c), 6), COUNT(*)
                FROM measurements
                WHERE bolt_id = @bolt_id
                  AND ts > now() - (@seconds || ' seconds')::interval
                  AND echo_time_ns IS NOT NULL"))
            {
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                cmd.Parameters.AddWithValue("seconds", NpgsqlDbType.Integer, sampleWindowSeconds);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                if (r.IsDBNull(0) || r.GetInt64(2) < (req.MinSamples ?? 20))
                    return Results.BadRequest(new { error = "最近窗口内有效 EchoTime 样本不足", sampleWindowSeconds, minSamples = req.MinSamples ?? 20, actual = r.GetInt64(2) });
                avgEcho = r.GetDecimal(0);
                avgTemp = r.GetDecimal(1);
                sampleCount = r.GetInt64(2);
            }

            var refTime = req.RefTimeNs ?? existingRefTime;
            var refTemp = req.RefTempC ?? existingRefTemp;
            if (refTime is null || refTemp is null)
                return Results.BadRequest(new { error = "缺少 RefTime/RefTemp，请先同步通道配置或在请求里传 refTimeNs/refTempC" });

            // 用螺栓自身温度系数(缺省回退 SDK 默认)，与采集热路径(PostgresMeasurementSink)一致，避免标定/运行两套温补
            var normalizedEcho = avgEcho / (1m + (boltTempCoeff ?? BoltPhysicsDefaults.SdkTempCoefficientPerC) * (avgTemp - refTemp.Value));
            var deltaTof = normalizedEcho - refTime.Value;
            var kL = deltaTof / estimatedPreloadN;
            if (kL <= 0)
                return Results.BadRequest(new { error = "计算得到 K_L <= 0，请检查 RefTime/当前 EchoTime/扭矩输入", avgEcho, normalizedEcho, refTime, deltaTof });
            // 计算得到的 kL 也过一遍量级护栏（卡 RefTime/Δt/载荷异常导致的非物理 kL）
            if (ValidateKLMagnitude(kL, refTime.Value, YieldForceN(rp02Mpa, crossSectionMm2), currentTarget) is { } klReject)
                return klReject;

            var uncertaintyRatio = req.UncertaintyRatio ?? 0.30m;
            var kLUncertainty = kL * uncertaintyRatio;
            var newCalibId = Guid.NewGuid();
            var note = req.Note ??
                $"扭矩法临时标定：T={req.TorqueNm}N·m, K={nutFactor}, d={diameterMm}mm, F≈{estimatedPreloadN:F1}N；正式验收需多点载荷标定替换。";

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                if (!await LockChannelForCalibrationAsync(conn, tx, channelId, ct))
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "通道不存在或已删除", channelId });
                }

                var pointsJson = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        type = "torque_estimate",
                        torqueNm = req.TorqueNm,
                        nutFactor,
                        nominalDiameterMm = diameterMm,
                        estimatedPreloadN,
                        avgEchoNs = avgEcho,
                        avgTempC = avgTemp,
                        normalizedEchoNs = normalizedEcho,
                        deltaTofNs = deltaTof,
                        sampleCount,
                        sampleWindowSeconds,
                        warning = "temporary torque estimate, not load-cell calibration"
                    }
                });

                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO calibration_records (
                        id, bolt_id, method, operator_user_id, reviewer_user_id,
                        loading_device, loading_device_cert, env_temp_c, env_humidity,
                        points_jsonb, k_l, k_l_uncertainty, r_squared, outliers_removed,
                        uncertainty_breakdown, fitter_version, ref_time_ns, ref_temp_c,
                        passed, notes, review_state, operator_signature_at, reviewer_signature_at, expires_at, created_at, sdk_version
                    ) VALUES (
                        @id, @bolt_id, 'torque_estimate', @op, @rev,
                        @ld, NULL, @env_temp, NULL,
                        @points::jsonb, @kl, @klu, 0, 0,
                        @unc::jsonb, 'torque:v1', @rt, @rtemp,
                        true, @note, 'self_signed', now(), now(), @exp, now(), @sdkver
                    )", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, newCalibId);
                    cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                    cmd.Parameters.AddWithValue("op", NpgsqlDbType.Bigint, userId);
                    cmd.Parameters.AddWithValue("rev", NpgsqlDbType.Bigint, userId);
                    cmd.Parameters.AddWithValue("ld", NpgsqlDbType.Varchar, req.LoadingDevice ?? "torque wrench");
                    cmd.Parameters.AddWithValue("env_temp", NpgsqlDbType.Numeric, avgTemp);
                    cmd.Parameters.AddWithValue("points", NpgsqlDbType.Jsonb, pointsJson);
                    cmd.Parameters.AddWithValue("kl", NpgsqlDbType.Numeric, kL);
                    cmd.Parameters.AddWithValue("klu", NpgsqlDbType.Numeric, kLUncertainty);
                    cmd.Parameters.AddWithValue("unc", NpgsqlDbType.Jsonb,
                        System.Text.Json.JsonSerializer.Serialize(new { uncertaintyRatio, source = "torque coefficient uncertainty" }));
                    cmd.Parameters.AddWithValue("rt", NpgsqlDbType.Numeric, refTime.Value);
                    cmd.Parameters.AddWithValue("rtemp", NpgsqlDbType.Numeric, refTemp.Value);
                    cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, note);
                    cmd.Parameters.AddWithValue("exp", NpgsqlDbType.Date,
                        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(req.ExpiresDays ?? 7)));
                    cmd.Parameters.AddWithValue("sdkver", NpgsqlDbType.Text, FxConnectorFactory.SdkVersion);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await SupersedeActiveCalibrationsAsync(conn, tx, boltId, newCalibId, ct);

                // target_preload_n 是设计/合同目标，不能被扭矩法估算值悄悄覆写——
                // 否则后续保持率永远相对"估算目标"自洽，掩盖真实偏差。仅在原本为空时兜底填入。
                await using (var cmd = new NpgsqlCommand(@"
                    UPDATE bolts SET
                        install_torque_nm = @torque,
                        target_preload_n = COALESCE(target_preload_n, @target),
                        nominal_diam = @nominal,
                        effective_length_mm = COALESCE(@effective_len, effective_length_mm),
                        total_length_mm = COALESCE(@total_len, total_length_mm),
                        cross_section_mm2 = COALESCE(@area, cross_section_mm2),
                        state = 'calibrated',
                        updated_at = now()
                    WHERE id = @bolt_id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("torque", NpgsqlDbType.Numeric, req.TorqueNm);
                    cmd.Parameters.AddWithValue("target", NpgsqlDbType.Numeric, estimatedPreloadN);
                    cmd.Parameters.AddWithValue("nominal", NpgsqlDbType.Varchar, $"M{diameterMm:0}");
                    cmd.Parameters.AddWithValue("effective_len", NpgsqlDbType.Numeric, (object?)req.EffectiveLengthMm ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("total_len", NpgsqlDbType.Numeric, (object?)req.TotalLengthMm ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("area", NpgsqlDbType.Numeric, (object?)req.CrossSectionMm2 ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await using (var cmd = new NpgsqlCommand(@"
                    UPDATE channels SET
                        k_l = @kl,
                        k_l_uncertainty = @klu,
                        k_l_intercept_ns = NULL,
                        ref_time_ns = @rt,
                        ref_temp_c = @rtemp,
                        active_calibration_id = @cid,
                        updated_at = now()
                    WHERE id = @channel_id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("kl", NpgsqlDbType.Numeric, kL);
                    cmd.Parameters.AddWithValue("klu", NpgsqlDbType.Numeric, kLUncertainty);
                    cmd.Parameters.AddWithValue("rt", NpgsqlDbType.Numeric, refTime.Value);
                    cmd.Parameters.AddWithValue("rtemp", NpgsqlDbType.Numeric, refTemp.Value);
                    cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Uuid, newCalibId);
                    cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                var afterJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    channelId,
                    boltId,
                    calibrationId = newCalibId,
                    req.TorqueNm,
                    nutFactor,
                    diameterMm,
                    estimatedPreloadN,
                    kL,
                    kLUncertainty,
                    refTime,
                    refTemp,
                    sampleCount,
                    // 强闯稳定性护栏必须留痕：事后排查坏标定时先看是不是 forced 进来的
                    forcedPastStabilityGuard = !stability.Stable,
                    stabilityStddevNs = stability.StddevNs,
                    stabilityDriftNs = stability.HalfWindowDriftNs,
                });
                await WriteChannelAuditAsync(conn, tx, userId, "torque_calibrate", channelId, afterJson, note, ct);

                await tx.CommitAsync(ct);
                resolver.Invalidate(deviceId, (ushort)channelNo);

                logger.LogWarning("扭矩法临时标定完成 channel={ChannelId} torque={Torque}Nm preload≈{Preload}N KL={KL}",
                    channelId, req.TorqueNm, estimatedPreloadN, kL);

                return Results.Ok(new
                {
                    channelId,
                    boltId,
                    calibrationId = newCalibId,
                    appliedAt = DateTime.UtcNow,
                    torque = new { req.TorqueNm, nutFactor, nominalDiameterMm = diameterMm, estimatedPreloadN },
                    measurement = new { avgEchoNs = avgEcho, avgTempC = avgTemp, normalizedEchoNs = normalizedEcho, deltaTofNs = deltaTof, sampleCount },
                    activeCalibration = new { kL, kLUncertainty, refTimeNs = refTime, refTempC = refTemp },
                    warning = "扭矩法临时估算，非载荷标定；正式验收需多点加载标定替换。"
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                logger.LogError(ex, "扭矩法临时标定失败 channelId={ChannelId}", channelId);
                return Results.Problem(detail: "服务器内部错误，请稍后重试；详情见系统日志。", statusCode: 500);
            }
        })
        .WithName("TorqueCalibrate")
        .WithSummary("扭矩法临时标定（T=KFd，自动从最近 EchoTime 反推 K_L）");

        // 记录一个扭矩阶梯实验点（不改变当前 active calibration）
        grp.MapPost("/torque-test-points", async (
            long channelId, TorqueTestPointRequest req,
            NpgsqlDataSource ds, HttpContext http, CancellationToken ct) =>
        {
            var userId = UserContext.Require(http).UserId;  // 记录是谁加的载荷点，取自 JWT

            long boltId;
            string nominalDiam;
            decimal? refTime, refTemp, boltTempCoeff;
            await using (var cmd = ds.CreateCommand(@"
                SELECT b.id, b.nominal_diam, c.ref_time_ns, c.ref_temp_c, b.temp_coefficient_per_c
                FROM channels c
                JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                WHERE c.id = @cid AND c.deleted_at IS NULL"))
            {
                cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    return Results.NotFound(new { error = $"channel_id={channelId} 不存在或未绑定 bolt", channelId });

                boltId = r.GetInt64(0);
                nominalDiam = r.GetString(1);
                refTime = r.IsDBNull(2) ? null : r.GetDecimal(2);
                refTemp = r.IsDBNull(3) ? null : r.GetDecimal(3);
                boltTempCoeff = r.IsDBNull(4) ? null : r.GetDecimal(4);
            }

            if (refTime is null || refTemp is null)
                return Results.BadRequest(new { error = "缺少 ref_time_ns/ref_temp_c，请先同步通道配置或做一次临时标定" });

            var diameterMm = req.NominalDiameterMm ?? ParseNominalDiameterMm(nominalDiam);
            if (diameterMm <= 0)
                return Results.BadRequest(new { error = "无法确定螺栓公称直径，请传 nominalDiameterMm" });

            var nutFactor = req.NutFactor ?? 0.20m;
            if (nutFactor <= 0)
                return Results.BadRequest(new { error = "nutFactor 必须大于 0" });

            var estimatedPreloadN = req.TorqueNm / (nutFactor * (diameterMm / 1000m));
            var seconds = Math.Clamp(req.SampleWindowSeconds ?? 60, 5, 600);
            var minSamples = Math.Clamp(req.MinSamples ?? 20, 1, 10_000);

            // 采样稳定性护栏（hardening §2）：阶梯点是拟合的原始证据，一个"拧的过程中采的"点
            // 就能把斜率带偏。窗口不稳直接拒绝，别等拟合看 R² 才发现。
            var stability = await EchoStabilityGuard.MeasureAsync(ds, channelId, seconds, ct);
            if (!stability.Stable && req.Force != true)
                return Results.BadRequest(new
                {
                    error = $"采样窗口内声时未达稳定判据（波动 {Fmt(stability.StddevNs)} ns，前后半窗趋势 {Fmt(stability.HalfWindowDriftNs)} ns）。"
                        + "请拧到目标扭矩后保持静置、等满一个采样窗口再采点；确认要用当前数据可传 force=true（会记入审计）。",
                    stability = stability.ToDiagnostics(),
                });

            decimal avgEcho, avgTemp, normalizedEcho, deltaTof;
            decimal? stddevEcho, minEcho, maxEcho;
            long sampleCount;
            DateTime windowStart, windowEnd;

            await using (var cmd = ds.CreateCommand(@"
                SELECT
                    ROUND(AVG(echo_time_ns), 6),
                    ROUND(STDDEV_POP(echo_time_ns), 6),
                    ROUND(MIN(echo_time_ns), 6),
                    ROUND(MAX(echo_time_ns), 6),
                    ROUND(AVG(sp_temp_c), 6),
                    COUNT(*),
                    MIN(ts),
                    MAX(ts)
                FROM measurements
                WHERE bolt_id = @bolt_id
                  AND ts > now() - (@seconds || ' seconds')::interval
                  AND echo_time_ns IS NOT NULL"))
            {
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                cmd.Parameters.AddWithValue("seconds", NpgsqlDbType.Integer, seconds);

                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                sampleCount = r.GetInt64(5);
                if (r.IsDBNull(0) || sampleCount < minSamples)
                    return Results.BadRequest(new { error = "最近窗口内有效 EchoTime 样本不足", seconds, minSamples, actual = sampleCount });

                avgEcho = r.GetDecimal(0);
                stddevEcho = r.IsDBNull(1) ? null : r.GetDecimal(1);
                minEcho = r.IsDBNull(2) ? null : r.GetDecimal(2);
                maxEcho = r.IsDBNull(3) ? null : r.GetDecimal(3);
                avgTemp = r.GetDecimal(4);
                windowStart = r.GetDateTime(6);
                windowEnd = r.GetDateTime(7);
            }

            // 用螺栓自身温度系数(缺省回退 SDK 默认)，与采集热路径一致
            normalizedEcho = avgEcho / (1m + (boltTempCoeff ?? BoltPhysicsDefaults.SdkTempCoefficientPerC) * (avgTemp - refTemp.Value));
            deltaTof = normalizedEcho - refTime.Value;

            long pointId;
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO torque_test_points (
                    bolt_id, channel_id, torque_nm, nut_factor, nominal_diameter_mm,
                    estimated_preload_n, sample_window_seconds, sample_count,
                    window_start, window_end,
                    avg_echo_time_ns, stddev_echo_time_ns, min_echo_time_ns, max_echo_time_ns,
                    avg_temp_c, ref_time_ns, ref_temp_c, temp_coefficient,
                    normalized_echo_time_ns, delta_tof_ns,
                    operator_user_id, note
                ) VALUES (
                    @bolt_id, @channel_id, @torque, @nut_factor, @diameter,
                    @preload, @seconds, @count,
                    @window_start, @window_end,
                    @avg_echo, @stddev_echo, @min_echo, @max_echo,
                    @avg_temp, @ref_time, @ref_temp, @tc,
                    @normalized_echo, @delta_tof,
                    @user_id, @note
                )
                RETURNING id", conn, tx))
            {
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                cmd.Parameters.AddWithValue("torque", NpgsqlDbType.Numeric, req.TorqueNm);
                cmd.Parameters.AddWithValue("nut_factor", NpgsqlDbType.Numeric, nutFactor);
                cmd.Parameters.AddWithValue("diameter", NpgsqlDbType.Numeric, diameterMm);
                cmd.Parameters.AddWithValue("preload", NpgsqlDbType.Numeric, estimatedPreloadN);
                cmd.Parameters.AddWithValue("seconds", NpgsqlDbType.Integer, seconds);
                cmd.Parameters.AddWithValue("count", NpgsqlDbType.Integer, (int)sampleCount);
                cmd.Parameters.AddWithValue("window_start", NpgsqlDbType.TimestampTz, windowStart);
                cmd.Parameters.AddWithValue("window_end", NpgsqlDbType.TimestampTz, windowEnd);
                cmd.Parameters.AddWithValue("avg_echo", NpgsqlDbType.Numeric, avgEcho);
                cmd.Parameters.AddWithValue("stddev_echo", NpgsqlDbType.Numeric, (object?)stddevEcho ?? DBNull.Value);
                cmd.Parameters.AddWithValue("min_echo", NpgsqlDbType.Numeric, (object?)minEcho ?? DBNull.Value);
                cmd.Parameters.AddWithValue("max_echo", NpgsqlDbType.Numeric, (object?)maxEcho ?? DBNull.Value);
                cmd.Parameters.AddWithValue("avg_temp", NpgsqlDbType.Numeric, avgTemp);
                cmd.Parameters.AddWithValue("ref_time", NpgsqlDbType.Numeric, refTime.Value);
                cmd.Parameters.AddWithValue("ref_temp", NpgsqlDbType.Numeric, refTemp.Value);
                    cmd.Parameters.AddWithValue("tc", NpgsqlDbType.Numeric, boltTempCoeff ?? BoltPhysicsDefaults.SdkTempCoefficientPerC);
                cmd.Parameters.AddWithValue("normalized_echo", NpgsqlDbType.Numeric, normalizedEcho);
                cmd.Parameters.AddWithValue("delta_tof", NpgsqlDbType.Numeric, deltaTof);
                cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, userId);
                cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)req.Note ?? DBNull.Value);

                pointId = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
            }

            // 标定实验数据写入必须可追溯（同文件 quick/torque/load 均有审计，此前唯独采点漏了）。
            // 与采点插入同事务：载荷点是标定曲线的原始证据，多一个来路不明的点就够把拟合带偏。
            await using (var audit = new NpgsqlCommand(@"
                INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at, ip_addr)
                VALUES (@uid, 'torque_point_capture', 'channel', @cid, NULL, @after::jsonb, @note, now(), @ip)", conn, tx))
            {
                audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, userId);
                audit.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                audit.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb, System.Text.Json.JsonSerializer.Serialize(new
                {
                    pointId, boltId, req.TorqueNm, nutFactor, diameterMm,
                    estimatedPreloadN, sampleCount, deltaTofNs = deltaTof,
                    forcedPastStabilityGuard = !stability.Stable,
                    stabilityStddevNs = stability.StddevNs,
                    stabilityDriftNs = stability.HalfWindowDriftNs,
                }));
                audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                    $"采集扭矩阶梯点 #{pointId}：{req.TorqueNm} N·m，估算预紧力 {estimatedPreloadN:0} N（bolt #{boltId}）");
                AuditClientIp.Bind(audit.Parameters);
                await audit.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);

            return Results.Ok(new
            {
                id = pointId,
                channelId,
                boltId,
                torqueNm = req.TorqueNm,
                nutFactor,
                nominalDiameterMm = diameterMm,
                estimatedPreloadN,
                sampleWindowSeconds = seconds,
                sampleCount,
                windowStart,
                windowEnd,
                avgEchoTimeNs = avgEcho,
                stddevEchoTimeNs = stddevEcho,
                avgTempC = avgTemp,
                refTimeNs = refTime,
                refTempC = refTemp,
                normalizedEchoTimeNs = normalizedEcho,
                deltaTofNs = deltaTof,
                note = "扭矩实验点已记录。继续按 50/100/150/200 N·m 采点后可看 summary 拟合线性。"
            });
        })
        .WithName("CaptureTorqueTestPoint")
        .WithSummary("采集一个扭矩阶梯实验点（不改变标定）");

        grp.MapGet("/torque-test-points", async (long channelId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            // 上限护栏：拟合展示用最近 500 点足够（正常阶梯实验 4~20 点）
            const string sql = @"
                SELECT id, torque_nm, nut_factor, nominal_diameter_mm, estimated_preload_n,
                       sample_count, window_start, window_end,
                       avg_echo_time_ns, stddev_echo_time_ns, avg_temp_c,
                       normalized_echo_time_ns, delta_tof_ns, note, created_at,
                       excluded, exclude_reason
                FROM torque_test_points
                WHERE channel_id = @channel_id
                ORDER BY created_at DESC
                LIMIT 500";

            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);

            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new
                {
                    id = r.GetInt64(0),
                    torqueNm = r.GetDecimal(1),
                    nutFactor = r.GetDecimal(2),
                    nominalDiameterMm = r.GetDecimal(3),
                    estimatedPreloadN = r.GetDecimal(4),
                    sampleCount = r.GetInt32(5),
                    windowStart = r.GetDateTime(6),
                    windowEnd = r.GetDateTime(7),
                    avgEchoTimeNs = r.GetDecimal(8),
                    stddevEchoTimeNs = r.IsDBNull(9) ? null : (decimal?)r.GetDecimal(9),
                    avgTempC = r.GetDecimal(10),
                    normalizedEchoTimeNs = r.GetDecimal(11),
                    deltaTofNs = r.GetDecimal(12),
                    note = r.IsDBNull(13) ? null : r.GetString(13),
                    createdAt = r.GetDateTime(14),
                    excluded = r.GetBoolean(15),
                    excludeReason = r.IsDBNull(16) ? null : r.GetString(16)
                });
            }

            return Results.Ok(new { channelId, count = items.Count, items });
        })
        .WithName("ListTorqueTestPoints")
        .WithSummary("查询扭矩实验点");

        // 稳定性预检（只读）：把采点/标定用的同一套判据暴露给前端当指示灯。
        // 向导/阶梯页轮询它，灯绿了再放开"采点"按钮——前后端一个标准，
        // 不会出现"前端说稳、后端打回"的两套口径。
        // 注册在 app 级而不是 grp：grp 的 ReviewerAccess 会与 endpoint 策略叠加，
        // 而这个只读指示灯值班（operator）也要能看。
        app.MapGet("/api/channels/{channelId:long}/echo-stability", async (
            long channelId, int? seconds, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var s = await EchoStabilityGuard.MeasureAsync(ds, channelId, seconds ?? 180, ct);
            return Results.Ok(new
            {
                channelId,
                stable = s.Stable,
                sampleCount = s.SampleCount,
                avgEchoNs = s.AvgEchoNs,
                stddevNs = s.StddevNs,
                halfWindowDriftNs = s.HalfWindowDriftNs,
                stddevMaxNs = CalibrationThresholds.EchoStddevMaxNs,
                halfWindowDriftMaxNs = CalibrationThresholds.EchoHalfWindowDriftMaxNs,
                windowSeconds = s.WindowSeconds,
            });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithTags("Calibration")
        .WithName("ChannelEchoStability")
        .WithSummary("采样稳定性预检：与标定护栏同判据，供前端指示灯轮询");

        // 剔除 / 恢复一个扭矩阶梯点。
        //
        // 现场最常见的坏点是"扳手还在拧就采了"：采样窗口（默认 180s）里混着加载过程的声时，
        // 均值落在两个扭矩档之间，把拟合斜率带偏且看不出来。其次是扭矩值输错、那段时间信号丢。
        // 拟合与报告一直按 excluded=false 过滤，但在此之前没有任何接口能写这个标志——
        // 坏点只能靠改库清理。这里补上，剔除是可逆的（误剔了能恢复），全程审计。
        //
        // 只打标志、不删行：标定原始数据是证据链的一部分，报告里坏点也要以剔除态列出来备查。
        // 已生效的标定不受影响（K_L 是应用拟合那一刻的快照），下一次 apply-torque-fit 才会用上新点集。
        grp.MapPatch("/torque-test-points/{pointId:long}", async (
            long channelId, long pointId, TorqueTestPointPatchRequest? body,
            HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (body?.Excluded is not { } excluded)
                return Results.BadRequest(new { error = "excluded 必填（true=剔除，false=恢复）" });
            var reason = string.IsNullOrWhiteSpace(body.Reason) ? null : body.Reason.Trim();
            if (excluded && reason is null)
                return Results.BadRequest(new { error = "剔除必须填原因（记入标定档案与报告）" });

            var user = UserContext.Require(http);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // 先关 reader 再决定回滚（同连接开着 reader 时回滚会抛，404 会变成 500）
            decimal? torqueNm = null;
            var wasExcluded = false;
            await using (var cmd = new NpgsqlCommand(@"
                SELECT torque_nm, excluded FROM torque_test_points
                WHERE id = @pid AND channel_id = @cid
                FOR UPDATE", conn, tx))
            {
                cmd.Parameters.AddWithValue("pid", NpgsqlDbType.Bigint, pointId);
                cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    torqueNm = r.GetDecimal(0);
                    wasExcluded = r.GetBoolean(1);
                }
            }
            if (torqueNm is null)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "实验点不存在或不属于该通道", channelId, pointId });
            }

            await using (var cmd = new NpgsqlCommand(@"
                UPDATE torque_test_points
                   SET excluded = @excluded,
                       exclude_reason = @reason
                 WHERE id = @pid", conn, tx))
            {
                cmd.Parameters.AddWithValue("excluded", NpgsqlDbType.Boolean, excluded);
                cmd.Parameters.AddWithValue("reason", NpgsqlDbType.Text, (object?)reason ?? DBNull.Value);
                cmd.Parameters.AddWithValue("pid", NpgsqlDbType.Bigint, pointId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var audit = new NpgsqlCommand(@"
                INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at, ip_addr)
                VALUES (@uid, 'torque_point_exclude', 'channel', @cid,
                        jsonb_build_object('pointId', @pid::bigint, 'excluded', @was),
                        jsonb_build_object('pointId', @pid::bigint, 'excluded', @excluded, 'reason', @reason::text),
                        @note, now(), @ip)", conn, tx))
            {
                audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                audit.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                audit.Parameters.AddWithValue("pid", NpgsqlDbType.Bigint, pointId);
                audit.Parameters.AddWithValue("was", NpgsqlDbType.Boolean, wasExcluded);
                audit.Parameters.AddWithValue("excluded", NpgsqlDbType.Boolean, excluded);
                audit.Parameters.AddWithValue("reason", NpgsqlDbType.Text, (object?)reason ?? DBNull.Value);
                audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                    excluded
                        ? $"剔除扭矩阶梯点 #{pointId}（{torqueNm:0} N·m）：{reason}"
                        : $"恢复扭矩阶梯点 #{pointId}（{torqueNm:0} N·m）参与拟合");
                AuditClientIp.Bind(audit.Parameters);
                await audit.ExecuteNonQueryAsync(ct);
            }

            // 剩余有效点数：前端据此提示"还差几个点才能拟合"（apply-torque-fit 默认要 3 个）
            long remaining;
            await using (var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM torque_test_points WHERE channel_id = @cid AND excluded = false", conn, tx))
            {
                cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                remaining = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
            }

            await tx.CommitAsync(ct);
            return Results.Ok(new
            {
                channelId, pointId, excluded, reason,
                remainingValidPoints = remaining,
                note = "已生效标定不受影响；下次应用拟合才会按新的点集重算。"
            });
        })
        .WithName("PatchTorqueTestPoint")
        .WithSummary("剔除 / 恢复一个扭矩阶梯实验点（只打标志不删行，审计）");

        grp.MapGet("/torque-test-points/summary", async (long channelId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            const string sql = @"
                SELECT
                    COUNT(*) AS n,
                    regr_slope(delta_tof_ns::double precision, estimated_preload_n::double precision) AS kl_slope,
                    regr_intercept(delta_tof_ns::double precision, estimated_preload_n::double precision) AS intercept_ns,
                    regr_r2(delta_tof_ns::double precision, estimated_preload_n::double precision) AS r_squared,
                    MIN(torque_nm) AS min_torque,
                    MAX(torque_nm) AS max_torque,
                    MIN(delta_tof_ns) AS min_delta,
                    MAX(delta_tof_ns) AS max_delta
                FROM torque_test_points
                WHERE channel_id = @channel_id AND excluded = false";

            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var n = r.GetInt64(0);

            return Results.Ok(new
            {
                channelId,
                pointCount = n,
                kLByLinearFitNsPerN = r.IsDBNull(1) ? null : (double?)r.GetDouble(1),
                interceptNs = r.IsDBNull(2) ? null : (double?)r.GetDouble(2),
                rSquared = r.IsDBNull(3) ? null : (double?)r.GetDouble(3),
                minTorqueNm = r.IsDBNull(4) ? null : (decimal?)r.GetDecimal(4),
                maxTorqueNm = r.IsDBNull(5) ? null : (decimal?)r.GetDecimal(5),
                minDeltaTofNs = r.IsDBNull(6) ? null : (decimal?)r.GetDecimal(6),
                maxDeltaTofNs = r.IsDBNull(7) ? null : (decimal?)r.GetDecimal(7),
                note = n < 3
                    ? "少于 3 个点，只能记录，不能判断线性。建议采 50/100/150/200 N·m。"
                    : "rSquared 越接近 1，扭矩-超声响应越线性；正式标定仍需已知载荷。"
            });
        })
        .WithName("TorqueTestPointSummary")
        .WithSummary("扭矩实验点线性拟合摘要");

        grp.MapPost("/apply-torque-fit", async (
            long channelId, ApplyTorqueFitRequest? req,
            NpgsqlDataSource ds, IChannelResolver resolver,
            HttpContext http,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("Calibration");
            var userId = UserContext.Require(http).UserId;  // 操作人/审核人/审计一律取自 JWT，不信任客户端 user_id

            var minPoints = req?.MinPoints ?? 3;

            // M3：绑定预读与拟合聚合全部在事务内、拿到通道锁之后执行——
            // 锁前读取在并发采点/排除点/换绑下可能过期（TOCTOU），入库的 K_L 与
            // points_jsonb 快照会跟实际点集对不上；通道锁把这些窗口统一串行化。
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                if (!await LockChannelForCalibrationAsync(conn, tx, channelId, ct))
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "通道不存在或已删除", channelId });
                }

                long boltId = 0, deviceId = 0;
                short channelNo = 0;
                decimal? refTimeRaw = null, refTempRaw = null;
                decimal? rp02Mpa = null, crossSectionMm2 = null, targetPreloadN = null;
                var bound = false;
                await using (var cmd = new NpgsqlCommand(@"
                    SELECT b.id, c.device_id, c.channel_no, c.ref_time_ns, c.ref_temp_c,
                           b.rp02_mpa, b.cross_section_mm2, b.target_preload_n
                    FROM channels c
                    JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                    WHERE c.id = @cid AND c.deleted_at IS NULL", conn, tx))
                {
                    cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct))
                    {
                        bound = true;
                        boltId = r.GetInt64(0);
                        deviceId = r.GetInt64(1);
                        channelNo = r.GetInt16(2);
                        refTimeRaw = r.IsDBNull(3) ? null : r.GetDecimal(3);
                        refTempRaw = r.IsDBNull(4) ? null : r.GetDecimal(4);
                        rp02Mpa = r.IsDBNull(5) ? null : r.GetDecimal(5);
                        crossSectionMm2 = r.IsDBNull(6) ? null : r.GetDecimal(6);
                        targetPreloadN = r.IsDBNull(7) ? null : r.GetDecimal(7);
                    }
                }
                if (!bound)
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = $"channel_id={channelId} 不存在或未绑定 bolt", channelId });
                }
                if (refTimeRaw is null || refTempRaw is null)
                {
                    await tx.RollbackAsync(ct);
                    return Results.BadRequest(new { error = "缺少 RefTime/RefTemp，无法应用拟合标定" });
                }
                var refTime = refTimeRaw.Value;
                var refTemp = refTempRaw.Value;

                decimal slope = 0, intercept = 0, rSquared = 0;
                long pointCount;
                string? pointsJson = null;
                await using (var cmd = new NpgsqlCommand(@"
                    WITH valid AS (
                        SELECT *
                        FROM torque_test_points
                        WHERE channel_id = @channel_id AND excluded = false
                    ),
                    fit AS (
                        SELECT
                            COUNT(*) AS n,
                            regr_slope(delta_tof_ns::double precision, estimated_preload_n::double precision) AS slope,
                            regr_intercept(delta_tof_ns::double precision, estimated_preload_n::double precision) AS intercept,
                            regr_r2(delta_tof_ns::double precision, estimated_preload_n::double precision) AS r2
                        FROM valid
                    ),
                    pts AS (
                        SELECT jsonb_agg(jsonb_build_object(
                            'id', id,
                            'torqueNm', torque_nm,
                            'estimatedPreloadN', estimated_preload_n,
                            'avgEchoTimeNs', avg_echo_time_ns,
                            'stddevEchoTimeNs', stddev_echo_time_ns,
                            'avgTempC', avg_temp_c,
                            'deltaTofNs', delta_tof_ns,
                            'sampleCount', sample_count
                        ) ORDER BY torque_nm, id) AS points_jsonb
                        FROM valid
                    )
                    SELECT fit.n, fit.slope, fit.intercept, fit.r2, pts.points_jsonb
                    FROM fit CROSS JOIN pts", conn, tx))
                {
                    cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    await r.ReadAsync(ct);

                    pointCount = r.GetInt64(0);
                    if (pointCount >= minPoints && !r.IsDBNull(1) && !r.IsDBNull(2) && !r.IsDBNull(3))
                    {
                        slope = Convert.ToDecimal(r.GetDouble(1));
                        intercept = Convert.ToDecimal(r.GetDouble(2));
                        rSquared = Convert.ToDecimal(r.GetDouble(3));
                        pointsJson = r.GetString(4);
                    }
                }
                if (pointsJson is null)
                {
                    await tx.RollbackAsync(ct);
                    return Results.BadRequest(new { error = "有效扭矩实验点不足，无法应用拟合", pointCount, minPoints });
                }

                if (slope <= 0)
                {
                    await tx.RollbackAsync(ct);
                    return Results.BadRequest(new { error = "拟合 K_L <= 0，拒绝应用", slope });
                }
                // 拟合得到的 kL 过量级护栏（ns/N，~1e-4）
                if (ValidateKLMagnitude(slope, refTime, YieldForceN(rp02Mpa, crossSectionMm2), targetPreloadN) is { } klReject)
                {
                    await tx.RollbackAsync(ct);
                    return klReject;
                }

                // R² 拟合优度门槛与取值依据见 CalibrationThresholds
                const decimal R2PassThreshold = CalibrationThresholds.R2Pass;
                const decimal R2RejectThreshold = CalibrationThresholds.R2Reject;
                if (rSquared < R2RejectThreshold)
                {
                    await tx.RollbackAsync(ct);
                    return Results.BadRequest(new
                    {
                        error = $"拟合优度 R² = {rSquared} 低于拒绝下限 {R2RejectThreshold}，线性度不达标，拒绝应用为标定。请补采载荷点或全部重做。",
                        rSquared, pointCount, slope, intercept
                    });
                }
                var passed = rSquared >= R2PassThreshold;

                var newCalibId = Guid.NewGuid();
                var uncertaintyRatio = req?.UncertaintyRatio ?? 0.30m;
                var kLUncertainty = slope * uncertaintyRatio;
                var note = req?.Note ??
                    $"应用扭矩阶梯拟合：有效点 {pointCount} 个，K_L={slope}, intercept={intercept}ns, R²={rSquared}。临时扭矩法拟合，非正式载荷标定。";

                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO calibration_records (
                        id, bolt_id, method, operator_user_id, reviewer_user_id,
                        loading_device, loading_device_cert, env_temp_c, env_humidity,
                        points_jsonb, k_l, k_l_uncertainty, k_l_intercept_ns,
                        r_squared, outliers_removed, uncertainty_breakdown, fitter_version,
                        ref_time_ns, ref_temp_c, passed, notes, review_state,
                        operator_signature_at, reviewer_signature_at, expires_at, created_at, sdk_version
                    ) VALUES (
                        @id, @bolt_id, 'torque_fit', @op, @rev,
                        'torque wrench', NULL, NULL, NULL,
                        @points::jsonb, @kl, @klu, @intercept,
                        @r2, 0, @unc::jsonb, 'torque-fit:v1',
                        @rt, @rtemp, @passed, @note, 'self_signed',
                        now(), now(), @exp, now(), @sdkver
                    )", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, newCalibId);
                    cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                    cmd.Parameters.AddWithValue("op", NpgsqlDbType.Bigint, userId);
                    cmd.Parameters.AddWithValue("rev", NpgsqlDbType.Bigint, userId);
                    cmd.Parameters.AddWithValue("points", NpgsqlDbType.Jsonb, pointsJson);
                    cmd.Parameters.AddWithValue("kl", NpgsqlDbType.Numeric, slope);
                    cmd.Parameters.AddWithValue("klu", NpgsqlDbType.Numeric, kLUncertainty);
                    cmd.Parameters.AddWithValue("intercept", NpgsqlDbType.Numeric, intercept);
                    cmd.Parameters.AddWithValue("r2", NpgsqlDbType.Numeric, rSquared);
                    cmd.Parameters.AddWithValue("unc", NpgsqlDbType.Jsonb,
                        System.Text.Json.JsonSerializer.Serialize(new { uncertaintyRatio, source = "torque fit temporary estimate" }));
                    cmd.Parameters.AddWithValue("rt", NpgsqlDbType.Numeric, refTime);
                    cmd.Parameters.AddWithValue("rtemp", NpgsqlDbType.Numeric, refTemp);
                    cmd.Parameters.AddWithValue("passed", NpgsqlDbType.Boolean, passed);
                    cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, note);
                    cmd.Parameters.AddWithValue("exp", NpgsqlDbType.Date,
                        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(req?.ExpiresDays ?? 7)));
                    cmd.Parameters.AddWithValue("sdkver", NpgsqlDbType.Text, FxConnectorFactory.SdkVersion);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // 仅在达标(passed)时激活并顶替旧标定；未达合格线的临时拟合只留档不激活——
                // 否则会把在产的好标定 supersede 掉，而 sink 对 passed=false 又不出预紧力 → 通道从"能出数"变"无数据"。
                if (passed)
                {
                    await SupersedeActiveCalibrationsAsync(conn, tx, boltId, newCalibId, ct);

                    await using (var cmd = new NpgsqlCommand(@"
                        UPDATE channels SET
                            k_l = @kl,
                            k_l_uncertainty = @klu,
                            k_l_intercept_ns = @intercept,
                            ref_time_ns = @rt,
                            ref_temp_c = @rtemp,
                            active_calibration_id = @cid,
                            updated_at = now()
                        WHERE id = @channel_id", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("kl", NpgsqlDbType.Numeric, slope);
                        cmd.Parameters.AddWithValue("klu", NpgsqlDbType.Numeric, kLUncertainty);
                        cmd.Parameters.AddWithValue("intercept", NpgsqlDbType.Numeric, intercept);
                        cmd.Parameters.AddWithValue("rt", NpgsqlDbType.Numeric, refTime);
                        cmd.Parameters.AddWithValue("rtemp", NpgsqlDbType.Numeric, refTemp);
                        cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Uuid, newCalibId);
                        cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                    // 仅在达标激活时才回写 bolts.state（未达标只留档不激活，状态不应变已标定）
                    await MarkBoltCalibratedAsync(conn, tx, boltId, ct);
                }

                var afterJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    channelId,
                    boltId,
                    calibrationId = newCalibId,
                    method = "torque_fit",
                    pointCount,
                    kL = slope,
                    kLInterceptNs = intercept,
                    rSquared,
                    passed
                });
                await WriteChannelAuditAsync(conn, tx, userId, "apply_torque_fit", channelId, afterJson, note, ct);

                await tx.CommitAsync(ct);
                resolver.Invalidate(deviceId, (ushort)channelNo);

                logger.LogWarning("已应用 torque_fit channel={ChannelId} KL={KL} intercept={Intercept} R2={R2}",
                    channelId, slope, intercept, rSquared);

                return Results.Ok(new
                {
                    channelId,
                    boltId,
                    calibrationId = newCalibId,
                    pointCount,
                    kL = slope,
                    kLInterceptNs = intercept,
                    kLUncertainty,
                    rSquared,
                    passed,
                    formula = "F = (Δt - kLInterceptNs) / kL",
                    warning = passed
                        ? "torque_fit 仍是扭矩法临时拟合，非正式载荷标定。"
                        : $"R²={rSquared} 介于 [{R2RejectThreshold}, {R2PassThreshold})，未达 0.98 合格线，已记为 passed=false 并留档，但【未激活到通道】——通道仍沿用原标定（避免把在产好标定顶替成无预紧力）。请补采载荷点重拟合。"
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                logger.LogError(ex, "应用 torque_fit 失败 channelId={ChannelId}", channelId);
                return Results.Problem(detail: "服务器内部错误，请稍后重试；详情见系统日志。", statusCode: 500);
            }
        })
        .WithName("ApplyTorqueFit")
        .WithSummary("应用扭矩阶梯拟合为当前 active calibration（含截距）");

        // ========== 正式多点载荷标定（GB/T 43232 路径 B/C + JJF 1059.1 + 两人会签）==========
        // 与 quick/torque 临时标定不同：用真实已知载荷点做最小二乘拟合，强制 R²≥0.98 + 评定扩展不确定度，
        // 且必须"操作人提交 → 另一名 reviewer 会签"才激活（不允许自签自用）。
        grp.MapPost("/load-calibrate", async (
            long channelId, LoadCalibrateRequest req,
            NpgsqlDataSource ds, HttpContext http,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("Calibration");
            var userId = UserContext.Require(http).UserId;

            if (req?.Points is null || req.Points.Count < 4)
                return Results.BadRequest(new { error = "正式载荷标定至少需要 4 个已知载荷点。" });
            if (req.Points.Any(p => p.ForceN <= 0))
                return Results.BadRequest(new { error = "载荷点的力值必须为正数（N），请检查输入。" });
            if (req.Points.GroupBy(p => p.ForceN).Any(g => g.Count() > 1))
                return Results.BadRequest(new { error = "存在重复力值的载荷点，请核对后重新录入。" });
            if (req.RefTimeNs <= 0m)
                return Results.BadRequest(new { error = "refTimeNs（基线声时 t₀）必须 > 0。" });

            // 提交阶段不激活、不失效缓存（会签时才失效），只需 bolt id + 护栏所需物性
            long boltId;
            decimal? rp02Mpa, crossSectionMm2, targetPreloadN;
            await using (var cmd = ds.CreateCommand(@"
                SELECT b.id, b.rp02_mpa, b.cross_section_mm2, b.target_preload_n
                FROM channels c
                JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                WHERE c.id = @cid AND c.deleted_at IS NULL"))
            {
                cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    return Results.NotFound(new { error = $"channel_id={channelId} 不存在或未绑定 bolt", channelId });
                boltId = r.GetInt64(0);
                rp02Mpa = r.IsDBNull(1) ? null : r.GetDecimal(1);
                crossSectionMm2 = r.IsDBNull(2) ? null : r.GetDecimal(2);
                targetPreloadN = r.IsDBNull(3) ? null : r.GetDecimal(3);
            }

            // 1. 最小二乘拟合（Δt = K_L·F + b，K_L 单位 ns/N）
            var points = req.Points.Select(p => new CalibrationPoint(p.ForceN, p.DeltaTofNs)).ToList();
            LinearFitResult fit;
            try
            {
                fit = new LeastSquaresFitter().Fit(points, useWeights: false, applyGrubbs: true);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            if (fit.KL <= 0)
                return Results.BadRequest(new { error = "拟合 K_L ≤ 0（方向/数据异常），拒绝。", kL = fit.KL });
            // 正式载荷标定的拟合 kL 也过量级护栏（卡 Δt 单位填错等）
            if (ValidateKLMagnitude((decimal)fit.KL, req.RefTimeNs, YieldForceN(rp02Mpa, crossSectionMm2), targetPreloadN) is { } klReject)
                return klReject;

            // 2. 正式载荷标定强制 R²≥0.98（不走 [0.95,0.98) 临时层，plan §8.5）
            const decimal r2PassThreshold = CalibrationThresholds.R2Pass;
            var rSquared = (decimal)fit.RSquared;
            if (rSquared < r2PassThreshold)
                return Results.BadRequest(new
                {
                    error = $"拟合优度 R²={rSquared} 低于正式载荷标定合格线 {r2PassThreshold}，请补采载荷点或重做。",
                    rSquared, pointCount = fit.PointCount, kL = fit.KL
                });

            // 3. JJF 1059.1 不确定度：A 类=拟合斜率标准误（ν=n-2）；可选 B 类=系统相对不确定度（载荷装置等，由计量输入）
            var components = new List<UncertaintyComponent>
            {
                new("u_A(fit_slope)", UncertaintyType.A, fit.KLStdError, 1.0, Math.Max(fit.PointCount - 2, 1)),
            };
            if (req.SystematicRelativeUncertainty is > 0m)
            {
                var uB = (double)req.SystematicRelativeUncertainty.Value * Math.Abs(fit.KL);
                components.Add(UncertaintyComponent.TypeB("u_B(systematic_rel)", uB));
            }
            var budget = UncertaintyEvaluator.Evaluate(components);

            var newCalibId = Guid.NewGuid();
            var pointsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                method = "load_calibrated",
                points = req.Points,
                fit = new { kL = fit.KL, interceptNs = fit.InterceptNs, rSquared = fit.RSquared, klStdError = fit.KLStdError, fit.PointCount, removed = fit.RemovedIndices },
            });
            var uncJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                combinedStandardUncertainty = budget.CombinedStandardUncertainty,
                effectiveDof = double.IsInfinity(budget.EffectiveDegreesOfFreedom) ? (double?)null : budget.EffectiveDegreesOfFreedom,
                coverageFactor = budget.CoverageFactor,
                expandedUncertainty = budget.ExpandedUncertainty,
                confidence = budget.ConfidenceLevel,
                components = budget.Components.Select(c => new { c.Name, type = c.Type.ToString(), c.StandardUncertainty, c.Sensitivity }),
            });

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO calibration_records (
                        id, bolt_id, method, operator_user_id, reviewer_user_id,
                        loading_device, loading_device_cert, env_temp_c, env_humidity,
                        points_jsonb, k_l, k_l_uncertainty, k_l_intercept_ns,
                        r_squared, outliers_removed, uncertainty_breakdown, fitter_version,
                        ref_time_ns, ref_temp_c, passed, notes, review_state,
                        operator_signature_at, reviewer_signature_at, expires_at, created_at, sdk_version
                    ) VALUES (
                        @id, @bolt_id, 'load_calibrated', @op, NULL,
                        @ld, @ldc, @et, @eh,
                        @pts::jsonb, @kl, @klu, @intercept,
                        @r2, @outliers, @unc::jsonb, 'lsq:v1',
                        @rt, @rtemp, true, @note, 'pending',
                        now(), NULL, @exp, now(), @sdkver
                    )", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, newCalibId);
                    cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                    cmd.Parameters.AddWithValue("op", NpgsqlDbType.Bigint, userId);
                    cmd.Parameters.AddWithValue("ld", NpgsqlDbType.Varchar, (object?)req.LoadingDevice ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("ldc", NpgsqlDbType.Varchar, (object?)req.LoadingDeviceCert ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("et", NpgsqlDbType.Numeric, (object?)req.EnvTempC ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("eh", NpgsqlDbType.Numeric, (object?)req.EnvHumidity ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("pts", NpgsqlDbType.Jsonb, pointsJson);
                    cmd.Parameters.AddWithValue("kl", NpgsqlDbType.Numeric, (decimal)fit.KL);
                    cmd.Parameters.AddWithValue("klu", NpgsqlDbType.Numeric, (decimal)budget.ExpandedUncertainty);
                    cmd.Parameters.AddWithValue("intercept", NpgsqlDbType.Numeric, (decimal)fit.InterceptNs);
                    cmd.Parameters.AddWithValue("r2", NpgsqlDbType.Numeric, rSquared);
                    cmd.Parameters.AddWithValue("outliers", NpgsqlDbType.Smallint, (short)fit.RemovedIndices.Count);
                    cmd.Parameters.AddWithValue("unc", NpgsqlDbType.Jsonb, uncJson);
                    cmd.Parameters.AddWithValue("rt", NpgsqlDbType.Numeric, req.RefTimeNs);
                    cmd.Parameters.AddWithValue("rtemp", NpgsqlDbType.Numeric, req.RefTempC);
                    cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)req.Note ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("exp", NpgsqlDbType.Date, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(req.ExpiresDays ?? 180)));
                    cmd.Parameters.AddWithValue("sdkver", NpgsqlDbType.Text, FxConnectorFactory.SdkVersion);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await WriteChannelAuditAsync(conn, tx, userId, "load_calibrate_submit", channelId,
                    System.Text.Json.JsonSerializer.Serialize(new { calibrationId = newCalibId, kL = fit.KL, rSquared = fit.RSquared, expandedUncertainty = budget.ExpandedUncertainty }),
                    $"提交正式载荷标定（待会签）bolt={boltId}", ct);

                await tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                logger.LogError(ex, "提交正式载荷标定失败 channel={Ch}", channelId);
                return Results.Problem(detail: "服务器内部错误，请稍后重试；详情见系统日志。", statusCode: 500);
            }

            logger.LogWarning("提交正式载荷标定 channel={Ch} calibId={Id} KL={KL} R²={R2} U={U}（待会签）",
                channelId, newCalibId, fit.KL, fit.RSquared, budget.ExpandedUncertainty);

            return Results.Ok(new
            {
                calibrationId = newCalibId,
                channelId, boltId,
                kL = fit.KL, interceptNs = fit.InterceptNs, rSquared = fit.RSquared,
                expandedUncertainty = budget.ExpandedUncertainty, coverageFactor = budget.CoverageFactor,
                pointCount = fit.PointCount, outliersRemoved = fit.RemovedIndices.Count,
                reviewState = "pending",
                note = "已提交，待会签。需【另一名】reviewer 调用 countersign 才激活生效（两人复核，不可自签）。"
            });
        })
        .WithName("LoadCalibrate")
        .WithSummary("正式多点载荷标定（最小二乘+R²≥0.98+JJF 不确定度，提交后待双签）");

        // 会签：必须由不同于提交人的 reviewer 复核，激活 pending 正式标定
        grp.MapPost("/calibrations/{calibrationId:guid}/countersign", async (
            long channelId, Guid calibrationId, HttpContext http,
            NpgsqlDataSource ds, IChannelResolver resolver,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("Calibration");
            var reviewerId = UserContext.Require(http).UserId;

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                // FOR UPDATE OF c：锁通道行把并发标定激活串行化（与 quick/torque/拟合同一把锁）。
                // reader 先读完再关闭、之后才回滚——开着 reader 回滚会抛异常把 404 变 500。
                long boltId = 0, deviceId = 0;
                short channelNo = 0;
                var channelFound = false;
                await using (var cmd = new NpgsqlCommand(@"
                    SELECT b.id, c.device_id, c.channel_no
                    FROM channels c
                    JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                    WHERE c.id = @cid AND c.deleted_at IS NULL
                    FOR UPDATE OF c", conn, tx))
                {
                    cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct))
                    {
                        channelFound = true;
                        boltId = r.GetInt64(0);
                        deviceId = r.GetInt64(1);
                        channelNo = r.GetInt16(2);
                    }
                }
                if (!channelFound)
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "通道不存在或未绑定 bolt", channelId });
                }

                long operatorId = 0, calibBoltId = 0;
                string? reviewState = null;
                decimal kl = 0, klu = 0, refTime = 0, refTemp = 0;
                decimal? intercept = null;
                await using (var cmd = new NpgsqlCommand(@"
                    SELECT operator_user_id, review_state, bolt_id, k_l, k_l_uncertainty, k_l_intercept_ns, ref_time_ns, ref_temp_c
                    FROM calibration_records WHERE id = @id FOR UPDATE", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, calibrationId);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct))
                    {
                        operatorId = r.GetInt64(0);
                        reviewState = r.GetString(1);
                        calibBoltId = r.GetInt64(2);
                        kl = r.GetDecimal(3);
                        klu = r.GetDecimal(4);
                        intercept = r.IsDBNull(5) ? null : r.GetDecimal(5);
                        refTime = r.GetDecimal(6);
                        refTemp = r.GetDecimal(7);
                    }
                }
                if (reviewState is null)
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "标定档案不存在", calibrationId });
                }

                if (calibBoltId != boltId)
                {
                    await tx.RollbackAsync(ct);
                    return Results.BadRequest(new { error = "该标定不属于此通道绑定的螺栓", calibrationId, channelId });
                }
                if (reviewState != "pending")
                {
                    await tx.RollbackAsync(ct);
                    return Results.Conflict(new { error = $"标定当前状态为 {reviewState}，仅 pending 可会签", calibrationId });
                }
                if (reviewerId == operatorId)
                {
                    await tx.RollbackAsync(ct);
                    return Results.BadRequest(new { error = "会签人必须与提交人不同（两人复核底线）。", operatorUserId = operatorId });
                }

                await using (var cmd = new NpgsqlCommand(@"
                    UPDATE calibration_records
                       SET reviewer_user_id = @rev, reviewer_signature_at = now(), review_state = 'approved',
                           -- 有效期从【激活(会签)】起算：pending 审核期间不消耗有效期
                           --（原按提交时刻固定,久审后的标定一激活就快到期）
                           expires_at = CURRENT_DATE + (expires_at - created_at::date)
                     WHERE id = @id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("rev", NpgsqlDbType.Bigint, reviewerId);
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, calibrationId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // supersede 同 bolt 旧的已生效标定
                await SupersedeActiveCalibrationsAsync(conn, tx, boltId, calibrationId, ct);

                await using (var cmd = new NpgsqlCommand(@"
                    UPDATE channels SET k_l=@kl, k_l_uncertainty=@klu, k_l_intercept_ns=@intercept,
                           ref_time_ns=@rt, ref_temp_c=@rtemp, active_calibration_id=@id, updated_at=now()
                     WHERE id=@channel_id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("kl", NpgsqlDbType.Numeric, kl);
                    cmd.Parameters.AddWithValue("klu", NpgsqlDbType.Numeric, klu);
                    cmd.Parameters.AddWithValue("intercept", NpgsqlDbType.Numeric, (object?)intercept ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("rt", NpgsqlDbType.Numeric, refTime);
                    cmd.Parameters.AddWithValue("rtemp", NpgsqlDbType.Numeric, refTemp);
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, calibrationId);
                    cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // 会签激活正式载荷标定后回写 bolts.state
                await MarkBoltCalibratedAsync(conn, tx, boltId, ct);

                await WriteChannelAuditAsync(conn, tx, reviewerId, "load_calibrate_countersign", channelId,
                    System.Text.Json.JsonSerializer.Serialize(new { calibrationId, operatorUserId = operatorId, reviewerUserId = reviewerId, kL = kl }),
                    $"会签激活正式载荷标定 bolt={boltId}", ct);

                await tx.CommitAsync(ct);
                resolver.Invalidate(deviceId, (ushort)channelNo);

                logger.LogWarning("会签激活正式载荷标定 channel={Ch} calibId={Id} operator={Op} reviewer={Rev}",
                    channelId, calibrationId, operatorId, reviewerId);

                return Results.Ok(new
                {
                    activated = true,
                    calibrationId, channelId, boltId,
                    operatorUserId = operatorId, reviewerUserId = reviewerId,
                    reviewState = "approved"
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                logger.LogError(ex, "会签失败 channel={Ch} calibId={Id}", channelId, calibrationId);
                return Results.Problem(detail: "服务器内部错误，请稍后重试；详情见系统日志。", statusCode: 500);
            }
        })
        .WithName("CountersignCalibration")
        .WithSummary("会签激活正式载荷标定（必须由不同于提交人的 reviewer 执行）");

        // 驳回：把 pending 正式标定置为 rejected（不激活、不动通道）。补齐"只能会签、无法驳回"导致 pending 堆积的缺口。
        grp.MapPost("/calibrations/{calibrationId:guid}/reject", async (
            long channelId, Guid calibrationId, string? reason, HttpContext http,
            NpgsqlDataSource ds, ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("Calibration");
            var reviewerId = UserContext.Require(http).UserId;
            var rejectReason = string.IsNullOrWhiteSpace(reason) ? "（未填写驳回原因）" : reason.Trim();

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                long channelBoltId;
                await using (var q = new NpgsqlCommand(@"
                    SELECT b.id FROM channels c
                    JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                    WHERE c.id = @cid AND c.deleted_at IS NULL", conn, tx))
                {
                    q.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                    var v = await q.ExecuteScalarAsync(ct);
                    if (v is null) { await tx.RollbackAsync(ct); return Results.NotFound(new { error = "通道不存在或未绑定 bolt", channelId }); }
                    channelBoltId = (long)v;
                }

                // reader 先关闭再回滚（否则 404 变 500）
                long calibBoltId = 0; string? reviewState = null;
                await using (var q = new NpgsqlCommand(
                    "SELECT bolt_id, review_state FROM calibration_records WHERE id = @id FOR UPDATE", conn, tx))
                {
                    q.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, calibrationId);
                    await using var r = await q.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct))
                    {
                        calibBoltId = r.GetInt64(0);
                        reviewState = r.GetString(1);
                    }
                }
                if (reviewState is null) { await tx.RollbackAsync(ct); return Results.NotFound(new { error = "标定档案不存在", calibrationId }); }
                if (calibBoltId != channelBoltId) { await tx.RollbackAsync(ct); return Results.BadRequest(new { error = "该标定不属于此通道绑定的螺栓", calibrationId, channelId }); }
                if (reviewState != "pending") { await tx.RollbackAsync(ct); return Results.Conflict(new { error = $"标定当前状态为 {reviewState}，仅 pending 可驳回", calibrationId }); }

                await using (var upd = new NpgsqlCommand(@"
                    UPDATE calibration_records
                       SET review_state = 'rejected', reviewer_user_id = @rev, reviewer_signature_at = now(),
                           notes = COALESCE(notes, '') || @suffix
                     WHERE id = @id AND review_state = 'pending'", conn, tx))
                {
                    upd.Parameters.AddWithValue("rev", NpgsqlDbType.Bigint, reviewerId);
                    upd.Parameters.AddWithValue("suffix", NpgsqlDbType.Text, $" [驳回:{rejectReason}]");
                    upd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, calibrationId);
                    if (await upd.ExecuteNonQueryAsync(ct) == 0) { await tx.RollbackAsync(ct); return Results.Conflict(new { error = "状态已变更，驳回未生效", calibrationId }); }
                }

                await WriteChannelAuditAsync(conn, tx, reviewerId, "load_calibrate_reject", channelId,
                    System.Text.Json.JsonSerializer.Serialize(new { calibrationId, reason = rejectReason }),
                    $"驳回 pending 正式标定 #{calibrationId}：{rejectReason}", ct);

                await tx.CommitAsync(ct);
                logger.LogWarning("驳回 pending 标定 channel={Ch} calibId={Id} reviewer={Rev} reason={Reason}",
                    channelId, calibrationId, reviewerId, rejectReason);
                return Results.Ok(new { rejected = true, calibrationId, channelId, reviewState = "rejected", reason = rejectReason });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                logger.LogError(ex, "驳回失败 channel={Ch} calibId={Id}", channelId, calibrationId);
                return Results.Problem(detail: "服务器内部错误，请稍后重试；详情见系统日志。", statusCode: 500);
            }
        })
        .WithName("RejectCalibration")
        .WithSummary("驳回 pending 正式标定（reviewer）");

        // 查通道标定历史。
        // 只读档案对 LoggedIn 开放（不挂在 ReviewerAccess 组上）：operator 打开螺栓详情页也要看标定履历，
        // 原先 403 会把详情页 init 整体打断（operator 看到"加载失败"）。写操作仍全部 reviewer+。
        app.MapGet("/api/channels/{channelId:long}/calibrations", async (long channelId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            // 软删过滤 + 上限护栏（单通道档案通常几十条，200 足够展示；防长期运行后无界膨胀）
            const string sql = @"
                SELECT cr.id, cr.method, cr.k_l, cr.k_l_uncertainty, cr.k_l_intercept_ns, cr.r_squared,
                       cr.ref_time_ns, cr.ref_temp_c, cr.passed,
                       cr.created_at, cr.expires_at, cr.superseded_by, cr.notes, cr.review_state,
                       cr.operator_user_id
                FROM calibration_records cr
                JOIN bolts b ON b.id = cr.bolt_id AND b.deleted_at IS NULL
                JOIN channels c ON c.id = b.channel_id AND c.deleted_at IS NULL
                WHERE c.id = @cid
                ORDER BY cr.created_at DESC
                LIMIT 200";

            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);

            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new
                {
                    id = r.GetGuid(0),
                    method = r.GetString(1),
                    kL = r.GetDecimal(2),
                    kLUncertainty = r.GetDecimal(3),
                    kLInterceptNs = r.IsDBNull(4) ? null : (decimal?)r.GetDecimal(4),
                    rSquared = r.GetDecimal(5),
                    refTimeNs = r.GetDecimal(6),
                    refTempC = r.GetDecimal(7),
                    passed = r.GetBoolean(8),
                    createdAt = r.GetDateTime(9),
                    expiresAt = r.GetDateTime(10),
                    supersededBy = r.IsDBNull(11) ? null : (Guid?)r.GetGuid(11),
                    // "生效中" = 未被取代【且】已激活（自签或会签）。pending/rejected 未被取代但从未生效，
                    // 原来只看 superseded_by 会给待会签记录错误打上"生效"徽标
                    isActive = r.IsDBNull(11) && r.GetString(13) is "approved" or "self_signed",
                    notes = r.IsDBNull(12) ? null : r.GetString(12),
                    reviewState = r.GetString(13),
                    // 两人四眼：仅 approved 表示经【另一名】reviewer 会签；self_signed 为操作人自签的临时标定
                    countersigned = r.GetString(13) == "approved",
                    // 提交人 id：前端据此把"会签"按钮对提交人本人禁用（后端仍强制校验）
                    operatorUserId = r.IsDBNull(14) ? null : (long?)r.GetInt64(14)
                });
            }
            return Results.Ok(new { channelId, count = items.Count, items });
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithTags("Calibration")
        .WithName("GetCalibrationHistory")
        .WithSummary("通道标定历史（按时间倒序，含 superseded 关系；只读，登录即可）");

        return app;
    }

    /// <summary>护栏诊断文案里的可空数值：算不出来（样本不足/半窗断流）显示 —。</summary>
    private static string Fmt(decimal? v) => v?.ToString("0.###") ?? "—";

    private static decimal ParseNominalDiameterMm(string nominalDiam)
    {
        var chars = nominalDiam.Where(c => char.IsDigit(c) || c == '.').ToArray();
        return decimal.TryParse(new string(chars), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;
    }

    /// <summary>
    /// K_L 量纲/量级护栏（全系统约定 K_L = Δt/F，单位 ns/N，量级约 1e-4）。
    /// 通过返回 null，不通过返回可直接当响应用的 <see cref="Results.BadRequest(object?)"/>。
    /// <para>
    /// 不只用于 quick（入参 kL），也用于 torque / apply-torque-fit / load 等【计算得到 kL】的路径——
    /// 这些路径虽然不会出现照证书填 1/N(~1e-11) 的错误，但仍可能因 RefTime/Δt/载荷异常算出
    /// 物理上不可能的 kL，统一卡一道护栏（plan §0 / sdk-api-facts §12）。
    /// </para>
    /// 两道物理边界（任一所需螺栓参数缺失则跳过该道，best-effort）：
    /// <list type="bullet">
    /// <item>上界：屈服力 F_y 下推得的声时变化 K_L·F_y 不应 ≥ 基线 t₀（声弹性 TOF 变化只是 t₀ 极小比例）。</item>
    /// <item>下界：目标预紧力 F_target 下推得的声时变化 K_L·F_target 必须 > 1 ns 测量噪声底。</item>
    /// </list>
    /// </summary>
    private static IResult? ValidateKLMagnitude(decimal kL, decimal refTimeNs, decimal? yieldForceN, decimal? targetPreloadN)
    {
        if (kL <= 0m)
            return Results.BadRequest(new { error = "kL 必须 > 0。本系统约定 K_L=Δt/F（单位 ns/N，量级约 1e-4）；勿按标定证书的 1/N 约定（约 1e-11）填写，两者相差一个 t₀。", kL });
        if (refTimeNs <= 0m)
            return Results.BadRequest(new { error = "refTimeNs（基线声时 t₀）必须 > 0。", refTimeNs });
        if (yieldForceN is > 0m && kL * yieldForceN.Value >= refTimeNs)
            return Results.BadRequest(new
            {
                error = "kL 量级过大：屈服力下推得的声时变化 ≥ 基线 t₀，物理上不可能。请确认 kL 用 ns/N 约定（约 1e-4），而非 1/N（约 1e-11）。",
                kL, yieldForceN, refTimeNs
            });
        if (targetPreloadN is > 0m && kL * targetPreloadN.Value <= 1m)
            return Results.BadRequest(new
            {
                error = "kL 量级过小：目标预紧力下推得的声时变化 ≤ 1 ns（低于测量噪声）。请确认 kL 用 ns/N 约定（约 1e-4），而非 1/N（约 1e-11）。",
                kL, targetPreloadN, deltaAtTargetNs = kL * targetPreloadN.Value
            });
        return null;
    }

    /// <summary>屈服力 F_y = Rp0.2 · A_e（MPa·mm² = N）；缺任一参数返回 null（护栏跳过上界）。</summary>
    private static decimal? YieldForceN(decimal? rp02Mpa, decimal? crossSectionMm2)
        => rp02Mpa is > 0m && crossSectionMm2 is > 0m ? rp02Mpa.Value * crossSectionMm2.Value : null;

    /// <summary>
    /// 统一写 audit_log（target_type='channel'）。CalibrationEndpoints 内多处标定操作复用，
    /// 消除重复的 INSERT 样板；调用方在事务内传入 conn/tx + 具体 action/after/note。
    /// </summary>
    private static async Task WriteChannelAuditAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        long userId, string action, long channelId, string afterJson, string note, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO audit_log (user_id, action, target_type, target_id, after_jsonb, note, at, ip_addr)
            VALUES (@uid, @action, 'channel', @cid, @after::jsonb, @note, now(), @ip)", conn, tx);
        cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, userId);
        cmd.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action);
        cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
        cmd.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb, afterJson);
        cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, note);
        AuditClientIp.Bind(cmd.Parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 标定激活后把螺栓状态置 'calibrated'。历史遗漏点：API 标定路径（quick/torque/torque_fit/countersign）
    /// 只更新 channels 的 k_l/active_calibration_id，漏写 bolts.state，导致已标定螺栓在总览/资产/验收报告里被误判"未标定"。
    /// 判定口径已改为按通道有效标定派生（单一事实源），此处同步 state 让冗余字段不再漂移、且旧数据经再标定自愈。
    /// </summary>
    private static async Task MarkBoltCalibratedAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long boltId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE bolts SET state='calibrated', updated_at=now() WHERE id=@id AND state <> 'calibrated'", conn, tx);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, boltId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 事务内锁定通道行（含软删守卫），把并发标定激活串行化。
    /// 激活路径（quick / torque / apply-torque-fit / 会签）都会 UPDATE channels 的
    /// k_l / active_calibration_id 冗余字段——不加锁时两个并发标定的后提交者可能把冗余
    /// 写回已被 superseded 的旧档案，热路径用错参数算预紧力（安全攸关）。
    /// 返回 false = 通道不存在或已软删（调用方回滚并返回业务错误）。
    /// </summary>
    private static async Task<bool> LockChannelForCalibrationAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long channelId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM channels WHERE id = @id AND deleted_at IS NULL FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// 把同一 bolt 下【已生效（approved 两人会签 或 self_signed 自签）且未被取代】的旧标定
    /// 标记为被 <paramref name="newCalibId"/> superseded。
    /// 过滤 review_state IN ('approved','self_signed')，刻意排除 'pending'：否则新标定会误把一条
    /// "待会签(pending)"的正式载荷标定也标成 superseded，导致它永远无法被会签激活。
    /// </summary>
    private static async Task SupersedeActiveCalibrationsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long boltId, Guid newCalibId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE calibration_records SET superseded_by = @new_id
             WHERE bolt_id = @bolt_id AND superseded_by IS NULL AND id <> @new_id
               AND review_state IN ('approved', 'self_signed')", conn, tx);
        cmd.Parameters.AddWithValue("new_id", NpgsqlDbType.Uuid, newCalibId);
        cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>快速标定请求体</summary>
public sealed record QuickCalibrateRequest(
    decimal KL,
    decimal RefTimeNs,
    decimal RefTempC,
    decimal? KLUncertainty = null,
    string? LoadingDevice = null,
    string? LoadingDeviceCert = null,
    decimal? EnvTempC = null,
    decimal? EnvHumidity = null,
    object? Points = null,
    string? Note = null,
    int? ExpiresDays = null);

/// <summary>
/// 扭矩法临时标定请求体。
/// <paramref name="Force"/>=true 跳过采样稳定性护栏（信号天然抖等特殊现场），跳过记入审计。
/// </summary>
public sealed record TorqueCalibrateRequest(
    decimal TorqueNm,
    decimal? NutFactor = null,
    decimal? NominalDiameterMm = null,
    decimal? EffectiveLengthMm = null,
    decimal? TotalLengthMm = null,
    decimal? CrossSectionMm2 = null,
    decimal? RefTimeNs = null,
    decimal? RefTempC = null,
    int? SampleWindowSeconds = null,
    int? MinSamples = null,
    decimal? UncertaintyRatio = null,
    string? LoadingDevice = null,
    string? Note = null,
    int? ExpiresDays = null,
    bool? Force = null);

/// <summary>
/// 扭矩阶梯实验点采集请求体。
/// <paramref name="Force"/>=true 跳过采样稳定性护栏（信号天然抖等特殊现场），跳过记入审计。
/// </summary>
public sealed record TorqueTestPointRequest(
    decimal TorqueNm,
    decimal? NutFactor = null,
    decimal? NominalDiameterMm = null,
    int? SampleWindowSeconds = null,
    int? MinSamples = null,
    string? Note = null,
    bool? Force = null);

/// <summary>
/// 扭矩阶梯点剔除 / 恢复请求体。<paramref name="Excluded"/>=true 时 <paramref name="Reason"/> 必填，
/// 剔除原因要进标定档案和阶梯报告，事后得说得清这个点为什么不算数。
/// </summary>
public sealed record TorqueTestPointPatchRequest(
    bool? Excluded = null,
    string? Reason = null);

/// <summary>应用扭矩拟合请求体</summary>
public sealed record ApplyTorqueFitRequest(
    int? MinPoints = null,
    decimal? UncertaintyRatio = null,
    string? Note = null,
    int? ExpiresDays = null);

/// <summary>正式载荷标定的一个数据点：已知载荷 F(N) + 温补后声时差 Δt(ns)。</summary>
public sealed record LoadCalibratePoint(double ForceN, double DeltaTofNs);

/// <summary>正式多点载荷标定请求体。</summary>
/// <param name="Points">已知载荷点（≥4），由拉伸机/测力垫圈给出 F，配套实测温补后 Δt</param>
/// <param name="RefTimeNs">基线声时 t₀（ns），存档用</param>
/// <param name="RefTempC">基线温度（°C），存档用</param>
/// <param name="SystematicRelativeUncertainty">可选：系统性相对不确定度（如载荷装置相对精度 0.005=0.5%），作为 JJF B 类分量；不传则只用拟合 A 类</param>
/// <param name="LoadingDevice">加载装置名称</param>
/// <param name="LoadingDeviceCert">加载装置检定证书号</param>
/// <param name="EnvTempC">环境温度</param>
/// <param name="EnvHumidity">环境湿度</param>
/// <param name="Note">备注</param>
/// <param name="ExpiresDays">有效期天数（默认 180，plan 建议 ≤180）</param>
public sealed record LoadCalibrateRequest(
    IReadOnlyList<LoadCalibratePoint> Points,
    decimal RefTimeNs,
    decimal RefTempC,
    decimal? SystematicRelativeUncertainty = null,
    string? LoadingDevice = null,
    string? LoadingDeviceCert = null,
    decimal? EnvTempC = null,
    decimal? EnvHumidity = null,
    string? Note = null,
    int? ExpiresDays = null);

