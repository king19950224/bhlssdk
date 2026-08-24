using BoltPreload.Application.Devices.ProbeScan;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Devices.ProbeScan;

/// <summary>
/// 验证 ProbeScanResultJudge 规则。规则在 资料/工程文档/probe-scan-design.md §4，
/// 阈值默认值（minSamples=20、snr=20dB、echoStd=5ns、pp=600）写死在测试里，
/// 避免阈值漂移时单测被"动态"绑架而无声变绿。
/// </summary>
public class ProbeScanResultJudgeTests
{
    private static readonly ProbeScanThresholds Default = new(
        MinSamples: 20,
        SnrGoodDb: 20.0,
        EchoStdGoodNs: 5.0,
        PeakToPeakMin: 600);

    [Fact]
    public void Samples_Zero_NotReporting()
    {
        var stats = new ProbeScanWindowStats(0, 0, null, null, null, null, null);
        ProbeScanResultJudge.Judge(stats, Default)
            .Should().Be(ProbeScanVerdict.NotReporting);
    }

    [Fact]
    public void Samples_BelowMin_Inconclusive()
    {
        var stats = new ProbeScanWindowStats(
            Samples: 5, SamplesWithEcho: 5,
            EchoAvgNs: 32200m, EchoStdNs: 1m,
            PeakToPeakAvg: 1280m, SnrAvgDb: 24m,
            BoundBoltCode: null);
        ProbeScanResultJudge.Judge(stats, Default)
            .Should().Be(ProbeScanVerdict.Inconclusive);
    }

    [Fact]
    public void EchoRate_Below_50pct_ActiveNoEcho()
    {
        var stats = new ProbeScanWindowStats(
            Samples: 80, SamplesWithEcho: 10,
            EchoAvgNs: null, EchoStdNs: null,
            PeakToPeakAvg: 1280m, SnrAvgDb: 24m,
            BoundBoltCode: null);
        ProbeScanResultJudge.Judge(stats, Default)
            .Should().Be(ProbeScanVerdict.ActiveNoEcho);
    }

    [Fact]
    public void HighRate_LowSnr_ActiveWeak()
    {
        var stats = new ProbeScanWindowStats(
            Samples: 80, SamplesWithEcho: 80,
            EchoAvgNs: 32200m, EchoStdNs: 1m,
            PeakToPeakAvg: 1280m, SnrAvgDb: 15m,   // SNR 不达标
            BoundBoltCode: null);
        ProbeScanResultJudge.Judge(stats, Default)
            .Should().Be(ProbeScanVerdict.ActiveWeak);
    }

    [Fact]
    public void HighRate_HighEchoStd_ActiveWeak()
    {
        var stats = new ProbeScanWindowStats(
            Samples: 80, SamplesWithEcho: 80,
            EchoAvgNs: 32200m, EchoStdNs: 12m,     // EchoTime 抖动过大
            PeakToPeakAvg: 1280m, SnrAvgDb: 24m,
            BoundBoltCode: null);
        ProbeScanResultJudge.Judge(stats, Default)
            .Should().Be(ProbeScanVerdict.ActiveWeak);
    }

    [Fact]
    public void HighRate_LowPeakToPeak_ActiveWeak()
    {
        var stats = new ProbeScanWindowStats(
            Samples: 80, SamplesWithEcho: 80,
            EchoAvgNs: 32200m, EchoStdNs: 1m,
            PeakToPeakAvg: 400m, SnrAvgDb: 24m,    // P-P 太低
            BoundBoltCode: null);
        ProbeScanResultJudge.Judge(stats, Default)
            .Should().Be(ProbeScanVerdict.ActiveWeak);
    }

