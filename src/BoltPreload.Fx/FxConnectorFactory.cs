using FxSDK.Models;
using FxSDK.Utility;
using Microsoft.Extensions.Logging;

namespace BoltPreload.Fx;

/// <summary>
/// 真机设备的连接参数（plain，对应 devices 表行或 Fx:Real:* 配置）。
/// 上层（Api 组合根）只构造本记录，<b>不直接 new 厂家 SDK 的 DeviceConfig</b>。
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
/// 单通道初始会话参数（plain DTO，不暴露厂家 SDK 的 <c>ChannelConfig</c> 给上层）。
/// <para>
/// 字段名与默认值【刻意对齐】<c>FxSDK.Models.ChannelConfig</c>（ProbeFreq=2.0 / EchoWidth=40 / Gain=10 /
/// MeasMode=2 / TcMode=1 ...），保证经 <see cref="FxConnectorFactory"/> 映射回 SDK 类型时行为与历史一致。
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
}

/// <summary>
/// 在 <c>BoltPreload.Fx</c> 内集中构造厂家 SDK 配置对象与连接器，把「FxSDK 组合」从 Api 收回到适配层，
/// 使上层不再 <c>using FxSDK.*</c>（商用底线：业务/组合层与厂家 SDK 解耦，便于换型号/换供应商）。
/// <para>初始通道会话由上层从 DB/配置加载为 plain 的 <see cref="FxChannelInit"/> 后传入，DB/配置访问不进适配层。</para>
/// </summary>
public static class FxConnectorFactory
{
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
            MCRConfig = new MCRConfig
            {
                EnabledChannels = channelConfigs.Select(c => c.Channel).Distinct().OrderBy(c => c).ToList()
            }
        };
        return new FxSdkConnector(spec.DeviceId, deviceConfig, logger, channelConfigs);
    }

    // 只映射加载器会设置的 12 个字段，其余（PulseConfig / MeasWin / TempProbe / IsDeltaToFMode ...）
    // 保留 SDK ChannelConfig 默认值——与历史 FxInitialChannelConfigLoader 的构造完全一致。
    private static ChannelConfig ToChannelConfig(FxChannelInit c) => new()
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
        RefTemp = c.RefTemp
    };
}
