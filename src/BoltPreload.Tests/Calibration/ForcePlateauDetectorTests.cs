using BoltPreload.Calibration;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Calibration;

public class ForcePlateauDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 18, 10, 0, 0, TimeSpan.FromHours(8));

    private static List<ForcePlateauDetector.Sample> Series(params (int sec, decimal forceN)[] pts)
        => pts.Select(p => new ForcePlateauDetector.Sample(T0.AddSeconds(p.sec), p.forceN)).ToList();

    [Fact]
    public void StablePlateau_IsDetected_WithMeanOfPlateauOnly()
    {
        // 前段是加载爬坡（20→60kN），末段 50 秒稳在 100kN 附近（±0.3kN < 1% 相对带）
        var s = Series((0, 20_000), (5, 40_000), (10, 60_000),
            (20, 100_100), (30, 99_900), (40, 100_200), (50, 100_000), (60, 99_800), (70, 100_050));

        var p = ForcePlateauDetector.Detect(s);

        p.Should().NotBeNull("末段力值稳定且时长/样本数均达标");
        p!.SampleCount.Should().Be(6, "爬坡段不得混进平台");
        p.MeanForceN.Should().BeInRange(99_900m, 100_100m);
        p.DurationSeconds.Should().BeApproximately(50, 0.1);
    }

    [Fact]
    public void RampInProgress_NoPlateau()
    {
        // 匀速加载中：任何以最新样本收尾的扩展集极差都超带
        var s = Series((0, 10_000), (10, 20_000), (20, 30_000), (30, 40_000), (40, 50_000), (50, 60_000));
        ForcePlateauDetector.Detect(s).Should().BeNull();
    }

    [Fact]
    public void ShortPause_BelowMinDuration_NoPlateau()
    {
        // 稳了 6 秒（< 最短 10s）——换手/读表级别的停顿不算档位
        var s = Series((0, 20_000), (4, 60_000), (8, 100_000), (10, 100_100), (12, 99_950), (14, 100_050));
        ForcePlateauDetector.Detect(s).Should().BeNull();
    }

    [Fact]
    public void LowForce_UsesAbsoluteBand()
    {
        // 3kN 载荷：相对带 1% 只有 30N，人工读表做不到；绝对带 500N 兜底后 ±150N 抖动应判稳
        var s = Series((0, 3_000), (10, 3_150), (20, 2_900), (30, 3_050), (40, 3_100), (50, 2_950));
        var p = ForcePlateauDetector.Detect(s);
        p.Should().NotBeNull();
        p!.SampleCount.Should().Be(6);
    }

    [Fact]
    public void TooFewSamples_NoPlateau()
    {
        var s = Series((0, 100_000), (15, 100_050));
        ForcePlateauDetector.Detect(s).Should().BeNull("2 条样本撑不起统计");
    }

    [Fact]
    public void EmptyInput_NoPlateau()
    {
        ForcePlateauDetector.Detect(Array.Empty<ForcePlateauDetector.Sample>()).Should().BeNull();
    }
}
