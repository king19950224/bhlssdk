using FxSDK.Models;
using FxSDK.Utility;
using Microsoft.Extensions.Logging;

namespace BoltPreload.Fx;

/// <summary>
/// 设备的连接参数（plain，对应 devices 表行或 Fx:Real:* 配置）。
/// 上层（Api 组合根）只构造本记录，<b>不直接 new SDK 的 DeviceConfig</b>。
/// </summary>
public sealed record FxRealDeviceSpec(
    long DeviceId,
    ushort Model,
    byte CommType,
    string CommParams,
    ushort Address,
    ushort TransferMode,
    ushort ChannelCount);

/// <summary>
/// 单通道初始会话参数（plain DTO， <c>ChannelConfig</c> ）。
/// <para>
/// 字段名与默认值对齐 <c>FxSDK.Models.ChannelConfig</c>（ProbeFreq=2.0 / Gain=10 / MeasMode=2 / TcMode=1 ...），
/// 保证经 <see cref="FxConnectorFactory"/> 映射回 SDK 类型时行为与历史一致。
/// 例外是 EchoWidth：SDK 1.6 把默认值改成了 15，这里保持 40 是跟设备/历史行为对齐的业务选择，不随 SDK 走。
/// </para>
/// </summary>
public sealed class FxChannelInit
{
    public ushort Address { get; set; } = 1;
    public ushort Channel { get; set; } = 1;
    public decimal ProbeFreq { get; set; } = 2.0m;
    public decimal Gain { get; set; } = 10m;
    public short Threshold { get; set; }
    public decimal EchoWin { get; set; }
    public ushort EchoWidth { get; set; } = 40;
    public ushort MeasMode { get; set; } = 2;
    public ushort TcMode { get; set; } = 1;
    public decimal? TempCoefficient { get; set; }
    public decimal? RefTime { get; set; }
    public decimal? RefTemp { get; set; }
    // SDK 1.6 自动法(MeasMode=0)置零锚点 + 参考波形（channels 表 0031 迁移新列）。
    // 阈值法通道恒为 null；缺锚点时自动法每次重启重新捕获，有跨载波周期的漂移风险
    public decimal? RefEnvTime { get; set; }
    public decimal? RefPeriodNs { get; set; }
    public byte[]? RefEchoWave { get; set; }
}

/// <summary>
/// 在 <c>BoltPreload.Fx</c> 内集中构造厂家 SDK 配置对象与连接器，把“FxSDK 组合”从 Api 收回到适配层，
/// 使上层不再 <c>using FxSDK.*</c>
/// <para>初始通道会话由上层从 DB/配置加载为 plain 的 <see cref="FxChannelInit"/> 后传入，DB/配置访问不进适配层。</para>
/// </summary>
public static class FxConnectorFactory
{
    /// <summary>
    /// 当前编入的厂家 SDK 版本，随 external/FxSDK 升级手工同步（csproj 的 PackageVersion 不进程序集元数据，读不到）。
    /// 写进 channels.sdk_version / calibration_records.sdk_version 做标定系数与算法版本的绑定追溯。
    /// </summary>
    public const string SdkVersion = "1.6.0";

    static FxConnectorFactory()
    {
        // SDK 1.6 默认给 SpTemp 上 τ=180s 指数平滑（TempTauProfile.Balanced），原始值挪到了新字段 RawTemp。
        // 我们的 sp_temp_c 历史数据、重标定的 RefTemp 基准全是按"接近原始温度"的口径建的，
        // 这里全局切 Direct 关掉平滑，只留 SDK 的范围保护(-40~120°C)，保语义连续。
        // 要吃厂家平滑得先解决 sp_temp_c 新旧语义区分，见 sdk-1.6-upgrade-plan.md §7 决策点 2/4。
        // 放静态构造函数：所有真机链路都经本工厂（CreateReal / DeveloperMode），首次触碰即生效。
        FxUtility.TempTauProfile = TempTauProfile.Direct;
    }

