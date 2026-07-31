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
