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
/// Phase 1 最小测量查询接口。
/// Phase 2 改成 MediatR 走 Application 层 Query Handler。
/// </summary>
public static class MeasurementEndpoints
{
    public static IEndpointRouteBuilder MapMeasurementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/measurements")
            .WithTags("Measurements")
            .RequireAuthorization(AuthPolicies.LoggedIn);

        // 最近 N 条（不限通道）— 用于 Phase 1 第一周里程碑验证
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
                SELECT id, bolt_id, channel_id, ts, wave_blob_url, point_count,
                       echo_start_ns, clock_int_ns, threshold,
                       peak_positive, peak_negative, peak_to_peak,
                       rms_value, noise_rms, snr_db
                FROM wave_features
                WHERE 1=1"
                + (channelId.HasValue ? " AND channel_id = @channel_id" : "")
                + (boltId.HasValue ? " AND bolt_id = @bolt_id" : "")
                + " ORDER BY ts DESC LIMIT @n";

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
                    ts = r.GetDateTime(3),
                    waveBlobUrl = r.GetString(4),
                    pointCount = r.GetInt32(5),
                    echoStartNs = r.IsDBNull(6) ? null : (decimal?)r.GetDecimal(6),
                    clockIntNs = r.IsDBNull(7) ? null : (int?)r.GetInt32(7),
                    threshold = r.IsDBNull(8) ? null : (short?)r.GetInt16(8),
                    peakPositive = r.IsDBNull(9) ? null : (int?)r.GetInt32(9),
                    peakNegative = r.IsDBNull(10) ? null : (int?)r.GetInt32(10),
                    peakToPeak = r.IsDBNull(11) ? null : (int?)r.GetInt32(11),
                    rmsValue = r.IsDBNull(12) ? null : (decimal?)r.GetDecimal(12),
                    noiseRms = r.IsDBNull(13) ? null : (decimal?)r.GetDecimal(13),
                    snrDb = r.IsDBNull(14) ? null : (decimal?)r.GetDecimal(14),
                    waveUrl = $"/api/waves/file?path={Uri.EscapeDataString(r.GetString(4))}"
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
                long channelId;
                long? boltId;
                await using (var cmd = new NpgsqlCommand(
                    "SELECT channel_id, bolt_id FROM wave_features WHERE id=@id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, featureId);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    if (!await r.ReadAsync(ct))
                    {
                        await tx.RollbackAsync(ct);
                        return Results.NotFound(new { error = "wave feature not found", featureId });
                    }
                    channelId = r.GetInt64(0);
                    boltId = r.IsDBNull(1) ? null : r.GetInt64(1);
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

        // 列出当前所有通道（含 auto_discovered 待人工确认的）
        chGroup.MapGet("/", async (NpgsqlDataSource ds, string? state, CancellationToken ct) =>
        {
            var sql = @"
                SELECT c.id, c.device_id, c.channel_no, c.state, c.first_seen_at, c.last_seen_at,
                       c.k_l, c.ref_time_ns, c.ref_temp_c,
                       b.id AS bolt_id, b.code AS bolt_code
                FROM channels c
                LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                WHERE c.deleted_at IS NULL"
                + (state is null ? "" : " AND c.state = @state")
                + " ORDER BY c.device_id, c.channel_no";

            await using var cmd = ds.CreateCommand(sql);
            if (state is not null) cmd.Parameters.AddWithValue("state", NpgsqlDbType.Varchar, state);
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

        // 未绑定测量计数（按 channel_id 汇总，运维一眼看出哪些通道在丢数据）
        chGroup.MapGet("/unbound/summary", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            const string sql = @"
                SELECT um.device_id, um.channel_id, um.channel_no,
                       c.state, COUNT(*) AS cnt,
                       MIN(um.ts) AS first_ts, MAX(um.ts) AS last_ts,
                       COUNT(um.echo_time_ns) AS valid_echo
                FROM unbound_measurements um
                JOIN channels c ON c.id = um.channel_id
                GROUP BY um.device_id, um.channel_id, um.channel_no, c.state
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
                    validEchoCount = r.GetInt64(7)
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

            long deviceId;
            short channelNo;
            long? boltId = null;
            string? beforeJson = null;

            await using (var cmd = new NpgsqlCommand(@"
                SELECT c.device_id, c.channel_no, b.id, to_jsonb(b)
                FROM channels c
                LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                WHERE c.id = @channel_id AND c.deleted_at IS NULL
                FOR UPDATE OF c", conn, tx))
            {
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "channel not found", channelId });
                }
                deviceId = r.GetInt64(0);
                channelNo = r.GetInt16(1);
                if (!r.IsDBNull(2))
                {
                    boltId = r.GetInt64(2);
                    beforeJson = r.GetString(3);
                }
            }

            if (boltId is null)
            {
                await tx.RollbackAsync(ct);
                return Results.BadRequest(new { error = "channel 未绑定 bolt", channelId });
            }

            await using (var cmd = new NpgsqlCommand(@"
                UPDATE bolts
                   SET enabled = false,
                       state = 'disabled',
                       deleted_at = now(),
                       updated_at = now()
                 WHERE id = @bolt_id", conn, tx))
            {
                cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new NpgsqlCommand(@"
                UPDATE channels
                   SET business_state = 'unbound',
                       k_l = NULL,
                       k_l_uncertainty = NULL,
                       k_l_intercept_ns = NULL,
                       active_calibration_id = NULL,
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

            await tx.CommitAsync(ct);
            resolver.Invalidate(deviceId, (ushort)channelNo);

            return Results.Ok(new { unbound = true, channelId, boltId });
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("UnbindChannelBolt")
        .WithSummary("解绑通道当前 bolt（事务 + 审计 + 缓存失效）");

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
            ChannelBindingSnapshot target;
            await using (var cmd = new NpgsqlCommand(@"
                SELECT id, device_id, channel_no, k_l, k_l_uncertainty, k_l_intercept_ns,
                       active_calibration_id, ref_time_ns, ref_temp_c, to_jsonb(c)
                FROM channels c
                WHERE id = @channel_id AND deleted_at IS NULL
                FOR UPDATE", conn, tx))
            {
                cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, toChannelId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "target channel not found", toChannelId });
                }

                target = ReadChannelBindingSnapshot(r);
            }

            BoltBindingSnapshot bolt;
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
                if (!await r.ReadAsync(ct))
                {
                    await tx.RollbackAsync(ct);
                    return Results.NotFound(new { error = "bolt not found", boltId });
                }

                bolt = ReadBoltBindingSnapshot(r);
            }

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
                    await tx.RollbackAsync(ct);
                    return Results.Conflict(new
                    {
                        error = "target channel already has active bolt",
                        channelId = toChannelId,
                        existingBoltId = r.GetInt64(0),
                        existingBoltCode = r.GetString(1)
                    });
                }
            }

            var isMove = bolt.ChannelId != toChannelId;
            var action = isMove ? "move" : "bind";
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
                await using var fromCmd = new NpgsqlCommand(@"
                    UPDATE channels
                       SET business_state = 'unbound',
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