    /// <summary>
    /// 开发期 SDK 协议帧日志开关。封装 <c>FxSDK.Utility.FxUtility.DeveloperMode</c>，
    /// 让 Api 不必直接依赖 <c>FxSDK.Utility</c>。
    /// </summary>
    public static bool DeveloperMode
    {
        get => FxUtility.DeveloperMode;
        set => FxUtility.DeveloperMode = value;
    }

    /// <summary>用真机参数 + plain 初始通道会话构造真机连接器。所有 FxSDK 类型构造都封在这里。</summary>
    public static IFxConnector CreateReal(
        FxRealDeviceSpec spec,
        IEnumerable<FxChannelInit> initialChannels,
        ILogger<FxSdkConnector> logger)
    {
        var channelConfigs = initialChannels.Select(ToChannelConfig).ToList();
        var deviceConfig = new DeviceConfig
        {
            Model = spec.Model,
            CommunicationType = spec.CommType,
            CommunicationParameters = spec.CommParams,
            Address = spec.Address,
            TransferMode = spec.TransferMode,
            UsChannelCount = (ushort)Math.Clamp((int)spec.ChannelCount, 1, 48),
            // 【不要】在这里预填 MCRConfig.EnabledChannels：GetSessionMcrChannels 的语义是
            // "本会话实际写到设备的轮询集"，预填会让 ChannelPresenceService 误以为设备已是
            // 全通道轮询而跳过下发——设备断电重置/厂家工具改动后系统就静坐干等，一条数据不来。
            // 保持空快照，连上后由 ChannelPresenceService 在首轮 sweep 内下发收敛（设计即如此）。
            TcpSessionOptions = new TcpSessionOptions
            {
                // 现场串口服务器约 5s 无流量就掐 TCP，SDK 默认 20s 心跳保不住连接。
                // 1.4 时代是直接改 SDK 源码写死 2s（sdk-changelog 7daeea7，有真机故障背书），
                // 1.6 做成了可配项，从这里设回去——升级时最容易丢的就是这一条。
                HeartbeatSeconds = 2,
                // 1.6 新增的"接收活动超时主动断线"先关掉：无数据检测已由 Ingestion 看门狗负责
                // （NoDataTimeoutSeconds），SDK 内部再来一套会抢在看门狗前制造断线事件，日志归因会乱。
                // 观察期过后如果想换成 SDK 内建，先把看门狗那套停了再开这个。
                EnableReceiveActivityTimeout = false
            }
        };
        return new FxSdkConnector(spec.DeviceId, deviceConfig, logger, channelConfigs);
    }

    // 只映射加载器会设置的字段，其余（MeasWin / TempProbe / IsDeltaToFMode ...）
    // 保留 SDK ChannelConfig 默认值——与历史 FxInitialChannelConfigLoader 的构造完全一致。
    // internal：FxSdkConnector 在线补配会话通道（ProvisionSessionChannelsAsync）复用同一映射，保证行为一致。
    internal static ChannelConfig ToChannelConfig(FxChannelInit c) => new()
    {
        Address = c.Address,
        Channel = c.Channel,
        ProbeFreq = c.ProbeFreq,
        Gain = c.Gain,
        Threshold = c.Threshold,
        EchoWin = c.EchoWin,
        EchoWidth = c.EchoWidth,
        MeasMode = c.MeasMode,
        TcMode = c.TcMode,
        TempCoefficient = c.TempCoefficient,
        RefTime = c.RefTime,
        RefTemp = c.RefTemp,
        // 1.6 自动法锚点回灌：连同参考波形一起进 SessionContext，SDK 才能按存盘锚点锁定同相上升沿。
        // TryUnpack 对坏数据返回 null——宁可走首帧捕获也不拿半截波形当基线
        RefEnvTime = c.RefEnvTime,
        RefPeriodNs = c.RefPeriodNs,
        LEchoWave = FxRefWaveCodec.TryUnpack(c.RefEchoWave)
    };
}
