using MathNet.Numerics.Distributions;

namespace BoltPreload.Calibration;

/// <summary>
/// 线性最小二乘标定拟合器（plan §8.5）。
/// <para>
/// 模型 <c>Δt = K_L·F + b</c>，K_L 为斜率（ns/N，见 资料/工程文档/sdk-api-facts.md §12）。
/// 支持普通/加权最小二乘 + R² + 斜率标准误；Grubbs 异常点剔除<b>仅在 n≥20 时生效</b>
/// （GB/T 4883-2008，小样本套 Grubbs 属误用，见 plan §8.5.1）。
/// </para>
/// </summary>
public sealed class LeastSquaresFitter : ICalibrationFitter
{
    /// <summary>Grubbs 检验最小样本数（GB/T 4883-2008）。低于此值不做统计剔除，靠 R² + 人工复核。</summary>
    public const int GrubbsMinSamples = 20;

    /// <summary>Grubbs 检验显著性水平（双侧）。</summary>
    private const double GrubbsAlpha = 0.05;

    public LinearFitResult Fit(IReadOnlyList<CalibrationPoint> points, bool useWeights = false, bool applyGrubbs = true)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
            throw new ArgumentException("标定至少需要 2 个点才能拟合直线（建议 ≥4，见 plan §7 路径 C）。", nameof(points));

        var kept = new List<int>(points.Count);
        for (var i = 0; i < points.Count; i++) kept.Add(i);

        var removed = new List<int>();
        string grubbsNote;

        // Grubbs 仅在 n≥20 时生效，且最多剔 1 个点（plan §8.5.1 / GB/T 4883-2008）。
        if (applyGrubbs && points.Count >= GrubbsMinSamples)
        {
            var outlier = FindGrubbsOutlier(points, kept);
            if (outlier >= 0)
            {
                removed.Add(outlier);
                kept.Remove(outlier);
                grubbsNote = $"Grubbs(n={points.Count},α={GrubbsAlpha}) 剔除原始点 #{outlier}";
            }
            else
            {
                grubbsNote = $"Grubbs(n={points.Count}) 未发现异常点";
            }
        }
        else
        {
            grubbsNote = applyGrubbs
                ? $"n={points.Count}<{GrubbsMinSamples}，不做 Grubbs（靠 R²+人工复核，plan §8.5.1）"
                : "Grubbs 关闭";
        }

        var (kl, intercept, r2, klSe) = FitCore(points, kept, useWeights);

