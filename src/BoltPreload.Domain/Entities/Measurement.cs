namespace BoltPreload.Domain.Entities;

/// <summary>
/// 时序测量记录。TimescaleDB hypertable，按 ts 切片，PRIMARY KEY (bolt_id, ts)。
/// 注意：status_code &lt; 0 的不存这里，存 MeasurementAnomaly。
/// </summary>
public class Measurement
{
    public long BoltId { get; set; }

    public long ChannelId { get; set; }

    /// <summary>设备事件时间（设备打的时间戳）</summary>
    public DateTimeOffset Ts { get; set; }

    /// <summary>服务端落库时间</summary>
    public DateTimeOffset IngestedAt { get; set; }

    public decimal EchoTimeNs { get; set; }

    public decimal? DeltaTofNs { get; set; }

    public decimal? SpTempC { get; set; }

    public float Gain { get; set; }

    /// <summary>0 正常 / -1 信号过小 / -2 阈值异常 / -4 未找到过零点 / -5 未知 / -6 SNR 太差</summary>
    public short StatusCode { get; set; }

    public decimal? PreloadN { get; set; }

    /// <summary>当前预紧力 / 目标预紧力</summary>
    public decimal? PreloadRatio { get; set; }

    /// <summary>预紧力的扩展不确定度（k=2，95% 置信）</summary>
    public decimal? PreloadUncertaintyN { get; set; }

    /// <summary>0~100 信号健康度（§8.6.4）</summary>
    public short? HealthScore { get; set; }

    public decimal? SnrDb { get; set; }

    /// <summary>跟模板波形的余弦相似度</summary>
    public decimal? Similarity { get; set; }

    public decimal? MainFreqMhz { get; set; }

    /// <summary>时频域特征向量（§8.11）</summary>
    public string? FeatureVectorJson { get; set; }

    public string? WaveBlobUrl { get; set; }

    /// <summary>算法版本标签 "tof:v1.0,preload:v1.0,..."（§8.12 版本管理）</summary>
    public string? AlgoVersion { get; set; }

    /// <summary>用的标定档案 ID（追溯用）</summary>
    public Guid? CalibrationId { get; set; }
}
