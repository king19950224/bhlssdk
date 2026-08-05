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

    /// <summary>
    /// 热加设备：从 devices 表读该行建运行时（只建不连——连接与下发工作模式由 IngestionService
    /// 对账拉起，职责分工与启动路径相同）。已在运行时中返回 true（幂等）；
    /// 行不存在/未启用/已软删返回 false。Simulated 模式抛 <see cref="NotSupportedException"/>。
    /// </summary>
    Task<bool> AddAsync(long deviceId, CancellationToken ct = default) =>
        throw new NotSupportedException("该实现不支持热增删设备");

    /// <summary>
    /// 热摘设备：摘出字典（数据路由立即断，IngestionService 对账下一拍取消消费任务并释放连接器），
    /// 并等任务真停。primary 不许摘（先 <see cref="TransferPrimary"/>）；返回 false = 设备不在运行时中。
    /// 任务未在 stopTimeout 内退出也返回 true——supervisor 会强制释放连接器，不值得让调用方一直吊着。
    /// </summary>
    Task<bool> RemoveAndStopAsync(long deviceId, TimeSpan stopTimeout, CancellationToken ct = default) =>
        throw new NotSupportedException("该实现不支持热增删设备");

    /// <summary>热重启 = 摘除等停 + 重建（改连接参数后用）。primary 重启后自动恢复 primary 标记。</summary>
    Task<bool> RestartAsync(long deviceId, TimeSpan stopTimeout, CancellationToken ct = default) =>
        throw new NotSupportedException("该实现不支持热增删设备");

    /// <summary>
    /// 转移主设备（不带 deviceId 的旧端点的默认路由）。运行时概念不落库：
    /// 进程重启后按 Fx:Real:DeviceId 配置或 id 最小行重选。返回 false = 目标不在运行时中。
    /// </summary>
    bool TransferPrimary(long deviceId) =>
        throw new NotSupportedException("该实现不支持热增删设备");
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
