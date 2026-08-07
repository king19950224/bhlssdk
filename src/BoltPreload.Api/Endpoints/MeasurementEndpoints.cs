using System.Text.Json;
using BoltPreload.Algorithms.Waveform;
using BoltPreload.Api.Auth;
using BoltPreload.Application.Ingestion;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

/// <summary>把当前波形设成这个通道的参考波形。原因和备注可不填，填了会进审计。</summary>
public sealed record ReferenceWaveRequest(string? Reason = null, string? Note = null);

/// <summary>
/// 一次波形文件读取的结果。三态：读到了、没这个文件、文件在但解析不了。
/// 中间那态得单独留着——波形写入没有 fsync（见 <c>LocalFileWaveStore</c>），断电后可能是个截断的
/// JSON，跟"没这个文件"一起报成"暂无波形"，看的人会去现场查探头。
/// </summary>
internal readonly record struct WaveLoad(
    short[]? Wave, double EchoStart, double ClockInt, short? Threshold, bool Corrupt)
{
    /// <summary>没有这个文件（正常情况：该通道还没采到波形，或已过保留期被清理）。</summary>
    public static WaveLoad Missing => new(null, 0, 1, null, Corrupt: false);

    /// <summary>文件在，但解析不出来。</summary>
    public static WaveLoad Broken => new(null, 0, 1, null, Corrupt: true);

    /// <summary>读到了可用波形。为真时 <see cref="Wave"/> 一定非空（编译器据此放行后续解引用）。</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Wave))]
    public bool Ok => Wave is not null;
}

/// <summary>
/// 测量数据查询端点：最新测量 / 按螺栓历史 / 设备参数历史 / 波形特征与对比 /
/// 通道清单（含未绑定暂存汇总、多通道矩阵）。
/// 通道-螺栓绑定管理（bind/move/unbind）见 <see cref="BoltBindingEndpoints"/>。
/// </summary>
public static class MeasurementEndpoints
{
    public static IEndpointRouteBuilder MapMeasurementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/measurements")
            .WithTags("Measurements")
            .RequireAuthorization(AuthPolicies.LoggedIn);

