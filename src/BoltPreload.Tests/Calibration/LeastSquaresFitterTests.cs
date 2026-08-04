using BoltPreload.Calibration;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Calibration;

/// <summary>
/// LeastSquaresFitter 标定拟合回归测试（plan §8.5）。
/// 约定：模型 Δt = K_L·F + b，K_L 单位 ns/N（资料/工程文档/sdk-api-facts.md §12）。
/// </summary>
public class LeastSquaresFitterTests
{
    private static readonly LeastSquaresFitter Sut = new();

    [Fact]
    public void Fit_PerfectLine_RecoversSlopeInterceptAndR2()
    {
        // Δt = 1e-4 · F（过原点），4 档载荷
        var points = new[]
        {
            new CalibrationPoint(10000, 1.0),
            new CalibrationPoint(20000, 2.0),
            new CalibrationPoint(30000, 3.0),
            new CalibrationPoint(40000, 4.0),
        };

        var r = Sut.Fit(points);

        r.KL.Should().BeApproximately(1e-4, 1e-9);
        r.InterceptNs.Should().BeApproximately(0.0, 1e-6);
        r.RSquared.Should().BeApproximately(1.0, 1e-9);
        r.PointCount.Should().Be(4);
        r.RemovedIndices.Should().BeEmpty();
    }

