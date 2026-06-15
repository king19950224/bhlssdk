namespace BoltPreload.Application.Devices;

/// <summary>
/// 通道配置服务：统一 DB 主数据、设备读回事实、SDK 运行态配置。
/// </summary>
public interface IChannelConfigService
{
    Task<ChannelConfigView?> GetAsync(long channelId, CancellationToken ct = default);

    Task<IReadOnlyList<ChannelConfigDifference>> CompareAsync(long deviceId, ushort channelNo, CancellationToken ct = default);
}

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

public sealed record ChannelConfigDifference(
    string Field,
    string? DatabaseValue,
    string? DeviceValue,
    bool IsDifferent,
    string Severity);
