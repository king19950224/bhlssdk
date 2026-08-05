namespace BoltPreload.Calibration;

/// <summary>
/// 一个标定点：某一档已知载荷 <paramref name="ForceN"/>(N) 下、温补后的纵波声时差 <paramref name="DeltaTofNs"/>(ns)。
/// </summary>
/// <param name="ForceN">该档已知轴力 F（N），由加载装置/测力垫圈给出</param>
/// <param name="DeltaTofNs">温补后声时差 Δt（ns）= 归一化 TOF − 基线 t₀</param>
/// <param name="SigmaF">该档力的标准偏差（N），用于加权最小二乘；&lt;=0 视为等权</param>
public readonly record struct CalibrationPoint(double ForceN, double DeltaTofNs, double SigmaF = 0.0);

/// <summary>
/// 线性标定拟合结果。模型 <c>Δt = K_L · F + b</c>（本系统 K_L 约定 = Δt/F，单位 ns/N，见 资料/工程文档/sdk-api-facts.md §12）。
/// </summary>
/// <param name="KL">斜率 = 载荷因子 K_L（ns/N）。用法：F = (Δt − b) / K_L</param>
/// <param name="InterceptNs">截距 b（ns）</param>
/// <param name="RSquared">拟合优度 R²（plan §8.5：≥0.98 合格）</param>
/// <param name="KLStdError">斜率标准误 = 由拟合数据得到的 K_L 标准不确定度 u(K_L)（A 类，仅来自回归残差；
/// 完整 JJF 1059.1 合成不确定度还需叠加加载装置/温度/分辨率等 B 类分量，见 §8.5.2，属另一增量）</param>
/// <param name="PointCount">参与最终拟合的点数（已扣除 Grubbs 剔除点）</param>
/// <param name="RemovedIndices">被 Grubbs 剔除的原始点下标（仅 n≥20 时可能非空）</param>
/// <param name="Note">说明（拟合方式 / Grubbs 是否生效等）</param>
public sealed record LinearFitResult(
    double KL,
    double InterceptNs,
    double RSquared,
    double KLStdError,
    int PointCount,
    IReadOnlyList<int> RemovedIndices,
    string Note);

/// <summary>
/// 标定拟合器：把若干 (F, Δt) 标定点拟合成 <c>Δt = K_L·F + b</c>，给出 K_L / 截距 / R² / 标准不确定度。
/// </summary>
public interface ICalibrationFitter
{
    /// <summary>
    /// 线性最小二乘拟合。
    /// </summary>
    /// <param name="points">标定点（至少 2 个，建议 ≥4，见 plan §7 路径 C）</param>
    /// <param name="useWeights">是否加权最小二乘（权重 = 1/σ_F²，仅当点提供了正的 SigmaF 时生效）</param>
    /// <param name="applyGrubbs">是否做 Grubbs 异常点剔除；<b>仅在 n≥20 时实际生效</b>（GB/T 4883-2008，见 plan §8.5.1）</param>
    LinearFitResult Fit(IReadOnlyList<CalibrationPoint> points, bool useWeights = false, bool applyGrubbs = true);
}
