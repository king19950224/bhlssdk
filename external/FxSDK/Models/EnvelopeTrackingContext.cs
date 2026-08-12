using MathNet.Numerics.Statistics;

namespace FxSDK.Models;

/// <summary>
/// 包络引导过零法的运行期上下文。
/// 扫波重新捕获当前工件的包络/过零配对；测量和监测优先使用该配对进行周期追踪。
/// </summary>
public sealed class EnvelopeTrackingContext
{
    private const int MaxScanAnchorCount = 5;
    private readonly List<TrackingAnchor> _scanAnchors = new();

    /// <summary>
    /// 本次计算使用的过零参考绝对时间(ns)。
    /// </summary>
    public decimal? ReferenceEchoTime { get; set; }

    /// <summary>
    /// 本次计算使用的包络参考绝对时间(ns)。
    /// </summary>
    public decimal? ReferenceEnvTime { get; set; }

    /// <summary>
    /// 本次计算使用的稳定载波周期(ns)。
    /// 有扫波锚点时取扫波中位周期，否则可使用置零时持久化的参考周期。
    /// </summary>
    public decimal? ReferencePeriodNs { get; set; }

    /// <summary>
    /// 当前波形起始绝对时间(ns)。
    /// </summary>
    public decimal EchoStart { get; set; }

    /// <summary>
    /// 探头频率(MHz)，用于限定同一周期内的上升沿搜索范围。
    /// </summary>
    public decimal? ProbeFreqMhz { get; set; }

    /// <summary>
    /// 本轮扫波得到的稳定包络锚点绝对时间(ns)。
    /// </summary>
    public decimal? TrackingEnvTime => GetStableAnchor()?.EnvelopeTime;

    /// <summary>
    /// 与 TrackingEnvTime 同批扫波得到的上升沿过零绝对时间(ns)。
    /// </summary>
    public decimal? TrackingEchoTime => GetStableAnchor()?.EchoTime;

    /// <summary>
    /// 本轮扫波所有有效采用周期的中位数(ns)。周期通常来自主回波实测，
    /// 实测不稳定时可能来自探头配置；单独取中位数可降低偶发过零误检的影响。
    /// </summary>
    public decimal? TrackingPeriodNs => GetStablePeriod();

    public bool HasTrackingAnchor => TrackingEnvTime.HasValue && TrackingEchoTime.HasValue;

    public void AddTrackingAnchor(decimal envelopeTime, decimal echoTime, decimal? periodNs)
    {
        _scanAnchors.Add(new TrackingAnchor(envelopeTime, echoTime, periodNs));
        if (_scanAnchors.Count > MaxScanAnchorCount)
        {
            _scanAnchors.RemoveAt(0);
        }
    }

    public void ClearTrackingAnchor()
    {
        _scanAnchors.Clear();
        ReferenceEnvTime = null;
        ReferenceEchoTime = null;
        ReferencePeriodNs = null;
    }

    private TrackingAnchor? GetStableAnchor()
    {
        if (_scanAnchors.Count == 0)
        {
            return null;
        }

        // 用声时中位数选出一条真实采样记录，保证 F0/Z0 始终来自同一帧。
        var medianEchoTime = (decimal)_scanAnchors.Select(anchor => Convert.ToDouble(anchor.EchoTime)).Median();
        return _scanAnchors.MinBy(anchor => Math.Abs(anchor.EchoTime - medianEchoTime));
    }

    private decimal? GetStablePeriod()
    {
        var validPeriods = _scanAnchors
            .Where(anchor => anchor.PeriodNs is > 0)
            .Select(anchor => Convert.ToDouble(anchor.PeriodNs!.Value))
            .ToArray();
        return validPeriods.Length == 0 ? null : Convert.ToDecimal(validPeriods.Median());
    }

    private readonly record struct TrackingAnchor(decimal EnvelopeTime, decimal EchoTime, decimal? PeriodNs);
}
