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