    [Fact]
    public void WaveMetricsAbsent_GoodEcho_FallsBackToEchoOnly_ActiveGood()
    {
        // 窗口内一条 wave_features 都没有（真机 MultiMeas 可能不随测量带波形）：
        // SNR/峰峰值是"指标缺失"不是"指标不达标"——退化为纯回波判定，
        // 回波率与抖动都达标就该判 good，否则真机上健康通道会被系统性压成 noisy
        var stats = new ProbeScanWindowStats(
            Samples: 80, SamplesWithEcho: 80,
            EchoAvgNs: 32200m, EchoStdNs: 1m,
            PeakToPeakAvg: null, SnrAvgDb: null,
            BoundBoltCode: null);
        ProbeScanResultJudge.Judge(stats, Default)
            .Should().Be(ProbeScanVerdict.ActiveGood);
    }

    [Fact]
    public void WaveMetricsAbsent_JitteryEcho_ActiveWeak()
    {
        // 纯回波退化路径仍要过抖动门：EchoStd 超标 → weak
        var stats = new ProbeScanWindowStats(
            Samples: 80, SamplesWithEcho: 80,
            EchoAvgNs: 32200m, EchoStdNs: 12m,
            PeakToPeakAvg: null, SnrAvgDb: null,
            BoundBoltCode: null);
        ProbeScanResultJudge.Judge(stats, Default)
            .Should().Be(ProbeScanVerdict.ActiveWeak);
    }

    [Fact]
    public void PartialWaveMetrics_StillJudgedStrictly_ActiveWeak()
    {
        // 窗口内有波形（PP 非空）但 SNR 为空（噪声段 RMS 为 0 之类）：
        // 有波形就走严格判定，缺 SNR 按不达标处理，不进纯回波退化路径
        var stats = new ProbeScanWindowStats(
            Samples: 80, SamplesWithEcho: 80,
            EchoAvgNs: 32200m, EchoStdNs: 1m,
            PeakToPeakAvg: 1280m, SnrAvgDb: null,
            BoundBoltCode: null);
        ProbeScanResultJudge.Judge(stats, Default)
            .Should().Be(ProbeScanVerdict.ActiveWeak);
    }

    [Fact]
    public void All_Conditions_Met_ActiveGood()
    {
        var stats = new ProbeScanWindowStats(
            Samples: 80, SamplesWithEcho: 80,
            EchoAvgNs: 32280.5m, EchoStdNs: 1.2m,
            PeakToPeakAvg: 1280m, SnrAvgDb: 24.3m,
            BoundBoltCode: "M20-001");
        ProbeScanResultJudge.Judge(stats, Default)
            .Should().Be(ProbeScanVerdict.ActiveGood);
    }

    [Fact]
    public void EchoRate_Borderline_94pct_NotGood()
    {
        // echoRate 94% < 95%，应回退到 ActiveWeak（其余指标都达标）
        var stats = new ProbeScanWindowStats(
            Samples: 100, SamplesWithEcho: 94,
            EchoAvgNs: 32200m, EchoStdNs: 1m,
            PeakToPeakAvg: 1280m, SnrAvgDb: 24m,
            BoundBoltCode: null);
        ProbeScanResultJudge.Judge(stats, Default)
            .Should().Be(ProbeScanVerdict.ActiveWeak);
    }

    [Theory]
    [InlineData(ProbeScanVerdict.ActiveGood,   "healthy")]
    [InlineData(ProbeScanVerdict.ActiveWeak,   "noisy")]
    [InlineData(ProbeScanVerdict.ActiveNoEcho, "suspect")]
    [InlineData(ProbeScanVerdict.NotReporting, "no_data")]
    public void HardwareState_Mapping(ProbeScanVerdict v, string expected)
    {
        ProbeScanResultJudge.ToHardwareState(v).Should().Be(expected);
    }

    [Fact]
    public void HardwareState_Inconclusive_IsNull()
    {
        // inconclusive 不更新 hardware_state，保持旧值
        ProbeScanResultJudge.ToHardwareState(ProbeScanVerdict.Inconclusive)
            .Should().BeNull();
    }
}
