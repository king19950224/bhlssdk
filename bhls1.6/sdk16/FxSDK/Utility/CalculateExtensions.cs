using System.Text;
using FxSDK.Models;
using MathNet.Numerics.Statistics;
using MathNet.Symbolics;

namespace FxSDK.Utility;

/// <summary>
/// 一些超声相关的计算方法
/// </summary>
public static class CalculateExtensions
{
    private const string EchoTimeExpression = "2 * l * 1000000 / v"; // 乘1,000,000，用于将秒转换为纳秒(l为mm, v为m/s)
    private const string LengthMmExpression = "v * t / 2000000";//依据时间和声速，获取长度
    private const decimal CalculationInterval = 1M;//计算用插值时间间隔
    private const double SoundVelocity = 5890.0d;
    private const int MinMeasuredPeriodIntervals = 3;
    private const double MaxPeriodMadRatio = 0.15d;
    private const double PeriodEnvelopeRatio = 0.20d;
    private const double TrackingSearchHalfPeriodRatio = 0.45d;
    private const double InitialSearchMaxPeriodRatio = 1.05d;
    private const decimal MinNominalPeriodRatio = 0.60M;
    private const decimal MaxNominalPeriodRatio = 1.40M;
    public const decimal DefaultTempCoeff = 0.00010269M;//默认温度补偿系数
    public const decimal StandardTemperature = 22.22M; //基准温度

    private readonly record struct EnvelopeFrontResult(double[] Waveform, double[] SmoothedEnvelope,
        EchoRegion MainEcho, decimal FrontIndex, decimal Threshold, double EnvelopeMax);
    /// <summary>
    /// 根据公式和变量值字典计算
    /// 多项式、指数、对数、三角函数等 支持科学计数法
    /// </summary>
    /// <param name="expression">类似 2*x+3*y 这样的表达式</param>
    /// <param name="variables">变量值字典 {{"x",2},{"y",3}}</param>
    public static double Evaluate(string expression, Dictionary<string, double> variables)
    {
        if (string.IsNullOrEmpty(expression))
        {
            throw new ArgumentException("Expression cannot be null or empty."); 
        }
        //解析公式
        var expr = SymbolicExpression.Parse(expression);
        //创建一个变量词典
        var symbols = new Dictionary<string, FloatingPoint>();
        foreach (var varItem in variables)
        {
            expr = expr.Substitute(varItem.Key, varItem.Value);
            symbols.Add(varItem.Key, FloatingPoint.NewReal(varItem.Value));
        }
        //计算表达式的值
        var result = expr.Evaluate(symbols);
        return (float) result.RealValue;
    }
    /// <summary>
    /// 通过试件长度(mm)和声速(m/s) 计算回波窗口(ns)
    /// </summary>
    /// <param name="lengthInMm">长度，单位：mm</param>
    /// <param name="quantizationUnit">量化单位</param>
    /// <param name="soundVelocity">声速，单位：m/s，默认为5890.00m/s</param>
    /// <returns>回波时间，单位：纳秒</returns>
    public static double CalcEchoWin(double lengthInMm, ushort quantizationUnit = 1000, double soundVelocity = SoundVelocity)
    {
        // 创建变量字典
        var variables = new Dictionary<string, double>
        {
            {"l", lengthInMm}, // 'l' 代表长度
            {"v", soundVelocity} // 'v' 代表声速
        };
        // 计算回波时间
        return QuantizeEchoWin(Evaluate(EchoTimeExpression, variables),quantizationUnit);
    }
    /// <summary>
    /// 量化回波窗口
    /// </summary>
    public static double QuantizeEchoWin(double echoWin, ushort quantizationUnit)
    {
        //向下取整到最接近的 quantizationUnit 的整数倍
        return Math.Floor(echoWin / quantizationUnit) * quantizationUnit;
    }
    /// <summary>
    /// 依据 tof 时间计算 试件长度(mm)
    /// </summary>
    public static double CalcLength(double tof, double soundVelocity = SoundVelocity)
    {
        // 创建变量字典
        var variables = new Dictionary<string, double>
        {
            {"t", tof}, // 't' 时间单位ns
            {"v", soundVelocity} // 'v' 代表声速
        };
        // 计算回波时间
        return Evaluate(LengthMmExpression, variables);
    }
    /// <summary>
    /// 计算声时差
    /// </summary>
    /// <param name="refEchoTime">参考声时(ns)</param>
    /// <param name="refTemp">参考温度</param>
    /// <param name="echoTime">当前声时(ns)</param>
    /// <param name="temp">当前温度</param>
    /// <param name="tcMode">温补模式</param>
    /// <param name="tc">温度补偿系数</param>
    public static decimal CalcDeltaTof(decimal refEchoTime, decimal refTemp, decimal echoTime, decimal temp, 
        ushort tcMode = 1, decimal tc = DefaultTempCoeff)
    {
        var normalizedEchoTime = echoTime;
        // 计算当前声时补偿到refTemp下的声时
        if (tcMode > 0)
        {
            normalizedEchoTime = echoTime / (1 + tc * (temp - refTemp));
        }
        // 计算声时差
        return normalizedEchoTime - refEchoTime;
    }
    /// <summary>
    /// 计算声时归一值
    /// </summary>
    /// <param name="echoTime">当前声时值</param>
    /// <param name="temp">当前温度</param>
    /// <param name="refTemp">归一化温度</param>
    /// <param name="tc">温度补偿系数</param>
    public static decimal CalcNormalTof(decimal echoTime, decimal temp, decimal refTemp, decimal tc = DefaultTempCoeff)
    {
        return echoTime / (1 + tc * (temp - refTemp));
    }
    /// <summary>
    /// 通过拟合公式和声时差计算超声轴力结果
    /// 0.354*x+0.25 或者 0.001*x^2+0.25*x+0.54
    /// </summary>
    /// <param name="fitFormula">拟合公式</param>
    /// <param name="deltaTof">声时差</param>
    public static decimal CalcUltraResult(string fitFormula, double deltaTof)
    {
        // 创建变量字典
        var variables = new Dictionary<string, double>
        {
            {"x", deltaTof}, // 'x' 代表声时差
        };
        // 使用 Evaluate 方法计算轴力
        var result = Evaluate(fitFormula, variables);
        return Convert.ToDecimal(result);
    }
    /// <summary>
    /// 计算回波时间索引(计算过零含小数索引)-阈值法
    /// -1 表示信号过小
    /// -2 阈值异常
    /// </summary>
    /// <param name="signal">信号</param>
    /// <param name="signalInterval">信号点间隔(ns)</param>
    /// <param name="assist">回波辅助信息</param>
    /// <param name="noiseBase">噪声基线</param>
    /// <param name="threshold">阈值</param>
    public static double CalcZeroIndexByThrSignal(List<double> signal, decimal signalInterval, out EchoAssist assist ,double noiseBase = 50d, 
        short threshold = 0)
    {
        assist = new EchoAssist();
        //1. 根据幅值判断信号是否有效
        var isValidSignal = signal.Any(s => Math.Abs(s) >= noiseBase);
        if (!isValidSignal)
        {
            return -1;//无效信号状态码
        }
        var dynamicThreshold = threshold == 0 ? signal.Max() * 0.6d : threshold;
        var edgeType = dynamicThreshold < 0 ? ThresholdEdgeType.Falling : ThresholdEdgeType.Rising;
        //2. 在阈值前后找 1us 范围内插值  整us
        var thrCrossIdx = FindForwardThresholdCrossing(signal.ToArray(), dynamicThreshold, edgeType);
        if (thrCrossIdx < 0)
        {
            return -2;//阈值异常 波形比阈值小
        }
        var usSpan = (int) (1000 / signalInterval); //1us多少个离散点
        var usStartIdx = (int) Math.Floor(thrCrossIdx / (decimal) usSpan) * usSpan;
        var usEndIdx = usStartIdx + 2 * usSpan; //只进行前后共2个us的点进行插值
        if (usEndIdx >= signal.Count) usEndIdx = signal.Count - 1;
        var rangeSignal = signal.GetRange(usStartIdx, usEndIdx - usStartIdx);
        //进行2us范围插值
        var calcSignal = SignalProcessor.InterpolateAndResample(rangeSignal, signalInterval, CalculationInterval);
        //3. 计算过阈值后的上升沿时间轴正向 过零点索引startIndex为0时
        var calcThrCrossIdx = FindForwardThresholdCrossing(calcSignal.ToArray(), dynamicThreshold, edgeType);
        assist.Mx = usStartIdx + calcThrCrossIdx * CalculationInterval / signalInterval; 
        assist.My = (decimal)dynamicThreshold;
        
        var calcZeroIdxTuple = FindPositiveZeroCrossing(calcSignal.ToArray(), calcThrCrossIdx);
        //信号过阈值 时间轴正向过零点索引
        var calcZeroIdx = Convert.ToDecimal(calcZeroIdxTuple.Item1);
        if (calcZeroIdxTuple.Item1 >= 0 && calcZeroIdxTuple.Item1 + 1 < calcSignal.Count)
        {
            var calcPrevIdx = calcZeroIdxTuple.Item1;
            var calcCurrentIdx = calcZeroIdxTuple.Item1 + 1;
            calcZeroIdx = CalcZeroCrossingIndex(calcPrevIdx, calcSignal[calcPrevIdx], calcCurrentIdx,calcSignal[calcCurrentIdx]);
        }
        var zeroIndex = usStartIdx + calcZeroIdx * CalculationInterval / signalInterval;
        assist.Zx = zeroIndex;
        return Convert.ToDouble(zeroIndex);
    }

