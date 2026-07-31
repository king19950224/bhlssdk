namespace BoltPreload.Algorithms.Preload;

/// <summary>
/// 预紧力计算的输入。
/// </summary>
/// <param name="EchoTimeNs">当前回波声时（ns）</param>
/// <param name="RefTimeNs">参考声时 t₀（ns），简化模型 A 是"未拧紧时" TOF，单波法是"标定基线" TOF</param>
/// <param name="TempC">当前温度（°C）</param>
/// <param name="RefTempC">参考温度（°C）</param>
/// <param name="TempCoefficient">温度补偿系数（1/°C），1.0269e-4 是 SDK 默认值</param>
/// <param name="Bolt">螺栓物理规格</param>
/// <param name="Calibration">标定数据（含 K_L 和不确定度）。null 时降级到简化模型 A</param>
public sealed record PreloadInput(
    decimal EchoTimeNs,
    decimal RefTimeNs,
    decimal TempC,
    decimal RefTempC,
    decimal TempCoefficient,
    BoltSpec Bolt,
    CalibrationData? Calibration);

/// <summary>
/// 螺栓物理规格（投影自 bolts 表的算法必需字段）。
/// </summary>
/// <param name="Grade">性能等级 "8.8" / "10.9" / "12.9"</param>
/// <param name="Rp02Mpa">屈服强度 Rp0.2（MPa）</param>
/// <param name="ElasticModulusGpa">弹性模量 E（GPa）</param>
/// <param name="NominalDiamMm">公称直径（mm 数值，如 16.0；bolts.nominal_diam 是 "M16" 字符串要剥前缀）</param>
/// <param name="EffectiveLengthMm">有效受力长度 L_e（mm）</param>
/// <param name="CrossSectionMm2">有效截面积 A_e（mm²）</param>
/// <param name="SoundVelocityMs">标定声速 v（m/s，碳钢默认 5890）</param>
public sealed record BoltSpec(
    string Grade,
    decimal Rp02Mpa,
    decimal ElasticModulusGpa,
    decimal NominalDiamMm,
    decimal EffectiveLengthMm,
    decimal CrossSectionMm2,
    decimal SoundVelocityMs);

/// <summary>
/// 标定数据快照（从 channels.k_l / channels.active_calibration_id 读，可追溯）。
/// </summary>
/// <param name="CalibrationId">对应 calibration_records.id</param>
/// <param name="KL">载荷因子。本实现约定 K_L = Δt/F，单位 <b>ns/N</b>（量级 ~1e-4），用法 F = Δt/K_L。
/// 注意与 plan/标定证书的 1/N 约定（~1e-11，F=Δt·K_L/t₀）相差因子 t₀，详见 GbSingleWaveCalculator 类注释。</param>
/// <param name="KLUncertainty">扩展不确定度 U (k=2)</param>
/// <param name="CalibratedAt">标定时间</param>
/// <param name="ExpiresAt">标定有效期</param>
/// <param name="Method">标定方法 "path_c_torque" / "lab_tensile" / "load_washer"</param>
public sealed record CalibrationData(
    Guid CalibrationId,
    decimal KL,
    decimal KLUncertainty,
    DateTimeOffset CalibratedAt,
    DateTimeOffset ExpiresAt,
    string Method);

/// <summary>
/// 预紧力计算结果。
/// </summary>
/// <param name="PreloadN">预紧力 F（N）。null 表示无法计算</param>
/// <param name="UncertaintyN">扩展不确定度 U (k=2, 95% 置信)</param>
/// <param name="DeltaTofNs">温补后的声时差（ns）</param>
/// <param name="WithinGbRange">是否在 GB/T 43232 适用范围 (30%~90% Rp0.2)</param>
/// <param name="ModelUsed">实际用的模型名</param>
/// <param name="CalibrationId">用的标定档案 ID（追溯用）</param>
public sealed record PreloadResult(
    decimal? PreloadN,
    decimal? UncertaintyN,
    decimal DeltaTofNs,
    bool WithinGbRange,
    string ModelUsed,
    Guid? CalibrationId);

/// <summary>标定有效期判断扩展（过期 / 临近过期预警）。</summary>
public static class CalibrationDataExtensions
{
    public static bool IsExpired(this CalibrationData c, DateTimeOffset now) => now > c.ExpiresAt;

    public static bool IsNearExpiry(this CalibrationData c, DateTimeOffset now, TimeSpan warning)
        => c.ExpiresAt - now <= warning;
}
