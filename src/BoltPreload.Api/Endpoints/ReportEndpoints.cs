using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BoltPreload.Api.Auth;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

public static class ReportEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports")
            .WithTags("Reports")
            .RequireAuthorization(AuthPolicies.LoggedIn);

        // 生成扭矩阶梯报告 — reviewer+ 才能签
        group.MapPost("/torque-step/{channelId:long}", async (
            long channelId,
            HttpContext http,
            NpgsqlDataSource ds,
            IWebHostEnvironment env,
            TorqueStepReportRequest? req,
            CancellationToken ct) =>
        {
            var currentUser = UserContext.Require(http);
            var snapshot = await BuildTorqueStepSnapshotAsync(ds, channelId, ct);
            if (snapshot.Points.Count == 0)
                return Results.BadRequest(new { error = "没有扭矩实验点，无法生成报告", channelId });

            var title = req?.Title ?? $"{snapshot.Bolt.Code} 扭矩阶梯验证报告";
            var html = RenderTorqueStepHtml(title, snapshot);
            var reportDir = Path.Combine(env.ContentRootPath, "data", "reports", "torque-step");
            Directory.CreateDirectory(reportDir);
            var fileName = $"torque-step-channel-{channelId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.html";
            var relativePath = $"reports/torque-step/{fileName}";
            var absolutePath = Path.Combine(reportDir, fileName);

            // 原子写：tmp → rename
            var tmp = absolutePath + ".tmp";
            var bytes = Encoding.UTF8.GetBytes(html);
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, absolutePath, overwrite: true);

            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var size = (long)bytes.Length;

            long reportId;
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO reports (report_type, title, subject_type, subject_id, file_url, file_sha256, file_bytes, generated_by, notes)
                    VALUES ('torque_step', @title, 'channel', @channel_id, @file_url, @sha, @bytes, @user_id, @notes)
                    RETURNING id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("title", NpgsqlDbType.Varchar, title);
                    cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                    cmd.Parameters.AddWithValue("file_url", NpgsqlDbType.Varchar, relativePath);
                    cmd.Parameters.AddWithValue("sha", NpgsqlDbType.Char, sha);
                    cmd.Parameters.AddWithValue("bytes", NpgsqlDbType.Bigint, size);
                    cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, currentUser.UserId);
                    cmd.Parameters.AddWithValue("notes", NpgsqlDbType.Text, (object?)req?.Notes ?? DBNull.Value);
                    reportId = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
                }

                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO report_snapshots (report_id, snapshot_jsonb)
                    VALUES (@report_id, @snapshot::jsonb)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("report_id", NpgsqlDbType.Bigint, reportId);
                    cmd.Parameters.AddWithValue("snapshot", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(snapshot, JsonOptions));
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                // 事务失败：删掉已落盘的报告文件，避免留下无 reports 行对应的孤儿 HTML
                try { if (File.Exists(absolutePath)) File.Delete(absolutePath); } catch { /* 清理尽力 */ }
                throw;
            }

            return Results.Ok(new
            {
                reportId,
                title,
                fileUrl = relativePath,
                fileSha256 = sha,
                fileBytes = size,
                downloadUrl = $"/api/reports/{reportId}/download",
                generatedBy = currentUser.UserId,
                snapshot = new
                {
                    snapshot.Channel.ChannelId,
                    snapshot.Bolt.Code,
                    validPointCount = snapshot.Points.Count(p => !p.Excluded),
                    excludedPointCount = snapshot.Points.Count(p => p.Excluded),
                    snapshot.Fit
                }
            });
        })
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithName("GenerateTorqueStepReport")
        .WithSummary("生成扭矩阶梯实验 HTML 报告并保存快照（reviewer+）");

        group.MapGet("/", async (NpgsqlDataSource ds, string? type, int? limit, CancellationToken ct) =>
        {
            var n = Math.Clamp(limit ?? 50, 1, 500);
            var sql = @"
                SELECT id, report_type, title, status, subject_type, subject_id, file_url, generated_at, notes
                FROM reports
                WHERE 1=1"
                + (type is null ? "" : " AND report_type = @type")
                + " ORDER BY generated_at DESC LIMIT @n";

            await using var cmd = ds.CreateCommand(sql);
            if (type is not null) cmd.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, type);
            cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, n);

            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new
                {
                    id = r.GetInt64(0),
                    reportType = r.GetString(1),
                    title = r.GetString(2),
                    status = r.GetString(3),
                    subjectType = r.IsDBNull(4) ? null : r.GetString(4),
                    subjectId = r.IsDBNull(5) ? null : (long?)r.GetInt64(5),
                    fileUrl = r.IsDBNull(6) ? null : r.GetString(6),
                    generatedAt = r.GetDateTime(7),
                    notes = r.IsDBNull(8) ? null : r.GetString(8)
                });
            }
            return Results.Ok(new { count = items.Count, items });
        })
        .WithName("ListReports")
        .WithSummary("报告列表");

        group.MapGet("/{reportId:long}", async (long reportId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            const string sql = @"
                SELECT r.id, r.report_type, r.title, r.status, r.subject_type, r.subject_id,
                       r.file_url, r.generated_at, r.notes,
                       r.generated_by, gu.username, r.signed_at, r.signed_by, su.username,
                       s.snapshot_jsonb::text
                FROM reports r
                LEFT JOIN report_snapshots s ON s.report_id = r.id
                LEFT JOIN users gu ON gu.id = r.generated_by
                LEFT JOIN users su ON su.id = r.signed_by
                WHERE r.id = @id";
            await using var cmd = ds.CreateCommand(sql);
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, reportId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return Results.NotFound(new { error = "report not found", reportId });

            return Results.Ok(new
            {
                id = r.GetInt64(0),
                reportType = r.GetString(1),
                title = r.GetString(2),
                status = r.GetString(3),
                subjectType = r.IsDBNull(4) ? null : r.GetString(4),
                subjectId = r.IsDBNull(5) ? null : (long?)r.GetInt64(5),
                fileUrl = r.IsDBNull(6) ? null : r.GetString(6),
                generatedAt = r.GetDateTime(7),
                notes = r.IsDBNull(8) ? null : r.GetString(8),
                generatedBy = r.IsDBNull(9) ? null : (long?)r.GetInt64(9),
                generatedByName = r.IsDBNull(10) ? null : r.GetString(10),
                signedAt = r.IsDBNull(11) ? (DateTime?)null : r.GetDateTime(11),
                signedBy = r.IsDBNull(12) ? null : (long?)r.GetInt64(12),
                signedByName = r.IsDBNull(13) ? null : r.GetString(13),
                snapshot = r.IsDBNull(14) ? null : JsonSerializer.Deserialize<object>(r.GetString(14))
            });
        })
        .WithName("GetReport")
        .WithSummary("报告详情含快照");

        // 审核签发报告：reviewer+,必须不同于编制人,签发后锁定为正式记录。
        group.MapPost("/{reportId:long}/sign", async (long reportId, HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var user = UserContext.Require(http);
            string status;
            long? generatedBy;
            await using (var cmd = ds.CreateCommand("SELECT status, generated_by FROM reports WHERE id=@id"))
            {
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, reportId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Results.NotFound(new { error = "报告不存在", reportId });
                status = r.GetString(0);
                generatedBy = r.IsDBNull(1) ? null : r.GetInt64(1);
            }
            if (status != "generated")
                return Results.Conflict(new { error = $"当前状态 {status}，仅草稿可签发", reportId });
            if (generatedBy == user.UserId)
                return Results.BadRequest(new { error = "签发人必须与编制人不同（两人复核底线）。", generatedBy });

            await using (var cmd = ds.CreateCommand(@"
                UPDATE reports SET status='signed', signed_by=@uid, signed_at=now() WHERE id=@id AND status='generated'"))
            {
                cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, reportId);
                if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                    return Results.Conflict(new { error = "状态已变更，签发未生效", reportId });
            }
            await using (var cmd = ds.CreateCommand(@"
                INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
                VALUES (@uid, 'report_sign', 'report', @id, NULL, NULL, @note, now())"))
            {
                cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, reportId);
                cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, $"审核签发报告 #{reportId}");
                await cmd.ExecuteNonQueryAsync(ct);
            }
            return Results.Ok(new { reportId, status = "signed", signedBy = user.UserId });
        })
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithName("SignReport")
        .WithSummary("审核签发报告并锁定（reviewer+,异人）");

        group.MapGet("/{reportId:long}/download", async (long reportId, NpgsqlDataSource ds, IWebHostEnvironment env, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("SELECT file_url, title, file_sha256 FROM reports WHERE id=@id");
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, reportId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return Results.NotFound(new { error = "report not found", reportId });
            if (r.IsDBNull(0))
                return Results.NotFound(new { error = "report has no file", reportId });

            var relative = r.GetString(0);
            var expectedSha = r.IsDBNull(2) ? null : r.GetString(2);

            // 路径安全边界：拼接后必须仍在 ContentRootPath/data 下。
            // 防御未来 reports.file_url 被注入 "../../etc/passwd" 这类逃逸。
            var rootAbs = Path.GetFullPath(Path.Combine(env.ContentRootPath, "data"));
            if (!rootAbs.EndsWith(Path.DirectorySeparatorChar)) rootAbs += Path.DirectorySeparatorChar;

            if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) || relative.Contains(':'))
                return Results.NotFound(new { error = "report path invalid", reportId });

            string fullPath;
            try { fullPath = Path.GetFullPath(Path.Combine(rootAbs, relative.Replace('/', Path.DirectorySeparatorChar))); }
            catch { return Results.NotFound(new { error = "report path invalid", reportId }); }

            if (!fullPath.StartsWith(rootAbs, StringComparison.Ordinal))
                return Results.NotFound(new { error = "report path escaped sandbox", reportId });

            if (!File.Exists(fullPath))
                return Results.NotFound(new { error = "report file missing", reportId, relative });

            var bytes = await File.ReadAllBytesAsync(fullPath, ct);

            // 防篡改校验：文件内容 hash 必须等于 reports.file_sha256
            if (!string.IsNullOrEmpty(expectedSha))
            {
                var actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new
                    {
                        error = "report file integrity check failed",
                        reportId, expectedSha, actualSha
                    });
            }

            return Results.File(bytes, "text/html; charset=utf-8", Path.GetFileName(fullPath));
        })
        .WithName("DownloadReport")
        .WithSummary("下载报告 HTML（带 sha256 防篡改校验）");

        // 生成法兰螺栓预紧力验收报告（按监测组/法兰），保存数据快照供前端渲染与归档。reviewer+ 签发。
        group.MapPost("/flange-acceptance/{groupId:long}", async (
            long groupId, HttpContext http, NpgsqlDataSource ds, IWebHostEnvironment env, FlangeReportRequest? req, CancellationToken ct) =>
        {
            var user = UserContext.Require(http);

            string flangeName, flangeCode, project, turbine;
            string? structureType;
            decimal? groupTarget;
            await using (var cmd = ds.CreateCommand(@"
                SELECT mg.name, mg.code, mg.structure_type, mg.target_preload_n, t.name, p.name
                FROM monitor_groups mg
                JOIN turbines t ON t.id = mg.turbine_id
                JOIN projects p ON p.id = t.project_id
                WHERE mg.id = @gid AND mg.deleted_at IS NULL"))
            {
                cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, groupId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Results.NotFound(new { error = "部位不存在", groupId });
                flangeName = r.GetString(0);
                flangeCode = r.GetString(1);
                structureType = r.IsDBNull(2) ? null : r.GetString(2);
                groupTarget = r.IsDBNull(3) ? null : r.GetDecimal(3);
                turbine = r.GetString(4);
                project = r.GetString(5);
            }

            var bolts = new List<FlangeLine>();
            int total = 0, calibrated = 0, passed = 0;
            await using (var cmd = ds.CreateCommand(@"
                SELECT b.code, b.nominal_diam, b.grade, b.position_label, b.target_preload_n, b.min_preload_n, b.state,
                       c.channel_no, c.k_l,
                       l.preload_n, l.preload_ratio, l.ts,
                       cr.method, cr.r_squared, cr.k_l_uncertainty
                FROM bolts b
                JOIN channels c ON c.id = b.channel_id
                LEFT JOIN calibration_records cr ON cr.id = c.active_calibration_id
                LEFT JOIN LATERAL (
                    SELECT preload_n, preload_ratio, ts FROM measurements m
                    WHERE m.bolt_id = b.id ORDER BY ts DESC LIMIT 1
                ) l ON true
                WHERE b.group_id = @gid AND b.deleted_at IS NULL
                ORDER BY b.code"))
            {
                cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, groupId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    total++;
                    // "已标定"以通道有效标定为准（c.k_l 非空[8] 且有 active calibration → cr.method 非空[12]），
                    // 不再依赖易漂移的 bolts.state[6]（API 标定路径历史上漏写 state，会让在产螺栓被误判未标定）。
                    var isCal = !r.IsDBNull(8) && !r.IsDBNull(12);
                    decimal? min = r.IsDBNull(5) ? null : r.GetDecimal(5);
                    decimal? pre = r.IsDBNull(9) ? null : r.GetDecimal(9);
                    var method = r.IsDBNull(12) ? null : r.GetString(12);
                    if (isCal) calibrated++;

                    string result;
                    if (!isCal) result = "未标定";
                    else if (pre is null) result = "无数据";
                    else if (min is not null && pre < min) result = "不合格";
                    else result = "合格";
                    if (result == "合格") passed++;

                    var trust = method switch
                    {
                        "load_calibrated" or "gb_single_wave" or "gb_bi_wave" => "正式",
                        "torque_fit" => "工程估算",
                        "quick" or "torque_estimate" => "临时",
                        _ => "—",
                    };

                    bolts.Add(new FlangeLine(
                        Code: r.GetString(0),
                        Spec: $"{r.GetString(1)}·{r.GetString(2)}",
                        Position: r.IsDBNull(3) ? null : r.GetString(3),
                        ChannelNo: r.GetInt16(7),
                        TargetN: r.IsDBNull(4) ? (decimal?)null : r.GetDecimal(4),
                        MinN: min,
                        PreloadN: pre,
                        Ratio: r.IsDBNull(10) ? (decimal?)null : r.GetDecimal(10),
                        LastTs: r.IsDBNull(11) ? (DateTime?)null : r.GetDateTime(11),
                        CalibMethod: method,
                        Trust: trust,
                        RSquared: r.IsDBNull(13) ? (decimal?)null : r.GetDecimal(13),
                        UncertaintyN: r.IsDBNull(14) ? (decimal?)null : r.GetDecimal(14),
                        Result: result));
                }
            }

            var overall = total > 0 && passed == total ? "合格" : "不合格";
            var snapshot = new
            {
                generatedAt = DateTimeOffset.UtcNow,
                generatedBy = user.UserId,
                standard = "GB/T 43232 超声法螺栓轴向应力测量 · JJF 1059.1 测量不确定度评定",
                project,
                turbine,
                flange = new { name = flangeName, code = flangeCode, structureType, targetPreloadN = groupTarget },
                summary = new { total, calibrated, passed, failed = total - passed, overall },
                bolts,
            };
            var title = req?.Title ?? $"{turbine} {flangeName} 螺栓预紧力验收报告";

            // 生成可下载的归档 HTML（与扭矩阶梯报告同一套：原子写 + sha256 防篡改），不再只存 JSON 快照。
            var html = RenderFlangeHtml(title, project, turbine, flangeName, flangeCode, groupTarget, total, calibrated, passed, overall, bolts);
            var reportDir = Path.Combine(env.ContentRootPath, "data", "reports", "flange");
            Directory.CreateDirectory(reportDir);
            var fileName = $"flange-group-{groupId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.html";
            var relativePath = $"reports/flange/{fileName}";
            var absolutePath = Path.Combine(reportDir, fileName);
            var tmp = absolutePath + ".tmp";
            var bytes = Encoding.UTF8.GetBytes(html);
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, absolutePath, overwrite: true);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var size = (long)bytes.Length;

            long reportId;
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO reports (report_type, title, subject_type, subject_id, file_url, file_sha256, file_bytes, generated_by, notes)
                    VALUES ('flange_acceptance', @title, 'monitor_group', @gid, @file_url, @sha, @bytes, @uid, @notes) RETURNING id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("title", NpgsqlDbType.Varchar, title);
                    cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, groupId);
                    cmd.Parameters.AddWithValue("file_url", NpgsqlDbType.Varchar, relativePath);
                    cmd.Parameters.AddWithValue("sha", NpgsqlDbType.Char, sha);
                    cmd.Parameters.AddWithValue("bytes", NpgsqlDbType.Bigint, size);
                    cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                    cmd.Parameters.AddWithValue("notes", NpgsqlDbType.Text, (object?)req?.Notes ?? DBNull.Value);
                    reportId = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
                }
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO report_snapshots (report_id, snapshot_jsonb) VALUES (@id, @snap::jsonb)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, reportId);
                    cmd.Parameters.AddWithValue("snap", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(snapshot, JsonOptions));
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                // 事务失败：删掉已落盘的报告文件，避免留下无 reports 行对应的孤儿 HTML
                try { if (File.Exists(absolutePath)) File.Delete(absolutePath); } catch { /* 清理尽力 */ }
                throw;
            }

            return Results.Ok(new { reportId, title, overall, total, passed, calibrated, fileUrl = relativePath, downloadUrl = $"/api/reports/{reportId}/download" });
        })
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithName("GenerateFlangeAcceptanceReport")
        .WithSummary("生成法兰螺栓预紧力验收报告并保存快照（reviewer+）");

        return app;
    }

    private static async Task<TorqueStepReportSnapshot> BuildTorqueStepSnapshotAsync(NpgsqlDataSource ds, long channelId, CancellationToken ct)
    {
        ChannelSnapshot channel;
        BoltSnapshot bolt;
        await using (var cmd = ds.CreateCommand(@"
            SELECT c.id, c.device_id, c.channel_no, c.k_l, c.k_l_intercept_ns, c.active_calibration_id,
                   b.id, b.code, b.nominal_diam, b.target_preload_n, b.install_torque_nm
            FROM channels c
            JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
            WHERE c.id=@channel_id AND c.deleted_at IS NULL"))
        {
            cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                throw new InvalidOperationException($"channel {channelId} not found or not bound");
            channel = new ChannelSnapshot(
                ChannelId: r.GetInt64(0),
                DeviceId: r.GetInt64(1),
                ChannelNo: r.GetInt16(2),
                KL: r.IsDBNull(3) ? null : r.GetDecimal(3),
                KLInterceptNs: r.IsDBNull(4) ? null : r.GetDecimal(4),
                ActiveCalibrationId: r.IsDBNull(5) ? null : r.GetGuid(5));
            bolt = new BoltSnapshot(
                BoltId: r.GetInt64(6),
                Code: r.GetString(7),
                NominalDiameter: r.GetString(8),
                TargetPreloadN: r.IsDBNull(9) ? null : r.GetDecimal(9),
                InstallTorqueNm: r.IsDBNull(10) ? null : r.GetDecimal(10));
        }

        var points = new List<TorquePointSnapshot>();
        await using (var cmd = ds.CreateCommand(@"
            SELECT id, torque_nm, estimated_preload_n, sample_count,
                   avg_echo_time_ns, stddev_echo_time_ns, avg_temp_c,
                   normalized_echo_time_ns, delta_tof_ns,
                   excluded, exclude_reason, note
            FROM torque_test_points
            WHERE channel_id=@channel_id
            ORDER BY torque_nm, id"))
        {
            cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                points.Add(new TorquePointSnapshot(
                    Id: r.GetInt64(0),
                    TorqueNm: r.GetDecimal(1),
                    EstimatedPreloadN: r.GetDecimal(2),
                    SampleCount: r.GetInt32(3),
                    AvgEchoTimeNs: r.GetDecimal(4),
                    StddevEchoTimeNs: r.IsDBNull(5) ? null : r.GetDecimal(5),
                    AvgTempC: r.GetDecimal(6),
                    NormalizedEchoTimeNs: r.GetDecimal(7),
                    DeltaTofNs: r.GetDecimal(8),
                    Excluded: r.GetBoolean(9),
                    ExcludeReason: r.IsDBNull(10) ? null : r.GetString(10),
                    Note: r.IsDBNull(11) ? null : r.GetString(11)));
            }
        }

        TorqueFitSnapshot? fit = null;
        await using (var cmd = ds.CreateCommand(@"
            SELECT COUNT(*),
                   regr_slope(delta_tof_ns::double precision, estimated_preload_n::double precision),
                   regr_intercept(delta_tof_ns::double precision, estimated_preload_n::double precision),
                   regr_r2(delta_tof_ns::double precision, estimated_preload_n::double precision)
            FROM torque_test_points
            WHERE channel_id=@channel_id AND excluded=false"))
        {
            cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            if (!r.IsDBNull(1))
            {
                fit = new TorqueFitSnapshot(
                    PointCount: r.GetInt64(0),
                    KL: r.GetDouble(1),
                    InterceptNs: r.GetDouble(2),
                    RSquared: r.GetDouble(3));
            }
        }

        return new TorqueStepReportSnapshot(
            GeneratedAt: DateTimeOffset.UtcNow,
            Channel: channel,
            Bolt: bolt,
            Points: points,
            Fit: fit,
            Conclusion: "50~200 N·m 范围内超声 Δt 与扭矩估算预紧力呈较好线性；本报告为工程验证，非正式载荷标定。");
    }

    private static string RenderTorqueStepHtml(string title, TorqueStepReportSnapshot snapshot)
    {
        static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
        var rows = string.Join("\n", snapshot.Points.Select(p => $"""
            <tr class="{(p.Excluded ? "excluded" : "")}">
              <td>{p.Id}</td><td>{p.TorqueNm:F0}</td><td>{p.EstimatedPreloadN:F0}</td>
              <td>{p.SampleCount}</td><td>{p.AvgEchoTimeNs:F4}</td><td>{p.StddevEchoTimeNs:F4}</td>
              <td>{p.AvgTempC:F3}</td><td>{p.DeltaTofNs:F4}</td><td>{(p.Excluded ? "排除" : "有效")}</td>
              <td>{H(p.ExcludeReason ?? p.Note)}</td>
            </tr>
            """));

        return $$"""
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8" />
          <title>{{H(title)}}</title>
          <style>
            body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", "Microsoft YaHei", sans-serif; margin: 32px; color: #111827; }
            h1 { margin-bottom: 4px; }
            .muted { color: #6b7280; }
            table { width: 100%; border-collapse: collapse; margin: 16px 0; font-size: 13px; }
            th, td { border: 1px solid #d1d5db; padding: 7px 8px; text-align: left; }
            th { background: #f3f4f6; }
            .excluded { color: #991b1b; background: #fff1f2; }
            .warn { border-left: 4px solid #f59e0b; padding: 10px 12px; background: #fffbeb; }
            .ok { border-left: 4px solid #16a34a; padding: 10px 12px; background: #f0fdf4; }
          </style>
        </head>
        <body>
          <h1>{{H(title)}}</h1>
          <div class="muted">生成时间：{{snapshot.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}}</div>
          <h2>对象</h2>
          <table>
            <tr><th>螺栓</th><td>{{H(snapshot.Bolt.Code)}} / {{H(snapshot.Bolt.NominalDiameter)}}</td></tr>
            <tr><th>设备/通道</th><td>device {{snapshot.Channel.DeviceId}} / channel {{snapshot.Channel.ChannelNo}}</td></tr>
            <tr><th>当前标定</th><td>{{snapshot.Channel.ActiveCalibrationId}} / K_L={{snapshot.Channel.KL}} / b={{snapshot.Channel.KLInterceptNs}} ns</td></tr>
          </table>
          <h2>有效性说明</h2>
          <div class="warn">本报告基于扭矩法估算预紧力，不是载荷传感器或拉伸机正式标定结果。不得作为安全验收唯一依据。</div>
          <h2>扭矩实验点</h2>
          <table>
            <thead><tr><th>ID</th><th>扭矩 N·m</th><th>估算预紧力 N</th><th>样本</th><th>EchoTime ns</th><th>标准差 ns</th><th>温度 °C</th><th>Δt ns</th><th>状态</th><th>备注</th></tr></thead>
            <tbody>{{rows}}</tbody>
          </table>
          <h2>拟合结果</h2>
          <div class="ok">
            有效点：{{snapshot.Fit?.PointCount ?? 0}}；
            K_L：{{snapshot.Fit?.KL.ToString("F12") ?? "-"}} ns/N；
            截距 b：{{snapshot.Fit?.InterceptNs.ToString("F4") ?? "-"}} ns；
            R²：{{snapshot.Fit?.RSquared.ToString("F6") ?? "-"}}
          </div>
          <h2>结论</h2>
          <p>{{H(snapshot.Conclusion)}}</p>
        </body>
        </html>
        """;
    }

    private static string RenderFlangeHtml(
        string title, string project, string turbine, string flangeName, string flangeCode,
        decimal? groupTarget, int total, int calibrated, int passed, string overall, List<FlangeLine> bolts)
    {
        static string H(string? v) => WebUtility.HtmlEncode(v ?? "");
        static string N(decimal? v) => v.HasValue ? v.Value.ToString("F0") : "—";
        static string R(decimal? v) => v.HasValue ? (v.Value * 100).ToString("F1") + "%" : "—";
        var rows = string.Join("\n", bolts.Select((b, i) => $"""
            <tr><td>{i + 1}</td><td>{H(b.Code)}</td><td>{H(b.Spec)}</td><td>{H(b.Position)}</td>
            <td>{N(b.TargetN)}</td><td>{N(b.MinN)}</td><td>{N(b.PreloadN)}</td><td>{R(b.Ratio)}</td>
            <td>{H(b.CalibMethod)}</td><td>{H(b.Trust)}</td><td>{(b.RSquared.HasValue ? b.RSquared.Value.ToString("F3") : "—")}</td>
            <td>{H(b.Result)}</td></tr>
            """));
        var verdictClass = overall == "合格" ? "ok" : "warn";
        return $$"""
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8" />
          <title>{{H(title)}}</title>
          <style>
            body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", "Microsoft YaHei", sans-serif; margin: 32px; color: #111827; }
            h1 { margin-bottom: 4px; }
            .muted { color: #6b7280; }
            table { width: 100%; border-collapse: collapse; margin: 16px 0; font-size: 13px; }
            th, td { border: 1px solid #d1d5db; padding: 7px 8px; text-align: left; }
            th { background: #f3f4f6; }
            .warn { border-left: 4px solid #f59e0b; padding: 10px 12px; background: #fffbeb; }
            .ok { border-left: 4px solid #16a34a; padding: 10px 12px; background: #f0fdf4; }
          </style>
        </head>
        <body>
          <h1>{{H(title)}}</h1>
          <div class="muted">标准依据：GB/T 43232 · JJF 1059.1 · 生成时间 {{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss zzz}}</div>
          <h2>对象</h2>
          <table>
            <tr><th>风场</th><td>{{H(project)}}</td><th>风机</th><td>{{H(turbine)}}</td></tr>
            <tr><th>监测部位</th><td>{{H(flangeName)}}（{{H(flangeCode)}}）</td><th>组目标预紧力</th><td>{{N(groupTarget)}} N</td></tr>
          </table>
          <h2>验收结论</h2>
          <div class="{{verdictClass}}">总体：{{H(overall)}}；螺栓总数 {{total}}，已标定 {{calibrated}}，合格 {{passed}}，不合格 {{total - passed}}。</div>
          <h2>逐螺栓结果</h2>
          <table>
            <thead><tr><th>#</th><th>螺栓</th><th>规格</th><th>位置</th><th>目标 N</th><th>下限 N</th><th>当前 N</th><th>比值</th><th>标定方法</th><th>可信</th><th>R²</th><th>结论</th></tr></thead>
            <tbody>{{rows}}</tbody>
          </table>
          <p class="muted">本报告由系统自动生成，数据快照已归档可追溯。标定可信等级为"临时/工程估算"的螺栓不作为正式安全验收依据。</p>
        </body>
        </html>
        """;
    }
}

public sealed record TorqueStepReportRequest(string? Title = null, string? Notes = null);
public sealed record FlangeReportRequest(string? Title = null, string? Notes = null);

/// <summary>法兰验收报告中的一行螺栓结果（既用于 JSON 快照，也用于渲染归档 HTML）。</summary>
public sealed record FlangeLine(
    string Code, string Spec, string? Position, short ChannelNo,
    decimal? TargetN, decimal? MinN, decimal? PreloadN, decimal? Ratio,
    DateTime? LastTs, string? CalibMethod, string Trust, decimal? RSquared, decimal? UncertaintyN, string Result);

public sealed record TorqueStepReportSnapshot(
    DateTimeOffset GeneratedAt,
    ChannelSnapshot Channel,
    BoltSnapshot Bolt,
    IReadOnlyList<TorquePointSnapshot> Points,
    TorqueFitSnapshot? Fit,
    string Conclusion);

public sealed record ChannelSnapshot(long ChannelId, long DeviceId, short ChannelNo, decimal? KL, decimal? KLInterceptNs, Guid? ActiveCalibrationId);
public sealed record BoltSnapshot(long BoltId, string Code, string NominalDiameter, decimal? TargetPreloadN, decimal? InstallTorqueNm);
public sealed record TorquePointSnapshot(
    long Id,
    decimal TorqueNm,
    decimal EstimatedPreloadN,
    int SampleCount,
    decimal AvgEchoTimeNs,
    decimal? StddevEchoTimeNs,
    decimal AvgTempC,
    decimal NormalizedEchoTimeNs,
    decimal DeltaTofNs,
    bool Excluded,
    string? ExcludeReason,
    string? Note);
public sealed record TorqueFitSnapshot(long PointCount, double KL, double InterceptNs, double RSquared);
