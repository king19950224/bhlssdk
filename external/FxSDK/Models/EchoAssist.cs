using System.Text.Json.Serialization;

namespace FxSDK.Models;

/// <summary>
/// 回波辅助信息，用于表达算法选点和主回波区域。
/// 缩写：M=Mark，Z=Zero，S=Start，E=End，F=Front；x 为波形索引，y 为幅值。
/// </summary>
public sealed class EchoAssist
{
    /// <summary>
    /// Mark X，算法选中的极值辅助点索引，可能是正峰也可能是负谷。
    /// </summary>
    [JsonPropertyName("mx")]
    public decimal? Mx { get; set; }

    /// <summary>
    /// Mark Y，算法选中的极值辅助点幅值。
    /// </summary>
    [JsonPropertyName("my")]
    public decimal? My { get; set; }

    /// <summary>
    /// Zero X，过零点索引。
    /// </summary>
    [JsonPropertyName("zx")]
    public decimal? Zx { get; set; }

    /// <summary>
    /// Start X，主回波/信号区域起点索引。
    /// </summary>
    [JsonPropertyName("sx")]
    public decimal? Sx { get; set; }

    /// <summary>
    /// End X，主回波/信号区域终点索引。
    /// </summary>
    [JsonPropertyName("ex")]
    public decimal? Ex { get; set; }

    /// <summary>
    /// Front X，自动法稳定前沿锚点索引。
    /// </summary>
    [JsonPropertyName("fx")]
    public decimal? Fx { get; set; }

    /// <summary>
    /// Front Y，自动法稳定前沿锚点幅值。
    /// </summary>
    [JsonPropertyName("fy")]
    public decimal? Fy { get; set; }

    /// <summary>
    /// 自动法本帧最终采用的载波周期(ns)。
    /// 扫波时用于形成稳定周期锚点，置零后可持久化供测量和监测复用。
    /// </summary>
    [JsonPropertyName("periodNs")]
    public decimal? PeriodNs { get; set; }
}
