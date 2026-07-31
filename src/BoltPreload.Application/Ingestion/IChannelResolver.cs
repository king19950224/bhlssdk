namespace BoltPreload.Application.Ingestion;

/// <summary>
/// (device_id, channel_no) → (channel_id, bolt_id) 路由解析。
/// <para>
/// 抽出来的原因：
/// 1. hot path（每条 ChannelResult 都要解析），必须能加缓存；
/// 2. 同时支持“自动发现”语义，未来可能换实现（如多设备路由表 / 网关）。
/// </para>
/// <para>
/// 实现规则：
/// - 找到 channel 但 bolt 未绑定 → 返回 (channelId, BoltId=0)；
/// - 通道未知 → 自动 INSERT channels(state='auto_discovered') 并返回；
/// - 写操作（标定、bolt 绑定）后必须调 <see cref="Invalidate"/> 让缓存重新加载。
/// </para>
/// </summary>
public interface IChannelResolver
{
    Task<ChannelRoute> ResolveAsync(long deviceId, ushort channelNo, DateTimeOffset firstSeenAt, CancellationToken ct = default);

    /// <summary>失效特定 (device, channel) 的缓存条目（写操作后调用）。</summary>
    void Invalidate(long deviceId, ushort channelNo);

    /// <summary>失效所有缓存（重大配置变更后调用，慎用）。</summary>
    void InvalidateAll();
}

/// <summary>解析结果（hot path 全量信息）。BoltId=0 → 未绑定。Calibration=null → 未标定。</summary>
/// <param name="ChannelId">channels.id</param>
/// <param name="BoltId">bolts.id；0 = 未绑定</param>
/// <param name="Calibration">激活的标定参数（来自 channels.k_l/ref_time_ns/ref_temp_c 冗余字段）</param>
/// <param name="BoltSpec">螺栓物理规格（hot path 计算需要的所有字段）</param>
/// <param name="TargetPreloadN">目标预紧力（用于算 preload_ratio）</param>
/// <param name="AutoDiscovered">本次解析是不是触发了自动发现（仅诊断用）</param>
public sealed record ChannelRoute(
    long ChannelId,
    long BoltId,
    CachedCalibration? Calibration = null,
    CachedBoltSpec? BoltSpec = null,
    decimal? TargetPreloadN = null,
    bool AutoDiscovered = false)
{
    /// <summary>有完整标定 + 物理规格 + 目标预紧力，可以算预紧力</summary>
    public bool CanComputePreload =>
        Calibration is not null
        && BoltSpec is not null
        && Calibration.KL > 0
        && Calibration.RefTimeNs > 0;
}

/// <summary>缓存的标定参数（从 channels 冗余字段读，避免 JOIN）</summary>
/// <param name="CalibrationId">calibration_records.id</param>
/// <param name="KL">载荷因子（1/N）</param>
/// <param name="KLUncertainty">扩展不确定度（k=2）</param>
/// <param name="RefTimeNs">参考声时</param>
/// <param name="RefTempC">参考温度</param>
/// <param name="ExpiresAt">标定有效期（calibration_records.expires_at，date）。过期后不得当正式预紧力监测值（plan §7.3）。null=未知有效期（按未过期处理，因 active_calibration_id 必有 NOT NULL 的 expires_at）。</param>
/// <param name="Passed">标定是否合格（calibration_records.passed）。false=已应用但未达合格线（如 torque_fit R² 在 [0.95,0.98)），热路径不得据此出"正式"预紧力。</param>
public sealed record CachedCalibration(
    Guid CalibrationId,
    decimal KL,
    decimal KLUncertainty,
    decimal RefTimeNs,
    decimal RefTempC,
    decimal? InterceptNs = null,
    DateTime? ExpiresAt = null,
    bool Passed = true)
{
    /// <summary>
    /// 标定是否已过期。expires_at 是 date 粒度，按 UTC 日期比较：当 utcNow 的日期晚于有效期日期即过期。
    /// 过期标定仍可看历史，但热路径不得据此算出"正式"预紧力（安全攸关，见 plan §7.3）。
    /// </summary>
    public bool IsExpiredAsOf(DateTime utcNow) => ExpiresAt is { } e && utcNow.Date > e.Date;

    /// <summary>
    /// 是否可作为"正式"预紧力监测依据：必须合格(Passed) 且 未过期。
    /// 不满足时热路径应保留原始 echo/温度，但不输出 preload_n（避免把不可信值当真）。
    /// </summary>
    public bool IsUsableForOfficialPreload(DateTime utcNow) => Passed && !IsExpiredAsOf(utcNow);
}

/// <summary>缓存的螺栓规格（投影自 bolts 表算法必需字段）。</summary>
/// <param name="TempCoefficientPerC">温度系数（1/°C）。null = 该材料未配，由调用方退化到 SDK 默认。</param>
public sealed record CachedBoltSpec(
    string Grade,
    decimal Rp02Mpa,
    decimal ElasticModulusGpa,
    decimal NominalDiamMm,
    decimal EffectiveLengthMm,
    decimal CrossSectionMm2,
    decimal SoundVelocityMs,
    decimal? TempCoefficientPerC = null);

/// <summary>缓存统计（暴露给 /api/diagnostics/cache-stats）</summary>
/// <param name="Hits">缓存命中次数</param>
/// <param name="Misses">缓存未命中次数（走 DB）</param>
/// <param name="AutoDiscoveries">触发自动发现的次数</param>
/// <param name="Invalidations">主动失效次数</param>
public sealed record CacheStats(long Hits, long Misses, long AutoDiscoveries, long Invalidations)
{
    public double HitRate => (Hits + Misses) == 0 ? 0 : (double)Hits / (Hits + Misses);
}
