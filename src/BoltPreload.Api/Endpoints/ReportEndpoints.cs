using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BoltPreload.Api.Auth;
using BoltPreload.Calibration;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 报告端点（/api/reports）：扭矩分步标定报告与法兰验收报告的生成、查询、导出。
/// 报告内容在生成时对当时数据做快照（JSON 落库），后续数据变化不影响已出报告。
/// </summary>
public static class ReportEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        // M3 拍板：列表/详情/下载维持 LoggedIn（不收紧到 reviewer+）。
        // 报告快照里的预紧力/标定数据，任何已登录角色在总览、螺栓详情页本来就全部可见，
        // 收紧只会堵死"现场对着验收报告核数据"的场景，换不来保密收益；生成/签发仍是 reviewer+。
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
            TorqueStepReportSnapshot snapshot;
            try
            {
                snapshot = await BuildTorqueStepSnapshotAsync(ds, channelId, ct);
            }
            catch (InvalidOperationException)
            {
                // 通道不存在/未绑定螺栓：给 404 而不是让异常冒泡成 500
                return Results.NotFound(new { error = "通道不存在或未绑定螺栓，无法生成扭矩阶梯报告", channelId });
            }
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
                return Results.NotFound(new { error = "报告不存在", reportId });

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

            // 签发与审计同事务：不允许出现"已签发但审计缺条"的断链
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE reports SET status='signed', signed_by=@uid, signed_at=now() WHERE id=@id AND status='generated'", conn, tx))
            {
                cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, reportId);
                if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Conflict(new { error = "状态已变更，签发未生效", reportId });
                }
            }
            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at, ip_addr)
                VALUES (@uid, 'report_sign', 'report', @id, NULL, NULL, @note, now(), @ip)", conn, tx))
            {
                cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, reportId);
                cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, $"审核签发报告 #{reportId}");
                AuditClientIp.Bind(cmd.Parameters);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new { reportId, status = "signed", signedBy = user.UserId });
        })
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithName("SignReport")
        .WithSummary("审核签发报告并锁定（reviewer+,异人）");

        group.MapGet("/{reportId:long}/download", async (long reportId, NpgsqlDataSource ds, IWebHostEnvironment env, ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("Reports");
            await using var cmd = ds.CreateCommand("SELECT file_url, title, file_sha256 FROM reports WHERE id=@id");
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, reportId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return Results.NotFound(new { error = "报告不存在", reportId });
            if (r.IsDBNull(0))
                return Results.NotFound(new { error = "该报告没有可下载的文件", reportId });

            var relative = r.GetString(0);
            var title = r.IsDBNull(1) ? null : r.GetString(1);
            var expectedSha = r.IsDBNull(2) ? null : r.GetString(2);

            // 路径安全边界：拼接后必须仍在 ContentRootPath/data 下。
            // 防御未来 reports.file_url 被注入 "../../etc/passwd" 这类逃逸。
            var rootAbs = Path.GetFullPath(Path.Combine(env.ContentRootPath, "data"));
            if (!rootAbs.EndsWith(Path.DirectorySeparatorChar)) rootAbs += Path.DirectorySeparatorChar;

            if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) || relative.Contains(':'))
            {
                logger.LogWarning("报告 {ReportId} file_url 非法：{Relative}", reportId, relative);
                return Results.NotFound(new { error = "报告文件不可用", reportId });
            }

            string fullPath;
            try { fullPath = Path.GetFullPath(Path.Combine(rootAbs, relative.Replace('/', Path.DirectorySeparatorChar))); }
            catch { return Results.NotFound(new { error = "报告文件不可用", reportId }); }

            if (!fullPath.StartsWith(rootAbs, StringComparison.Ordinal))
            {
                logger.LogWarning("报告 {ReportId} file_url 越界：{Relative}", reportId, relative);
                return Results.NotFound(new { error = "报告文件不可用", reportId });
            }

            if (!File.Exists(fullPath))
            {
                logger.LogWarning("报告 {ReportId} 文件缺失：{Relative}", reportId, relative);
                return Results.NotFound(new { error = "报告文件不存在，可能已被清理", reportId });
            }

            var bytes = await File.ReadAllBytesAsync(fullPath, ct);

            // 防篡改校验：文件内容 hash 必须等于 reports.file_sha256
            if (!string.IsNullOrEmpty(expectedSha))
            {
                var actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogError("报告 {ReportId} 完整性校验失败 expected={Expected} actual={Actual}", reportId, expectedSha, actualSha);
                    return Results.BadRequest(new { error = "报告文件完整性校验未通过，文件可能已被篡改", reportId });
                }
            }

            // 存盘名是 flange-group-1-2026....html 这种 ASCII，为的是文件系统和 URL 上不出岔子；
            // 但客户存下来的要是报告标题。两者分开，别为了下载好看去动落盘的名字。
            return Results.File(bytes, "text/html; charset=utf-8", DownloadName(title, fullPath));
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
            int total = 0, calibrated = 0, passed = 0, failed = 0, uncal = 0, nodata = 0, tempPassed = 0;
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

                    // 没标定过（method 为空）在报告里是破折号，与"标定方法不认识"分开
                    var trustLevel = method is null ? null : DisplayLabels.TrustLevel(method);
                    var trust = trustLevel is null ? "—" : DisplayLabels.TrustLevelLabel(trustLevel);
                    var isFormal = trustLevel == "formal";

                    // 验收结论必须区分标定可信等级：临时/工程估算标定测出的"达标"只能记"合格*"，
                    // 不能与正式载荷标定的合格混为一谈（报告脚注说明 * 含义）
                    string result;
                    if (!isCal) result = "未标定";
                    else if (pre is null) result = "无数据";
                    else if (min is not null && pre < min) result = "不合格";
                    else result = isFormal ? "合格" : "合格*";
                    switch (result)
                    {
                        case "合格": case "合格*": passed++; if (!isFormal) tempPassed++; break;
                        case "不合格": failed++; break;
                        case "未标定": uncal++; break;
                        default: nodata++; break;
                    }

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

            // 空部位不出报告：与扭矩阶梯"无实验点 400"对称（UI 有按钮门槛，但 API 直调也必须拦）
            if (total == 0)
                return Results.BadRequest(new { error = "该部位没有在册螺栓，无法生成验收报告", groupId });

            // 总体结论：有低于下限 → 不合格；有未标定/无数据 → 未完成；全部达标但含临时标定 → 有条件合格
            var overall = failed > 0 ? "不合格"
                : uncal + nodata > 0 ? "未完成（存在未标定或无数据螺栓）"
                : tempPassed > 0 ? "有条件合格（含临时/工程估算标定）"
                : "合格";
            var snapshot = new
            {
                generatedAt = DateTimeOffset.UtcNow,
                generatedBy = user.UserId,
                standard = "GB/T 43232-2023 紧固件轴向应力超声测量方法 · JJF 1059.1-2012 测量不确定度评定与表示",
                project,
                turbine,
                flange = new { name = flangeName, code = flangeCode, structureType, targetPreloadN = groupTarget },
                summary = new { total, calibrated, passed, failed, uncalibrated = uncal, nodata, tempPassed, overall },
                bolts,
            };
            var title = req?.Title ?? $"{turbine} {flangeName} 螺栓预紧力验收报告";

            // 生成可下载的归档 HTML（与扭矩阶梯报告同一套：原子写 + sha256 防篡改），不再只存 JSON 快照。
            var html = RenderFlangeHtml(title, project, turbine, flangeName, flangeCode, groupTarget, total, calibrated, passed, failed, uncal, nodata, overall, bolts);
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
            SELECT t.id, t.torque_nm, t.estimated_preload_n, t.sample_count,
                   t.avg_echo_time_ns, t.stddev_echo_time_ns, t.avg_temp_c,
                   t.normalized_echo_time_ns, t.delta_tof_ns,
                   t.excluded, t.exclude_reason, t.note,
                   (c.ref_time_ns IS NOT NULL AND t.ref_time_ns = c.ref_time_ns) AS baseline_matched
            FROM torque_test_points t
            JOIN channels c ON c.id = t.channel_id
            WHERE t.channel_id=@channel_id
            ORDER BY t.torque_nm, t.id"))
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
                    Note: r.IsDBNull(11) ? null : r.GetString(11),
                    BaselineMatched: r.GetBoolean(12)));
            }
        }

        // 拟合口径与 apply-torque-fit / summary 一致：只用【当前基线下】未剔除的点。
        // 旧基线点的 Δt 参考系已随基线重设失效，混进拟合会把斜率带偏，报告必须与实际可应用的拟合同源。
        TorqueFitSnapshot? fit = null;
        await using (var cmd = ds.CreateCommand(@"
            SELECT COUNT(*),
                   regr_slope(t.delta_tof_ns::double precision, t.estimated_preload_n::double precision),
                   regr_intercept(t.delta_tof_ns::double precision, t.estimated_preload_n::double precision),
                   regr_r2(t.delta_tof_ns::double precision, t.estimated_preload_n::double precision)
            FROM torque_test_points t
            JOIN channels c ON c.id = t.channel_id
            WHERE t.channel_id=@channel_id AND t.excluded=false
              AND c.ref_time_ns IS NOT NULL AND t.ref_time_ns = c.ref_time_ns"))
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

        // 结论按实际数据生成（扭矩范围、R² 分档），不写死——报告结论必须与数据一致
        var valid = points.Where(p => !p.Excluded && p.BaselineMatched).ToList();
        var staleCount = points.Count(p => !p.Excluded && !p.BaselineMatched);
        string conclusion;
        if (fit is null || valid.Count < 2)
        {
            conclusion = staleCount > 0
                ? $"当前基线下的有效实验点不足（另有 {staleCount} 个点采于旧基线、参考系已失效，不参与拟合），"
                  + "无法给出线性度结论；请在当前基线下重新逐档采点后再生成。本报告为工程验证，非正式载荷标定。"
                : "有效实验点不足，无法给出线性度结论；请补采扭矩阶梯点后重新生成。本报告为工程验证，非正式载荷标定。";
        }
        else
        {
            var tMin = valid.Min(p => p.TorqueNm);
            var tMax = valid.Max(p => p.TorqueNm);
            var linearity = fit.RSquared >= CalibrationThresholds.R2PassDouble ? "线性关系良好"
                : fit.RSquared >= CalibrationThresholds.R2RejectDouble ? "线性关系较好"
                : "线性度不足（建议补采载荷点或排查耦合/过渡过程数据）";
            conclusion = $"{tMin:F0}~{tMax:F0} N·m 范围内，超声声时差 Δt 与扭矩法估算预紧力{linearity}（R²={fit.RSquared:F4}，有效点 {valid.Count} 个）。本报告为工程验证，非正式载荷标定。";
        }

        return new TorqueStepReportSnapshot(
            GeneratedAt: DateTimeOffset.UtcNow,
            Channel: channel,
            Bolt: bolt,
            Points: points,
            Fit: fit,
            Conclusion: conclusion);
    }

    /// <summary>
    /// 下载时给客户看到的文件名，用报告标题。标题里可能被人打进斜杠、冒号之类，
    /// 直接塞进 Content-Disposition 会被浏览器当成路径，逐个换成下划线。
    /// 标题为空或清干净后什么都不剩，就退回存盘用的那个名字。
    /// </summary>
    internal static string DownloadName(string? title, string fullPath)
    {
        var fallback = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(title)) return fallback;

        var cleaned = new string(title.Trim()
            .Select(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c)
            .ToArray());
        if (string.IsNullOrWhiteSpace(cleaned.Replace('_', ' '))) return fallback;

        var ext = Path.GetExtension(fallback);
        // 文件名太长有的系统存不下，截到 80 个字符够表达标题了
        if (cleaned.Length > 80) cleaned = cleaned[..80];
        return cleaned.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? cleaned : cleaned + ext;
    }

    private static string RenderTorqueStepHtml(string title, TorqueStepReportSnapshot snapshot)
    {
        static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
        var rows = string.Join("\n", snapshot.Points.Select(p => $"""
            <tr class="{(p.Excluded || !p.BaselineMatched ? "excluded" : "")}">
              <td>{p.Id}</td><td>{p.TorqueNm:F0}</td><td>{p.EstimatedPreloadN:F0}</td>
              <td>{p.SampleCount}</td><td>{p.AvgEchoTimeNs:F4}</td><td>{p.StddevEchoTimeNs:F4}</td>
              <td>{p.AvgTempC:F3}</td><td>{p.DeltaTofNs:F4}</td><td>{(p.Excluded ? "排除" : p.BaselineMatched ? "有效" : "旧基线")}</td>
              <td>{H(p.ExcludeReason ?? (!p.Excluded && !p.BaselineMatched ? "采于旧基线，参考系失效，不参与拟合" : p.Note))}</td>
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
          <div class="muted">测量依据：GB/T 43232-2023《紧固件 轴向应力超声测量方法》 · 生成时间：{{snapshot.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}}（当地时间）</div>
          <h2>对象</h2>
          <table>
            <tr><th>螺栓</th><td>{{H(snapshot.Bolt.Code)}} / {{H(snapshot.Bolt.NominalDiameter)}}</td></tr>
            <tr><th>设备 / 通道</th><td>设备 {{snapshot.Channel.DeviceId}} / 通道 {{snapshot.Channel.ChannelNo}}</td></tr>
            <tr><th>当前生效标定</th><td>K_L = {{snapshot.Channel.KL?.ToString("E4") ?? "—"}} ns/N，截距 b = {{snapshot.Channel.KLInterceptNs?.ToString("F2") ?? "—"}} ns（标定记录 {{snapshot.Channel.ActiveCalibrationId?.ToString() ?? "—"}}）</td></tr>
          </table>
          <h2>有效性说明</h2>
          <div class="warn">本报告基于扭矩法估算预紧力，不是载荷传感器或拉伸机正式标定结果。不得作为安全验收唯一依据。</div>
          <h2>扭矩实验点</h2>
          <table>
            <thead><tr><th>记录号</th><th>扭矩 (N·m)</th><th>估算预紧力 (N)</th><th>样本数</th><th>回波声时 (ns)</th><th>标准差 (ns)</th><th>温度 (°C)</th><th>Δt (ns)</th><th>状态</th><th>备注</th></tr></thead>
            <tbody>{{rows}}</tbody>
          </table>
          <h2>拟合结果</h2>
          <div class="{{(snapshot.Fit?.RSquared >= CalibrationThresholds.R2RejectDouble ? "ok" : "warn")}}">
            有效点：{{snapshot.Fit?.PointCount ?? 0}}；
            K_L：{{snapshot.Fit?.KL.ToString("E4") ?? "—"}} ns/N；
            截距 b：{{snapshot.Fit?.InterceptNs.ToString("F2") ?? "—"}} ns；
            R²：{{snapshot.Fit?.RSquared.ToString("F4") ?? "—"}}{{(snapshot.Fit is { RSquared: < CalibrationThresholds.R2RejectDouble } ? "（线性度不足，建议补点重测）" : "")}}
          </div>
          <h2>结论</h2>
          <p>{{H(snapshot.Conclusion)}}</p>
          <table style="margin-top:36px">
            <tr><th style="width:20%">编制（签字）</th><td style="width:30%"></td><th style="width:20%">审核（签字）</th><td style="width:30%"></td></tr>
            <tr><th>日期</th><td></td><th>日期</th><td></td></tr>
          </table>
        </body>
        </html>
        """;
    }

    private static string RenderFlangeHtml(
        string title, string project, string turbine, string flangeName, string flangeCode,
        decimal? groupTarget, int total, int calibrated, int passed, int failed, int uncal, int nodata, string overall, List<FlangeLine> bolts)
    {
        static string H(string? v) => WebUtility.HtmlEncode(v ?? "");
        static string N(decimal? v) => v.HasValue ? v.Value.ToString("F0") : "—";
        static string R(decimal? v) => v.HasValue ? (v.Value * 100).ToString("F1") + "%" : "—";
        // 当前值按 plan §9.15.4 带扩展不确定度交付："100 ± 5"，k=2 在表头统一标注。
        // 算不出预紧力时不显示 ±（那种情况连中心值都没有）；有预紧力但没不确定度时只显示中心值。
        static string NU(decimal? v, decimal? u) =>
            !v.HasValue ? "—"
            : u.HasValue ? $"{v.Value:F0} ± {u.Value:F0}"
            : v.Value.ToString("F0");
        var rows = string.Join("\n", bolts.Select((b, i) => $"""
            <tr><td>{i + 1}</td><td>{H(b.Code)}</td><td>{H(b.Spec)}</td><td>{H(b.Position)}</td>
            <td>{N(b.TargetN)}</td><td>{N(b.MinN)}</td><td>{NU(b.PreloadN, b.UncertaintyN)}</td><td>{R(b.Ratio)}</td>
            <td>{DisplayLabels.CalibrationMethod(b.CalibMethod)}</td><td>{H(b.Trust)}</td><td>{(b.RSquared.HasValue ? b.RSquared.Value.ToString("F3") : "—")}</td>
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
          <div class="muted">测量依据：GB/T 43232-2023《紧固件 轴向应力超声测量方法》 · JJF 1059.1-2012《测量不确定度评定与表示》 · 生成时间 {{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}}（当地时间）</div>
          <div class="muted">测量方法与精度：GB/T 43232 单波法（纵波），标定可靠时 {{H(DisplayLabels.PrecisionSpec("gb_single_wave"))}}。本机型为多通道在线监测设备，不具备横波采集能力，故不适用双波法（±3%）。</div>
          <h2>对象</h2>
          <table>
            <tr><th>风场</th><td>{{H(project)}}</td><th>风机</th><td>{{H(turbine)}}</td></tr>
            <tr><th>监测部位</th><td>{{H(flangeName)}}（{{H(flangeCode)}}）</td><th>组目标预紧力</th><td>{{N(groupTarget)}} N</td></tr>
          </table>
          <h2>验收结论</h2>
          <div class="{{verdictClass}}">总体：{{H(overall)}}；螺栓总数 {{total}}，已标定 {{calibrated}}，合格 {{passed}}，不合格 {{failed}}{{(uncal > 0 ? $"，未标定 {uncal}" : "")}}{{(nodata > 0 ? $"，无数据 {nodata}" : "")}}。</div>
          <h2>逐螺栓结果</h2>
          <table>
            <thead><tr><th>#</th><th>螺栓</th><th>规格</th><th>位置</th><th>目标 (N)</th><th>下限 (N)</th><th>当前 (N，± 为 U，k=2)</th><th>保持率</th><th>标定方法</th><th>可信等级</th><th>R²</th><th>结论</th></tr></thead>
            <tbody>{{rows}}</tbody>
          </table>
          <p class="muted">本报告由系统自动生成，数据快照已归档可追溯。“合格*”表示该螺栓预紧力达标但其标定为临时/工程估算等级，不作为正式安全验收依据；正式验收须以载荷标定（正式等级）为准。</p>
          <p class="muted">各标定方法的标称精度：正式载荷标定 / GB 单波法 {{H(DisplayLabels.PrecisionSpec("load_calibrated"))}}；扭矩阶梯拟合 {{H(DisplayLabels.PrecisionSpec("torque_fit"))}}；快速标定 / 扭矩估算 {{H(DisplayLabels.PrecisionSpec("quick"))}}。精度以标定有效、信号质量满足要求为前提。</p>
          <table style="margin-top:36px">
            <tr><th style="width:20%">编制（签字）</th><td style="width:30%"></td><th style="width:20%">审核（签字）</th><td style="width:30%"></td></tr>
            <tr><th>日期</th><td></td><th>日期</th><td></td></tr>
          </table>
        </body>
        </html>
        """;
    }
}

/// <summary>出一份扭矩分步标定报告。标题和备注可不填，不填就用默认的。</summary>
public sealed record TorqueStepReportRequest(string? Title = null, string? Notes = null);

/// <summary>出一份法兰验收报告。标题和备注可不填。</summary>
public sealed record FlangeReportRequest(string? Title = null, string? Notes = null);

/// <summary>法兰验收报告里的一行螺栓结果。存 JSON 快照和渲染归档 HTML 用的是同一份，省得两边对不上。</summary>
public sealed record FlangeLine(
    string Code, string Spec, string? Position, short ChannelNo,
    decimal? TargetN, decimal? MinN, decimal? PreloadN, decimal? Ratio,
    DateTime? LastTs, string? CalibMethod, string Trust, decimal? RSquared, decimal? UncertaintyN, string Result);

/// <summary>
/// 扭矩分步报告的数据快照。生成那一刻把用到的数全部按 JSON 存下来，
/// 之后螺栓参数、标定怎么改都动不了已经出过的报告——报告是要签字存档的，不能回头变样。
/// </summary>
public sealed record TorqueStepReportSnapshot(
    DateTimeOffset GeneratedAt,
    ChannelSnapshot Channel,
    BoltSnapshot Bolt,
    IReadOnlyList<TorquePointSnapshot> Points,
    TorqueFitSnapshot? Fit,
    string Conclusion);

/// <summary>快照里的通道信息，带上出报告那一刻用的 K_L 和当时生效的那条标定。</summary>
public sealed record ChannelSnapshot(long ChannelId, long DeviceId, short ChannelNo, decimal? KL, decimal? KLInterceptNs, Guid? ActiveCalibrationId);

/// <summary>快照里的螺栓信息，记的是出报告当时的规格和目标值，后来改了也不影响这份报告。</summary>
public sealed record BoltSnapshot(long BoltId, string Code, string NominalDiameter, decimal? TargetPreloadN, decimal? InstallTorqueNm);

/// <summary>
/// 快照里的一个扭矩档位测量点。被剔除的点也留着，连同剔除原因一起——拟合过程要能复核。
/// <paramref name="BaselineMatched"/>=false 表示该点采于旧基线（之后通道基线被重设），
/// Δt 参考系已失效，不参与拟合，报告中以"旧基线"状态列出备查。
/// </summary>
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
    string? Note,
    bool BaselineMatched = true);
/// <summary>快照里的线性拟合结果。K_L 按全系统的 ns/N 口径存。</summary>
public sealed record TorqueFitSnapshot(long PointCount, double KL, double InterceptNs, double RSquared);
