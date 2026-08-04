using BoltPreload.Api.Auth;
using BoltPreload.Application.Devices;
using BoltPreload.Application.Ingestion;
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
    // 【软删约定，跨切面统一】：所有健康/告警聚合都必须排除软删实体。本 CTE 对 channels / monitor_groups /
    //   turbines / bolts 均加 deleted_at IS NULL；各级 open_alarms 子查询同样按 bolts.deleted_at 过滤
    //   （见 /projects、/turbines、/groups 与 DashboardEndpoints 的按 severity 计数）。新增聚合请沿用此约定。
    internal const string BoltStatusCte = @"
        bolt_status AS (
            SELECT b.id, b.group_id, mg.turbine_id, t.project_id,
                   l.preload_n, l.preload_ratio, l.ts AS last_ts,
                   CASE
                     -- 是否已标定：以通道有效标定（k_l + active_calibration_id）为单一事实源，
                     -- 与采集算预紧力所依据的完全一致；不再单看 bolts.state（该冗余字段由标定接口维护，
                     -- 历史上 API 标定路径漏写会漂移，导致已标定却显示未标定）。
                     WHEN c.k_l IS NULL OR c.active_calibration_id IS NULL THEN 'uncalibrated'
                     -- 标定过期（UTC 日粒度，与采集热路径 CachedCalibration.IsExpiredAsOf 同口径）：
                     -- 归 uncalibrated（前端文案'未标定/过期'）。不加这一档时，过期后到下一条测量间
                     -- 会拿旧 preload 继续判 normal（假安全窗口）、之后又落进 nodata 误导排查方向。
                     WHEN acr.expires_at IS NOT NULL AND (now() AT TIME ZONE 'UTC')::date > acr.expires_at THEN 'uncalibrated'
                     WHEN l.preload_n IS NULL OR l.ts < now() - interval '10 minutes' THEN 'nodata'
                     WHEN b.min_preload_n IS NOT NULL AND l.preload_n < b.min_preload_n THEN 'loose'
                     WHEN b.min_preload_n IS NOT NULL AND l.preload_n < b.min_preload_n * 1.1 THEN 'warn'
                     -- 偏高/超张力：保持率 > 1.10（与告警 Alarm:StaticThreshold:TriggerHighRatio 默认对齐）。
                     -- 预紧力远超目标同样是风险（可能接近屈服 / 标定目标配错），不能一律算 normal。
                     WHEN l.preload_ratio > 1.10 THEN 'high'
                     ELSE 'normal'
                   END AS status
            FROM bolts b
            JOIN channels c ON c.id = b.channel_id AND c.deleted_at IS NULL
            JOIN monitor_groups mg ON mg.id = b.group_id AND mg.deleted_at IS NULL
            JOIN turbines t ON t.id = mg.turbine_id AND t.deleted_at IS NULL
            LEFT JOIN calibration_records acr ON acr.id = c.active_calibration_id
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
        count(*) FILTER (WHERE status='nodata') AS nodata,
        count(*) FILTER (WHERE status='high') AS high";

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
                       COALESCE(bs.uncalibrated,0), COALESCE(bs.nodata,0), COALESCE(bs.high,0),
                       (SELECT count(*) FROM alarms a JOIN bolts b2 ON b2.id=a.bolt_id AND b2.deleted_at IS NULL
                          JOIN monitor_groups mg2 ON mg2.id=b2.group_id AND mg2.deleted_at IS NULL
                          JOIN turbines t2 ON t2.id=mg2.turbine_id AND t2.deleted_at IS NULL
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
                    openAlarms = r.GetInt64(13),
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
                       COALESCE(bs.uncalibrated,0), COALESCE(bs.nodata,0), COALESCE(bs.high,0),
                       (SELECT count(*) FROM alarms a JOIN bolts b2 ON b2.id=a.bolt_id AND b2.deleted_at IS NULL
                          JOIN monitor_groups mg2 ON mg2.id=b2.group_id AND mg2.deleted_at IS NULL
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
                    openAlarms = r.GetInt64(15),
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
                       COALESCE(bs.uncalibrated,0), COALESCE(bs.nodata,0), COALESCE(bs.high,0),
                       (SELECT count(*) FROM alarms a JOIN bolts b2 ON b2.id=a.bolt_id AND b2.deleted_at IS NULL
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
                    openAlarms = r.GetInt64(13),
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

        // 新建螺栓建档并绑定通道——现场"多接/新接探头"的处理闭环。
        // bolts.channel_id NOT NULL 且每通道唯一活跃螺栓（ux_bolts_active_channel），
        // 因此建档必须带通道，一个事务完成：INSERT bolt → 通道置 bound →（可选）回填
        // unbound_measurements → channel_binding_history + audit_log → resolver 缓存失效。
        grp.MapPost("/monitor-groups/{groupId:long}/bolts", async (
            long groupId, BoltCreateRequest? body, HttpContext http,
            NpgsqlDataSource ds, IChannelResolver resolver, CancellationToken ct) =>
        {
            if (body is null || body.ChannelId <= 0)
                return Results.BadRequest(new { error = "channelId 必填且为正数" });
            if (body.Code is { Length: > 64 })
                return Results.BadRequest(new { error = "code 长度不能超过 64 字符" });
            if (body.PositionLabel is { Length: > 64 })
                return Results.BadRequest(new { error = "positionLabel 长度不能超过 64 字符" });
            if (body.TargetPreloadN is < 0 || body.MinPreloadN is < 0)
                return Results.BadRequest(new { error = "目标预紧力 / 报警下限不能为负" });
            if (body.MinPreloadN is > 0 && body.TargetPreloadN is > 0 && body.MinPreloadN > body.TargetPreloadN)
                return Results.BadRequest(new { error = "报警下限不能大于目标预紧力" });
            if (body.CrossSectionMm2 is <= 0 || body.EffectiveLengthMm is <= 0 || body.TotalLengthMm is <= 0
                || body.ElasticModulusGpa is <= 0 || body.Rp02Mpa is <= 0 || body.SoundVelocityMs is <= 0)
                return Results.BadRequest(new { error = "规格参数必须为正数" });

            // 规格预设：截面积按 GB/T 3098.1 应力截面积；未知规格必须显式给 crossSectionMm2
            var nominal = string.IsNullOrWhiteSpace(body.NominalDiam) ? BoltSpecPresets.DefaultNominalDiam : body.NominalDiam.Trim().ToUpperInvariant();
            var csa = body.CrossSectionMm2 ?? (BoltSpecPresets.StressAreaMm2.TryGetValue(nominal, out var a) ? a : (decimal?)null);
            if (csa is null)
                return Results.BadRequest(new { error = $"未知螺栓规格 {nominal}，请显式提供 crossSectionMm2", knownSpecs = BoltSpecPresets.StressAreaMm2.Keys });
            // 未知等级与未知规格同口径显式拒绝：屈服强度要喂给 GB 适用范围判断（30%~90% Rp0.2），
            // 静默套用 8.8 级的 640 会让 9.8 级（Rp0.2≈720）或手误的等级拿到错的安全边界。
            var grade = string.IsNullOrWhiteSpace(body.Grade) ? BoltSpecPresets.DefaultGrade : body.Grade.Trim();
            if (body.Rp02Mpa is null && !BoltSpecPresets.GradeRp02Mpa.ContainsKey(grade))
                return Results.BadRequest(new { error = $"未知性能等级 {grade}，请显式提供 rp02Mpa", knownGrades = BoltSpecPresets.GradeRp02Mpa.Keys });
            var rp02 = body.Rp02Mpa ?? BoltSpecPresets.GradeRp02Mpa[grade];

            var user = UserContext.Require(http);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            string? groupCode = null;
            long groupProjectId = 0;
            await using (var cmd = new NpgsqlCommand(
                "SELECT code, project_id FROM monitor_groups WHERE id = @gid AND deleted_at IS NULL", conn, tx))
            {
                cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, groupId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    groupCode = r.GetString(0);
                    groupProjectId = r.GetInt64(1);
                }
            }
            if (groupCode is null)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "监测组不存在", groupId });
            }

            // 通道行锁：与 bind-bolt/move-channel 同一把锁，防并发抢同一通道。
            // 注意：必须先关 reader 再 Rollback（同一连接上开着 reader 时回滚会抛，404 变 500）
            long deviceId = 0, deviceProjectId = 0;
            short channelNo = 0;
            var channelFound = false;
            await using (var cmd = new NpgsqlCommand(@"
                SELECT c.device_id, c.channel_no, d.project_id
                FROM channels c
                JOIN devices d ON d.id = c.device_id
                WHERE c.id = @cid AND c.deleted_at IS NULL
                FOR UPDATE OF c", conn, tx))
            {
                cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, body.ChannelId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    channelFound = true;
                    deviceId = r.GetInt64(0);
                    channelNo = r.GetInt16(1);
                    deviceProjectId = r.GetInt64(2);
                }
            }
            if (!channelFound)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "通道不存在或已删除", channelId = body.ChannelId });
            }

            // 项目边界：通道所属设备与监测组必须同项目，否则会把 A 项目的通道错绑进 B 项目的法兰
            //（bind-bolt/move-channel 只防跨设备，建档是唯一"从零建立归属"的入口，必须在这里拦）
            if (deviceProjectId != groupProjectId)
            {
                await tx.RollbackAsync(ct);
                return Results.BadRequest(new
                {
                    error = "通道所属设备与监测组不在同一项目，禁止跨项目绑定",
                    channelId = body.ChannelId, deviceProjectId, groupProjectId
                });
            }

            long existingBoltId = 0;
            string? existingBoltCode = null;
            await using (var cmd = new NpgsqlCommand(@"
                SELECT id, code FROM bolts
                WHERE channel_id = @cid AND deleted_at IS NULL
                LIMIT 1", conn, tx))
            {
                cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, body.ChannelId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    existingBoltId = r.GetInt64(0);
                    existingBoltCode = r.GetString(1);
                }
            }
            if (existingBoltCode is not null)
            {
                await tx.RollbackAsync(ct);
                return Results.Conflict(new
                {
                    error = "通道已绑定其他螺栓",
                    channelId = body.ChannelId,
                    existingBoltId,
                    existingBoltCode
                });
            }

            // code 缺省自动生成中文“通道X-Y号螺栓”（与自动建档同一套 ChannelNaming）；
            // 与既有（含软删）冲突时追加 -2/-3…
            // （UNIQUE(group_id, code) 不排除软删行，解绑后重建同通道螺栓是常见场景）
            string code;
            if (string.IsNullOrWhiteSpace(body.Code))
            {
                var baseCode = ChannelNaming.DefaultBoltCode(channelNo);
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var cmd = new NpgsqlCommand(@"
                    SELECT code FROM bolts
                    WHERE group_id = @gid AND (code = @base OR code LIKE @base || '-%')", conn, tx))
                {
                    cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, groupId);
                    cmd.Parameters.AddWithValue("base", NpgsqlDbType.Varchar, baseCode);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct)) taken.Add(r.GetString(0));
                }
                code = baseCode;
                for (var n = 2; taken.Contains(code); n++) code = $"{baseCode}-{n}";
            }
            else
            {
                code = body.Code.Trim();
            }

            long boltId;
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO bolts (
                    group_id, channel_id, code, nominal_diam, grade, rp02_mpa, material,
                    elastic_modulus_gpa, effective_length_mm, total_length_mm, cross_section_mm2,
                    sound_velocity_ms, target_preload_n, min_preload_n, position_label,
                    install_date, gb_compliant, state, enabled)
                VALUES (@gid, @cid, @code, @nd, @grade, @rp02, @material,
                        @e, @eff_len, @total_len, @csa,
                        @v, @target, @min, @pos,
                        CURRENT_DATE, true, 'uncalibrated', true)
                ON CONFLICT (group_id, code) DO NOTHING
                RETURNING id", conn, tx))
            {
                cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, groupId);
                cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, body.ChannelId);
                cmd.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
                cmd.Parameters.AddWithValue("nd", NpgsqlDbType.Varchar, nominal);
                cmd.Parameters.AddWithValue("grade", NpgsqlDbType.Varchar, grade);
                cmd.Parameters.AddWithValue("rp02", NpgsqlDbType.Numeric, rp02);
                cmd.Parameters.AddWithValue("material", NpgsqlDbType.Varchar, string.IsNullOrWhiteSpace(body.Material) ? BoltSpecPresets.DefaultMaterial : body.Material.Trim());
                cmd.Parameters.AddWithValue("e", NpgsqlDbType.Numeric, body.ElasticModulusGpa ?? BoltSpecPresets.DefaultElasticModulusGpa);
                cmd.Parameters.AddWithValue("eff_len", NpgsqlDbType.Numeric, body.EffectiveLengthMm ?? BoltSpecPresets.DefaultEffectiveLengthMm);
                cmd.Parameters.AddWithValue("total_len", NpgsqlDbType.Numeric, body.TotalLengthMm ?? BoltSpecPresets.DefaultTotalLengthMm);
                cmd.Parameters.AddWithValue("csa", NpgsqlDbType.Numeric, csa.Value);
                cmd.Parameters.AddWithValue("v", NpgsqlDbType.Numeric, body.SoundVelocityMs ?? BoltSpecPresets.DefaultSoundVelocityMs);
                cmd.Parameters.AddWithValue("target", NpgsqlDbType.Numeric, (object?)body.TargetPreloadN ?? DBNull.Value);
                cmd.Parameters.AddWithValue("min", NpgsqlDbType.Numeric, (object?)body.MinPreloadN ?? DBNull.Value);
                cmd.Parameters.AddWithValue("pos", NpgsqlDbType.Varchar, (object?)(string.IsNullOrWhiteSpace(body.PositionLabel) ? null : body.PositionLabel.Trim()) ?? DBNull.Value);
                var scalar = await cmd.ExecuteScalarAsync(ct);
                if (scalar is not long id)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Conflict(new { error = "该组已存在同编号螺栓", groupId, code });
                }
                boltId = id;
            }

            await using (var cmd = new NpgsqlCommand(@"
                UPDATE channels
                   SET business_state = 'bound', updated_at = now()
                 WHERE id = @cid", conn, tx))
            {
                cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, body.ChannelId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // 暂存数据回填：与 bind-bolt 的回填完全同构（measurements 迁入 + wave_features 归属 + 清空暂存）
            long backfilled = 0;
            if (body.BackfillUnbound ?? true)
            {
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO measurements (
                        bolt_id, channel_id, ts, ingested_at,
                        echo_time_ns, delta_tof_ns, sp_temp_c, gain, status_code,
                        wave_blob_url, algo_version
                    )
                    SELECT @bolt_id, channel_id, ts, ingested_at,
                           echo_time_ns, delta_tof_ns, sp_temp_c, COALESCE(gain, 0), status_code,
                           wave_blob_url, 'phase1:raw:backfilled'
                    FROM unbound_measurements
                    WHERE channel_id = @channel_id
                    ON CONFLICT (channel_id, ts) DO NOTHING", conn, tx))
                {
                    cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                    cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, body.ChannelId);
                    backfilled = await cmd.ExecuteNonQueryAsync(ct);
                }

                await using (var cmd = new NpgsqlCommand(@"
                    UPDATE wave_features
                       SET bolt_id = @bolt_id
                     WHERE channel_id = @channel_id
                       AND bolt_id IS NULL", conn, tx))
                {
                    cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                    cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, body.ChannelId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await using (var cmd = new NpgsqlCommand(
                    "DELETE FROM unbound_measurements WHERE channel_id = @channel_id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, body.ChannelId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            // 绑定历史：action='bind'（首次建档无源通道），不动通道既有标定（keep_uncomputed）——
            // 建档前若已对该通道做过标定（如先标定后建档的调试流），不能被静默清掉
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO channel_binding_history (
                    bolt_id, from_channel_id, to_channel_id, action, calibration_action,
                    moved_by, before_jsonb, after_jsonb, note
                )
                SELECT @bolt_id, NULL, @channel_id, 'bind', 'keep_uncomputed',
                       @user_id, NULL, to_jsonb(b), @note
                FROM bolts b
                WHERE b.id = @bolt_id", conn, tx))
            {
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, body.ChannelId);
                cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, user.UserId);
                cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)body.Note ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var audit = new NpgsqlCommand(@"
                INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
                SELECT @uid, 'create_bolt', 'bolt', @id, NULL, to_jsonb(b), @note, now()
                FROM bolts b WHERE b.id = @id", conn, tx))
            {
                audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                audit.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, boltId);
                audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                    $"新建螺栓 {code} 并绑定 CH{channelNo}（设备 {deviceId}，回填 {backfilled} 条暂存数据）");
                await audit.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            resolver.Invalidate(deviceId, (ushort)channelNo);

            return Results.Ok(new
            {
                boltId, code, groupId,
                channelId = body.ChannelId, channelNo, deviceId,
                positionLabel = string.IsNullOrWhiteSpace(body.PositionLabel) ? null : body.PositionLabel.Trim(),
                backfilled
            });
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("CreateGroupBolt")
        .WithSummary("新建螺栓建档并绑定通道（规格预设 + 暂存数据回填 + 审计，一个事务）");

        // 组孔位数编辑：法兰实际孔数与配置不符时（多接/扩容），现场自助修正，俯视图等分角度才对得上
        grp.MapPatch("/monitor-groups/{groupId:long}", async (
            long groupId, GroupPatchRequest? body, HttpContext http,
            NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (body?.ExpectedBoltCount is not { } n || n < 1 || n > 128)
                return Results.BadRequest(new { error = "expectedBoltCount 必填且在 1..128 范围内" });

            var user = UserContext.Require(http);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // 先关 reader 再决定是否 Rollback（同一连接上开着 reader 时回滚会抛，404 变 500）
            string? groupCode = null;
            short? oldCount = null;
            await using (var cmd = new NpgsqlCommand(@"
                SELECT code, expected_bolt_count FROM monitor_groups
                WHERE id = @gid AND deleted_at IS NULL
                FOR UPDATE", conn, tx))
            {
                cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, groupId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    groupCode = r.GetString(0);
                    oldCount = r.IsDBNull(1) ? null : r.GetInt16(1);
                }
            }
            if (groupCode is null)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "监测组不存在", groupId });
            }

            await using (var cmd = new NpgsqlCommand(@"
                UPDATE monitor_groups
                   SET expected_bolt_count = @n, updated_at = now()
                 WHERE id = @gid", conn, tx))
            {
                cmd.Parameters.AddWithValue("n", NpgsqlDbType.Smallint, (short)n);
                cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, groupId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var audit = new NpgsqlCommand(@"
                INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
                VALUES (@uid, 'update_group_config', 'monitor_group', @gid,
                        jsonb_build_object('expectedBoltCount', @old::int),
                        jsonb_build_object('expectedBoltCount', @new::int),
                        @note, now())", conn, tx))
            {
                audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                audit.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, groupId);
                audit.Parameters.AddWithValue("old", NpgsqlDbType.Integer, (object?)(int?)oldCount ?? DBNull.Value);
                audit.Parameters.AddWithValue("new", NpgsqlDbType.Integer, n);
                audit.Parameters.AddWithValue("note", NpgsqlDbType.Text, $"修改监测组 {groupCode} 孔位数 {oldCount?.ToString() ?? "—"} → {n}");
                await audit.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return Results.Ok(new { groupId, code = groupCode, expectedBoltCount = n, previous = oldCount });
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("PatchMonitorGroup")
        .WithSummary("修改监测组配置孔位数（expected_bolt_count，审计）");

        return app;
    }

    // 读取连续 7 列 total/normal/warn/loose/uncalibrated/nodata/high 为 health 对象
    internal static object Health(NpgsqlDataReader r, int offset) => new
    {
        total = r.GetInt64(offset),
        normal = r.GetInt64(offset + 1),
        warn = r.GetInt64(offset + 2),
        loose = r.GetInt64(offset + 3),
        uncalibrated = r.GetInt64(offset + 4),
        nodata = r.GetInt64(offset + 5),
        high = r.GetInt64(offset + 6),
    };
}

/// <summary>
/// 螺栓规格预设：建档时按公称直径/等级预填必填规格，现场可在螺栓设置里修正。
/// 与 dev 种子（deploy/sql/dev/0003）的占位规格取值一致。
/// </summary>
internal static class BoltSpecPresets
{
    /// <summary>GB/T 3098.1 粗牙螺纹应力截面积 As（mm²）</summary>
    public static readonly IReadOnlyDictionary<string, decimal> StressAreaMm2 =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["M12"] = 84.3m,
            ["M16"] = 157m,
            ["M20"] = 245m,
            ["M24"] = 353m,
            ["M30"] = 561m,
            ["M36"] = 817m,
            ["M42"] = 1121m,
            ["M48"] = 1473m,
        };

    /// <summary>性能等级 → Rp0.2 规定塑性延伸强度（MPa）</summary>
    public static readonly IReadOnlyDictionary<string, decimal> GradeRp02Mpa =
        new Dictionary<string, decimal>
        {
            ["8.8"] = 640m,
            ["10.9"] = 940m,
            ["12.9"] = 1080m,
        };

    // 建档缺省规格（现场最常见的一种），三个建档入口共用：单颗建档、点位表导入、在位自动建档。
    // 收口之前这组值在三处各写一份，BoltAutoEnroller 那份注释还写着"与点位表导入同源"，
    // 实际是复制粘贴 —— 改默认规格得记得同时改三个文件，漏一个就只有那条路径建出老规格。

    /// <summary>缺省材料</summary>
    public const string DefaultMaterial = "42CrMo";

    /// <summary>缺省公称直径</summary>
    public const string DefaultNominalDiam = "M20";

    /// <summary>缺省性能等级</summary>
    public const string DefaultGrade = "8.8";

    /// <summary>缺省弹性模量（GPa）</summary>
    public const decimal DefaultElasticModulusGpa = 206m;

    /// <summary>缺省有效长度（mm）</summary>
    public const decimal DefaultEffectiveLengthMm = 200m;

    /// <summary>缺省总长（mm）</summary>
    public const decimal DefaultTotalLengthMm = 220m;

    /// <summary>缺省声速（m/s）</summary>
    public const decimal DefaultSoundVelocityMs = 5890m;
}

/// <summary>新建螺栓建档请求。规格字段可空——按 NominalDiam/Grade 预设补齐。</summary>
public sealed record BoltCreateRequest(
    long ChannelId,
    string? Code = null,
    string? NominalDiam = null,
    string? Grade = null,
    decimal? Rp02Mpa = null,
    string? Material = null,
    decimal? ElasticModulusGpa = null,
    decimal? EffectiveLengthMm = null,
    decimal? TotalLengthMm = null,
    decimal? CrossSectionMm2 = null,
    decimal? SoundVelocityMs = null,
    decimal? TargetPreloadN = null,
    decimal? MinPreloadN = null,
    string? PositionLabel = null,
    bool? BackfillUnbound = null,
    string? Note = null);

/// <summary>监测组配置修改请求（当前仅孔位数）。</summary>
public sealed record GroupPatchRequest(int? ExpectedBoltCount = null);
