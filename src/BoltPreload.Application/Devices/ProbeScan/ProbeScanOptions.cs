namespace BoltPreload.Application.Devices.ProbeScan;

/// <summary>
/// 通道自动检测的默认参数。具体值依据见 资料/工程文档/probe-scan-design.md §3 §4。
/// 阈值可被 API 请求覆盖；这里只是 fallback。
/// </summary>
public sealed class ProbeScanOptions
{
    /// <summary>每通道切换后稳定等待秒数（避免抓到上一个通道的残包）。</summary>
    public int DefaultSettleSeconds { get; set; } = 3;

    /// <summary>每通道有效采样窗口秒数。</summary>
    public int DefaultSampleSeconds { get; set; } = 8;

    /// <summary>少于此样本数直接判 inconclusive。</summary>
    public int DefaultMinSamples { get; set; } = 20;

    /// <summary>判 active_good 需要的最低 SNR (dB)。</summary>
    public double DefaultSnrGoodDb { get; set; } = 20.0;

    /// <summary>判 active_good 容许的 EchoTime 最大标准差 (ns)。</summary>
    public double DefaultEchoStdGoodNs { get; set; } = 5.0;

    /// <summary>判 active_good 需要的最低峰峰值。</summary>
    public int DefaultPeakToPeakMin { get; set; } = 600;

    /// <summary>判 active_good 需要的最低回波率（0~1）。</summary>
    public double DefaultEchoRateGood { get; set; } = ProbeScanThresholds.EchoRateGoodDefault;

    /// <summary>低于此回波率（0~1）直接判 active_no_echo。</summary>
    public double DefaultEchoRateNoEcho { get; set; } = ProbeScanThresholds.EchoRateNoEchoDefault;

    /// <summary>启动时是否清理 DB 中残留的 running/pending 扫描记录（API 崩溃自愈）。</summary>
    public bool CleanStaleRunsOnStartup { get; set; } = true;

    /// <summary>
    /// 默认扫描策略。
    /// <para>"mcr_single" — 推荐。每次把单通道写进 MCRConfig.EnabledChannels，设备在 MultiMeas 下连续上报。</para>
    /// <para>"single_meas" — 仅供特定型号使用；现场实测 model=3 多通道设备的 SingleMeas 是单次测量、不会循环上报，扫描会全 inconclusive。</para>
    /// </summary>
    public string DefaultStrategy { get; set; } = "mcr_single";
}

/// <summary>
/// 判定阈值快照（每次扫描记录一份，便于回溯）。
/// <para>
/// 回波率两档此前写死在 ProbeScanResultJudge 里，而同一个 good 判据的另外三项都可配 ——
/// 想在现场调阈值的人会发现只能调一半。两个参数带默认值是为了兼容老快照：
/// 落库的是 JSON，缺字段时若退化成 0.0，回波率判据就恒成立了。
/// </para>
/// </summary>
public sealed record ProbeScanThresholds(
    int MinSamples,
    double SnrGoodDb,
    double EchoStdGoodNs,
    int PeakToPeakMin,
    double EchoRateGood = ProbeScanThresholds.EchoRateGoodDefault,
    double EchoRateNoEcho = ProbeScanThresholds.EchoRateNoEchoDefault)
{
    /// <summary>回波率合格线默认值（0~1），见 probe-scan-design.md §4。</summary>
    public const double EchoRateGoodDefault = 0.95;

    /// <summary>回波率"判无回波"下限默认值（0~1）。</summary>
    public const double EchoRateNoEchoDefault = 0.5;

    public static ProbeScanThresholds FromOptions(ProbeScanOptions o) => new(
        o.DefaultMinSamples, o.DefaultSnrGoodDb, o.DefaultEchoStdGoodNs, o.DefaultPeakToPeakMin,
        o.DefaultEchoRateGood, o.DefaultEchoRateNoEcho);
}
