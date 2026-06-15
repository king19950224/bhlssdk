using BoltPreload.Algorithms.Waveform;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Algorithms;

/// <summary>
/// WaveDiagnostics 单元测试。验证方案 §8.6 波形相似度（归一化互相关）、衰减比与健康度评分。
/// </summary>
public class WaveDiagnosticsTests
{
    private const double ClockInt = 25.0;

    /// <summary>生成一段高斯包络正弦回波（模拟超声回波形态）。</summary>
    private static short[] MakeEcho(int n, double center, double amp, double sigma = 12, double freq = 0.5)
    {
        var d = new short[n];
        for (int i = 0; i < n; i++)
        {
            var env = amp * Math.Exp(-Math.Pow(i - center, 2) / (2 * sigma * sigma));
            d[i] = (short)Math.Clamp(env * Math.Sin((i - center) * freq), -32000, 32000);
        }
        return d;
    }

    private static WaveSeries Series(short[] data, double echoStart = 0) => new(data, echoStart, ClockInt);

    [Fact]
    public void Compare_IdenticalWaves_SimilarityIsOne()
    {
        var w = MakeEcho(256, 128, 500);
        var r = WaveDiagnostics.Compare(Series(w), Series((short[])w.Clone()), snrDb: 40);

        r.Similarity.Should().BeApproximately(1.0, 0.001, "完全相同的波形归一化互相关应为 1");
        r.AttenuationRatio.Should().BeApproximately(1.0, 0.001);
        r.HealthScore.Should().Be(100, "相似度/衰减满分 + SNR 40dB 满分 → 健康度 100");
    }

    [Fact]
    public void Compare_ShiftedWave_StaysSimilarAndReportsLag()
    {
        // 参考波形峰在 138，当前波形峰在 128（提前 10 点）→ 形态一致、相似度仍高，最佳对齐 lag = -10 点
        var current = MakeEcho(256, 128, 500);
        var reference = MakeEcho(256, 138, 500);
        var r = WaveDiagnostics.Compare(Series(current), Series(reference), snrDb: 30);

        r.Similarity.Should().BeGreaterThan(0.85, "纯平移不改变形态，互相关对平移鲁棒");
        r.BestLagNs.Should().BeApproximately(-10 * ClockInt, 30, "当前波形相对参考提前约 10 个采样点");
    }

    [Fact]
    public void Compare_HalfAmplitude_SimilarityHighButAttenuationLow()
    {
        // 形态相同、幅度减半：相似度（归一化）仍≈1，但衰减比≈0.5（信号变弱 → 耦合劣化征兆）
        var current = MakeEcho(256, 128, 250);
        var reference = MakeEcho(256, 128, 500);
        var r = WaveDiagnostics.Compare(Series(current), Series(reference), snrDb: 30);

        r.Similarity.Should().BeApproximately(1.0, 0.01);
        r.AttenuationRatio.Should().BeApproximately(0.5, 0.05);
    }

    [Fact]
    public void Compare_DifferentShape_SimilarityLow()
    {
        // 当前是回波，参考是高频交替方波 → 形态差异大，相似度应低
        var current = MakeEcho(256, 128, 500);
        var reference = new short[256];
        for (int i = 0; i < 256; i++) reference[i] = (short)(i % 2 == 0 ? 300 : -300);

        var r = WaveDiagnostics.Compare(Series(current), Series(reference), snrDb: 30);

        r.Similarity.Should().BeLessThan(0.6, "形态明显不同时相似度应明显偏低");
    }

    [Fact]
    public void Compare_EmptyWave_ReturnsZeroWithoutThrowing()
    {
        var r = WaveDiagnostics.Compare(Series(Array.Empty<short>()), Series(MakeEcho(256, 128, 500)));

        r.Similarity.Should().Be(0);
        r.HealthScore.Should().Be(0);
    }

    [Fact]
    public void Compare_LowSnr_LowersHealthScore()
    {
        var w = MakeEcho(256, 128, 500);
        var high = WaveDiagnostics.Compare(Series(w), Series((short[])w.Clone()), snrDb: 40);
        var low = WaveDiagnostics.Compare(Series(w), Series((short[])w.Clone()), snrDb: 10);

        high.HealthScore.Should().BeGreaterThan(low.HealthScore, "SNR 是健康度的加权项之一，低 SNR 应拉低健康度");
        low.HealthScore.Should().BeLessThan(100);
    }

    [Fact]
    public void Compare_DifferentClockInt_ResamplesBeforeComparing()
    {
        // 同一条连续回波 f(t)，按不同采样间隔采样：当前 25ns/256 点，参考 50ns/128 点。
        // 重采样到同一时间刻度后二者本质是同一波形 → 相似度应高。
        static double F(double t)
        {
            const double c = 3200.0;
            return 500 * Math.Exp(-Math.Pow((t - c) / 300.0, 2)) * Math.Sin((t - c) * 0.02);
        }
        var current = new short[256];
        for (int i = 0; i < 256; i++) current[i] = (short)F(i * 25.0);
        var reference = new short[128];
        for (int i = 0; i < 128; i++) reference[i] = (short)F(i * 50.0);

        var r = WaveDiagnostics.Compare(
            new WaveSeries(current, 0, 25.0),
            new WaveSeries(reference, 0, 50.0),
            snrDb: 30);

        r.Similarity.Should().BeGreaterThan(0.8, "重采样到同一时间刻度后，同形态波形相似度应高");
    }
}
