namespace BoltPreload.Application.Devices;

/// <summary>
/// 通道配置服务：统一 DB 主数据、设备读回事实、SDK 运行态配置。
/// </summary>
public interface IChannelConfigService
{
    Task<ChannelConfigView?> GetAsync(long channelId, CancellationToken ct = default);

    Task<IReadOnlyList<ChannelConfigDifference>> CompareAsync(long deviceId, ushort channelNo, CancellationToken ct = default);
}

/// <summary>
/// 单通道配置全景视图：channels 表主数据 + 三份配置值
/// （Desired=库内期望，Actual=设备最近读回，Effective=读回优先合并结果）。
/// </summary>
public sealed record ChannelConfigView(
    long ChannelId,
    long DeviceId,
    ushort ChannelNo,
    string State,
    bool SamplingEnabled,
    string HardwareState,
    string BusinessState,
    string ConfigSource,
    DateTimeOffset? ConfigSyncedAt,
    decimal ProbeFreqMhz,
    float Gain,
    short Threshold,
    ushort MeasMode,
    decimal? RefTimeNs,
    decimal? RefTempC,
    ushort TcMode,
    decimal? TempCoefficient,
    decimal? KL,
    decimal? KLInterceptNs,
    Guid? ActiveCalibrationId,
    DateTimeOffset? LastSeenAt,
    ChannelConfigValues Desired,
    ChannelConfigValues? Actual,
    ChannelConfigValues Effective);

/// <summary>一组通道测量参数值，字段可空表示该来源没有此参数；Source 标记取值来源。</summary>
public sealed record ChannelConfigValues(
    decimal? ProbeFreqMhz,
    decimal? Gain,
    short? Threshold,
    ushort? MeasMode,
    decimal? RefTimeNs,
    decimal? RefTempC,
    ushort? TcMode,
    decimal? TempCoefficient,
    decimal? EchoWinNs,
    ushort? EchoWidthUs,
    string Source);

/// <summary>单字段的库值/设备读回值对比结果。Severity：ok / info / warn（影响测量口径的字段不一致）。</summary>
public sealed record ChannelConfigDifference(
    string Field,
    string? DatabaseValue,
    string? DeviceValue,
    bool IsDifferent,
    string Severity);
