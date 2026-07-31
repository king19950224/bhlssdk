using System.Threading.Channels;
using BoltPreload.Fx.Models;

namespace BoltPreload.Fx;

/// <summary>
/// 设备连接抽象层（详见方案 §5.1 SDK 接入适配层）。
/// <para>
/// 把厂家 SDK 的 <c>event OnResultReceived</c> 转成内部 <see cref="Channel{T}"/>，下游消费方不直接依赖 SDK。
/// 真实实现见 <c>FxSdkConnector</c>（包装 FxSDK 1.4）；
/// 测试和开发用 <c>SimulatedFxConnector</c>（生成假数据，不依赖真机）。
/// </para>
/// </summary>
public interface IFxConnector : IAsyncDisposable
{
    /// <summary>当前连接状态</summary>
    FxConnectorState State { get; }

    /// <summary>设备唯一 ID（业务层指定的逻辑 ID，用于路由）</summary>
    long DeviceId { get; }

    /// <summary>解码后的结果流。下游 <c>await foreach</c> 消费即可。</summary>
    ChannelReader<FxDecodedEnvelope> Results { get; }

    /// <summary>结果通道因背压（消费跟不上）累计丢弃的测量数（H3，可观测，经 /api/runtime/devices 暴露）。</summary>
    long DroppedCount { get; }

    /// <summary>
    /// 背压溢出兜底（H3）：通道满时被挤掉的最旧测量交给此委托（通常 = 落 WAL，由 WalReplayService 重放补写）。
    /// 由消费方（IngestionService）在启动时按需设置；null 表示仅计数后直接丢弃。
    /// </summary>
    Func<FxDecodedEnvelope, ValueTask>? OverflowSink { get; set; }

    /// <summary>异步建立连接</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>异步断开连接（不影响 Results 通道已经入队的数据）</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 安全重连。实现应保证 Results 通道继续可读，不因为一次重连而永久关闭。
    /// </summary>
    Task ReconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>切换设备工作模式（待机 / 单通道 / 多通道 / 全波 / 读参数）</summary>
    Task SetWorkModeAsync(FxWorkMode mode, ushort channel = 0, ushort address = 1, CancellationToken cancellationToken = default);

    /// <summary>读设备级参数（异步返回，结果通过 Results 流回来）</summary>
    Task ReadDeviceParamAsync(string propertyName, ushort address = 1, CancellationToken cancellationToken = default);

    /// <summary>读通道级参数</summary>
    Task ReadChannelParamAsync(string propertyName, ushort channel = 1, ushort address = 1, CancellationToken cancellationToken = default);