        // 最近 N 条（不限通道）— 全局最新测量速览
        group.MapGet("/latest", async (NpgsqlDataSource dataSource, int? limit, CancellationToken ct) =>
        {
            var n = Math.Clamp(limit ?? 20, 1, 500);

            const string sql = @"
                SELECT bolt_id, channel_id, ts, echo_time_ns, sp_temp_c, gain, status_code, algo_version
                FROM measurements
                ORDER BY ts DESC
                LIMIT @n";

            await using var cmd = dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, n);

            var results = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new
                {
                    boltId = reader.GetInt64(0),
                    channelId = reader.GetInt64(1),
                    ts = reader.GetDateTime(2),
                    echoTimeNs = reader.IsDBNull(3) ? null : (decimal?)reader.GetDecimal(3),
                    spTempC = reader.IsDBNull(4) ? null : (decimal?)reader.GetDecimal(4),
                    gain = reader.GetFloat(5),
                    statusCode = reader.GetInt16(6),
                    algoVersion = reader.IsDBNull(7) ? null : reader.GetString(7)
                });
            }

            return Results.Ok(new { count = results.Count, items = results });
        })
        .WithName("GetLatestMeasurements")
        .WithSummary("最近测量数据（按 ts 降序）");

        // 按螺栓查最近
        group.MapGet("/by-bolt/{boltId:long}/latest", async (long boltId, NpgsqlDataSource dataSource, int? limit, CancellationToken ct) =>
        {
            var n = Math.Clamp(limit ?? 100, 1, 1000);

            // 带上扩展不确定度 U(k=2)：plan §9.15.4 要求测量结果必须带不确定度交付，
            // 界面写"100 ± 5 kN (k=2)"而不是光秃秃一个 100。此前这列只入库、没人读。
            const string sql = @"
                SELECT ts, echo_time_ns, sp_temp_c, status_code, preload_n, preload_ratio,
                       preload_uncertainty_n
                FROM measurements
                WHERE bolt_id = @bolt_id
                ORDER BY ts DESC
                LIMIT @n";

            await using var cmd = dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
            cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, n);

            var items = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new
                {
                    ts = reader.GetDateTime(0),
                    echoTimeNs = reader.IsDBNull(1) ? null : (decimal?)reader.GetDecimal(1),
                    spTempC = reader.IsDBNull(2) ? null : (decimal?)reader.GetDecimal(2),
                    statusCode = reader.GetInt16(3),
                    preloadN = reader.IsDBNull(4) ? null : (decimal?)reader.GetDecimal(4),
                    preloadRatio = reader.IsDBNull(5) ? null : (decimal?)reader.GetDecimal(5),
                    uncertaintyN = reader.IsDBNull(6) ? null : (decimal?)reader.GetDecimal(6)
                });
            }
            return Results.Ok(new { boltId, count = items.Count, items });
        })
        .WithName("GetMeasurementsByBolt");

        // 计数（运维快速验证）。默认只数近 24h：无谓词 COUNT(*) 在 hypertable 上是全 chunk 扫描，
        // 数据到年级后任何登录用户点一下就能把库拖住（DoS 面）。要全量就显式给大 hours，上限一年。
        group.MapGet("/count", async (NpgsqlDataSource dataSource, int? hours, CancellationToken ct) =>
        {
            var h = Math.Clamp(hours ?? 24, 1, 24 * 366);
            await using var cmd = dataSource.CreateCommand(
                "SELECT COUNT(*) FROM measurements WHERE ts > now() - make_interval(hours => $1)");
            cmd.Parameters.AddWithValue(h);
            var count = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
            return Results.Ok(new { count, hours = h });
        })
        .WithName("GetMeasurementsCount");

        // 设备参数历史（诊断用）
        var paramGroup = app.MapGroup("/api/devices/{deviceId:long}/parameters")
            .WithTags("DeviceParameters")
            .RequireAuthorization(AuthPolicies.LoggedIn);

        paramGroup.MapGet("/history", async (
            long deviceId, NpgsqlDataSource dataSource,
            string? paramName, short? channelNo, int? limit, CancellationToken ct) =>
        {
            var n = Math.Clamp(limit ?? 50, 1, 500);
            var sql = @"
                SELECT id, device_id, address, channel_no, param_name,
                       param_value_text, param_value_jsonb, received_at, ingested_at
                FROM device_parameter_history
                WHERE device_id = @device_id"
                + (paramName is null ? "" : " AND param_name = @param_name")
                + (channelNo.HasValue ? " AND channel_no = @channel_no" : "")
                + " ORDER BY received_at DESC LIMIT @n";

            await using var cmd = dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
            if (paramName is not null) cmd.Parameters.AddWithValue("param_name", NpgsqlDbType.Varchar, paramName);
            if (channelNo.HasValue) cmd.Parameters.AddWithValue("channel_no", NpgsqlDbType.Smallint, channelNo.Value);
            cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, n);

            var items = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new
                {
                    id = reader.GetInt64(0),
                    deviceId = reader.GetInt64(1),
                    address = reader.GetInt16(2),
                    channelNo = reader.IsDBNull(3) ? null : (short?)reader.GetInt16(3),
                    paramName = reader.GetString(4),
                    valueText = reader.IsDBNull(5) ? null : reader.GetString(5),
                    valueJsonb = reader.IsDBNull(6) ? null : reader.GetString(6),
                    receivedAt = reader.GetDateTime(7),
                    ingestedAt = reader.GetDateTime(8)
                });
            }

            return Results.Ok(new { deviceId, count = items.Count, items });
        })
        .WithName("GetDeviceParameterHistory")
        .WithSummary("设备参数历史（按时间倒序，可按 paramName/channelNo 过滤）");

        // 通道最新一次的所有参数（snapshot）
        paramGroup.MapGet("/channel/{channelNo:int}/snapshot", async (
            long deviceId, short channelNo, NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            const string sql = @"
                SELECT DISTINCT ON (param_name) param_name, param_value_text, param_value_jsonb, received_at
                FROM device_parameter_history
                WHERE device_id = @device_id AND channel_no = @channel_no
                ORDER BY param_name, received_at DESC";

            await using var cmd = dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
            cmd.Parameters.AddWithValue("channel_no", NpgsqlDbType.Smallint, channelNo);

            var snapshot = new Dictionary<string, object>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                snapshot[name] = new
                {
                    valueText = reader.IsDBNull(1) ? null : reader.GetString(1),
                    valueJsonb = reader.IsDBNull(2) ? null : reader.GetString(2),
                    receivedAt = reader.GetDateTime(3)
                };
            }

            return Results.Ok(new { deviceId, channelNo, snapshot });
        })
        .WithName("GetChannelSnapshot")
        .WithSummary("通道当前参数快照（取每个参数最新一次的值）");

        // ========== 波形（EchoWave）===========

        var waveGroup = app.MapGroup("/api/waves")
            .WithTags("Waves")
            .RequireAuthorization(AuthPolicies.LoggedIn);

        // 查最近 N 条带波形的 measurement（用于算法回放）
        waveGroup.MapGet("/recent", async (NpgsqlDataSource ds, long? boltId, int? limit, CancellationToken ct) =>
        {
            var n = Math.Clamp(limit ?? 20, 1, 200);
            var sql = @"
                SELECT m.bolt_id, m.ts, m.echo_time_ns, m.sp_temp_c, m.wave_blob_url
                FROM measurements m
                WHERE m.wave_blob_url IS NOT NULL"
                + (boltId.HasValue ? " AND m.bolt_id = @bid" : "")
                + " ORDER BY m.ts DESC LIMIT @n";

            await using var cmd = ds.CreateCommand(sql);
            if (boltId.HasValue) cmd.Parameters.AddWithValue("bid", NpgsqlDbType.Bigint, boltId.Value);
            cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, n);

            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new
                {
                    boltId = r.GetInt64(0),
                    ts = r.GetDateTime(1),
                    echoTimeNs = r.IsDBNull(2) ? null : (decimal?)r.GetDecimal(2),
                    spTempC = r.IsDBNull(3) ? null : (decimal?)r.GetDecimal(3),
                    wavePath = r.GetString(4),
                    waveUrl = $"/api/waves/file?path={Uri.EscapeDataString(r.GetString(4))}"
                });
            }
            return Results.Ok(new { count = items.Count, items });
        })
        .WithName("ListRecentWaves")
        .WithSummary("最近带波形的测量（按时间倒序）");

        // 下载/查看波形 JSON
        waveGroup.MapGet("/file", async (string path, IWaveStore store, CancellationToken ct) =>
        {
            var bytes = await store.ReadRawAsync(path, ct);
            return bytes is null
                ? Results.NotFound(new { error = "波形文件不存在或路径非法", path })
                : Results.File(bytes, "application/json", Path.GetFileName(path));
        })
        .WithName("DownloadWave")
        .WithSummary("按相对路径下载波形 JSON 文件");

        waveGroup.MapGet("/features/recent", async (
            NpgsqlDataSource ds, long? channelId, long? boltId, int? limit, CancellationToken ct) =>
        {
            var n = Math.Clamp(limit ?? 50, 1, 500);
            var sql = @"
                SELECT wf.id, wf.bolt_id, wf.channel_id, c.channel_no, wf.ts, wf.wave_blob_url, wf.point_count,
                       wf.echo_start_ns, wf.clock_int_ns, wf.threshold,
                       wf.peak_positive, wf.peak_negative, wf.peak_to_peak,
                       wf.rms_value, wf.noise_rms, wf.snr_db
                FROM wave_features wf
                LEFT JOIN channels c ON c.id = wf.channel_id
                WHERE 1=1"
                + (channelId.HasValue ? " AND wf.channel_id = @channel_id" : "")
                + (boltId.HasValue ? " AND wf.bolt_id = @bolt_id" : "")
                + " ORDER BY wf.ts DESC LIMIT @n";

            await using var cmd = ds.CreateCommand(sql);
            if (channelId.HasValue) cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId.Value);
            if (boltId.HasValue) cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId.Value);
            cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, n);

            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new
                {
                    id = r.GetInt64(0),
                    boltId = r.IsDBNull(1) ? null : (long?)r.GetInt64(1),
                    channelId = r.GetInt64(2),
                    channelNo = r.IsDBNull(3) ? null : (int?)r.GetInt16(3),
                    ts = r.GetDateTime(4),
                    waveBlobUrl = r.GetString(5),
                    pointCount = r.GetInt32(6),
                    echoStartNs = r.IsDBNull(7) ? null : (decimal?)r.GetDecimal(7),
                    clockIntNs = r.IsDBNull(8) ? null : (int?)r.GetInt32(8),
                    threshold = r.IsDBNull(9) ? null : (short?)r.GetInt16(9),
                    peakPositive = r.IsDBNull(10) ? null : (int?)r.GetInt32(10),
                    peakNegative = r.IsDBNull(11) ? null : (int?)r.GetInt32(11),
                    peakToPeak = r.IsDBNull(12) ? null : (int?)r.GetInt32(12),
                    rmsValue = r.IsDBNull(13) ? null : (decimal?)r.GetDecimal(13),
                    noiseRms = r.IsDBNull(14) ? null : (decimal?)r.GetDecimal(14),
                    snrDb = r.IsDBNull(15) ? null : (decimal?)r.GetDecimal(15),
                    waveUrl = $"/api/waves/file?path={Uri.EscapeDataString(r.GetString(5))}"
                });
            }

            return Results.Ok(new { count = items.Count, items });
        })
        .WithName("ListWaveFeatures")
        .WithSummary("最近波形特征");

        waveGroup.MapPost("/features/{featureId:long}/set-reference", async (
            long featureId, HttpContext http, NpgsqlDataSource ds, ReferenceWaveRequest? body, CancellationToken ct) =>
        {
            var currentUser = UserContext.Require(http);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                // reader 先读完关闭再回滚（开着 reader 回滚会抛异常，把 404 变 500）
                long channelId = 0;
                long? boltId = null;
                var featureFound = false;
                await using (var cmd = new NpgsqlCommand(
                    "SELECT channel_id, bolt_id FROM wave_features WHERE id=@id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, featureId);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct))
                    {
                        featureFound = true;
                        channelId = r.GetInt64(0);
                        boltId = r.IsDBNull(1) ? null : r.GetInt64(1);
                    }
                }
                if (!featureFound)
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "波形特征记录不存在", featureId });
                }

                await using (var cmd = new NpgsqlCommand(
                    "UPDATE reference_waves SET active=false WHERE channel_id=@channel_id AND active=true", conn, tx))
                {
                    cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                long refId;
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO reference_waves (channel_id, bolt_id, wave_feature_id, reason, created_by, note)
                    VALUES (@channel_id, @bolt_id, @feature_id, @reason, @user_id, @note)
                    RETURNING id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                    cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, (object?)boltId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("feature_id", NpgsqlDbType.Bigint, featureId);
                    cmd.Parameters.AddWithValue("reason", NpgsqlDbType.Varchar, body?.Reason ?? "manual");
                    cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, currentUser.UserId);
                    cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)body?.Note ?? DBNull.Value);
                    refId = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
                }

                await tx.CommitAsync(ct);
                return Results.Ok(new { referenceWaveId = refId, channelId, boltId, waveFeatureId = featureId, active = true });
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("SetReferenceWave")
        .WithSummary("把某个波形特征设为通道参考波形");

        // 波形对比诊断（§8.6）：当前波形 vs 通道 active 参考波形 → 相似度 / 衰减 / 健康度。
        // 只做诊断计算与返回，不触发告警（阈值是否驱动告警须由可追溯的告警规则单独决定）。
        waveGroup.MapGet("/compare/{channelId:long}", async (
            long channelId, NpgsqlDataSource ds, IWaveStore store, IMemoryCache cache,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            // 1. 当前（最新）波形特征
            string? curUrl = null; DateTime curTs = default; decimal? curSnr = null; int? curPp = null; long curId = 0;
            await using (var cmd = ds.CreateCommand(@"
                SELECT id, wave_blob_url, ts, snr_db, peak_to_peak
                FROM wave_features WHERE channel_id=@cid
                ORDER BY ts DESC LIMIT 1"))
            {
                cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    curId = r.GetInt64(0);
                    curUrl = r.GetString(1);
                    curTs = r.GetDateTime(2);
                    curSnr = r.IsDBNull(3) ? null : r.GetDecimal(3);
                    curPp = r.IsDBNull(4) ? null : r.GetInt32(4);
                }
            }
            if (curUrl is null)
                return Results.Ok(new { channelId, hasCurrent = false, hasReference = false });

            // 2. active 参考波形（健康基线）
            string? refUrl = null; DateTime refTs = default; long? refFeatureId = null;
            await using (var cmd = ds.CreateCommand(@"
                SELECT wf.wave_blob_url, wf.ts, wf.id
                FROM reference_waves rw JOIN wave_features wf ON wf.id = rw.wave_feature_id
                WHERE rw.channel_id=@cid AND rw.active=true
                ORDER BY rw.created_at DESC LIMIT 1"))
            {
                cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    refUrl = r.GetString(0);
                    refTs = r.GetDateTime(1);
                    refFeatureId = r.GetInt64(2);
                }
            }

            // 重计算成本（载入双波形 + FFT + O(n²) 互相关）只取决于 当前特征 id + 参考特征 id；
            // 前端 1.5s 轮询会反复请求同一通道，这里按 (cur,ref) 做短 TTL 缓存，避免重复重算打爆 CPU。
            var cacheKey = $"wavecmp:{channelId}:{curId}:{refFeatureId?.ToString() ?? "0"}";
            if (cache.TryGetValue(cacheKey, out object? cachedCmp) && cachedCmp is not null)
                return Results.Ok(cachedCmp);

            var cur = await LoadWaveAsync(store, curUrl, lf.CreateLogger("Waves"), ct);
            if (!cur.Ok)
                return Results.Ok(new
                {
                    channelId, hasCurrent = false, hasReference = false,
                    waveError = cur.Corrupt ? "当前波形文件已损坏，无法解析（不代表该通道没有采到波形）" : null,
                });
            var c = cur;
            object currentDto = new
            {
                featureId = curId, ts = curTs,
                echoStart = c.EchoStart, clockInt = c.ClockInt, threshold = c.Threshold,
                pointCount = c.Wave.Length, snrDb = curSnr, peakToPeak = curPp,
                waveData = c.Wave
            };

            // 没有参考波形：只回当前波形，前端提示"设为参考"
            if (refUrl is null)
                return Results.Ok(new { channelId, hasCurrent = true, hasReference = false, current = currentDto });

            var refer = await LoadWaveAsync(store, refUrl, lf.CreateLogger("Waves"), ct);
            if (!refer.Ok)
                return Results.Ok(new
                {
                    channelId, hasCurrent = true, hasReference = false, current = currentDto,
                    waveError = refer.Corrupt ? "参考波形文件已损坏，无法比对（需重新设定参考波形）" : null,
                });
            var rf = refer;

            var cmp = WaveDiagnostics.Compare(
                new WaveSeries(c.Wave, c.EchoStart, c.ClockInt),
                new WaveSeries(rf.Wave, rf.EchoStart, rf.ClockInt),
                snrDb: curSnr.HasValue ? (double)curSnr.Value : null);

            var result = new
            {
                channelId,
                hasCurrent = true,
                hasReference = true,
                referenceFeatureId = refFeatureId,
                current = currentDto,
                reference = new
                {
                    ts = refTs,
                    echoStart = rf.EchoStart, clockInt = rf.ClockInt, threshold = rf.Threshold,
                    pointCount = rf.Wave.Length, waveData = rf.Wave
                },
                metrics = new
                {
                    similarity = Math.Round(cmp.Similarity, 4),
                    bestLagNs = Math.Round(cmp.BestLagNs, 1),
                    zeroLagCorrelation = Math.Round(cmp.ZeroLagCorrelation, 4),
                    attenuationRatio = Math.Round(cmp.AttenuationRatio, 4),
                    healthScore = cmp.HealthScore,
                    scoreSnr = Math.Round(cmp.ScoreSnr, 3),
                    scoreSimilarity = Math.Round(cmp.ScoreSimilarity, 3),
                    scoreAttenuation = Math.Round(cmp.ScoreAttenuation, 3)
                }
            };
            // 缓存计算结果：key 含 cur/ref 特征 id，新测量或换参考会自然换 key；TTL 仅作内存兜底上限。
            cache.Set(cacheKey, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30),
                Size = 1
            });
            return Results.Ok(result);
        })
        .WithName("CompareWave")
        .WithSummary("波形对比诊断：当前波形 vs 通道参考波形（相似度/衰减/健康度，§8.6）");

        // 从 WaveStore 读回并解析一条波形 JSON（LocalFileWaveStore 存的 camelCase 结构）。
        // 解析失败和"文件不存在"分开报，原因见 WaveLoad。
        static async Task<WaveLoad> LoadWaveAsync(
            IWaveStore store, string path, ILogger log, CancellationToken ct)
        {
            var bytes = await store.ReadRawAsync(path, ct);
            if (bytes is null) return WaveLoad.Missing;
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                var root = doc.RootElement;
                double echoStart = root.TryGetProperty("echoStart", out var es) && es.ValueKind == JsonValueKind.Number ? es.GetDouble() : 0;
                double clockInt = root.TryGetProperty("clockInt", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetDouble() : 1;
                short? threshold = root.TryGetProperty("threshold", out var th) && th.ValueKind == JsonValueKind.Number ? (short?)th.GetInt32() : null;
                short[] wave = Array.Empty<short>();
                if (root.TryGetProperty("waveData", out var wd) && wd.ValueKind == JsonValueKind.Array)
                {
                    wave = new short[wd.GetArrayLength()];
                    int i = 0;
                    foreach (var el in wd.EnumerateArray())
                        wave[i++] = (short)el.GetInt32();
                }
                return new WaveLoad(wave, echoStart, clockInt, threshold, Corrupt: false);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
            {
                log.LogWarning(ex,
                    "波形文件存在但解析失败，按损坏处理 path={Path} size={Size}B", path, bytes.Length);
                return WaveLoad.Broken;
            }
        }

        // ========== 通道自动发现 / 未绑定测量 ==========
        var chGroup = app.MapGroup("/api/channels")
            .WithTags("Channels")
            .RequireAuthorization(AuthPolicies.LoggedIn);

        // 列出当前所有通道（含 auto_discovered 待人工确认的）。
        // deviceId 过滤下推到 SQL（此前前端拉全库再客户端过滤）；limit 护栏防自动发现流量撑大结果集。
        chGroup.MapGet("/", async (NpgsqlDataSource ds, string? state, long? deviceId, int? limit, CancellationToken ct) =>
        {
            var lim = Math.Clamp(limit ?? 2000, 1, 5000);
            var sql = @"
                SELECT c.id, c.device_id, c.channel_no, c.state, c.first_seen_at, c.last_seen_at,
                       c.k_l, c.ref_time_ns, c.ref_temp_c,
                       b.id AS bolt_id, b.code AS bolt_code
                FROM channels c
                LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                WHERE c.deleted_at IS NULL"
                + (state is null ? "" : " AND c.state = @state")
                + (deviceId is null ? "" : " AND c.device_id = @device_id")
                + " ORDER BY c.device_id, c.channel_no LIMIT @lim";

            await using var cmd = ds.CreateCommand(sql);
            if (state is not null) cmd.Parameters.AddWithValue("state", NpgsqlDbType.Varchar, state);
            if (deviceId is not null) cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId.Value);
            cmd.Parameters.AddWithValue("lim", NpgsqlDbType.Integer, lim);
            await using var r = await cmd.ExecuteReaderAsync(ct);

            var items = new List<object>();
            while (await r.ReadAsync(ct))
            {
                items.Add(new
                {
                    id = r.GetInt64(0),
                    deviceId = r.GetInt64(1),
                    channelNo = r.GetInt16(2),
                    state = r.GetString(3),
                    firstSeenAt = r.IsDBNull(4) ? null : (DateTime?)r.GetDateTime(4),
                    lastSeenAt = r.IsDBNull(5) ? null : (DateTime?)r.GetDateTime(5),
                    kL = r.IsDBNull(6) ? null : (decimal?)r.GetDecimal(6),
                    refTimeNs = r.IsDBNull(7) ? null : (decimal?)r.GetDecimal(7),
                    refTempC = r.IsDBNull(8) ? null : (decimal?)r.GetDecimal(8),
                    boltId = r.IsDBNull(9) ? null : (long?)r.GetInt64(9),
                    boltCode = r.IsDBNull(10) ? null : r.GetString(10)
                });
            }
            return Results.Ok(new { count = items.Count, items });
        })
        .WithName("ListChannels")
        .WithSummary("列出所有通道（含自动发现），可按 state 过滤");

        // 未绑定测量：暂存等待 bolt 绑定的数据
        chGroup.MapGet("/unbound", async (
            NpgsqlDataSource ds, long? channelId, int? limit, CancellationToken ct) =>
        {
            var n = Math.Clamp(limit ?? 100, 1, 1000);
            var sql = @"
                SELECT id, device_id, channel_id, channel_no, ts,
                       echo_time_ns, sp_temp_c, gain, status_code, wave_blob_url
                FROM unbound_measurements
                WHERE 1=1"
                + (channelId.HasValue ? " AND channel_id = @cid" : "")
                + " ORDER BY ts DESC LIMIT @n";

            await using var cmd = ds.CreateCommand(sql);
            if (channelId.HasValue) cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId.Value);
            cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, n);

            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new
                {
                    id = r.GetInt64(0),
                    deviceId = r.GetInt64(1),
                    channelId = r.GetInt64(2),
                    channelNo = r.GetInt16(3),
                    ts = r.GetDateTime(4),
                    echoTimeNs = r.IsDBNull(5) ? null : (decimal?)r.GetDecimal(5),
                    spTempC = r.IsDBNull(6) ? null : (decimal?)r.GetDecimal(6),
                    gain = r.IsDBNull(7) ? null : (float?)r.GetFloat(7),
                    statusCode = r.GetInt16(8),
                    wavePath = r.IsDBNull(9) ? null : r.GetString(9),
                    waveUrl = r.IsDBNull(9) ? null : $"/api/waves/file?path={Uri.EscapeDataString(r.GetString(9))}"
                });
            }
            return Results.Ok(new { count = items.Count, items });
        })
        .WithName("ListUnboundMeasurements")
        .WithSummary("未绑定 bolt 的测量数据（按时间倒序）");

        // 未绑定测量计数（按 channel_id 汇总，运维一眼看出哪些通道在丢数据）。
        // 只报"真正在等绑定"的通道：软删通道（历史清理）与已绑螺栓的通道（遗留暂存）不算——
        // 否则法兰页/设备页的未绑定提示会拿陈年数据报假警。遗留暂存仍可经 /unbound 原始列表查看。
        // 带回 hardware_state：前端"检测到探头未建档"引导只对在位状态机确认过的通道弹
        //（暂存行可能含 probe-scan 期间落的无回波样本/偶发杂散，行数≠有信号）。
        chGroup.MapGet("/unbound/summary", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            const string sql = @"
                SELECT um.device_id, um.channel_id, um.channel_no,
                       c.state, COUNT(*) AS cnt,
                       MIN(um.ts) AS first_ts, MAX(um.ts) AS last_ts,
                       COUNT(um.echo_time_ns) AS valid_echo,
                       c.hardware_state
                FROM unbound_measurements um
                JOIN channels c ON c.id = um.channel_id AND c.deleted_at IS NULL
                LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                WHERE b.id IS NULL
                GROUP BY um.device_id, um.channel_id, um.channel_no, c.state, c.hardware_state
                ORDER BY cnt DESC";
            await using var cmd = ds.CreateCommand(sql);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new
                {
                    deviceId = r.GetInt64(0),
                    channelId = r.GetInt64(1),
                    channelNo = r.GetInt16(2),
                    channelState = r.GetString(3),
                    count = r.GetInt64(4),
                    firstTs = r.GetDateTime(5),
                    lastTs = r.GetDateTime(6),
                    validEchoCount = r.GetInt64(7),
                    hardwareState = r.GetString(8)
                });
            }
            return Results.Ok(new { items });
        })
        .WithName("UnboundSummary")
        .WithSummary("未绑定测量汇总（哪些通道在等 bolt 绑定）");

        // 多通道矩阵：前端通道总览用，一行一个通道
        chGroup.MapGet("/matrix", async (NpgsqlDataSource ds, long? deviceId, CancellationToken ct) =>
        {
            var sql = @"
                SELECT
                    c.id AS channel_id,
                    c.device_id,
                    c.channel_no,
                    c.state AS channel_state,
                    c.probe_freq_mhz,
                    c.gain,
                    c.meas_mode,
                    c.threshold,
                    c.k_l IS NOT NULL AS calibrated,
                    c.active_calibration_id,
                    b.id AS bolt_id,
                    b.code AS bolt_code,
                    b.nominal_diam,
                    b.target_preload_n,
                    last_m.ts AS last_ts,
                    last_m.echo_time_ns,
                    last_m.sp_temp_c,
                    last_m.preload_n,
                    last_m.preload_ratio,
                    last_w.snr_db,
                    last_w.peak_to_peak,
                    open_alarm.open_count,
                    unbound.unbound_count
                FROM channels c
                LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                LEFT JOIN LATERAL (
                    SELECT ts, echo_time_ns, sp_temp_c, preload_n, preload_ratio
                    FROM measurements m
                    WHERE m.channel_id = c.id
                    ORDER BY ts DESC LIMIT 1
                ) last_m ON true
                LEFT JOIN LATERAL (
                    SELECT snr_db, peak_to_peak
                    FROM wave_features wf
                    WHERE wf.channel_id = c.id
                    ORDER BY ts DESC LIMIT 1
                ) last_w ON true
                LEFT JOIN LATERAL (
                    SELECT COUNT(*) AS open_count
                    FROM alarms a
                    WHERE a.channel_id = c.id AND a.cleared_at IS NULL
                ) open_alarm ON true
                LEFT JOIN LATERAL (
                    SELECT COUNT(*) AS unbound_count
                    FROM unbound_measurements um
                    WHERE um.channel_id = c.id
                ) unbound ON true
                WHERE c.deleted_at IS NULL"
                + (deviceId.HasValue ? " AND c.device_id = @device_id" : "")
                + " ORDER BY c.device_id, c.channel_no";

            await using var cmd = ds.CreateCommand(sql);
            if (deviceId.HasValue)
                cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId.Value);

            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var lastTs = r.IsDBNull(14) ? null : (DateTime?)r.GetDateTime(14);
                var ageSeconds = lastTs is null ? null : (double?)(DateTime.UtcNow - DateTime.SpecifyKind(lastTs.Value, DateTimeKind.Utc)).TotalSeconds;
                items.Add(new
                {
                    channelId = r.GetInt64(0),
                    deviceId = r.GetInt64(1),
                    channelNo = r.GetInt16(2),
                    channelState = r.GetString(3),
                    probeFreqMhz = r.GetDecimal(4),
                    gain = r.GetFloat(5),
                    measMode = r.GetInt16(6),
                    threshold = r.GetInt16(7),
                    calibrated = r.GetBoolean(8),
                    activeCalibrationId = r.IsDBNull(9) ? null : (Guid?)r.GetGuid(9),
                    boltId = r.IsDBNull(10) ? null : (long?)r.GetInt64(10),
                    boltCode = r.IsDBNull(11) ? null : r.GetString(11),
                    nominalDiam = r.IsDBNull(12) ? null : r.GetString(12),
                    targetPreloadN = r.IsDBNull(13) ? null : (decimal?)r.GetDecimal(13),
                    lastTs,
                    ageSeconds,
                    echoTimeNs = r.IsDBNull(15) ? null : (decimal?)r.GetDecimal(15),
                    spTempC = r.IsDBNull(16) ? null : (decimal?)r.GetDecimal(16),
                    preloadN = !r.GetBoolean(8) || r.IsDBNull(17) ? null : (decimal?)r.GetDecimal(17),
                    preloadRatio = !r.GetBoolean(8) || r.IsDBNull(18) ? null : (decimal?)r.GetDecimal(18),
                    snrDb = r.IsDBNull(19) ? null : (decimal?)r.GetDecimal(19),
                    peakToPeak = r.IsDBNull(20) ? null : (int?)r.GetInt32(20),
                    openAlarmCount = r.GetInt64(21),
                    unboundCount = r.GetInt64(22)
                });
            }

            return Results.Ok(new { count = items.Count, items });
        })
        .WithName("ChannelMatrix")
        .WithSummary("多通道矩阵：每通道绑定、标定、最近测量、波形质量、告警状态");

        return app;
    }
}

