namespace BoltPreload.Application.Devices.ProbeScan;

/// <summary>
/// 单通道窗口统计（由 Repository 在采样窗口内从 DB 聚合得到）。
/// 所有字段以"采样窗口内累计/平均"为单位，null 表示该窗口无对应数据。
/// ChannelName 是 channels.name（可读位置标签），扫描时拉一份给报告显示，与 BoundBoltCode 一并提示运维。
/// </summary>
public sealed record ProbeScanWindowStats(
    int Samples,
    int SamplesWithEcho,
    decimal? EchoAvgNs,
    decimal? EchoStdNs,
    decimal? PeakToPeakAvg,
    decimal? SnrAvgDb,
    string? BoundBoltCode,
    // ChannelName 是后加字段，默认 null 让既有测试与可能的旧调用代码免改
    string? ChannelName = null);

/// <summary>判定结果。</summary>
public enum ProbeScanVerdict
{
    /// <summary>样本数不足或异常，无法判定。</summary>
    Inconclusive,
    /// <summary>切到该通道后完全没数据上报。</summary>
    NotReporting,
    /// <summary>有 ChannelResult 但 EchoTime 大量缺失。</summary>
    ActiveNoEcho,
    /// <summary>有信号但质量不达标（SNR 不足 / EchoTime 抖动 / P-P 偏低）。</summary>
    ActiveWeak,
    /// <summary>健康通道。</summary>
    ActiveGood
}

/// <summary><see cref="ProbeScanVerdict"/> 与数据库/API 中 snake_case 字符串的映射。</summary>
public static class ProbeScanVerdictNames
{
    public const string Inconclusive  = "inconclusive";
    public const string NotReporting  = "not_reporting";
    public const string ActiveNoEcho  = "active_no_echo";
    public const string ActiveWeak    = "active_weak";
    public const string ActiveGood    = "active_good";

    public static string Format(ProbeScanVerdict v) => v switch
    {
        ProbeScanVerdict.Inconclusive => Inconclusive,
        ProbeScanVerdict.NotReporting => NotReporting,
        ProbeScanVerdict.ActiveNoEcho => ActiveNoEcho,
        ProbeScanVerdict.ActiveWeak   => ActiveWeak,
        ProbeScanVerdict.ActiveGood   => ActiveGood,
        _ => Inconclusive
    };
}

/// <summary>
/// 纯函数判定器。规则见 资料/工程文档/probe-scan-design.md §4。
/// </summary>
public static class ProbeScanResultJudge
{
    /// <summary>verdict 到 channels.hardware_state 的映射（inconclusive 返回 null = 不更新）。</summary>
    public static string? ToHardwareState(ProbeScanVerdict v) => v switch
    {
        ProbeScanVerdict.ActiveGood   => "healthy",
        ProbeScanVerdict.ActiveWeak   => "noisy",
        ProbeScanVerdict.ActiveNoEcho => "suspect",
        ProbeScanVerdict.NotReporting => "no_data",
        _ => null
    };

    public static ProbeScanVerdict Judge(ProbeScanWindowStats stats, ProbeScanThresholds t)
    {
        if (stats.Samples == 0)
            return ProbeScanVerdict.NotReporting;

        if (stats.Samples < t.MinSamples)
            return ProbeScanVerdict.Inconclusive;

        // 至此 samples >= minSamples
        var echoRate = stats.Samples > 0 ? (double)stats.SamplesWithEcho / stats.Samples : 0.0;

        if (echoRate < t.EchoRateNoEcho)
            return ProbeScanVerdict.ActiveNoEcho;

        // echoRate >= t.EchoRateNoEcho
        bool good =
            echoRate >= t.EchoRateGood
            && stats.SnrAvgDb is not null && (double)stats.SnrAvgDb.Value >= t.SnrGoodDb
            && stats.EchoStdNs is not null && (double)stats.EchoStdNs.Value <= t.EchoStdGoodNs
            && stats.PeakToPeakAvg is not null && (double)stats.PeakToPeakAvg.Value >= t.PeakToPeakMin;

        return good ? ProbeScanVerdict.ActiveGood : ProbeScanVerdict.ActiveWeak;
    }
}