    /// <summary>
    /// 包络引导的上升沿过零自动法。
    /// 包络只用于确定周期级粗位置；最终声时始终取预测位置附近的上升沿过零点。
    /// </summary>
    /// <remarks>
    /// 置零后使用 ReferenceEnvTime/ReferenceEchoTime 计算包络位移预测值；没有置零锚点时，
    /// 选择当前包络前沿之后的第一个合格上升沿过零点。返回值仍为输入波形中的小数索引。
    /// </remarks>
    public static double CalcZeroIndexByEnvelopeGuidedZero(List<double> signal, decimal signalInterval,
        out EchoAssist assist, int consecutive = 20, double noiseBase = 50d,
        EnvelopeTrackingContext? trackingContext = null)
    {
        assist = new EchoAssist();
        var locateStatus = TryLocateEnvelopeFront(signal, signalInterval, consecutive, noiseBase,
            finThreshold: 0.09d, preferFirstValidRegion: false, out var envelopeFront);
        if (locateStatus < 0)
        {
            // 自动法对外只保留协议级粗分类：波形幅值不足沿用 -1；
            // 主回波区域或包络前沿未定位统一映射为 -2（检测条件未满足）。
            // TryLocateEnvelopeFront 的返回语义不改，避免影响复用它的动态特征法。
            return locateStatus == (short)MeasStatusCode.SignalTooSmall
                ? locateStatus
                : (short)MeasStatusCode.ThresholdNotReached;
        }

        assist.Mx = envelopeFront.FrontIndex;
        assist.My = envelopeFront.Threshold;
        assist.Fx = envelopeFront.FrontIndex;
        assist.Fy = envelopeFront.Threshold;
        assist.Sx = envelopeFront.MainEcho.Start;
        assist.Ex = envelopeFront.MainEcho.End;

        // 周期决定首次选点的最大相位距离、跟踪搜索窗和过零质量检查窗口。
        // 优先使用扫波/置零形成的稳定周期；没有稳定周期时从当前主回波实测；
        // 实测不可靠才使用探头配置的名义周期。两者都不可用时宁可失败，也不再静默假定 2MHz(500ns)。
        var periodNs = ResolveCarrierPeriodNs(envelopeFront, signalInterval, trackingContext);
        if (!periodNs.HasValue)
        {
            // 已经找到 F，但无法形成可信周期，归为特征跟踪失败。
            return (short)MeasStatusCode.SimilarityLow;
        }
        assist.PeriodNs = periodNs.Value;
        var periodPoints = Math.Max(2d, Convert.ToDouble(periodNs.Value / signalInterval));
        var hasReferenceAnchor = trackingContext is
        {
            ReferenceEchoTime: not null,
            ReferenceEnvTime: not null
        };

        var predictedIndex = envelopeFront.FrontIndex;
        int searchStart;
        int searchEnd;
        if (hasReferenceAnchor)
        {
            var currentEnvelopeTimeNs = trackingContext!.EchoStart +
                                        envelopeFront.FrontIndex * signalInterval;
            var predictedEchoTimeNs = trackingContext.ReferenceEchoTime!.Value +
                                      currentEnvelopeTimeNs - trackingContext.ReferenceEnvTime!.Value;
            predictedIndex = (predictedEchoTimeNs - trackingContext.EchoStart) / signalInterval;
            // 整数窗口只用于限制遍历范围。Ceiling 可能把 0.45T 扩大到 0.5T，
            // 最终候选还要在下方按小数距离严格过滤，避免正好位于相邻周期中点时偏向前一周期。
            var halfWindowPoints = Convert.ToInt32(Math.Ceiling(periodPoints * TrackingSearchHalfPeriodRatio));
            searchStart = Convert.ToInt32(Math.Floor(predictedIndex)) - halfWindowPoints;
            searchEnd = Convert.ToInt32(Math.Ceiling(predictedIndex)) + halfWindowPoints + 1;
        }
        else
        {
            // 首次/旧项目没有包络锚点时，在当前主回波附近捕获包络前沿之后的上升沿。
            var paddingPoints = Convert.ToInt32(Math.Ceiling(periodPoints));
            searchStart = envelopeFront.MainEcho.Start - paddingPoints;
            searchEnd = envelopeFront.MainEcho.End + paddingPoints;
        }

        var risingCrossings = FindQualifiedRisingZeroCrossings(envelopeFront, searchStart, searchEnd,
            periodPoints, noiseBase).ToList();
        if (risingCrossings.Count == 0)
        {
            return (short)MeasStatusCode.SimilarityLow;
        }

        decimal selectedCrossing;
        if (hasReferenceAnchor)
        {
            // 已有 F0/Z0 时继续围绕位移预测点选最近的同向过零点，避免包络点轻微抖动造成整周期跳变。
            // 必须使用未取整的 0.45T 再过滤一次；整数搜索窗口因 Ceiling 扩大时，
            // 不允许距离为 0.5T 的左右候选进入比较，否则等距时会固定偏向较早的过零点。
            var maxTrackingOffsetPoints = Convert.ToDecimal(periodPoints * TrackingSearchHalfPeriodRatio);
            var trackingCandidates = risingCrossings
                .Where(index => Math.Abs(index - predictedIndex) <= maxTrackingOffsetPoints)
                .ToList();
            if (trackingCandidates.Count == 0)
            {
                return (short)MeasStatusCode.SimilarityLow;
            }

            selectedCrossing = trackingCandidates.MinBy(index => Math.Abs(index - predictedIndex));
        }
        else
        {
            // 首次扫波只接受包络粗定位点之后的第一个上升沿。包络前沿受平滑和阈值位置影响，
            // F 到首个上升沿可能略大于 0.8T，因此放宽到 1.05T；仍然只取第一个合格点，
            // 不会越过它去选择更后的周期。正式测量/监测的 ±0.45T 跟踪窗口保持不变。
            var maxInitialOffsetPoints = Convert.ToDecimal(periodPoints * InitialSearchMaxPeriodRatio);
            var firstCrossingAfterEnvelope = risingCrossings
                .Where(index => index >= predictedIndex && index - predictedIndex <= maxInitialOffsetPoints)
                .OrderBy(index => index)
                .Cast<decimal?>()
                .FirstOrDefault();
            if (!firstCrossingAfterEnvelope.HasValue)
            {
                return (short)MeasStatusCode.SimilarityLow;
            }

            selectedCrossing = firstCrossingAfterEnvelope.Value;
        }
        assist.Zx = selectedCrossing;
        return Convert.ToDouble(selectedCrossing);
    }

