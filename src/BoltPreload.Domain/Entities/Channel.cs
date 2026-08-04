namespace BoltPreload.Domain.Entities;

/// <summary>
/// 物理通道。一台设备多个通道。
/// 当前生效的标定参数（k_l 等）冗余在这里，避免热路径 JOIN calibration_records。
/// </summary>
public class Channel
{
    public long Id { get; set; }

    public long DeviceId { get; set; }

    public short ChannelNo { get; set; } = 1;

    public decimal ProbeFreqMhz { get; set; } = 2.0m;

    public float Gain { get; set; } = 10;

    /// <summary>0 = 自动取峰值 × 60%</summary>
    public short Threshold { get; set; }

    public decimal? EchoWinNs { get; set; }

    public short EchoWidthUs { get; set; } = 15;

    public short PulseVoltage { get; set; } = 50;
    public short PulseForward { get; set; } = 3;
    public short PulseReverse { get; set; }

    /// <summary>0 自动 / 1 位置 / 2 阈值（默认）</summary>
    public short MeasMode { get; set; } = 2;

    /// <summary>0 不补 / 1 材料 / 2 自定义</summary>
    public short TcMode { get; set; } = 1;

    /// <summary>自定义温度补偿系数（仅 TcMode=2 用）</summary>
    public decimal? TempCoefficient { get; set; }

    /// <summary>参考声时（ns），标定时确定</summary>
    public decimal? RefTimeNs { get; set; }

    /// <summary>参考温度（°C），标定时确定</summary>
    public decimal? RefTempC { get; set; }

    /// <summary>
    /// 当前生效的载荷因子 K_L，冗余自 calibration_records。
    /// 单位是 <b>ns/N</b>（量级 ~1e-4），全系统统一按 K_L = Δt/F 存，别照标定证书上 1/N 的 1e-11 填。
    /// </summary>
    public decimal? KL { get; set; }

    /// <summary>当前 K_L 的扩展不确定度（k=2）</summary>
    public decimal? KLUncertainty { get; set; }

    /// <summary>指向当前生效的标定档案 ID</summary>
    public Guid? ActiveCalibrationId { get; set; }

    /// <summary>模板波形文件 URL（信号质量评估的基线）</summary>
    public string? TemplateWaveUrl { get; set; }

    /// <summary>选用的 TOF 提取算法名（对应 ITofExtractor.Name）</summary>
    public string TofAlgorithm { get; set; } = "ThrSignal";

    public bool Enabled { get; set; } = true;

    /// <summary>是否应加载到 SDK Session / 参与采集诊断。</summary>
    public bool SamplingEnabled { get; set; } = true;

    /// <summary>
    /// 物理/链路状态。实际会被写入的只有五个值：建库缺省 unknown，
    /// 在位跟踪写 healthy/no_data（ChannelPresenceService），探头扫描按 verdict 写
    /// healthy/noisy/suspect/no_data（ProbeScanResultJudge.ToHardwareState）。
    /// 中文名在 DisplayLabels.HardwareStates。
    /// </summary>
    public string HardwareState { get; set; } = "unknown";

    /// <summary>业务绑定状态：unbound/bound/moved/disabled。</summary>
    public string BusinessState { get; set; } = "unbound";

    /// <summary>当前 DB 配置来源：database/device_readback/appsettings/manual/recalibrated。</summary>
    public string ConfigSource { get; set; } = "database";

    public DateTimeOffset? ConfigSyncedAt { get; set; }

    public DateTimeOffset DiscoveredAt { get; set; }
    public string State { get; set; } = "manual";
    public DateTimeOffset? FirstSeenAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