        var weightNote = useWeights ? "加权最小二乘" : "普通最小二乘";
        return new LinearFitResult(
            KL: kl,
            InterceptNs: intercept,
            RSquared: r2,
            KLStdError: klSe,
            PointCount: kept.Count,
            RemovedIndices: removed,
            Note: $"{weightNote}; {grubbsNote}");
    }

    /// <summary>
    /// 对 <paramref name="indices"/> 指定的点做（加权）最小二乘，返回 (斜率 K_L, 截距 b, R², 斜率标准误)。
    /// </summary>
    private static (double Kl, double Intercept, double R2, double KlStdError) FitCore(
        IReadOnlyList<CalibrationPoint> points, IReadOnlyList<int> indices, bool useWeights)
    {
        var n = indices.Count;
        if (n < 2)
            throw new ArgumentException("有效点不足 2 个，无法拟合。");

        double sumW = 0, sumWx = 0, sumWy = 0;
        foreach (var i in indices)
        {
            var p = points[i];
            var w = Weight(p, useWeights);
            sumW += w;
            sumWx += w * p.ForceN;
            sumWy += w * p.DeltaTofNs;
        }
        var xBar = sumWx / sumW;
        var yBar = sumWy / sumW;

        double sxx = 0, sxy = 0;
        foreach (var i in indices)
        {
            var p = points[i];
            var w = Weight(p, useWeights);
            var dx = p.ForceN - xBar;
            sxx += w * dx * dx;
            sxy += w * dx * (p.DeltaTofNs - yBar);
        }
        if (sxx <= 0)
            throw new ArgumentException("所有标定点的载荷 F 相同（或无方差），无法拟合斜率。请采集不同载荷档位。");

        var slope = sxy / sxx;
        var intercept = yBar - slope * xBar;

        double ssRes = 0, ssTot = 0;
        foreach (var i in indices)
        {
            var p = points[i];
            var w = Weight(p, useWeights);
            var fitted = slope * p.ForceN + intercept;
            var resid = p.DeltaTofNs - fitted;
            ssRes += w * resid * resid;
            var dy = p.DeltaTofNs - yBar;
            ssTot += w * dy * dy;
        }

        var r2 = ssTot > 0 ? 1.0 - ssRes / ssTot : (ssRes <= double.Epsilon ? 1.0 : 0.0);
        // 斜率标准误：s²/Sxx，s² = 残差均方（自由度 n-2）。n=2 时完美拟合、自由度 0 → 标准误置 0。
        var slopeSe = n > 2 ? Math.Sqrt(ssRes / (n - 2) / sxx) : 0.0;

        return (slope, intercept, r2, slopeSe);
    }

    private static double Weight(CalibrationPoint p, bool useWeights)
    {
        if (!useWeights) return 1.0;
        return p.SigmaF > 0 ? 1.0 / (p.SigmaF * p.SigmaF) : 1.0;
    }

    /// <summary>
    /// Grubbs 单点检验：对当前点集做普通 OLS，找标准化残差最大且超过临界值的点，返回其原始下标；无则返回 -1。
    /// 临界值用精确公式 G_crit = ((N-1)/√N)·√( t²/(N-2+t²) )，t 取自学生 t 分布，
    /// 尾部取双侧 α/(2N)——判的是绝对值最大的残差，两个方向都得算。N=20/α=0.05 给出 ≈2.708，
    /// 与 GB/T 4883-2008 表 A.2 的双侧临界值一致。
    /// <para>
    /// 这个数别记成 2.557，那是同一张表的单侧值（尾部取 α/N）。照单侧值核对会以为实现算错了，
    /// 真改成单侧则剔除会松约 6%：标准化残差 2.56~2.71 之间的点从剔除变成保留。
    /// 口径由 <c>LeastSquaresFitterTests.Fit_GrubbsGate_IsTheTwoSidedCriticalValue</c> 钉着。
    /// </para>
    /// </summary>
    private static int FindGrubbsOutlier(IReadOnlyList<CalibrationPoint> points, IReadOnlyList<int> indices)
    {
        var n = indices.Count;
        if (n < GrubbsMinSamples) return -1;

        var (slope, intercept, _, _) = FitCore(points, indices, useWeights: false);

        var residuals = new double[n];
        double meanResid = 0;
        for (var k = 0; k < n; k++)
        {
            var p = points[indices[k]];
            residuals[k] = p.DeltaTofNs - (slope * p.ForceN + intercept);
            meanResid += residuals[k];
        }
        meanResid /= n;

        double varResid = 0;
        for (var k = 0; k < n; k++)
            varResid += (residuals[k] - meanResid) * (residuals[k] - meanResid);
        var sResid = Math.Sqrt(varResid / (n - 1));
        if (sResid <= double.Epsilon) return -1; // 残差几乎为 0，无异常点

        var maxG = 0.0;
        var maxK = -1;
        for (var k = 0; k < n; k++)
        {
            var g = Math.Abs(residuals[k] - meanResid) / sResid;
            if (g > maxG) { maxG = g; maxK = k; }
        }

        var t = StudentT.InvCDF(0.0, 1.0, n - 2, 1.0 - GrubbsAlpha / (2.0 * n));
        var gCrit = (n - 1) / Math.Sqrt(n) * Math.Sqrt(t * t / (n - 2 + t * t));

        return maxG > gCrit ? indices[maxK] : -1;
    }
}
