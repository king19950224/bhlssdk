using BoltPreload.Application.Ingestion;
using BoltPreload.Fx;

namespace BoltPreload.Application.Devices;

/// <summary>
/// 多设备连接运行时管理器。
/// <para>
/// 商业版不允许把 IFxConnector 注册成单例后到处直接使用。
/// 所有设备连接、状态、Watchdog、运行指标都应先归集到 DeviceConnectorManager。
/// 当前阶段先承载单设备，接口按多设备设计。
/// </para>
/// </summary>
public interface IDeviceConnectorManager
{
    IReadOnlyCollection<DeviceRuntimeContext> Runtimes { get; }

    DeviceRuntimeContext Primary { get; }

    DeviceRuntimeContext? Find(long deviceId);

    /// <summary>
    /// 运行时更新设备物理通道数（devices.channel_count 变更后同步内存运行时，免重启）。
    /// 热插拔全通道轮询（ChannelPresenceService）与探头扫描默认通道集都以此为准。
    /// 返回 false 表示设备不在运行时中。
    /// </summary>
    bool TryUpdateChannelCount(long deviceId, ushort channelCount);
}

/// <summary>授权容量核算用的运行时口径。</summary>
public static class DeviceConnectorManagerExtensions
{
    /// <summary>
    /// 当前在用的通道总数，按 (deviceId, address, channel) 去重
    /// —— 多设备下不同设备可能用同一 address/channel，不去重会互相抵消。
    /// <para>
    /// 展示（/api/runtime/devices）与执法（LicenseEnforcementState）必须同一口径：
    /// 此前两处各写了一份逐字相同的 LINQ，单边改一处，运维看到的数就和闸门实际执法的数分家了。
    /// </para>
    /// </summary>
    public static int CountActiveChannels(this IDeviceConnectorManager manager) =>
        manager.Runtimes
            .SelectMany(rt => rt.Connector.GetSessionChannelConfigs()
                .Select(c => (rt.DeviceId, c.Address, c.Channel)))
            .Distinct()
            .Count();
}

/// <summary>一台设备的运行时上下文：连接器 + 采集状态 + 启动参数，由 DeviceConnectorManager 创建并持有。</summary>
public sealed record DeviceRuntimeContext(
    long DeviceId,
    string Name,
    bool IsPrimary,
    IFxConnector Connector,
    IIngestionRuntimeStatus RuntimeStatus,
    DeviceRuntimeOptions Options);

/// <param name="ChannelCount">设备物理通道数（devices.channel_count / 模拟器 ChannelCount）；热插拔全通道轮询的目标范围 1..N</param>
public sealed record DeviceRuntimeOptions(
    string Mode,
    string CommunicationParameters,
    ushort Address,
    ushort TransferMode,
    ushort Model,
    ushort ChannelCount = 0);
