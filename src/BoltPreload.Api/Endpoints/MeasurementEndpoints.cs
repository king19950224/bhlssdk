using System.Text.Json;
using BoltPreload.Algorithms.Waveform;
using BoltPreload.Api.Auth;
using BoltPreload.Application.Ingestion;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Endpoints;

public sealed record ReferenceWaveRequest(string? Reason = null, string? Note = null);
public sealed record BindBoltRequest(long BoltId, string CalibrationAction = "invalidate", bool BackfillUnbound = false, string? Note = null);
public sealed record MoveBoltChannelRequest(long ToChannelId, string CalibrationAction = "invalidate", bool BackfillUnbound = false, string? Note = null);
public sealed record UnbindChannelRequest(string? Note = null);

/// <summary>
/// 测量数据查询（最新值 / 按螺栓历史 / 未绑定暂存）与通道-螺栓绑定管理端点。
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

            const string sql = @"
                SELECT ts, echo_time_ns, sp_temp_c, status_code, preload_n, preload_ratio
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
                    preloadRatio = reader.IsDBNull(5) ? null : (decimal?)reader.GetDecimal(5)
                });
            }
            return Results.Ok(new { boltId, count = items.Count, items });
        })
        .WithName("GetMeasurementsByBolt");

        // 计数（运维快速验证）
        group.MapGet("/count", async (NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            await using var cmd = dataSource.CreateCommand("SELECT COUNT(*) FROM measurements");
            var count = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
            return Results.Ok(new { count });
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
        // 注意：IWaveStore 在 BoltPreload.Application.Ingestion 命名空间
        // 这里用全限定名避免 using 顶部混入

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
            long channelId, NpgsqlDataSource ds, IWaveStore store, IMemoryCache cache, CancellationToken ct) =>
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

            var cur = await LoadWaveAsync(store, curUrl, ct);
            if (cur is null)
                return Results.Ok(new { channelId, hasCurrent = false, hasReference = false });
            var c = cur.Value;
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

            var refer = await LoadWaveAsync(store, refUrl, ct);
            if (refer is null)
                return Results.Ok(new { channelId, hasCurrent = true, hasReference = false, current = currentDto });
            var rf = refer.Value;

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

        // 从 WaveStore 读回并解析一条波形 JSON（LocalFileWaveStore 存的 camelCase 结构）
        static async Task<(short[] Wave, double EchoStart, double ClockInt, short? Threshold)?> LoadWaveAsync(
            IWaveStore store, string path, CancellationToken ct)
        {
            var bytes = await store.ReadRawAsync(path, ct);
            if (bytes is null) return null;
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
                return (wave, echoStart, clockInt, threshold);
            }
            catch { return null; }
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

        chGroup.MapPost("/{channelId:long}/bind-bolt", async (
            long channelId,
            BindBoltRequest req,
            HttpContext http,
            NpgsqlDataSource ds,
            IChannelResolver resolver,
            CancellationToken ct) =>
        {
            if (req.BoltId <= 0)
                return Results.BadRequest(new { error = "boltId 必须为正数" });

            var user = UserContext.Require(http);
            var result = await MoveBoltBindingAsync(
                ds, resolver, req.BoltId, channelId, user.UserId,
                req.CalibrationAction, req.BackfillUnbound, req.Note, ct);
            return result;
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("BindBoltToChannel")
        .WithSummary("把 bolt 绑定/迁移到指定物理通道（事务 + 审计 + 缓存失效）");

        app.MapPost("/api/bolts/{boltId:long}/move-channel", async (
            long boltId,
            MoveBoltChannelRequest req,
            HttpContext http,
            NpgsqlDataSource ds,
            IChannelResolver resolver,
            CancellationToken ct) =>
        {
            if (req.ToChannelId <= 0)
                return Results.BadRequest(new { error = "toChannelId 必须为正数" });

            var user = UserContext.Require(http);
            var result = await MoveBoltBindingAsync(
                ds, resolver, boltId, req.ToChannelId, user.UserId,
                req.CalibrationAction, req.BackfillUnbound, req.Note, ct);
            return result;
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithTags("Bolts")
        .WithName("MoveBoltChannel")
        .WithSummary("迁移 bolt 到另一个物理通道");

        chGroup.MapPost("/{channelId:long}/unbind-bolt", async (
            long channelId,
            UnbindChannelRequest? req,
            HttpContext http,
            NpgsqlDataSource ds,
            IChannelResolver resolver,
            CancellationToken ct) =>
        {
            var user = UserContext.Require(http);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // reader 先读完关闭再回滚（开着 reader 回滚会抛异常，把 404 变 500）
            long deviceId = 0;
            short channelNo = 0;
            long? boltId = null;
            string? beforeJson = null;
            var channelFound = false;

            await using (var cmd = new NpgsqlCommand(@"
                SELECT c.device_id, c.channel_no, b.id, to_jsonb(b)
                FROM channels c
                LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                WHERE c.id = @channel_id AND c.deleted_at IS NULL
                FOR UPDATE OF c", conn, tx))
            {
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    channelFound = true;
                    deviceId = r.GetInt64(0);
                    channelNo = r.GetInt16(1);
                    if (!r.IsDBNull(2))
                    {
                        boltId = r.GetInt64(2);
                        beforeJson = r.GetString(3);
                    }
                }
            }
            if (!channelFound)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "通道不存在", channelId });
            }

            if (boltId is null)
            {
                await tx.RollbackAsync(ct);
                return Results.BadRequest(new { error = "该通道未绑定螺栓", channelId });
            }

            // channel_id 守卫：并发 move 可能已把该螺栓迁到别的通道（move 只锁目标通道与螺栓行，
            // 不锁源通道）——按 id 盲删会把迁走的螺栓一并软删。0 行受影响 = 绑定已变，放弃本次解绑。
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE bolts
                   SET enabled = false,
                       state = 'disabled',
                       deleted_at = now(),
                       updated_at = now()
                 WHERE id = @bolt_id AND channel_id = @channel_id AND deleted_at IS NULL", conn, tx))
            {
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId.Value);
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Conflict(new { error = "绑定关系已变更（并发迁移/解绑？），请刷新后重试", channelId, boltId });
                }
            }

            // 探头仍在位时置 auto_enroll_hold：阻止立刻自动重建；真正离位后由
            // ChannelPresenceService 清 hold。重启不清——避免「删了重启又回来」。
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE channels
                   SET business_state = 'unbound',
                       k_l = NULL,
                       k_l_uncertainty = NULL,
                       k_l_intercept_ns = NULL,
                       active_calibration_id = NULL,
                       auto_enroll_hold = CASE
                           WHEN hardware_state IN ('healthy', 'noisy', 'suspect') THEN true
                           ELSE auto_enroll_hold END,
                       updated_at = now()
                 WHERE id = @channel_id", conn, tx))
            {
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO channel_binding_history (
                    bolt_id, from_channel_id, to_channel_id, action, calibration_action,
                    moved_by, before_jsonb, note
                ) VALUES (
                    @bolt_id, @from_channel_id, NULL, 'unbind', 'invalidate',
                    @user_id, @before::jsonb, @note
                )", conn, tx))
            {
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId.Value);
                cmd.Parameters.AddWithValue("from_channel_id", NpgsqlDbType.Bigint, channelId);
                cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, user.UserId);
                cmd.Parameters.AddWithValue("before", NpgsqlDbType.Jsonb, (object?)beforeJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)req?.Note ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // audit_log 同事务补录：binding_history 是绑定专用轨迹，审计日志页只查 audit_log，
            // 缺这条会让"解绑"在统一审计视图里隐身
            await using (var audit = new NpgsqlCommand(@"
                INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
                VALUES (@uid, 'channel_unbind', 'bolt', @bid, @before::jsonb, NULL, @note, now())", conn, tx))
            {
                audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, user.UserId);
                audit.Parameters.AddWithValue("bid", NpgsqlDbType.Bigint, boltId.Value);
                audit.Parameters.AddWithValue("before", NpgsqlDbType.Jsonb, (object?)beforeJson ?? DBNull.Value);
                audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                    $"解绑通道 CH{channelNo}（设备 {deviceId}）螺栓 #{boltId}，原因：{req?.Note ?? "（未填）"}");
                await audit.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            resolver.Invalidate(deviceId, (ushort)channelNo);

            return Results.Ok(new { unbound = true, channelId, boltId });
        })
        // 解绑=软删螺栓+清标定，破坏性操作与前端权限口径一致（UI 只给 reviewer 露按钮），
        // 后端策略同步收紧为 reviewer+，堵住 operator 直调 API 的旁路
        .RequireAuthorization(AuthPolicies.ReviewerAccess)
        .WithName("UnbindChannelBolt")
        .WithSummary("解绑通道当前 bolt（事务 + 审计 + 缓存失效；reviewer+）");

        return app;
    }

    private static async Task<IResult> MoveBoltBindingAsync(
        NpgsqlDataSource ds,
        IChannelResolver resolver,
        long boltId,
        long toChannelId,
        long userId,
        string calibrationAction,
        bool backfillUnbound,
        string? note,
        CancellationToken ct)
    {
        var normalizedAction = NormalizeCalibrationAction(calibrationAction);
        if (normalizedAction is null)
        {
            return Results.BadRequest(new
            {
                error = "calibrationAction 必须是 invalidate / transfer_with_validation / keep_uncomputed"
            });
        }

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // 以下三段均遵循「reader 先读完关闭，再回滚/返回」——开着 reader 回滚会抛异常，
            // 把本应 404/409 的业务分支变成 500。
            ChannelBindingSnapshot? target = null;
            await using (var cmd = new NpgsqlCommand(@"
                SELECT id, device_id, channel_no, k_l, k_l_uncertainty, k_l_intercept_ns,
                       active_calibration_id, ref_time_ns, ref_temp_c, to_jsonb(c)
                FROM channels c
                WHERE id = @channel_id AND deleted_at IS NULL
                FOR UPDATE", conn, tx))
            {
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, toChannelId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                    target = ReadChannelBindingSnapshot(r);
            }
            if (target is null)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "目标通道不存在", toChannelId });
            }

            BoltBindingSnapshot? bolt = null;
            await using (var cmd = new NpgsqlCommand(@"
                SELECT b.id, b.channel_id, c.device_id, c.channel_no,
                       c.k_l, c.k_l_uncertainty, c.k_l_intercept_ns,
                       c.active_calibration_id, c.ref_time_ns, c.ref_temp_c,
                       to_jsonb(b), to_jsonb(c)
                FROM bolts b
                JOIN channels c ON c.id = b.channel_id
                WHERE b.id = @bolt_id AND b.deleted_at IS NULL
                FOR UPDATE OF b", conn, tx))
            {
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                    bolt = ReadBoltBindingSnapshot(r);
            }
            if (bolt is null)
            {
                await tx.RollbackAsync(ct);
                return Results.NotFound(new { error = "螺栓不存在", boltId });
            }

            long existingBoltId = 0;
            string? existingBoltCode = null;
            await using (var cmd = new NpgsqlCommand(@"
                SELECT id, code
                FROM bolts
                WHERE channel_id = @channel_id
                  AND id <> @bolt_id
                  AND deleted_at IS NULL
                LIMIT 1", conn, tx))
            {
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, toChannelId);
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
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
                    error = "目标通道已绑定其他螺栓",
                    channelId = toChannelId,
                    existingBoltId,
                    existingBoltCode
                });
            }

            // 安全：禁止跨设备绑定/迁移——通道与螺栓必须在同一物理设备，否则测量/标定上下文错乱
            if (target.DeviceId != bolt.DeviceId)
            {
                await tx.RollbackAsync(ct);
                return Results.BadRequest(new
                {
                    error = "不允许跨设备绑定或迁移螺栓",
                    boltDeviceId = bolt.DeviceId,
                    targetDeviceId = target.DeviceId
                });
            }

            var isMove = bolt.ChannelId != toChannelId;
            var action = isMove ? "move" : "bind";
            // 同通道"再绑一次"(!isMove) 不动标定：默认 invalidate 会把在产标定清掉（UI 双击 / 幂等重绑的坑）
            if (isMove)
                await ApplyCalibrationPolicyAsync(conn, tx, target.ChannelId, bolt, normalizedAction, ct);

            await using (var cmd = new NpgsqlCommand(@"
                UPDATE bolts
                   SET channel_id = @channel_id,
                       enabled = true,
                       deleted_at = NULL,
                       updated_at = now()
                 WHERE id = @bolt_id", conn, tx))
            {
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, toChannelId);
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (isMove)
            {
                // 源通道螺栓已迁走：镜像 unbind 清理标定,避免空通道仍显示 calibrated / 下次绑定误用旧 K_L
                await using var fromCmd = new NpgsqlCommand(@"
                    UPDATE channels
                       SET business_state = 'unbound',
                           k_l = NULL,
                           k_l_uncertainty = NULL,
                           k_l_intercept_ns = NULL,
                           active_calibration_id = NULL,
                           updated_at = now()
                     WHERE id = @from_channel_id", conn, tx);
                fromCmd.Parameters.AddWithValue("from_channel_id", NpgsqlDbType.Bigint, bolt.ChannelId);
                await fromCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new NpgsqlCommand(@"
                UPDATE channels
                   SET business_state = 'bound',
                       updated_at = now()
                 WHERE id = @channel_id", conn, tx))
            {
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, toChannelId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            long backfilled = 0;
            if (backfillUnbound)
            {
                await using var cmd = new NpgsqlCommand(@"
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
                    ON CONFLICT (channel_id, ts) DO NOTHING", conn, tx);
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, toChannelId);
                backfilled = await cmd.ExecuteNonQueryAsync(ct);

                await using var wfCmd = new NpgsqlCommand(@"
                    UPDATE wave_features
                       SET bolt_id = @bolt_id
                     WHERE channel_id = @channel_id
                       AND bolt_id IS NULL", conn, tx);
                wfCmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                wfCmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, toChannelId);
                await wfCmd.ExecuteNonQueryAsync(ct);

                // 回填后清理该通道的 unbound 暂存（已迁入 measurements 或与既有行重复）：避免 /unbound 报表持续报陈旧数据、避免重复回填
                await using var delCmd = new NpgsqlCommand(
                    "DELETE FROM unbound_measurements WHERE channel_id = @channel_id", conn, tx);
                delCmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, toChannelId);
                await delCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new NpgsqlCommand(@"
                INSERT INTO channel_binding_history (
                    bolt_id, from_channel_id, to_channel_id, action, calibration_action,
                    moved_by, before_jsonb, after_jsonb, note
                )
                SELECT @bolt_id, @from_channel_id, @to_channel_id, @action, @calibration_action,
                       @user_id, @before::jsonb, to_jsonb(b), @note
                FROM bolts b
                WHERE b.id = @bolt_id", conn, tx))
            {
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
                cmd.Parameters.AddWithValue("from_channel_id", NpgsqlDbType.Bigint, bolt.ChannelId);
                cmd.Parameters.AddWithValue("to_channel_id", NpgsqlDbType.Bigint, toChannelId);
                cmd.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action);
                cmd.Parameters.AddWithValue("calibration_action", NpgsqlDbType.Varchar, normalizedAction);
                cmd.Parameters.AddWithValue("user_id", NpgsqlDbType.Bigint, userId);
                cmd.Parameters.AddWithValue("before", NpgsqlDbType.Jsonb, bolt.BoltJson);
                cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text, (object?)note ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // audit_log 同事务补录（统一审计视图只查 audit_log，见解绑处说明）
            await using (var audit = new NpgsqlCommand(@"
                INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
                SELECT @uid, @action, 'bolt', @bid, @before::jsonb, to_jsonb(b), @note
                FROM bolts b WHERE b.id = @bid", conn, tx))
            {
                audit.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, userId);
                audit.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, isMove ? "bolt_move_channel" : "channel_bind");
                audit.Parameters.AddWithValue("bid", NpgsqlDbType.Bigint, boltId);
                audit.Parameters.AddWithValue("before", NpgsqlDbType.Jsonb, bolt.BoltJson);
                audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                    (isMove
                        ? $"迁移螺栓 #{boltId} 通道 {bolt.ChannelId} → {toChannelId}（标定策略 {normalizedAction}，回填 {backfilled} 条）"
                        : $"绑定螺栓 #{boltId} 到通道 {toChannelId}（回填 {backfilled} 条）")
                    + (string.IsNullOrWhiteSpace(note) ? "" : $"，备注：{note}"));
                await audit.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            resolver.Invalidate(bolt.DeviceId, (ushort)bolt.ChannelNo);
            resolver.Invalidate(target.DeviceId, (ushort)target.ChannelNo);

            return Results.Ok(new
            {
                moved = isMove,
                boltId,
                fromChannelId = bolt.ChannelId,
                toChannelId,
                calibrationAction = normalizedAction,
                backfilled
            });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static string? NormalizeCalibrationAction(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "invalidate"
            : value.Trim().ToLowerInvariant();
        return normalized is "invalidate" or "transfer_with_validation" or "keep_uncomputed"
            ? normalized
            : null;
    }

    private static async Task ApplyCalibrationPolicyAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long targetChannelId,
        BoltBindingSnapshot source,
        string calibrationAction,
        CancellationToken ct)
    {
        if (calibrationAction == "transfer_with_validation")
        {
            await using var cmd = new NpgsqlCommand(@"
                UPDATE channels
                   SET k_l = @kl,
                       k_l_uncertainty = @klu,
                       k_l_intercept_ns = @intercept,
                       active_calibration_id = @calib_id,
                       ref_time_ns = COALESCE(@ref_time, ref_time_ns),
                       ref_temp_c = COALESCE(@ref_temp, ref_temp_c),
                       updated_at = now()
                 WHERE id = @channel_id", conn, tx);
            cmd.Parameters.AddWithValue("kl", NpgsqlDbType.Numeric, (object?)source.KL ?? DBNull.Value);
            cmd.Parameters.AddWithValue("klu", NpgsqlDbType.Numeric, (object?)source.KLUncertainty ?? DBNull.Value);
            cmd.Parameters.AddWithValue("intercept", NpgsqlDbType.Numeric, (object?)source.KLInterceptNs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("calib_id", NpgsqlDbType.Uuid, (object?)source.ActiveCalibrationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ref_time", NpgsqlDbType.Numeric, (object?)source.RefTimeNs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ref_temp", NpgsqlDbType.Numeric, (object?)source.RefTempC ?? DBNull.Value);
            cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, targetChannelId);
            await cmd.ExecuteNonQueryAsync(ct);
            return;
        }

        if (calibrationAction == "keep_uncomputed")
        {
            // 保留目标通道自身已有标定不动（no-op）。操作员显式选择"沿用目标通道现有标定"——
            // 典型场景：把螺栓重新绑回它原来的通道，标定本就对应该螺栓，不该被清空或被源通道覆盖。
            // 安全提示：若目标通道原是为别的螺栓标定的，沿用其 K_L 会给新螺栓算出错误预紧力；需要重标定时应选 invalidate。
            // calibration_action 已写入 channel_binding_history，可审计追溯。
            return;
        }

        // invalidate（默认）：清空目标通道标定，重标定通过前不出正式预紧力
        await using (var cmd = new NpgsqlCommand(@"
            UPDATE channels
               SET k_l = NULL,
                   k_l_uncertainty = NULL,
                   k_l_intercept_ns = NULL,
                   active_calibration_id = NULL,
                   updated_at = now()
             WHERE id = @channel_id", conn, tx))
        {
            cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, targetChannelId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static ChannelBindingSnapshot ReadChannelBindingSnapshot(NpgsqlDataReader r) => new(
        ChannelId: r.GetInt64(0),
        DeviceId: r.GetInt64(1),
        ChannelNo: r.GetInt16(2),
        KL: r.IsDBNull(3) ? null : r.GetDecimal(3),
        KLUncertainty: r.IsDBNull(4) ? null : r.GetDecimal(4),
        KLInterceptNs: r.IsDBNull(5) ? null : r.GetDecimal(5),
        ActiveCalibrationId: r.IsDBNull(6) ? null : r.GetGuid(6),
        RefTimeNs: r.IsDBNull(7) ? null : r.GetDecimal(7),
        RefTempC: r.IsDBNull(8) ? null : r.GetDecimal(8),
        ChannelJson: r.GetString(9));

    private static BoltBindingSnapshot ReadBoltBindingSnapshot(NpgsqlDataReader r) => new(
        BoltId: r.GetInt64(0),
        ChannelId: r.GetInt64(1),
        DeviceId: r.GetInt64(2),
        ChannelNo: r.GetInt16(3),
        KL: r.IsDBNull(4) ? null : r.GetDecimal(4),
        KLUncertainty: r.IsDBNull(5) ? null : r.GetDecimal(5),
        KLInterceptNs: r.IsDBNull(6) ? null : r.GetDecimal(6),
        ActiveCalibrationId: r.IsDBNull(7) ? null : r.GetGuid(7),
        RefTimeNs: r.IsDBNull(8) ? null : r.GetDecimal(8),
        RefTempC: r.IsDBNull(9) ? null : r.GetDecimal(9),
        BoltJson: r.GetString(10),
        ChannelJson: r.GetString(11));
}

internal sealed record ChannelBindingSnapshot(
    long ChannelId,
    long DeviceId,
    short ChannelNo,
    decimal? KL,
    decimal? KLUncertainty,
    decimal? KLInterceptNs,
    Guid? ActiveCalibrationId,
    decimal? RefTimeNs,
    decimal? RefTempC,
    string ChannelJson);

internal sealed record BoltBindingSnapshot(
    long BoltId,
    long ChannelId,
    long DeviceId,
    short ChannelNo,
    decimal? KL,
    decimal? KLUncertainty,
    decimal? KLInterceptNs,
    Guid? ActiveCalibrationId,
    decimal? RefTimeNs,
    decimal? RefTempC,
    string BoltJson,
    string ChannelJson);