    /// <summary>
    /// 解析自动法使用的载波周期。稳定参考周期用于保持监测窗口不随单帧噪声抖动；
    /// 当前主回波实测周期用于新扫波和旧项目自适应；探头频率只作为可靠兜底。
    /// </summary>
    private static decimal? ResolveCarrierPeriodNs(EnvelopeFrontResult envelopeFront, decimal signalInterval,
        EnvelopeTrackingContext? trackingContext)
    {
        decimal? nominalPeriodNs = trackingContext?.ProbeFreqMhz is > 0
            ? 1000M / trackingContext.ProbeFreqMhz.Value
            : null;

        // 已置零或已有稳定扫波锚点时，优先保持 T0。若探头频率后来发生明显变化，
        // 旧 T0 会因超出名义周期允许范围而被拒绝，避免沿用错误搜索窗口。
        if (trackingContext?.ReferencePeriodNs is > 0 &&
            IsPeriodPlausible(trackingContext.ReferencePeriodNs.Value, nominalPeriodNs, signalInterval))
        {
            return trackingContext.ReferencePeriodNs.Value;
        }

        if (TryEstimateCarrierPeriodNs(envelopeFront, signalInterval, nominalPeriodNs, out var measuredPeriodNs))
        {
            return measuredPeriodNs;
        }

        return nominalPeriodNs is > 0 ? nominalPeriodNs : null;
    }

