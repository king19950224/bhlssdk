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
/// 单个通道测量结果
/// </summary>
public class ChannelResult : DecodedResult
{
    /// <summary>
    /// 通道号
    /// </summary>
    public ushort Channel { get; set; } = 1;
    /// <summary>
    /// 状态码
    /// 0 正常
    /// -1 表示信号过小
    /// -2 阈值异常
    /// -3 信号溢出
    /// -4 相似度低
    /// -5 未知异常
    /// -6 信噪比低
    /// </summary>
    public short StatusCode { get; set; } = 0;
    /// <summary>
    /// 回波声时
    /// </summary>
    public decimal? EchoTime { get; set; }
    /// <summary>
    /// 回波声时2
    /// </summary>
    public decimal? EchoTime2 { get; set; }
    /// <summary>
    /// 温度
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
    public decimal[] ParamsArray { get; set; }
    /// <summary>
    /// 中间结果值
    /// </summary>
    public decimal[] MidResults { get; set; }
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