namespace BoltPreload.Domain.Entities;

/// <summary>
/// 业务实体螺栓。1:1 绑到 Channel。
/// 标定参数 K_L 不在这里，在 Channel 上（按当前生效值）；标定档案在 CalibrationRecord。
/// </summary>
public class Bolt
{
    public long Id { get; set; }

    public long GroupId { get; set; }

    public long ChannelId { get; set; }

    public string Code { get; set; } = string.Empty;

    /// <summary>"M16" / "M20" 等</summary>
    public string NominalDiam { get; set; } = string.Empty;

    /// <summary>性能等级 "8.8" / "10.9" / "12.9"</summary>
    public string Grade { get; set; } = string.Empty;

    /// <summary>屈服强度 Rp0.2 (MPa)</summary>
    public decimal Rp02Mpa { get; set; }

    /// <summary>材料牌号 "42CrMo" / "45" / "Q235" / ...</summary>
    public string Material { get; set; } = string.Empty;

    public decimal ElasticModulusGpa { get; set; }

    public decimal EffectiveLengthMm { get; set; }

    public decimal? GripLengthMm { get; set; }

    public decimal TotalLengthMm { get; set; }

    public decimal CrossSectionMm2 { get; set; }

    /// <summary>纵波声速 m/s（碳钢默认 5890）</summary>
    public decimal SoundVelocityMs { get; set; } = 5890m;

    /// <summary>材料温度系数（1/°C），缺省时算法退化到 SDK 默认值。</summary>
    public decimal? TempCoefficientPerC { get; set; }

    public decimal? InstallTorqueNm { get; set; }

    /// <summary>目标预紧力 F₀ (N)</summary>
    public decimal? TargetPreloadN { get; set; }

    /// <summary>报警下限 (N)</summary>
    public decimal? MinPreloadN { get; set; }

    /// <summary>"12 点钟方向" / "塔筒外侧" 等</summary>
    public string? PositionLabel { get; set; }

    public DateOnly? InstallDate { get; set; }

    /// <summary>是否符合 GB/T 43232 适用范围（直径 ≥ M5、长径比 ≥ 3:1、等级 ≥ 8.8 级）</summary>
    public bool GbCompliant { get; set; } = true;

    /// <summary>uncalibrated / calibrated / expired / disabled</summary>
    public string State { get; set; } = "uncalibrated";

    /// <summary>
    /// 人工确认时间（两级档案模型，见 bolt-lifecycle-design.md）。
    /// NULL = 占位档：自动建档且无人工痕迹，探头离位满 N 天自动回收、迁移时可被吸收；
    /// 非 NULL = 资产档：永久留存，拔探头走失联告警。
    /// 盖章点：标定激活 / 通道验收 / 人工改规格 / 人工绑定迁移 / 人工建档。
    /// </summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