    /// <summary>
    /// 从主回波高能量区的相邻上升沿过零间距估算实际载波周期。
    /// 使用中位数和 MAD 剔除噪声过零、漏周期和局部畸变；至少需要三个稳定周期间距。
    /// </summary>
    private static bool TryEstimateCarrierPeriodNs(EnvelopeFrontResult envelopeFront, decimal signalInterval,
        decimal? nominalPeriodNs, out decimal periodNs)
    {
        periodNs = 0;
        if (signalInterval <= 0)
        {
            return false;
        }

        var waveform = envelopeFront.Waveform;
        var start = Math.Clamp(envelopeFront.MainEcho.Start, 0, Math.Max(0, waveform.Length - 1));
        var end = Math.Clamp(envelopeFront.MainEcho.End, start, waveform.Length);
        if (end - start < 3)
        {
            return false;
        }

        var minEnvelope = envelopeFront.EnvelopeMax * PeriodEnvelopeRatio;
        var risingCrossings = new List<double>();
        for (var index = start; index < end - 1; index++)
        {
            if (!IsPositiveZeroCrossing(waveform[index], waveform[index + 1]))
            {
                continue;
            }

            // 只使用主回波高能量部分，避免衰减尾部和区域边界的噪声过零污染周期估计。
            var envelopeIndex = Math.Clamp(index, 0, envelopeFront.SmoothedEnvelope.Length - 1);
            if (envelopeFront.SmoothedEnvelope[envelopeIndex] < minEnvelope)
            {
                continue;
            }

            risingCrossings.Add(Convert.ToDouble(
                CalcZeroCrossingIndex(index, waveform[index], index + 1, waveform[index + 1])));
        }

        if (risingCrossings.Count < MinMeasuredPeriodIntervals + 1)
        {
            return false;
        }

        var intervals = risingCrossings.Zip(risingCrossings.Skip(1), (left, right) => right - left)
            .Where(interval => interval >= 2d)
            .ToList();
        if (nominalPeriodNs is > 0)
        {
            var nominalPoints = Convert.ToDouble(nominalPeriodNs.Value / signalInterval);
            intervals = intervals
                .Where(interval => interval >= nominalPoints * Convert.ToDouble(MinNominalPeriodRatio) &&
                                   interval <= nominalPoints * Convert.ToDouble(MaxNominalPeriodRatio))
                .ToList();
        }
        if (intervals.Count < MinMeasuredPeriodIntervals)
        {
            return false;
        }

        var initialMedian = intervals.Median();
        var initialMad = intervals.Select(interval => Math.Abs(interval - initialMedian)).Median();
        var outlierTolerance = Math.Max(initialMedian * 0.30d, initialMad * 3d);
        var stableIntervals = intervals
            .Where(interval => Math.Abs(interval - initialMedian) <= outlierTolerance)
            .ToArray();
        if (stableIntervals.Length < MinMeasuredPeriodIntervals)
        {
            return false;
        }

        var stableMedian = stableIntervals.Median();
        var stableMad = stableIntervals.Select(interval => Math.Abs(interval - stableMedian)).Median();
        if (!(stableMedian > 0) || stableMad / stableMedian > MaxPeriodMadRatio)
        {
            return false;
        }

        var measuredPeriodNs = Convert.ToDecimal(stableMedian) * signalInterval;
        if (!IsPeriodPlausible(measuredPeriodNs, nominalPeriodNs, signalInterval))
        {
            return false;
        }

        periodNs = measuredPeriodNs;
        return true;
    }

    private static bool IsPeriodPlausible(decimal periodNs, decimal? nominalPeriodNs, decimal signalInterval)
    {
        if (periodNs < signalInterval * 2)
        {
            return false;
        }

        return nominalPeriodNs is not > 0 ||
               periodNs >= nominalPeriodNs.Value * MinNominalPeriodRatio &&
               periodNs <= nominalPeriodNs.Value * MaxNominalPeriodRatio;
    }

    private static IEnumerable<decimal> FindQualifiedRisingZeroCrossings(EnvelopeFrontResult envelopeFront,
        int searchStart, int searchEnd, double periodPoints, double noiseBase)
    {
        var waveform = envelopeFront.Waveform;
        var start = Math.Clamp(searchStart, 0, Math.Max(0, waveform.Length - 1));
        var end = Math.Clamp(searchEnd, start, waveform.Length);
        var excursionWindow = Math.Max(2, Convert.ToInt32(Math.Ceiling(periodPoints * 0.30d)));
        var minExcursion = Math.Max(1d, Math.Min(noiseBase * 0.30d, envelopeFront.EnvelopeMax * 0.06d));
        var minEnvelope = envelopeFront.EnvelopeMax * 0.05d;

        for (var index = start; index < end - 1; index++)
        {
            if (!IsPositiveZeroCrossing(waveform[index], waveform[index + 1]))
            {
                continue;
            }

            var envelopeIndex = Math.Clamp(index, 0, envelopeFront.SmoothedEnvelope.Length - 1);
            if (envelopeFront.SmoothedEnvelope[envelopeIndex] < minEnvelope)
            {
                continue;
            }

            var localStart = Math.Max(0, index - excursionWindow);
            var localEnd = Math.Min(waveform.Length, index + excursionWindow + 2);
            var negativeExcursion = waveform.Skip(localStart).Take(index - localStart + 1).Min();
            var positiveExcursion = waveform.Skip(index + 1).Take(localEnd - index - 1).Max();
            if (negativeExcursion > -minExcursion || positiveExcursion < minExcursion)
            {
                continue;
            }

            yield return CalcZeroCrossingIndex(index, waveform[index], index + 1, waveform[index + 1]);
        }
    }

