using System.Text;
using BoltPreload.Api.Auth;
using BoltPreload.Application.Devices;
using BoltPreload.Application.Ingestion;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 点位表（接线表）：通道↔物理位置↔螺栓档案的批量入口。
/// <para>
/// 对应关系的唯一事实源是工程文件（接线表），不是任何信号检测手段——本端点把这张表变成系统档案：
/// 批量写 channels.name（物理位置标签）+ 按行批量建档螺栓（与单颗建档同一套不变量：
/// 每通道唯一活跃螺栓、暂存数据回填、绑定历史、审计）。导出同格式 CSV 作为竣工资料。
/// </para>
/// <para>导入为全或无事务：任一行无效（通道不存在/规格未知/编号冲突）整批回滚并返回错误行清单——
/// 点位表是台账，"导入了一半"比导入失败更危险。已绑定螺栓的通道只更新位置、跳过建档（幂等重导）。</para>
/// </summary>
public static class WiringTableEndpoints
{
    private const int MaxRows = 64;

    public static IEndpointRouteBuilder MapWiringTableEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/devices/{deviceId:long}/wiring-table")
            .WithTags("Acceptance");

        // ---------- 导出 ----------
        grp.MapGet("/export.csv", async (long deviceId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            string deviceCode;
            await using (var d = ds.CreateCommand("SELECT code FROM devices WHERE id = @id AND deleted_at IS NULL"))
            {
                d.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, deviceId);
                if (await d.ExecuteScalarAsync(ct) is not string code)
                    return Results.NotFound(new { error = "设备不存在", deviceId });
                deviceCode = code;
            }

            var csv = new StringBuilder();
            csv.Append('\uFEFF');
            // 前 7 列与导入格式一致（可导出→修改→回导）；其后为只读信息列，导入时忽略
            csv.AppendLine("channel_no,position_label,bolt_code,nominal_diam,grade,target_preload_n,min_preload_n,hardware_state,accepted_at,bolt_position_label");

            await using var cmd = ds.CreateCommand(@"
                SELECT c.channel_no, c.name, b.code, b.nominal_diam, b.grade,
                       b.target_preload_n, b.min_preload_n,
                       c.hardware_state, c.accepted_at, b.position_label
                FROM channels c
                LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                WHERE c.device_id = @d AND c.deleted_at IS NULL
                ORDER BY c.channel_no");
            cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                csv.Append(r.GetInt16(0)).Append(',')
                   .Append(AcceptanceEndpoints.Csv(r.IsDBNull(1) ? "" : r.GetString(1))).Append(',')
                   .Append(AcceptanceEndpoints.Csv(r.IsDBNull(2) ? "" : r.GetString(2))).Append(',')
                   .Append(r.IsDBNull(3) ? "" : r.GetString(3)).Append(',')
                   .Append(r.IsDBNull(4) ? "" : r.GetString(4)).Append(',')
                   .Append(r.IsDBNull(5) ? "" : r.GetDecimal(5).ToString("F0")).Append(',')
                   .Append(r.IsDBNull(6) ? "" : r.GetDecimal(6).ToString("F0")).Append(',')
                   .Append(r.IsDBNull(7) ? "" : r.GetString(7)).Append(',')
                   .Append(r.IsDBNull(8) ? "" : r.GetDateTime(8).ToString("yyyy-MM-dd HH:mm:ss")).Append(',')
                   .Append(AcceptanceEndpoints.Csv(r.IsDBNull(9) ? "" : r.GetString(9))).Append('\n');
            }
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8",
                $"wiring-{deviceCode}-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("ExportWiringTable")
        .WithSummary("导出点位表 CSV（前 7 列可修改后回导）");

        // ---------- 导入 ----------
        grp.MapPost("/import", async (
            long deviceId,
            WiringImportRequest? req,
            HttpContext http,
            NpgsqlDataSource ds,
            IChannelResolver resolver,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (req?.Rows is not { Count: > 0 } rows)
                return Results.BadRequest(new { error = "rows 必填且非空" });
            if (rows.Count > MaxRows)
                return Results.BadRequest(new { error = $"单次最多导入 {MaxRows} 行（单板通道上限）", got = rows.Count });
            var dupNos = rows.GroupBy(x => x.ChannelNo).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            if (dupNos.Length > 0)
                return Results.BadRequest(new { error = "点位表内通道号重复", duplicated = dupNos });
            var createBolts = req.CreateBolts ?? true;
            if (createBolts && req.GroupId is not > 0)
                return Results.BadRequest(new { error = "createBolts=true 时必须指定 groupId（螺栓所属监测组）" });

            var user = UserContext.Require(http);
            var logger = loggerFactory.CreateLogger("WiringTable");

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            long deviceProjectId = 0;
            await using (var cmd = new NpgsqlCommand(
                "SELECT project_id FROM devices WHERE id = @id AND deleted_at IS NULL", conn, tx))
            {
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, deviceId);
                var scalar = await cmd.ExecuteScalarAsync(ct);
                if (scalar is not long pid)
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "设备不存在", deviceId });
                }
                deviceProjectId = pid;
            }

            string groupCode = "";
            if (createBolts)
            {
                await using var cmd = new NpgsqlCommand(
                    "SELECT code, project_id FROM monitor_groups WHERE id = @gid AND deleted_at IS NULL", conn, tx);
                cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, req.GroupId!.Value);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "监测组不存在", groupId = req.GroupId });
                }
                groupCode = r.GetString(0);
                var groupProjectId = r.GetInt64(1);
                if (groupProjectId != deviceProjectId)
                {
                    await tx.RollbackAsync(ct);
                    return Results.BadRequest(new
                    {
                        error = "设备与监测组不在同一项目，禁止跨项目建档",
                        deviceProjectId, groupProjectId
                    });
                }
            }

            // 校验通道并锁行（与建档/换绑同一把锁语义），任何缺失整批拒绝
            var errors = new List<object>();
            var channelByNo = new Dictionary<int, (long Id, long? BoltId)>();
            foreach (var row in rows)
            {
                if (row.ChannelNo is < 1 or > 64)
                {
                    errors.Add(new { channelNo = row.ChannelNo, error = "通道号超出 1..64" });
                    continue;
                }
                await using var cmd = new NpgsqlCommand(@"
                    SELECT c.id, b.id
                    FROM channels c
                    LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                    WHERE c.device_id = @d AND c.channel_no = @ch AND c.deleted_at IS NULL
                    FOR UPDATE OF c", conn, tx);
                cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
                cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Smallint, (short)row.ChannelNo);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                {
                    errors.Add(new { channelNo = row.ChannelNo, error = "通道不存在（先在设备页设置通道数重建目录）" });
                    continue;
                }
                channelByNo[row.ChannelNo] = (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1));
            }
            if (errors.Count > 0)
            {
                await tx.RollbackAsync(ct);
                return Results.BadRequest(new { error = "点位表校验未通过，整批未导入", rows = errors });
            }

            var updatedPositions = 0;
            var created = new List<object>();
            var createdChannelNos = new List<int>();
            var skippedBound = new List<int>();
            long backfilled = 0;

            foreach (var row in rows)
            {
                var (channelId, boltId) = channelByNo[row.ChannelNo];
                var pos = string.IsNullOrWhiteSpace(row.PositionLabel) ? null : row.PositionLabel.Trim();

                if (pos is not null)
                {
                    await using var cmd = new NpgsqlCommand(
                        "UPDATE channels SET name = @n, updated_at = now() WHERE id = @id AND name IS DISTINCT FROM @n", conn, tx);
                    cmd.Parameters.AddWithValue("n", NpgsqlDbType.Varchar, pos);
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
                    updatedPositions += await cmd.ExecuteNonQueryAsync(ct);
                }

                if (!createBolts) continue;
                if (boltId is not null) { skippedBound.Add(row.ChannelNo); continue; }

                // 规格/等级预设（与单颗建档同源），未知规格拒绝整批
                var nominal = string.IsNullOrWhiteSpace(row.NominalDiam) ? "M20" : row.NominalDiam.Trim().ToUpperInvariant();
                if (!BoltSpecPresets.StressAreaMm2.TryGetValue(nominal, out var csa))
                {
                    await tx.RollbackAsync(ct);
                    return Results.BadRequest(new
                    {
                        error = $"CH{row.ChannelNo} 螺栓规格 {nominal} 无预设截面积，请改用已知规格或先单颗建档",
                        knownSpecs = BoltSpecPresets.StressAreaMm2.Keys
                    });
                }
                var grade = string.IsNullOrWhiteSpace(row.Grade) ? "8.8" : row.Grade.Trim();
                var rp02 = BoltSpecPresets.GradeRp02Mpa.TryGetValue(grade, out var rp) ? rp : 640m;

                // 编号：显式优先；缺省中文「通道X-Y号螺栓」（与自动建档同一套 ChannelNaming），
                // 与既有（含软删）冲突时追加 -2/-3…
                string code;
                if (!string.IsNullOrWhiteSpace(row.BoltCode))
                {
                    code = row.BoltCode.Trim();
                }
                else
                {
                    var baseCode = ChannelNaming.DefaultBoltCode(row.ChannelNo);
                    var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    await using (var cmd = new NpgsqlCommand(
                        "SELECT code FROM bolts WHERE group_id = @gid AND (code = @base OR code LIKE @base || '-%')", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, req.GroupId!.Value);
                        cmd.Parameters.AddWithValue("base", NpgsqlDbType.Varchar, baseCode);
                        await using var r = await cmd.ExecuteReaderAsync(ct);
                        while (await r.ReadAsync(ct)) taken.Add(r.GetString(0));
                    }
                    code = baseCode;
                    for (var n = 2; taken.Contains(code); n++) code = $"{baseCode}-{n}";
                }

                long newBoltId;
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO bolts (
                        group_id, channel_id, code, nominal_diam, grade, rp02_mpa, material,
                        elastic_modulus_gpa, effective_length_mm, total_length_mm, cross_section_mm2,
                        sound_velocity_ms, target_preload_n, min_preload_n, position_label,
                        install_date, gb_compliant, state, enabled)
                    VALUES (@gid, @cid, @code, @nd, @grade, @rp02, '42CrMo',
                            206, 200, 220, @csa, 5890, @target, @min, @pos,
                            CURRENT_DATE, true, 'uncalibrated', true)
                    ON CONFLICT (group_id, code) DO NOTHING
                    RETURNING id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, req.GroupId!.Value);
                    cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                    cmd.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
                    cmd.Parameters.AddWithValue("nd", NpgsqlDbType.Varchar, nominal);
                    cmd.Parameters.AddWithValue("grade", NpgsqlDbType.Varchar, grade);
                    cmd.Parameters.AddWithValue("rp02", NpgsqlDbType.Numeric, rp02);
                    cmd.Parameters.AddWithValue("csa", NpgsqlDbType.Numeric, csa);
                    cmd.Parameters.AddWithValue("target", NpgsqlDbType.Numeric, (object?)row.TargetPreloadN ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("min", NpgsqlDbType.Numeric, (object?)row.MinPreloadN ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("pos", NpgsqlDbType.Varchar, (object?)pos ?? DBNull.Value);
                    var scalar = await cmd.ExecuteScalarAsync(ct);
                    if (scalar is not long id)
                    {
                        await tx.RollbackAsync(ct);
                        return Results.Conflict(new { error = $"CH{row.ChannelNo} 螺栓编号 {code} 与该组既有编号冲突，整批未导入" });
                    }
                    newBoltId = id;
                }

                await using (var cmd = new NpgsqlCommand(
                    "UPDATE channels SET business_state = 'bound', updated_at = now() WHERE id = @cid", conn, tx))
                {
                    cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // 暂存回填：与单颗建档同构（测量迁入 + 波形特征归属 + 清空暂存）
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO measurements (
                        bolt_id, channel_id, ts, ingested_at,
                        echo_time_ns, delta_tof_ns, sp_temp_c, gain, status_code,
                        wave_blob_url, algo_version)
                    SELECT @bolt_id, channel_id, ts, ingested_at,
                           echo_time_ns, delta_tof_ns, sp_temp_c, COALESCE(gain, 0), status_code,
                           wave_blob_url, 'phase1:raw:backfilled'
                    FROM unbound_measurements
                    WHERE channel_id = @channel_id
                    ON CONFLICT (channel_id, ts) DO NOTHING", conn, tx))
                {
                    cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, newBoltId);
                    cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                    backfilled += await cmd.ExecuteNonQueryAsync(ct);
                }
                await using (var cmd = new NpgsqlCommand(
                    "UPDATE wave_features SET bolt_id = @bolt_id WHERE channel_id = @channel_id AND bolt_id IS NULL", conn, tx))
                {
                    cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, newBoltId);
                    cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await using (var cmd = new NpgsqlCommand(
                    "DELETE FROM unbound_measurements WHERE channel_id = @channel_id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO channel_binding_history (
                        bolt_id, from_channel_id, to_channel_id, action, calibration_action,
                        moved_by, before_jsonb, after_jsonb, note)
                    SELECT @bolt_id, NULL, @channel_id, 'bind', 'keep_uncomputed',
                           @user_id, NULL, to_jsonb(b), @note
                    FROM bolts b WHERE b.id = @bolt_id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, newBoltId);
                    cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                    cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, user.UserId);
                    cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, $"点位表导入建档。{req.Note ?? ""}".Trim());
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await using (var audit = new NpgsqlCommand(@"
                    INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
                    SELECT @uid, 'create_bolt', 'bolt', @id, NULL, to_jsonb(b), @note, now()
                    FROM bolts b WHERE b.id = @id", conn, tx))
                {
                    audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                    audit.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, newBoltId);
                    audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                        $"点位表导入：新建螺栓 {code} 绑定 CH{row.ChannelNo}（设备 {deviceId}）");
                    await audit.ExecuteNonQueryAsync(ct);
                }

                created.Add(new { channelNo = row.ChannelNo, boltId = newBoltId, code });
                createdChannelNos.Add(row.ChannelNo);
            }

            // 汇总审计（§0 责任链：与数据变更同事务）
            await using (var audit = new NpgsqlCommand(@"
                INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
                VALUES (@uid, 'wiring_import', 'device', @did, NULL, @after::jsonb, @note, now())", conn, tx))
            {
                audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                audit.Parameters.AddWithValue("did", NpgsqlDbType.Bigint, deviceId);
                audit.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb, System.Text.Json.JsonSerializer.Serialize(new
                {
                    rows = rows.Count, updatedPositions, createdBolts = created.Count, skippedBound
                }));
                audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                    $"点位表导入 device={deviceId}：{rows.Count} 行，更新位置 {updatedPositions}，建档 {created.Count}，跳过已绑定 {skippedBound.Count}。{req.Note ?? ""}".Trim());
                await audit.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            // 建档通道的路由缓存失效：新 bolt 立刻参与数据路由（回填后续数据直接进 measurements）
            foreach (var chNo in createdChannelNos)
                resolver.Invalidate(deviceId, (ushort)chNo);

            logger.LogInformation(
                "点位表导入完成 device={DeviceId} rows={Rows} positions={Pos} bolts={Bolts} skipped={Skipped} backfilled={Backfilled} by user={UserId}",
                deviceId, rows.Count, updatedPositions, created.Count, skippedBound.Count, backfilled, user.UserId);

            return Results.Ok(new
            {
                deviceId,
                total = rows.Count,
                updatedPositions,
                createdBolts = created,
                skippedBound,
                backfilled
            });
        })
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithName("ImportWiringTable")
        .WithSummary("导入点位表：批量位置标注 + 批量建档绑定（reviewer+，全或无事务，审计）");

        return app;
    }
}

/// <summary>点位表一行（前端从 CSV 解析成 JSON 提交）。</summary>
public sealed record WiringRow(
    int ChannelNo,
    string? PositionLabel = null,
    string? BoltCode = null,
    string? NominalDiam = null,
    string? Grade = null,
    decimal? TargetPreloadN = null,
    decimal? MinPreloadN = null);

/// <summary>POST /wiring-table/import 请求体。</summary>
public sealed record WiringImportRequest(
    List<WiringRow>? Rows = null,
    long? GroupId = null,
    bool? CreateBolts = null,
    string? Note = null);
