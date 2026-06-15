using MathNet.Numerics.Distributions;

namespace BoltPreload.Calibration;

/// <summary>不确定度分量类型（JJF 1059.1-2012）。A 类=统计方法（重复观测）；B 类=非统计方法（仪器/资料）。</summary>
public enum UncertaintyType
{
    A,
    B,
}

/// <summary>
/// 一个不确定度分量（JJF 1059.1）。已折算为标准不确定度 u_i，并带灵敏系数 c_i 与自由度 ν_i。
/// </summary>
/// <param name="Name">分量名（如 u(Δt) / u(F) / u(T)）</param>
/// <param name="Type">A 类 / B 类</param>
/// <param name="StandardUncertainty">标准不确定度 u_i（已折算，A 类=s/√n，B 类=a/除数）</param>
/// <param name="Sensitivity">灵敏系数 c_i = ∂y/∂x_i；缺省 1（即 u_i 已是对结果的贡献）</param>
/// <param name="DegreesOfFreedom">自由度 ν_i；A 类一般 n-1，B 类按可信度，缺省 +∞</param>
public sealed record UncertaintyComponent(
    string Name,
    UncertaintyType Type,
    double StandardUncertainty,
    double Sensitivity = 1.0,
    double DegreesOfFreedom = double.PositiveInfinity)
{
    /// <summary>该分量对被测量的贡献 c_i·u_i。</summary>
    public double Contribution => Sensitivity * StandardUncertainty;

    /// <summary>
    /// A 类：由 ≥2 次重复观测构造。u = s/√n（平均值的实验标准偏差），ν = n-1。
    /// </summary>
    public static UncertaintyComponent TypeA(string name, IReadOnlyList<double> samples, double sensitivity = 1.0)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var n = samples.Count;
        if (n < 2)
            throw new ArgumentException("A 类不确定度至少需要 2 次重复观测。", nameof(samples));

        double mean = 0;
        foreach (var v in samples) mean += v;
        mean /= n;

        double ss = 0;
        foreach (var v in samples) ss += (v - mean) * (v - mean);
        var s = Math.Sqrt(ss / (n - 1));         // 单次实验标准偏差
        var uMean = s / Math.Sqrt(n);            // 平均值的标准不确定度
        return new UncertaintyComponent(name, UncertaintyType.A, uMean, sensitivity, n - 1);
    }

    /// <summary>
    /// B 类（均匀分布）：u = a/√3，a 为误差限半宽（如仪器手册 ±a）。自由度默认 +∞。
    /// </summary>
    public static UncertaintyComponent TypeBUniform(string name, double halfWidth, double sensitivity = 1.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(halfWidth);
        return new UncertaintyComponent(name, UncertaintyType.B, halfWidth / Math.Sqrt(3.0), sensitivity);
    }

    /// <summary>B 类（直接给定标准不确定度，如已知 u）。</summary>
    public static UncertaintyComponent TypeB(string name, double standardUncertainty, double sensitivity = 1.0,
        double degreesOfFreedom = double.PositiveInfinity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(standardUncertainty);
        return new UncertaintyComponent(name, UncertaintyType.B, standardUncertainty, sensitivity, degreesOfFreedom);
    }
}

/// <summary>不确定度评定结果（JJF 1059.1 报告项）。</summary>
/// <param name="CombinedStandardUncertainty">合成标准不确定度 u_c</param>
/// <param name="EffectiveDegreesOfFreedom">有效自由度 ν_eff（Welch-Satterthwaite）</param>
/// <param name="CoverageFactor">包含因子 k</param>
/// <param name="ExpandedUncertainty">扩展不确定度 U = k·u_c</param>
/// <param name="ConfidenceLevel">置信水平（如 0.95）</param>
/// <param name="Components">参与合成的分量</param>
public sealed record UncertaintyBudget(
    double CombinedStandardUncertainty,
    double EffectiveDegreesOfFreedom,
    double CoverageFactor,
    double ExpandedUncertainty,
    double ConfidenceLevel,
    IReadOnlyList<UncertaintyComponent> Components);

/// <summary>
/// JJF 1059.1-2012 不确定度评定引擎（合成 + 扩展，含 Welch-Satterthwaite 有效自由度）。
/// <para>
/// 本引擎只做"机器"——把各分量按平方和合成、按有效自由度定包含因子。
/// 具体的灵敏系数 c_i 与各输入量不确定度 u_i 属计量输入，必须由负责标定的工程师按设备/装置参数提供并复核，
/// 不在此处臆造（见 plan §8.5.2）。引擎本身每步都有单元测试覆盖。
/// </para>
/// </summary>
public static class UncertaintyEvaluator
{
    /// <summary>大于此有效自由度即视为"无限大"，包含因子取 95% 常用 k=2（JJF 报告惯例）。</summary>
    private const double LargeDof = 1.0e6;

    public static UncertaintyBudget Evaluate(IReadOnlyList<UncertaintyComponent> components, double confidenceLevel = 0.95)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Count == 0)
            throw new ArgumentException("至少需要 1 个不确定度分量。", nameof(components));
        if (confidenceLevel is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(confidenceLevel), "置信水平须在 (0,1)。");

        // 合成标准不确定度 u_c = √(Σ (c_i·u_i)²)（假设各分量不相关）
        double sumSq = 0;
        foreach (var c in components)
            sumSq += c.Contribution * c.Contribution;
        var uc = Math.Sqrt(sumSq);

        // Welch-Satterthwaite 有效自由度：ν_eff = u_c⁴ / Σ( (c_i·u_i)⁴ / ν_i )；ν_i=∞ 的项贡献 0
        double denom = 0;
        foreach (var c in components)
        {
            if (double.IsInfinity(c.DegreesOfFreedom) || c.DegreesOfFreedom <= 0) continue;
            var ci = c.Contribution;
            denom += ci * ci * ci * ci / c.DegreesOfFreedom;
        }
        var nuEff = denom <= 0 ? double.PositiveInfinity : (uc * uc * uc * uc) / denom;

        // 包含因子 k：ν_eff 充分大用 95% 常用 k=2；否则用 t 分布临界值（GUM/JJF）
        double k;
        if (double.IsInfinity(nuEff) || nuEff >= LargeDof)
        {
            k = 2.0;
        }
        else
        {
            var p = (1.0 + confidenceLevel) / 2.0;   // 双侧 → 单侧分位
            k = StudentT.InvCDF(0.0, 1.0, nuEff, p);
        }

        return new UncertaintyBudget(
            CombinedStandardUncertainty: uc,
            EffectiveDegreesOfFreedom: nuEff,
            CoverageFactor: k,
            ExpandedUncertainty: k * uc,
            ConfidenceLevel: confidenceLevel,
            Components: components.ToArray());
    }
}