    private static EchoRegion SelectMainEchoRegion(IReadOnlyList<EchoRegion> regions,
        IReadOnlyList<double> smoothEnergy)
    {
        var regionEnergies = regions.Select(region => smoothEnergy.Skip(region.Start)
            .Take(Math.Max(1, region.End - region.Start)).Sum()).ToList();
        var maxEnergy = regionEnergies.Count == 0 ? 0 : regionEnergies.Max();
        var waveformSpan = Math.Max(1, smoothEnergy.Count);
        var bestIndex = 0;
        var bestScore = double.MinValue;
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            var energyScore = maxEnergy <= 0 ? 0 : regionEnergies[i] / maxEnergy;
            var cycleScore = Math.Min(1d, region.NCycles / 3d);
            var earlyScore = 1d - Math.Clamp(region.Start / (double)waveformSpan, 0d, 1d);
            var score = 0.50d * energyScore + 0.30d * cycleScore + 0.20d * earlyScore;
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }
        return regions[bestIndex];
    }

    /// <summary>
    /// 计算回波时间索引(计算过零含小数索引)-动态特征
    /// -1 表示信号过小
    /// -2 阈值异常
    /// </summary>
    /// <param name="signal">信号</param>
    /// <param name="signalInterval">信号点间隔(ns)</param>
    /// <param name="assist">回波辅助信息</param>
    /// <param name="consecutive"></param>
    /// <param name="noiseBase">噪声基线</param>
    /// <param name="dynThr">小数阈值小于0.5</param>
    public static double CalcZeroIndexByEnv(List<double> signal, decimal signalInterval, out EchoAssist assist, 
        int consecutive = 20, double noiseBase = 50d, double dynThr = 0.3d)
    {
        assist = new EchoAssist();
        // 动态特征法保持原有“第一个有效主回波 + 30% 包络前沿”的选择语义；
        // 只把纯包络定位步骤抽出，供新的自动法复用。
        var locateStatus = TryLocateEnvelopeFront(signal, signalInterval, consecutive, noiseBase,
            finThreshold: 0.08d, preferFirstValidRegion: true, out var envelopeFront, dynThr);
        if (locateStatus < 0)
        {
            return locateStatus;
        }

        assist.Mx = envelopeFront.FrontIndex;
        assist.My = envelopeFront.Threshold;
        assist.Sx = envelopeFront.MainEcho.Start;
        assist.Ex = envelopeFront.MainEcho.End;
        // 动态特征法仍直接返回包络过阈值点，不改变原有结果定义。
        return Convert.ToDouble(envelopeFront.FrontIndex);
    }

    private static int TryLocateEnvelopeFront(List<double> signal, decimal signalInterval, int consecutive,
        double noiseBase, double finThreshold, bool preferFirstValidRegion,
        out EnvelopeFrontResult result, double dynThr = 0.3d)
    {
        result = default;
        if (signal.Count < 3 || !signal.Any(value => Math.Abs(value) >= noiseBase))
        {
            return -1;
        }

        var dcEnd = Math.Min(signal.Count, Math.Max(40, signal.Count / 10));
        var dcMean = signal.Take(dcEnd).Average();
        var waveform = signal.Select(value => value - dcMean).ToArray();
        var regions = SignalProcessor.DetectRegions(waveform, 0.25d, finThreshold, minWidth: consecutive)
            .Where(region => region.End > region.Start && region.Start >= 0 && region.End <= waveform.Length)
            .ToList();
        if (regions.Count == 0)
        {
            return -5;
        }

        EchoRegion mainEcho;
        if (preferFirstValidRegion)
        {
            mainEcho = regions.FirstOrDefault(region => region.NCycles >= 3) ?? regions.First();
        }
        else
        {
            var energy = waveform.Select(value => value * value).ToArray();
            var smoothEnergy = SavitzkyGolayFilter.Smooth(energy);
            mainEcho = SelectMainEchoRegion(regions, smoothEnergy);
        }

        var envelope = SignalProcessor.HilbertEnvelope(waveform);
        var smoothedEnvelope = SavitzkyGolayFilter.Smooth(envelope);
        var mainEchoData = smoothedEnvelope.Skip(mainEcho.Start).Take(mainEcho.End - mainEcho.Start).ToArray();
        if (mainEchoData.Length == 0)
        {
            return -5;
        }

        var envelopeMax = mainEchoData.Max();
        if (envelopeMax <= 0)
        {
            return -5;
        }
        var threshold = envelopeMax * dynThr;
        var regionCrossing = FindForwardThresholdCrossing(mainEchoData, threshold);
        if (regionCrossing < 0)
        {
            return -5;
        }

        var thresholdCrossing = regionCrossing + mainEcho.Start;
        var usSpan = Math.Max(1, Convert.ToInt32(1000M / signalInterval));
        var usStartIndex = Math.Max(0, thresholdCrossing / usSpan * usSpan);
        var usEndIndex = usStartIndex + 2 * usSpan;
        if (usEndIndex >= smoothedEnvelope.Length)
        {
            // 保持原动态特征法的末端取样边界语义。
            usEndIndex = smoothedEnvelope.Length - 1;
        }
        if (usEndIndex - usStartIndex < 2)
        {
            return -5;
        }

        var rangeSignal = smoothedEnvelope.Skip(usStartIndex).Take(usEndIndex - usStartIndex).ToList();
        var calculatedSignal = SignalProcessor.InterpolateAndResample(rangeSignal, signalInterval, CalculationInterval);
        var calculatedCrossing = FindForwardThresholdCrossing(calculatedSignal.ToArray(), threshold);
        if (calculatedCrossing < 0)
        {
            return -5;
        }

        var frontIndex = usStartIndex + calculatedCrossing * CalculationInterval / signalInterval;
        result = new EnvelopeFrontResult(waveform, smoothedEnvelope, mainEcho, frontIndex,
            Convert.ToDecimal(threshold), envelopeMax);
        return 0;
    }
    /// <summary>
    /// 计算回波时间索引(计算过零含小数索引)-波谷法
    /// -1 表示信号过小
    /// </summary>
    /// <param name="signal">信号</param>
    /// <param name="assist">回波辅助信息</param>
    /// <param name="noiseBase">噪声基线</param>
    public static double CalcZeroIndexByAutoValley(List<double> signal, out EchoAssist assist, double noiseBase = 10d)
    {
        assist = new EchoAssist();
        //1. 根据幅值判断信号是否有效
        var isValidSignal = signal.Any(s => Math.Abs(s) >= noiseBase);
        if (!isValidSignal)
        {
            return -1;//无效信号状态码
        }
        //2. 找最小波谷,然后找上升沿过零点
        var (minIndex,_) = FindMinMax(signal.ToArray());
        assist.Mx = minIndex; 
        assist.My = (decimal) signal[minIndex];
        var zeroIndexTuple = FindPositiveZeroCrossing(signal.ToArray(), minIndex);
        var calcZeroIdx = Convert.ToDecimal(zeroIndexTuple.Item1);
        if (zeroIndexTuple.Item1 >= 0 && zeroIndexTuple.Item1 + 1 < signal.Count)
        {
            var calcPrevIdx = zeroIndexTuple.Item1;
            var calcCurrentIdx = zeroIndexTuple.Item1 + 1;
            calcZeroIdx = CalcZeroCrossingIndex(calcPrevIdx, signal[calcPrevIdx], calcCurrentIdx,signal[calcCurrentIdx]);
        }
        assist.Zx = calcZeroIdx;
        return Convert.ToDouble(calcZeroIdx);
    }
    /// <summary>
    /// 计算回波时间索引(计算过零含小数索引)-位置法
    /// -1 表示信号过小
    /// </summary>
    /// <param name="signal">信号</param>
    /// <param name="posIndex">信号</param>
    /// <param name="assist">回波辅助信息</param>
    /// <param name="noiseBase">噪声基线</param>
    public static double CalcZeroIndexByPosition(List<double> signal,int posIndex, out EchoAssist assist, double noiseBase = 10d)
    {
        assist = new EchoAssist();
        //1. 根据幅值判断信号是否有效
        var isValidSignal = signal.Any(s => Math.Abs(s) >= noiseBase);
        if (!isValidSignal)
        {
            return -1;//无效信号状态码
        }
        //2. 根据位置找上升沿过零点
        if (posIndex > 0)
        {
            assist.Mx = posIndex; 
            assist.My = (decimal) signal[posIndex];
        }

        var zeroIndexTuple = FindPositiveZeroCrossing(signal.ToArray(), posIndex);
        var calcZeroIdx = Convert.ToDecimal(zeroIndexTuple.Item1);
        if (zeroIndexTuple.Item1 >= 0 && zeroIndexTuple.Item1 + 1 < signal.Count)
        {
            var calcPrevIdx = zeroIndexTuple.Item1;
            var calcCurrentIdx = zeroIndexTuple.Item1 + 1;
            calcZeroIdx = CalcZeroCrossingIndex(calcPrevIdx, signal[calcPrevIdx], calcCurrentIdx,signal[calcCurrentIdx]);
        }
        assist.Zx = calcZeroIdx;
        return Convert.ToDouble(calcZeroIdx);
    }
    /// <summary>
    /// 计算回波时间索引(计算过零含小数索引)-阈值法
    /// -1 表示信号过小
    /// -2 阈值异常
    /// </summary>
    /// <param name="signal">信号</param>
    /// <param name="assist">回波辅助信息</param>
    /// <param name="noiseBase">噪声基线</param>
    /// <param name="threshold">阈值</param>
    public static double CalcZeroIndexBySimpleThr(List<double> signal, out EchoAssist assist ,double noiseBase = 10d, short threshold = 0)
    {
        assist = new EchoAssist();
        //1. 根据幅值判断信号是否有效
        var isValidSignal = signal.Any(s => Math.Abs(s) >= noiseBase);
        if (!isValidSignal)
        {
            return -1;//无效信号状态码
        }
        //2. 找过阈值索引
        var edgeType = threshold < 0 ? ThresholdEdgeType.Falling : ThresholdEdgeType.Rising;
        //2. 在阈值前后找 时间轴正向和反向过零点 将反向到正向过零点之间信号取出进行插值
        var thrCrossIdx = FindForwardThresholdCrossing(signal.ToArray(), threshold, edgeType);
        if (thrCrossIdx < 0)
        {
            return -2;//阈值异常 波形比阈值小
        }
        assist.Mx = thrCrossIdx;
        assist.My = (decimal) signal[thrCrossIdx];
        var zeroIndexTuple = FindPositiveZeroCrossing(signal.ToArray(), thrCrossIdx);
        var calcZeroIdx = Convert.ToDecimal(zeroIndexTuple.Item1);
        if (zeroIndexTuple.Item1 >= 0 && zeroIndexTuple.Item1 + 1 < signal.Count)
        {
            var calcPrevIdx = zeroIndexTuple.Item1;
            var calcCurrentIdx = zeroIndexTuple.Item1 + 1;
            calcZeroIdx = CalcZeroCrossingIndex(calcPrevIdx, signal[calcPrevIdx], calcCurrentIdx,signal[calcCurrentIdx]);
        }
        assist.Zx = calcZeroIdx;
        return Convert.ToDouble(calcZeroIdx);
    }
    /// <summary>
    /// 计算首个过阈值的显著极值点索引（正阈值 → 波峰，负阈值 → 波谷）
    /// -1: 无有效信号（输入为空）
    /// -2: 阈值异常（信号未越过阈值）
    /// -3: 越过阈值但未找到有效极值点（如单调信号）
    /// 返回值: 极值点索引（double，当前返回整数，预留亚采样插值扩展）
    /// </summary>
    public static double CalcFirstPeakIndexBySimpleThr(List<double>? signal, short threshold = 0)
    {
        if (signal == null || signal.Count == 0)
            return -1; // 无信号

        // 确定检测方向：正阈值 → 上升沿 + 找波峰；负阈值 → 下降沿 + 找波谷
        var isFindingPeak = threshold >= 0;
        var edgeType = isFindingPeak ? ThresholdEdgeType.Rising : ThresholdEdgeType.Falling;

        // 查找首次越过阈值的位置
        var thrCrossIdx = FindForwardThresholdCrossing(signal.ToArray(), threshold, edgeType);
        if (thrCrossIdx < 0)
            return -2; // 未越过阈值

        // 从 crossing 点之后开始搜索极值点（至少需要 i-1, i, i+1）
        for (var i = thrCrossIdx + 1; i < signal.Count - 1; i++)
        {
            var prev = signal[i - 1];
            var curr = signal[i];
            var next = signal[i + 1];

            if (isFindingPeak)
            {
                // 波峰条件：prev < curr 且 curr >= next（允许平台右端）
                if (curr > prev && curr >= next)
                {
                    return i;
                }
            }
            else
            {
                // 波谷条件：prev > curr 且 curr <= next（允许平台右端）
                if (curr < prev && curr <= next)
                {
                    return i;
                }
            }
        }
        // 越过阈值但未找到符合要求的极值点
        return -3;
    }
    /// <summary>
    /// 时间轴正向查找过阈值索引（支持上升沿和下降沿）
    /// </summary>
    /// <param name="signal">信号数组</param>
    /// <param name="threshold">阈值</param>
    /// <param name="edgeType">边沿类型：上升沿 / 下降沿</param>
    /// <param name="startIndex">开始查找索引</param>
    /// <returns>满足边沿条件的索引，未找到返回 -1</returns>
    public static int FindForwardThresholdCrossing(double[] signal, double threshold, ThresholdEdgeType edgeType = ThresholdEdgeType.Rising, int startIndex = 0)
    {
        var currentIndex = startIndex + 1;
        while (currentIndex < signal.Length)
        {
            var prev = signal[currentIndex - 1];
            var curr = signal[currentIndex];
            if (edgeType == ThresholdEdgeType.Rising && prev < curr && curr >= threshold)
            {
                return currentIndex;
            }
            else if (edgeType == ThresholdEdgeType.Falling && prev > curr && curr <= threshold)
            {
                return currentIndex;
            }
            currentIndex++;
        }
        return -1;
    }
    /// <summary>
    /// 时间轴反向查找过阈值索引（支持上升沿和下降沿）
    /// </summary>
    /// <param name="signal">信号数组</param>
    /// <param name="threshold">阈值</param>
    /// <param name="edgeType">边沿类型：上升沿 / 下降沿</param>
    /// <param name="startIndex">开始查找索引</param>
    /// <returns>满足边沿条件的索引，未找到返回 -1</returns>
    public static int FindBackwardThresholdCrossing(double[] signal, double threshold, ThresholdEdgeType edgeType = ThresholdEdgeType.Rising, int startIndex = -1)
    {
        // 如果startIndex为-1或超出范围，则从数组末尾开始
        var currentIndex = (startIndex == -1 || startIndex >= signal.Length) ? signal.Length - 1 : startIndex;
    
        // 确保索引在有效范围内
        currentIndex = Math.Min(currentIndex, signal.Length - 1);
        currentIndex = Math.Max(currentIndex, 0); // 至少需要从0开始
    
        while (currentIndex > 0)  // 需要前一个点进行比较
        {
            var curr = signal[currentIndex];
            var next = signal[currentIndex - 1];  // 向前看
        
            if (edgeType == ThresholdEdgeType.Rising && curr < threshold && next >= threshold)
            {
                return currentIndex - 1;  // 返回cross索引
            }
            else if (edgeType == ThresholdEdgeType.Falling && curr > threshold && next <= threshold)
            {
                return currentIndex - 1;  // 返回cross索引
            }
            currentIndex--;
        }
        return -1;
    }
    /// <summary>
    /// 查找上升沿=过零点索引,返回时间轴正向和时间轴反向
    /// </summary>
    /// <param name="signal">信号</param>
    /// <param name="startIndex">查找开始索引</param>
    public static Tuple<int, int> FindPositiveZeroCrossing(double[] signal, int startIndex = 0)
    {
        //时间轴正向
        var forwardZeroIdx = -1;
        var forwardIndex = startIndex + 1;
        while (forwardIndex < signal.Length - 1)
        {
            if (IsPositiveZeroCrossing(signal[forwardIndex], signal[forwardIndex + 1]))
            {
                forwardZeroIdx = forwardIndex;
                break;
            }
            forwardIndex++;
        }
        //时间轴反向
        var backwardZeroIdx = -1;
        var backwardIndex = startIndex - 1;
        while (backwardIndex > 0)
        {
            if (IsPositiveZeroCrossing(signal[backwardIndex - 1], signal[backwardIndex]))
            {
                backwardZeroIdx = backwardIndex;
                break;
            }
            backwardIndex--;
        }
        return Tuple.Create(forwardZeroIdx, backwardZeroIdx);
    }
    /// <summary>
    /// 上升过零点判断
    /// </summary>
    private static bool IsPositiveZeroCrossing(double prevValue, double currentValue)
    {
        return prevValue < 0 && currentValue >= 0;
    }
    /// <summary>
    /// 线性计算获取过零点索引
    /// </summary>
    /// <param name="x1">前一点索引</param>
    /// <param name="y1">前一点值</param>
    /// <param name="x2">后一点索引</param>
    /// <param name="y2">后一点值</param>
    private static decimal CalcZeroCrossingIndex(int x1, double y1, int x2, double y2)
    {
        // 检查是否存在过零条件
        if (Math.Abs(y1 - y2) < double.Epsilon)
        {
            return x1; // 如果两点几乎重合，则返回第一个点的索引
        }
        // 线性插值公式
        var interpolatedIndex = (x1 + (0 - y1) * ((x2 - x1) / (y2 - y1)));
        return Convert.ToDecimal(interpolatedIndex);
    }
    /// <summary>
    /// 获取同步时间(2000年1月1日之后的天数)
    /// </summary>
    /// <param name="now">当前时间</param>
    public static Tuple<uint, uint> GetSyncTimeBase2000(DateTime now)
    {
        var startDate = new DateTime(2000, 1, 1);
        var currentDate = new DateTime(now.Year, now.Month, now.Day);

        var syncDays = Convert.ToUInt32(Math.Floor((currentDate - startDate).TotalDays));
        var syncSecs = Convert.ToUInt32(now.Hour * 3600 + now.Minute * 60 + now.Second);
        return new Tuple<uint, uint>(syncDays, syncSecs);
    }
    /// <summary>
    /// 在趋势序列中查找第一个满足条件的索引
    /// </summary>
    /// <param name="data">有序数据集（建议提前排序）</param>
    /// <param name="startIndex">起始搜索索引</param>
    /// <param name="targetValue">目标比较值</param>
    /// <param name="searchForward">true: 向前搜索(递减索引), false: 向后搜索(递增索引)</param>
    /// <param name="isRisingTrend">true: 上升趋势中查找突破点, false: 下降趋势查找支撑点</param>
    /// <returns>找到的索引，未找到返回-1</returns>
    public static int FindTrendIndex(IReadOnlyList<double> data, int startIndex, double targetValue, 
        bool searchForward = false, bool isRisingTrend = true)
    {
        // 参数校验
        if (data.Count == 0)
            return -1;
    
        if (startIndex < 0 || startIndex >= data.Count)
            throw new ArgumentOutOfRangeException(nameof(startIndex), 
                $"Start index must be between 0 and {data.Count - 1}");

        // 确定搜索方向和终止条件
        var step = searchForward ? -1 : 1;
        var endIndex = searchForward ? -1 : data.Count;
    
        // 根据趋势类型创建比较策略
        var condition = isRisingTrend 
            ? searchForward 
                ? (Func<double, bool>)(v => v <= targetValue)  // 上升趋势前向找支撑
                : v => v >= targetValue                        // 上升趋势后向找突破
            : searchForward 
                ? v => v >= targetValue                        // 下降趋势前向找阻力
                : v => v <= targetValue;                       // 下降趋势后向找支撑

        // 执行趋势扫描
        for (var i = startIndex; i != endIndex; i += step)
        {
            if (condition(data[i]))
                return i;
        }
        return -1;
    }
    /// <summary>
    /// 获取数据范围内的最大和最小值索引
    /// </summary>
    public static (int minIndex, int maxIndex) FindMinMax(double[] data, int startIndex = 0, int endIndex = -1)
    {
        if (data == null || data.Length == 0)
            throw new ArgumentException("Data array cannot be null or empty");
        
        endIndex = endIndex >= 0 ? endIndex : data.Length - 1;
        
        // 严格校验索引范围
        if (startIndex < 0 || endIndex >= data.Length || startIndex > endIndex)
            throw new ArgumentOutOfRangeException(
                $"Invalid range: start={startIndex}, end={endIndex}, data.Length={data.Length}");
        
        var lockObj = new object();
        int minIndex = startIndex, maxIndex = startIndex;

        Parallel.For(startIndex, endIndex + 1,
            () => (minIndex: -1, maxIndex: -1, minVal: double.MaxValue, maxVal: double.MinValue),
            (i, _, local) =>
            {
                var current = data[i]; 
                // 更新本地最小值及索引
                if (current < local.minVal || (Math.Abs(current - local.minVal) < double.Epsilon && i < local.minIndex))
                {
                    local.minVal = current;
                    local.minIndex = i;
                }
                // 更新本地最大值及索引
                if (current > local.maxVal || (Math.Abs(current - local.maxVal) < double.Epsilon && i > local.maxIndex))
                {
                    local.maxVal = current;
                    local.maxIndex = i;
                }
                return local;
            },
            local =>
            {
                if (local.minIndex == -1) return; // 忽略未初始化线程
                lock (lockObj)
                {
                    // 合并最小值（优先较小索引）
                    if (local.minVal < data[minIndex] || 
                        (local.minVal == data[minIndex] && local.minIndex < minIndex))
                    {
                        minIndex = local.minIndex;
                    }
                    // 合并最大值（优先较大索引）
                    if (local.maxVal > data[maxIndex] || 
                        (local.maxVal == data[maxIndex] && local.maxIndex > maxIndex))
                    {
                        maxIndex = local.maxIndex;
                    }
                }
            });

        return (minIndex, maxIndex);
    }
    /// <summary>
    /// 计算截尾中位数：对于小样本固定去掉一个最小值和一个最大值；
    /// 对于大样本（Count >= 10）则按 trimPercent 比例截尾。
    /// <param name="source">数据源</param>
    /// <param name="trimPercent">截尾比例（0.0 ~ 0.5），仅在数据点 >= 10 时生效</param>
    /// <returns>中位数，若数据不足则返回 null</returns>
    /// </summary>
    public static double? TrimmedMedian(this IEnumerable<double> source, double trimPercent = 0.1)
    {
        var list = source.ToList();
        if (list.Count == 0) return null;
        
        List<double> trimmedList;
        switch (list.Count)
        {
            case <= 3:
                // 数据太少，无法有效截尾，直接返回普通中位数
                trimmedList = list;
                break;
            case < 10:
            {
                // 小样本：固定去掉一个最小值和一个最大值
                var sorted = list.OrderBy(x => x).ToList();
                trimmedList = sorted.Skip(1).Take(sorted.Count - 2).ToList();
                break;
            }
            default:
            {
                // 大样本：使用比例截尾
                trimPercent = Math.Max(0.0, Math.Min(0.5, trimPercent));
                var totalTrim = (int)(list.Count * trimPercent);
                var removeCount = Math.Min(totalTrim, list.Count / 2);
                if (removeCount * 2 >= list.Count)
                {
                    trimmedList = list; // 退化为普通中位数
                }
                else
                {
                    var sorted = list.OrderBy(x => x).ToList();
                    trimmedList = sorted.Skip(removeCount).Take(sorted.Count - 2 * removeCount).ToList();
                }
                break;
            }
        }
        return trimmedList.Median();
    }
    /// <summary>
    /// 基于 3σ 原则剔除一次异常值后，计算新的均值和样本标准差。
    /// 仅执行单次过滤：保留 [μ - 3σ, μ + 3σ] 范围内的数据，然后重新计算统计量。
    /// </summary>
    /// <param name="samples">输入样本数据（不应为 null）</param>
    /// <returns>(Mean, StandardDeviation)：过滤后的均值和标准差；若过滤后数据不足，则回退到原始统计量</returns>
    public static (double Mean, double StandardDeviation) TrimmedMeanStdBy3Sigma(this IEnumerable<double> samples)
    {
        if (samples == null)
            throw new ArgumentNullException(nameof(samples));

        var list = samples.ToList();
        if (list.Count == 0)
            throw new ArgumentException("Sample collection is empty.", nameof(samples));

        // 原始统计量
        var (originalMean, originalStd) = list.MeanStandardDeviation();

        // 特殊情况：单点或标准差为0
        if (list.Count == 1 || originalStd == 0)
            return (originalMean, originalStd);

        // 3σ 范围
        var lower = originalMean - 3 * originalStd;
        var upper = originalMean + 3 * originalStd;

        // 单次过滤
        var filtered = list.Where(x => x >= lower && x <= upper).ToList();

        // 若过滤后数据太少，回退到原始结果
        if (filtered.Count < 2)
            return (originalMean, originalStd);

        // 返回基于过滤后数据的新统计量
        return filtered.MeanStandardDeviation();
    }
}
/// <summary>
/// 曲线趋势
/// </summary>
public enum ThresholdEdgeType
{
    Rising,   // 上升
    Falling   // 下降
}
/// <summary>
/// 信号质量(信噪比db)
/// </summary>
public static class SignalQualityLevel
{
    /// <summary>
    /// 优秀信号质量 - 活塞波尖锐突出，噪声几乎看不见
    /// </summary>
    public const double Excellent = 30.0d;
    /// <summary>
    /// 良好信号质量 - 信号清晰，噪声轻微波动
    /// </summary>
    public const double Good = 20.0d;
    /// <summary>
    /// 一般信号质量 - 信号明显，但上升沿/下降沿有抖动
    /// </summary>
    public const double Fair = 10.0d;
    /// <summary>
    /// 较差信号质量 - 信号可见，但顶部可能分裂或波动
    /// </summary>
    public const double Poor = 6.0d;
}
