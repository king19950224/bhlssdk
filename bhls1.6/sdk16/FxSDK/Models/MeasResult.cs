namespace FxSDK.Models;

/// <summary>
/// 多通道结果
/// </summary>
public class MultiChannelResult : DecodedResult
{
    public Dictionary<ushort, ChannelResult> Results { get; set; } = new();
    /// <summary>
    /// 获取某个通道结果
    /// </summary>
    public ChannelResult? GetChannelResult(ushort channel)
    {
        Results.TryGetValue(channel, out var result);
        return result;
    }
}
/// <summary>
/// 超声波形类型
/// </summary>
public enum EchoWaveKind
{
    /// <summary>
    /// 纵波
    /// </summary>
    Longitudinal = 0,
    /// <summary>
    /// 横波
    /// </summary>
    Transverse = 1,
    /// <summary>
    /// 纵波全波
    /// </summary>
    LongitudinalFull = 2
}
/// <summary>
/// 单个通道测量结果
/// </summary>
public class ChannelResult : DecodedResult
{
    /// <summary>
    /// 通道号
    /// </summary>
    public ushort Channel { get; set; } = 1;
    /// <summary>
    /// 测量状态码
    /// </summary>
    public MeasStatusCode StatusCode { get; set; } = MeasStatusCode.Normal;
    /// <summary>
    /// 回波声时
    /// </summary>
    public decimal? EchoTime { get; set; }
    /// <summary>
    /// 回波声时2
    /// </summary>
    public decimal? EchoTime2 { get; set; }
    /// <summary>
    /// 原始温度
    /// </summary>
    public decimal? RawTemp { get; set; }
    /// <summary>
    /// 保护和 Tau 平滑后的有效温度
    /// </summary>
    public decimal? SpTemp { get; set; }
    /// <summary>
    /// 声时差
    /// </summary>
    public decimal? DeltaTof { get; set; }
    /// <summary>
    /// 回波模型
    /// </summary>
    public EchoWave? EchoWave { get; set; }
    /// <summary>
    /// 回波波形类型
    /// </summary>
    public EchoWaveKind EchoWaveKind { get; set; } = EchoWaveKind.Longitudinal;
    /// <summary>
    /// 低电平值
    /// </summary>
    public ushort? LowAi { get; set; }
    /// <summary>
    /// 高电平值
    /// </summary>
    public ushort? HighAi { get; set; }
    /// <summary>
    /// 角度计数
    /// </summary>
    public int? Angle { get; set; }
    /// <summary>
    /// 解码参数数组
    /// </summary>
    public decimal[] ParamsArray { get; set; } = Array.Empty<decimal>();
    /// <summary>
    /// 回波辅助信息
    /// </summary>
    public EchoAssist? Assist { get; set; }
}
/// <summary>
/// 回波数据模型
/// </summary>
public sealed class EchoWave
{
    /// <summary>
    /// 回波开始时间(ns)
    /// </summary>
    public uint EchoStart { get; set; }
    /// <summary>
    /// 时钟间隔(ns)
    /// </summary>
    public ushort ClockInt { get; set; } = 10;//默认10ns
    /// <summary>
    /// 回波数据点数组
    /// </summary>
    public List<short>? WaveData { get; set; }
    /// <summary>
    /// 中心频率，为0则为原始波形未经处理
    /// </summary>
    public ushort Fc { get; set; } = 0;
    /// <summary>
    /// 测量阈值
    /// </summary>
    public short? Thr { get; set; }
}