    [Fact]
    public void Fit_NonZeroIntercept_RecoversIntercept()
    {
        // Δt = 1e-4 · F + 0.5
        var points = new[]
        {
            new CalibrationPoint(10000, 1.5),
            new CalibrationPoint(20000, 2.5),
            new CalibrationPoint(30000, 3.5),
            new CalibrationPoint(40000, 4.5),
        };

        var r = Sut.Fit(points);

        r.KL.Should().BeApproximately(1e-4, 1e-9);
        r.InterceptNs.Should().BeApproximately(0.5, 1e-6);
        r.RSquared.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Fit_NoisyData_GivesHighButImperfectR2()
    {
        // 在完美线上加微噪声：R² 应很高但 < 1，斜率仍接近 1e-4
        var points = new[]
        {
            new CalibrationPoint(10000, 1.02),
            new CalibrationPoint(20000, 1.97),
            new CalibrationPoint(30000, 3.04),
            new CalibrationPoint(40000, 3.96),
            new CalibrationPoint(50000, 5.01),
        };

        var r = Sut.Fit(points);

        r.KL.Should().BeApproximately(1e-4, 5e-6);
        r.RSquared.Should().BeInRange(0.98, 1.0);
        r.KLStdError.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void Fit_SmallSampleWithOutlier_DoesNotApplyGrubbs()
    {
        // n=5 < 20：即使开启 Grubbs，也不得剔除（GB/T 4883-2008 小样本套 Grubbs 属误用，plan §8.5.1）
        var points = new[]
        {
            new CalibrationPoint(10000, 1.0),
            new CalibrationPoint(20000, 2.0),
            new CalibrationPoint(30000, 9.0),   // 明显离群
            new CalibrationPoint(40000, 4.0),
            new CalibrationPoint(50000, 5.0),
        };

        var r = Sut.Fit(points, applyGrubbs: true);

        r.RemovedIndices.Should().BeEmpty("n<20 不做 Grubbs 统计剔除");
        r.PointCount.Should().Be(5);
        r.Note.Should().Contain("不做 Grubbs");
    }

    [Fact]
    public void Fit_LargeSampleWithGrossOutlier_GrubbsRemovesOnePoint()
    {
        // n=20：除 index 9（中间档，低杠杆）外都在线 Δt=1e-4·F，该点偏 +5ns 粗大误差 → Grubbs 应剔除它。
        // 故意放中间档而非端点：端点高杠杆会把回归线拉向离群点、压低其残差，可能漏检。
        var points = new List<CalibrationPoint>();
        for (var i = 1; i <= 20; i++)
        {
            var f = 1000.0 * i;
            var dt = 1e-4 * f;
            if (i == 10) dt += 5.0; // index 9，粗大离群
            points.Add(new CalibrationPoint(f, dt));
        }

        var r = Sut.Fit(points, applyGrubbs: true);

        r.RemovedIndices.Should().ContainSingle().Which.Should().Be(9);
        r.PointCount.Should().Be(19);
        r.RSquared.Should().BeApproximately(1.0, 1e-6, "剔除离群点后剩余点完美共线");
        r.KL.Should().BeApproximately(1e-4, 1e-9);
    }

    [Fact]
    public void Fit_LargeCleanSample_GrubbsRemovesNothing()
    {
        var points = new List<CalibrationPoint>();
        for (var i = 1; i <= 22; i++)
            points.Add(new CalibrationPoint(1000.0 * i, 1e-4 * (1000.0 * i)));

        var r = Sut.Fit(points, applyGrubbs: true);

        r.RemovedIndices.Should().BeEmpty();
        r.PointCount.Should().Be(22);
    }

    /// <summary>
    /// 剔除门槛必须是双侧临界值（N=20/α=0.05 → G_crit≈2.708，GB/T 4883-2008 表 A.2），
    /// 不是单侧的 ≈2.557。
    /// <para>
    /// 这条用例是为一个文档缺陷立的桩：<c>FindGrubbsOutlier</c> 的注释曾把"可手工核对"的锚点
    /// 写成 2.557（那是单侧值），而实现取的是双侧尾 α/(2N)。照注释去核对的人会算出 2.71、
    /// 以为实现有 bug，顺手"改对"成单侧就把安全相关标定数据的异常剔除放松了约 6%。
    /// </para>
    /// <para>
    /// 判别办法是让偏移量卡在两个门槛之间：δ=0.242 ns 在双侧门槛下保留、单侧门槛下会被剔。
    /// 直接断言临界值本身做不到这件事——那只能在测试里重算一遍同样的公式，改了实现照样过。
    /// </para>
    /// </summary>
    [Fact]
    public void Fit_GrubbsGate_IsTheTwoSidedCriticalValue()
    {
        // 双侧门槛（≈2.708）之下：保留。实现若换成单侧（≈2.557），这个点就会被剔掉。
        Sut.Fit(ScatteredPoints(extraNs: 0.242), applyGrubbs: true)
            .RemovedIndices.Should().BeEmpty(
                "标准化残差落在单侧与双侧临界值之间的点，按双侧口径应当保留");

        // 明显超过双侧门槛：剔除。少了这一半，上面那条可能只是因为剔除压根没生效而通过。
        Sut.Fit(ScatteredPoints(extraNs: 0.30), applyGrubbs: true)
            .RemovedIndices.Should().ContainSingle().Which.Should().Be(9,
                "超过双侧临界值的点必须被剔除");
    }

    /// <summary>
    /// 20 点：全点带固定散布，index 9 再额外偏 <paramref name="extraNs"/>。
    /// 全点都有散布是关键——只有一个点偏离时残差标准差全由它自己撑起，标准化残差会饱和在
    /// (N-1)/√N≈4.25，怎么调都落不到临界值附近，测不出门槛在哪。
    /// </summary>
    private static List<CalibrationPoint> ScatteredPoints(double extraNs)
    {
        // 固定散布序列（手写，不用随机数，保证可复现），量级 ~0.1ns
        double[] scatter =
        [
            0.08, -0.11, 0.05, 0.12, -0.07, 0.10, -0.13, 0.06, -0.04, 0.09,
            -0.10, 0.11, -0.06, 0.04, 0.13, -0.12, 0.07, -0.05, 0.10, -0.08,
        ];
        var pts = new List<CalibrationPoint>(20);
        for (var i = 0; i < 20; i++)
        {
            var f = 1000.0 * (i + 1);
            var dt = 1e-4 * f + scatter[i] + (i == 9 ? extraNs : 0.0);
            pts.Add(new CalibrationPoint(f, dt));
        }
        return pts;
    }

    [Fact]
    public void Fit_TooFewPoints_Throws()
    {
        var points = new[] { new CalibrationPoint(10000, 1.0) };

        var act = () => Sut.Fit(points);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Fit_AllSameForce_Throws()
    {
        var points = new[]
        {
            new CalibrationPoint(10000, 1.0),
            new CalibrationPoint(10000, 2.0),
            new CalibrationPoint(10000, 3.0),
        };

        var act = () => Sut.Fit(points);

        act.Should().Throw<ArgumentException>();
    }
}
