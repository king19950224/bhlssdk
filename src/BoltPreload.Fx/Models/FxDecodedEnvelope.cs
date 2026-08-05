namespace BoltPreload.Fx.Models;

/// <summary>
/// SDK 解码结果的内部封装，把 SDK 类型转成业务用的 record。
/// 一个 envelope 装一个解码结果（ChannelResult / MultiChannelResult / DeviceParameter 之一）。
/// 波形不单独成包：设备总是随测量结果带波形，放在 ChannelResult.EchoWave 里。
/// </summary>
/// <param name="DeviceId">业务层逻辑设备 ID</param>
/// <param name="ReceiveTime">设备打的事件时间</param>
/// <param name="IngestedAt">服务端接收时间</param>
/// <param name="ResultType">结果类型</param>
public sealed record FxDecodedEnvelope(
    long DeviceId,
    DateTimeOffset ReceiveTime,
    DateTimeOffset IngestedAt,
    FxResultType ResultType,
    FxChannelResultDto? ChannelResult = null,
    FxDeviceParameterDto? DeviceParameter = null,
    FxMultiChannelResultDto? MultiChannelResult = null);

/// <summary>信封承载的解码结果类型，决定 <see cref="FxDecodedEnvelope"/> 上哪个载荷字段非空。</summary>
public enum FxResultType
{
    ChannelResult,
    MultiChannelResult,
    DeviceParameter
}

/// <summary>
/// 单通道测量结果 DTO（解耦 SDK 类型）。
/// 字段类型严格对齐 C# SDK 1.4 <c>FxSDK.Models.ChannelResult</c>。
/// </summary>
/// <remarks>
/// 状态码（按 C# SDK 1.4 源码 ChannelResult 注释）：
/// <list type="bullet">
/// <item><c> 0</c> 正常</item>
/// <item><c>-1</c> 信号过小</item>
/// <item><c>-2</c> 阈值异常</item>
/// <item><c>-3</c> 信号溢出</item>
/// <item><c>-4</c> 相似度低</item>
/// <item><c>-5</c> 未知异常</item>
/// <item><c>-6</c> 信噪比低</item>
/// </list>
/// </remarks>
/// <param name="Address">设备地址</param>
/// <param name="Channel">通道号（1 起）</param>
/// <param name="StatusCode">状态码（见 remarks）</param>
/// <param name="EchoTimeNs">回波声时（ns）</param>
/// <param name="EchoTime2Ns">第二回波声时（双波法用）</param>
/// <param name="SpTempC">温度（°C），来自设备 Pt1000</param>
/// <param name="DeltaTofNs">声时差（ns），SDK 已做温度补偿</param>
/// <param name="Gain">增益</param>
/// <param name="EchoWave">波形数据（如有）</param>
/// <param name="LowAi">模拟低电平回读</param>
/// <param name="HighAi">模拟高电平回读</param>
/// <param name="Angle">角度计数</param>
/// <param name="MidResults">算法中间结果（诊断用）</param>
public sealed record FxChannelResultDto(
    ushort Address,
    ushort Channel,
    short StatusCode,
    decimal? EchoTimeNs,
    decimal? EchoTime2Ns,
    decimal? SpTempC,
    decimal? DeltaTofNs,
    float Gain,
    FxEchoWaveDto? EchoWave,
    ushort? LowAi,
    ushort? HighAi,
    int? Angle,
    decimal[]? MidResults);

/// <summary>
/// 回波波形 DTO（解耦 SDK 类型）。
/// 字段对齐 C# SDK 1.4 <c>FxSDK.Models.EchoWave</c>。
/// </summary>
/// <param name="Fc">探头中心频率（0 = 原始波形未处理）</param>
/// <param name="EchoStart">波形起始时间（ns）</param>
/// <param name="ClockInt">采样间隔（ns）。model=3 实际 125ns，model=2 实际 10ns；字段默认值 10ns 仅是占位</param>
/// <param name="WaveData">波形数据（16-bit 有符号整数）</param>
/// <param name="Threshold">测量阈值</param>
public sealed record FxEchoWaveDto(
    ushort Fc,
    uint EchoStart,
    ushort ClockInt,
    short[] WaveData,
    short? Threshold);

/// <summary>
/// 多通道结果 DTO。各通道时间戳独立（轮询采集，详见方案 §8.7.3）。
/// </summary>
/// <param name="Channels">通道号 → 各通道独立结果</param>
public sealed record FxMultiChannelResultDto(
    IReadOnlyDictionary<ushort, FxChannelResultDto> Channels);

/// <summary>
/// 设备参数读取响应 DTO。
/// </summary>
/// <param name="Address">设备地址</param>
/// <param name="Channel">参数所在通道（null 表示设备级参数）</param>
/// <param name="ParamName">参数名（对应 SDK <c>DeviceConfig.PROP_*</c> 或 <c>ChannelConfig.PROP_*</c> 常量）</param>
/// <param name="ParamValue">参数值（运行时类型，按 ParamName 解读）</param>
public sealed record FxDeviceParameterDto(
    ushort Address,
    ushort? Channel,
    string ParamName,
    object? ParamValue);