    /// <summary>
    /// 配置多通道轮询启用通道。真实 SDK 对应 DeviceConfig.MCRConfig，
    /// 会写设备端轮询参数，不只是更新本地 SessionContext。
    /// </summary>
    Task ConfigureMultiChannelPollingAsync(
        IReadOnlyCollection<ushort> enabledChannels,
        ushort? singleCount = null,
        ushort? dailyCount = null,
        uint? startSeconds = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 写通道级参数（Phase 2 用于重标定 / 改阈值 / 改增益等）。
    /// <para>典型用法：把当前 TOF/Temp 写成新 RefTime/RefTemp，等价于在线重标定。</para>
    /// </summary>
    /// <param name="channel">通道号</param>
    /// <param name="updates">字段更新字典，键见 <see cref="ChannelConfigField"/>。null 值的字段不更新</param>
    /// <param name="address">设备地址</param>
    Task ConfigureChannelAsync(
        ushort channel,
        ChannelConfigUpdate updates,
        ushort address = 1,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 通道重标定：用当前实测 EchoTime / SpTemp 作为新基线（RefTime / RefTemp）整组写入设备。
    /// <para>
    /// 内部流程：
    /// 1. 取出本地缓存的最近一次 ChannelResult（EchoTime + SpTemp）；
    /// 2. 触发 ReadChannelParam(LEchoWave) 拿当前波形（SDK 上行）；
    /// 3. 等 LEchoWave 回来后，调 SDK ConfigureChannelAsync(propertyName="RefTime") 把
    ///    MeasMode + Threshold + RefTime + RefTemp + LEchoWave 一次性整组写到设备。
    /// </para>
    /// <para>注意：调用前螺栓必须处于"已知预紧状态"（拧紧到目标力矩或自由状态），否则基线无效。</para>
    /// </summary>
    /// <returns>实际写入的新基线值（RefTime/RefTemp），便于回写本地数据库</returns>
    /// <param name="explicitRefTimeNs">可选：显式指定 RefTime；不指定则取最近 ChannelResult.EchoTime</param>
    /// <param name="explicitRefTempC">可选：显式指定 RefTemp；不指定则取最近 ChannelResult.SpTemp</param>
    Task<RecalibrateResult> RecalibrateChannelAsync(
        ushort channel,
        ushort address = 1,
        decimal? explicitRefTimeNs = null,
        decimal? explicitRefTempC = null,
        CancellationToken cancellationToken = default);

    /// <summary>返回已注入 SDK SessionContext 的通道配置快照（仅诊断用）</summary>
    IReadOnlyList<FxSessionChannelConfigSnapshot> GetSessionChannelConfigs();

    /// <summary>
    /// 在线补配通道的 SDK 本地会话配置（sendCommand=false，只写 SessionContext，不写设备参数）。
    /// <para>
    /// 用途：通道目录扩容（如 40→48）后，SDK 只对 SessionContext 里有 ChannelConfig 且 ProbeFreq&gt;0
    /// 的通道计算 EchoTime——不补配的话新通道插上探头也识别不出回波，只能等进程重启重新装载。
    /// 语义为"只补缺"：同一 (address, channel) 已有会话配置时跳过，不覆盖真实读回/重标定后的会话状态。
    /// </para>
    /// 默认空实现（模拟器/测试桩没有 SDK 会话概念）。
    /// </summary>
    Task ProvisionSessionChannelsAsync(IReadOnlyCollection<FxChannelInit> channels, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// 当前设备端 MCR 轮询启用的通道集（实时会话状态）。ProbeScan 扫描前据此快照，扫描后恢复业务通道集——
    /// 比读 DB 的 sampling_enabled 标志更可靠（后者可能与设备实际 MCR 不同步）。
    /// 默认返回空（无 MCR 概念的实现，如模拟器 / 测试桩）。
    /// </summary>
    IReadOnlyList<ushort> GetSessionMcrChannels() => Array.Empty<ushort>();
}

/// <summary>重标定结果</summary>
/// <param name="Channel">通道号</param>
/// <param name="Address">设备地址</param>
/// <param name="NewRefTimeNs">写入设备的新 RefTime (ns)</param>
/// <param name="NewRefTempC">写入设备的新 RefTemp (°C)</param>
/// <param name="EchoWaveBytes">本次写入的 LEchoWave 数据点数（用于日志/审计）</param>
/// <param name="Timestamp">操作时间</param>
public sealed record RecalibrateResult(
    ushort Channel,
    ushort Address,
    decimal NewRefTimeNs,
    decimal NewRefTempC,
    int EchoWaveBytes,
    DateTimeOffset Timestamp);

/// <summary>SDK SessionContext 中已注入的通道配置快照（诊断用）</summary>
public sealed record FxSessionChannelConfigSnapshot(
    ushort Address,
    ushort Channel,
    decimal ProbeFreqMhz,
    decimal Gain,
    ushort MeasMode,
    short Threshold,
    decimal? RefTimeNs,
    decimal? RefTempC,
    ushort TcMode,
    decimal? TempCoefficient);

/// <summary>
/// 通道配置增量更新（null 表示不改）。字段类型严格对齐 SDK ChannelConfig。
/// </summary>
/// <param name="ProbeFreqMhz">探头中心频率（MHz）</param>
/// <param name="EchoWinNs">回波窗口起始时间（ns）</param>
/// <param name="EchoWidthUs">回波窗口宽度（us）</param>
/// <param name="Gain">增益（model=3 范围 1~47）</param>
/// <param name="Threshold">测量阈值（0 = 自动取峰值 × 60%）</param>
/// <param name="MeasMode">测量模式 0 自动 / 1 位置 / 2 阈值</param>
/// <param name="TcMode">温度补偿模式 0 不补 / 1 材料 / 2 自定义</param>
/// <param name="TempCoefficient">自定义温度系数（仅 TcMode=2 用）</param>
/// <param name="RefTimeNs">参考声时 RefTime（ns）— 重标定的核心字段</param>
/// <param name="RefTempC">参考温度 RefTemp（°C）— 重标定的核心字段</param>
public sealed record ChannelConfigUpdate(
    decimal? ProbeFreqMhz = null,
    decimal? EchoWinNs = null,
    ushort? EchoWidthUs = null,
    decimal? Gain = null,
    short? Threshold = null,
    ushort? MeasMode = null,
    ushort? TcMode = null,
    decimal? TempCoefficient = null,
    decimal? RefTimeNs = null,
    decimal? RefTempC = null);

public enum FxConnectorState
{
    Idle,
    Connecting,
    Connected,
    Reconnecting,
    Disconnected,
    Faulted
}

/// <summary>
/// 设备工作模式。严格对齐 FxSDK 1.4 <c>WorkMode</c> 枚举顺序，便于强转。
/// </summary>
public enum FxWorkMode
{
    Standby = 0,
    SingleMeas = 1,
    MultiMeas = 2,
    DualMeas = 3,
    ReadParam = 4,
    FullWaveRead = 5,
    Reboot = 6,
    LWaveRead = 7,
    TWaveRead = 8
}
