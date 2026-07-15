using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using BoltPreload.Application.Ingestion;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Infrastructure.Ingestion;

/// <summary>
/// 带 IMemoryCache 的通道解析器。
/// <para>
/// 缓存键：<c>"chan:{deviceId}:{channelNo}"</c>
/// </para>
/// <para>
/// TTL：5 分钟绝对过期 + 1 分钟滑动。
/// 写操作（calibrate / bind bolt / 通道停用）必须主动 <see cref="Invalidate"/>。
/// </para>
/// <para>
/// 自动发现：cache miss 且 DB 也没记录 → INSERT channels(state='auto_discovered')，
/// 然后把新 ChannelRoute (boltId=0, autoDiscovered=true) 写入缓存。
/// 注意：auto_discovered 不会被持续缓存太久（用 30s 短 TTL），因为业主可能马上绑定 bolt。
/// </para>
/// </summary>
public sealed class CachedChannelResolver : IChannelResolver
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CachedChannelResolver> _logger;

    private static readonly TimeSpan BoundTtl = TimeSpan.FromMinutes(5);       // 已绑定 bolt 的通道
    private static readonly TimeSpan UnboundTtl = TimeSpan.FromSeconds(30);    // 未绑定 / auto_discovered（更短，方便业主绑定后快速生效）
    private static readonly TimeSpan TouchLastSeenInterval = TimeSpan.FromMinutes(1);  // last_seen UPDATE 节流：每通道每分钟最多一次

    private static readonly Regex NominalDiamRegex = new(
        @"^\s*M?(?<v>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 节流：(channelId) → 最近一次 UPDATE 时间
    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastTouchedAt = new();

    // 持有所有"曾经缓存过的 key"，让 InvalidateAll 只清自己的 key，不踩 IMemoryCache 其他使用方
    private readonly ConcurrentDictionary<string, byte> _knownKeys = new();

    // 简单计数器，用于 /api/diagnostics/cache-stats 暴露效率
    private long _hits;
    private long _misses;
    private long _autoDiscoveries;
    private long _invalidations;

    public CacheStats Stats => new(
        Hits: Interlocked.Read(ref _hits),
        Misses: Interlocked.Read(ref _misses),
        AutoDiscoveries: Interlocked.Read(ref _autoDiscoveries),
        Invalidations: Interlocked.Read(ref _invalidations));

    public CachedChannelResolver(NpgsqlDataSource dataSource, IMemoryCache cache, ILogger<CachedChannelResolver> logger)
    {
        _dataSource = dataSource;
        _cache = cache;
        _logger = logger;
    }

    private static string CacheKey(long deviceId, ushort channelNo) => $"chan:{deviceId}:{channelNo}";

    public async Task<ChannelRoute> ResolveAsync(long deviceId, ushort channelNo, DateTimeOffset firstSeenAt, CancellationToken ct = default)
    {
        var key = CacheKey(deviceId, channelNo);
        if (_cache.TryGetValue(key, out ChannelRoute? cached) && cached is not null)
        {
            Interlocked.Increment(ref _hits);
            return cached;
        }

        Interlocked.Increment(ref _misses);
        var route = await ResolveFromDbAsync(deviceId, channelNo, firstSeenAt, ct);
        if (route.AutoDiscovered) Interlocked.Increment(ref _autoDiscoveries);

        // TTL 分两档：已绑 bolt 缓存 5 分钟（业务对象，几乎不变）；未绑缓存 30 秒（等业主绑）
        var ttl = route.BoltId > 0 ? BoundTtl : UnboundTtl;
        _knownKeys.TryAdd(key, 0);
        var entryOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            SlidingExpiration = ttl > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : null,
            Size = 1
        };
        // 条目 TTL 过期/被容量驱逐时同步移出 _knownKeys——否则 key 集合只增不减,多年运行缓慢膨胀
        entryOptions.RegisterPostEvictionCallback(static (k, _, reason, state) =>
        {
            if (reason is EvictionReason.Expired or EvictionReason.Capacity or EvictionReason.TokenExpired
                && state is ConcurrentDictionary<string, byte> keys && k is string sk)
                keys.TryRemove(sk, out byte _);
        }, _knownKeys);
        _cache.Set(key, route, entryOptions);

        return route;
    }

    public void Invalidate(long deviceId, ushort channelNo)
    {
        var key = CacheKey(deviceId, channelNo);
        _cache.Remove(key);
        _knownKeys.TryRemove(key, out _);
        Interlocked.Increment(ref _invalidations);
    }

    /// <summary>
    /// 只失效本 resolver 写入过的 key。Phase 1 早期实现是 mc.Compact(1.0) 整个进程缓存清空，
    /// 多个 caller 共享 IMemoryCache 时会误伤 — 改成显式遍历 _knownKeys。
    /// </summary>
    public void InvalidateAll()
    {
        foreach (var key in _knownKeys.Keys)
        {
            _cache.Remove(key);
        }
        _knownKeys.Clear();
        Interlocked.Increment(ref _invalidations);
        _logger.LogWarning("通道路由缓存全量失效");
    }

    private async Task<ChannelRoute> ResolveFromDbAsync(long deviceId, ushort channelNo, DateTimeOffset firstSeenAt, CancellationToken ct)
    {
        // ① 查 channel + bolt + 标定 + 物理规格（一次 JOIN 拿完）
        // 走 channels 表的冗余字段（k_l / ref_time_ns / ref_temp_c / active_calibration_id），
        // 避免 hot path 再 JOIN calibration_records
        // expires_at 取自 active_calibration_id 指向的 calibration_records（NOT NULL date）。
        // 仅 cache miss 时多一个按主键的 LEFT JOIN，热路径（缓存命中）无感。
        const string querySql = @"
            SELECT
                c.id AS channel_id,
                b.id AS bolt_id,
                c.k_l, c.k_l_uncertainty, c.ref_time_ns, c.ref_temp_c, c.active_calibration_id, c.k_l_intercept_ns,
                b.grade, b.rp02_mpa, b.elastic_modulus_gpa, b.nominal_diam,
                b.effective_length_mm, b.cross_section_mm2, b.sound_velocity_ms,
                b.target_preload_n, b.temp_coefficient_per_c,
                cal.expires_at, cal.passed
            FROM channels c
            LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
            LEFT JOIN calibration_records cal ON cal.id = c.active_calibration_id
            WHERE c.device_id = @device_id AND c.channel_no = @channel_no AND c.deleted_at IS NULL
            LIMIT 1";

        await using (var cmd = _dataSource.CreateCommand(querySql))
        {
            cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
            cmd.Parameters.AddWithValue("channel_no", NpgsqlDbType.Smallint, (short)channelNo);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var channelId = reader.GetInt64(0);
                var boltId = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);

                CachedCalibration? calib = null;
                if (!reader.IsDBNull(2) && !reader.IsDBNull(4) && !reader.IsDBNull(6))
                {
                    calib = new CachedCalibration(
                        CalibrationId: reader.GetGuid(6),
                        KL: reader.GetDecimal(2),
                        KLUncertainty: reader.IsDBNull(3) ? 0m : reader.GetDecimal(3),
                        RefTimeNs: reader.GetDecimal(4),
                        RefTempC: reader.IsDBNull(5) ? 20m : reader.GetDecimal(5),
                        InterceptNs: reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                        ExpiresAt: reader.IsDBNull(17) ? null : reader.GetDateTime(17),
                        // passed 列 NOT NULL,此处 NULL 只可能是 active_calibration_id 悬空(标定记录被删的脏数据)——
                        // 悬空引用不能当"合格标定"继续出正式预紧力,默认 false 让该通道停算并暴露问题
                        Passed: !reader.IsDBNull(18) && reader.GetBoolean(18));
                }

                CachedBoltSpec? spec = null;
                decimal? targetPreload = null;
                if (boltId > 0)
                {
                    spec = new CachedBoltSpec(
                        Grade: reader.GetString(8),
                        Rp02Mpa: reader.GetDecimal(9),
                        ElasticModulusGpa: reader.GetDecimal(10),
                        NominalDiamMm: ParseNominalDiam(reader.GetString(11)),
                        EffectiveLengthMm: reader.GetDecimal(12),
                        CrossSectionMm2: reader.GetDecimal(13),
                        SoundVelocityMs: reader.GetDecimal(14),
                        TempCoefficientPerC: reader.IsDBNull(16) ? null : (decimal?)reader.GetDecimal(16));
                    targetPreload = reader.IsDBNull(15) ? null : (decimal?)reader.GetDecimal(15);
                }

                TryTouchLastSeen(channelId, firstSeenAt, ct);
                return new ChannelRoute(channelId, boltId, calib, spec, targetPreload, AutoDiscovered: false);
            }
        }

        // ② 自动发现
        // 注意 NOT EXISTS 墓碑检查：被显式软删的通道(deleted_at IS NOT NULL)不允许自动复活。
        // 设备会周期性上报全部物理通道的参数帧(含未接探头的)，若无此检查，运维删掉的空通道
        // 会在下一个参数扫描周期被重建，删除操作永远不生效。重新启用通道须显式清 deleted_at
        //（走通道管理/审批），而不是靠数据流"悄悄"复活。
        const string insertSql = @"
            INSERT INTO channels (device_id, channel_no, state, first_seen_at, last_seen_at)
            SELECT @device_id, @channel_no, 'auto_discovered', @ts, @ts
            WHERE NOT EXISTS (
                SELECT 1 FROM channels
                WHERE device_id = @device_id AND channel_no = @channel_no AND deleted_at IS NOT NULL)
            ON CONFLICT DO NOTHING
            RETURNING id";

        try
        {
            await using var cmd = _dataSource.CreateCommand(insertSql);
            cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
            cmd.Parameters.AddWithValue("channel_no", NpgsqlDbType.Smallint, (short)channelNo);
            cmd.Parameters.AddWithValue("ts", NpgsqlDbType.TimestampTz, firstSeenAt);

            var scalar = await cmd.ExecuteScalarAsync(ct);
            if (scalar is long newId)
            {
                _logger.LogWarning("自动发现新通道 device={DeviceId} channel={Channel} → channel_id={Id}", deviceId, channelNo, newId);
                return new ChannelRoute(newId, 0, AutoDiscovered: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "自动发现失败 device={DeviceId} channel={Channel}", deviceId, channelNo);
        }

        // ③ INSERT 没返回 id（并发抢先了）→ 再查一次
        await using (var requery = _dataSource.CreateCommand(
            "SELECT id FROM channels WHERE device_id=@d AND channel_no=@c AND deleted_at IS NULL LIMIT 1"))
        {
            requery.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
            requery.Parameters.AddWithValue("c", NpgsqlDbType.Smallint, (short)channelNo);
            var r = await requery.ExecuteScalarAsync(ct);
            if (r is long id2)
                return new ChannelRoute(id2, 0, AutoDiscovered: false);
        }

        return new ChannelRoute(0, 0, AutoDiscovered: false);
    }

    /// <summary>
    /// 把 bolts.nominal_diam（如 "M16"、"M16x80"、"M16.5"、"3/4"）解析成毫米数值。
    /// 用前缀正则只取第一个数字（含小数），其余忽略；解析失败返回 0。
    /// </summary>
    internal static decimal ParseNominalDiam(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0m;
        var match = NominalDiamRegex.Match(s);
        if (!match.Success) return 0m;
        return decimal.TryParse(match.Groups["v"].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m;
    }

    /// <summary>
    /// 节流：同一 channel 每分钟最多 UPDATE 一次 last_seen_at。
    /// 防御 50Hz × N 通道场景下 UPDATE 写放大。
    /// </summary>
    private void TryTouchLastSeen(long channelId, DateTimeOffset ts, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastTouchedAt.TryGetValue(channelId, out var last) && now - last < TouchLastSeenInterval)
            return;
        _lastTouchedAt[channelId] = now;
        _ = TouchLastSeenAsync(channelId, ts, ct);
    }

    private async Task TouchLastSeenAsync(long channelId, DateTimeOffset ts, CancellationToken ct)
    {
        const string sql = @"
            UPDATE channels SET last_seen_at = @ts
            WHERE id = @id AND (last_seen_at IS NULL OR last_seen_at < @ts)";
        try
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
            cmd.Parameters.AddWithValue("ts", NpgsqlDbType.TimestampTz, ts);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (OperationCanceledException) { /* 进程退出，OK */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "更新 channel last_seen 失败 id={Id}", channelId);
        }
    }
}
