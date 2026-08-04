namespace BoltPreload.Tests.Integration;

/// <summary>
/// 构造"R² 可手算"的四点标定数据，供扭矩阶梯拟合与正式载荷标定两条路径共用。
/// <para>
/// 做法是在理想直线 Δt = a·F 上叠一组对称残差 (+d, −d, −d, +d)。这组残差满足 <c>Σe = 0</c> 且
/// <c>Σ(F−F̄)·e = 0</c>，所以最小二乘的斜率和截距原样不变，只有 R² 随 d 单调下降。
/// 这样就能指定落在门槛哪一侧，不用试凑。
/// </para>
/// <para>
/// R²(d) = 1 − 4d² / (a²·S_xx + 4d²)，其中 a=5e-4、S_xx=5e8 → a²·S_xx = 125。
/// d=0.5 → 0.9921；d=1.0 → 0.9690；d=1.5 → 0.9328。
/// </para>
/// <para>
/// 斜率取 5e-4 ns/N 是为了过 <c>ValidateKLMagnitude</c> 的量级护栏，和
/// <see cref="IntegrationSeed.SeedCalibratableChannelAsync"/> 里的螺栓物性配套，改一个得连带算另一个。
/// </para>
/// </summary>
internal static class CalibrationFitData
{
    /// <summary>K_L = Δt/F（ns/N）。</summary>
    public const decimal Slope = 0.0005m;

    /// <summary>四档载荷（N），等间距，S_xx = 5e8。</summary>
    public static readonly decimal[] Forces = [10_000m, 20_000m, 30_000m, 40_000m];

    /// <summary>a²·S_xx，R² 公式的分母常数项。</summary>
    private const decimal SlopeSquaredTimesSxx = 125m;

    /// <summary>残差幅度 d 对应的四个 Δt（ns）。</summary>
    public static decimal[] DeltaTofs(decimal d) =>
    [
        Forces[0] * Slope + d,
        Forces[1] * Slope - d,
        Forces[2] * Slope - d,
        Forces[3] * Slope + d,
    ];

    /// <summary>闭式解算出的 R²，用作断言期望值（端点那边是数值拟合，两者独立）。</summary>
    public static decimal ExpectedR2(decimal d)
    {
        var ssRes = 4m * d * d;
        return 1m - ssRes / (SlopeSquaredTimesSxx + ssRes);
    }
}
