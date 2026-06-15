namespace BoltPreload.Domain.Entities;

/// <summary>
/// 异常测量记录（status_code &lt; 0 的入此表）。
/// 跟正常测量分开存避免污染时序统计；hypertable 按 ts 切片。
/// </summary>
public class MeasurementAnomaly
{
    public long BoltId { get; set; }

    public long ChannelId { get; set; }

    public DateTimeOffset Ts { get; set; }

    public short StatusCode { get; set; }

    /// <summary>"信号过小" / "阈值异常" / "未找到过零点" / "SNR 太差"</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>触发时的原始数据（用于事后追溯）</summary>
    public string? RawDataJson { get; set; }
}
