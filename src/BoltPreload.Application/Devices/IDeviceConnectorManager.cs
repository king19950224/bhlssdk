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
}

public sealed record DeviceRuntimeContext(
    long DeviceId,
    string Name,
    bool IsPrimary,
    IFxConnector Connector,
    IIngestionRuntimeStatus RuntimeStatus,
    DeviceRuntimeOptions Options);

public sealed record DeviceRuntimeOptions(
    string Mode,
    string CommunicationParameters,
    ushort Address,
    ushort TransferMode,
    ushort Model);
