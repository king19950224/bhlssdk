using System.Numerics;
using MathNet.Filtering;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using MathNet.Numerics.Interpolation;
using MathNet.Numerics.Statistics;

namespace FxSDK.Utility;

/// <summary>
/// 回波区域参数封装
/// </summary>
public record EchoRegion(
    int Start,//开始索引
    int End,//结束索引
    double NCycles,//周期数
    double PeakEnergy
);

/// <summary>
/// 信号处理工具类
/// </summary>
public static class SignalProcessor
{
    private const int FilterOrder = 21; //默认滤波阶数(奇数-FIR对称)
    /// <summary>
    /// 对给定的数据进行插值处理
    /// 2025年11月21日修改为先计算重采样后的点数,然后进行重采样；
    /// </summary>
    /// <param name="originalData">原始数据点</param>
    /// <param name="originalInterval">原始采样间隔(ns)</param>
    /// <param name="newInterval">新的采样间隔(ns)</param>
    /// <returns>插值后的新数据列表</returns>
    public static List<double> InterpolateAndResample(List<double> originalData, 
        decimal originalInterval, decimal newInterval)
    {
        return CubicResampleWithPadding(originalData, originalInterval, newInterval);
    }
    /// <summary>
    /// 三次样条插值(镜像延拓)
    /// </summary>
    private static List<double> CubicResampleWithPadding(List<double> originalData, 
        decimal originalInterval, decimal newInterval)
    {
        var extendedData = new List<double>();
        // --- 使用镜像延拓 ---
        // 1. 头部：补一个“第二个点”的值 (对称)
        extendedData.Add(originalData[1]);
        // 2. 加入原始数据
        extendedData.AddRange(originalData);
        // 3. 尾部：补一个“倒数第二个点”的值 (对称)
        extendedData.Add(originalData[^2]);

        // --- 坐标构建 ---
        var xValues = new List<double>();
        for (var i = 0; i < extendedData.Count; i++)
        {
            xValues.Add((i - 1) * (double)originalInterval); 
        }

        // --- 插值 ---
        var interpolator = CubicSpline.InterpolateNatural(xValues, extendedData);

        // --- 还原 ---
        var newPointCount = (int)Math.Ceiling(originalData.Count * originalInterval / newInterval);
        var interpolatedData = new List<double>();

        var x = 0d; 
        for (var i = 0; i < newPointCount; i++)
        {
            var y = interpolator.Interpolate(x);
            interpolatedData.Add(y);
            x += (double)newInterval;
        }

        return interpolatedData;
    }
    /// <summary>
    /// 使用线性插值将非等间隔的x-y数据重采样为等间隔数据
    /// </summary>
    /// <param name="rawXs">原始x数组 (X轴，必须单调递增)</param>
    /// <param name="rawYs">原始y数组 (Y轴)</param>
    /// <param name="targetInterval">目标x间隔 (例如 0.1 表示每 0.1 采一个点)</param>
    /// <returns>重采样后的y数组</returns>
    public static (double[] xs, double[] ys) LinearResample(double[] rawXs, double[] rawYs, double targetInterval)
    {
        // 1. 数据校验与清洗
        if (rawXs.Length != rawYs.Length) throw new ArgumentException("X和Y数组长度必须一致");
        
        // 去除可能的 NaN 或无穷大 (防止插值报错)
        var validIndices = new List<int>();
        for (var i = 0; i < rawXs.Length; i++)
        {
            if (!double.IsNaN(rawXs[i]) && !double.IsInfinity(rawXs[i]) &&
                !double.IsNaN(rawYs[i]) && !double.IsInfinity(rawYs[i]))
            {
                validIndices.Add(i);
            }
        }
        
        var cleanX = validIndices.Select(i => rawXs[i]).ToArray();
        var cleanY = validIndices.Select(i => rawYs[i]).ToArray();

        if (cleanX.Length < 2) throw new ArgumentException("有效数据点不足");

        // 2. 创建线性样条插值器
        var spline = LinearSpline.InterpolateSorted(cleanX, cleanY);

        // 3. 生成目标 X 轴 (等间隔x)
        var startX = cleanX[0];
        var endX = cleanX[^1];
        
        // 计算需要生成的点数
        var pointCount = (int)Math.Round((endX - startX) / targetInterval) + 1;
        var resampledYs = new double[pointCount];
        var resampledXs = new double[pointCount];
        // 4. 执行插值
        for (var i = 0; i < pointCount; i++)
        {
            var currentX = startX + i * targetInterval;
            resampledXs[i] = currentX;
            // 如果 currentX 略微超出范围（浮点数精度问题）
            resampledYs[i] = spline.Interpolate(currentX);
            
        }
        return (resampledXs, resampledYs);
    }
    /// <summary>
    /// 删除信号的直流分量
    /// </summary>
    private static IEnumerable<double> RemoveDcComponent(IEnumerable<double> signal)
    {
        var dataList = signal.ToList();
        var mean = dataList.Mean();
        for (var i = 0; i < dataList.Count; i++)
        {
            dataList[i] -= mean;
        }
        return dataList;
    }
    /// <summary>
    /// 应用带通滤波器对信号进行滤波
    /// </summary>
    /// <param name="signal">输入信号</param>
    /// <param name="sampleRate">采样率（Hz）</param>
    /// <param name="fc">中心频率（Hz）</param>
    /// <param name="order">滤波器阶数，默认为20</param>
    /// <returns>滤波后的信号</returns>
    public static List<double> BandPassFilter(List<double> signal, double sampleRate, double fc = 2000000d,
        int order = FilterOrder)
    {
        var dcFreeSignal = RemoveDcComponent(signal).ToList(); //滤波之前先删除直流分量
        if (!(fc > 0)) return dcFreeSignal; //中心频率大于0 
        
        var lowCutOff = fc * 0.5;
        var highCutOff = fc * 1.5;
        var bandPassFilter =
            OnlineFilter.CreateBandpass(ImpulseResponse.Finite, sampleRate, lowCutOff, highCutOff, order);
        var delaySamples = (order - 1) / 2;
        // 增加延迟波形
        for (var i = 0; i < delaySamples; i++)
        {
            dcFreeSignal.Add(0);
        }
        // 滤波处理
        var filteredSignal = bandPassFilter.ProcessSamples(dcFreeSignal.ToArray()).ToList();
        // 去除群延迟
        filteredSignal.RemoveRange(0, delaySamples);
        return filteredSignal;
    }

    /// <summary>
    /// 应用显示用零相位低通滤波器。居中对称卷积不会移动波形位置，系数归一化避免直流增益变化。
    /// </summary>
    /// <param name="signal">输入信号</param>
    /// <param name="sampleRate">采样率（Hz）</param>
    /// <param name="cutoff">截止频率（Hz）</param>
    /// <param name="order">滤波器阶数，自动修正为奇数抽头数</param>
    /// <returns>滤波后的信号</returns>
    public static List<double> LowPassFilter(List<double> signal, double sampleRate, double cutoff,
        int order = FilterOrder)
    {
        if (signal.Count == 0 || !(sampleRate > 0) || !(cutoff > 0))
        {
            return signal;
        }

        var safeCutoff = Math.Min(cutoff, sampleRate * 0.45d);
        if (!(safeCutoff > 0))
        {
            return signal;
        }

        var taps = Math.Max(3, order);
        if (taps % 2 == 0)
        {
            taps++;
        }

        var coeffs = CreateLowPassFirCoefficients(sampleRate, safeCutoff, taps);
        var half = taps / 2;
        var result = new List<double>(signal.Count);
        for (var i = 0; i < signal.Count; i++)
        {
            var sum = 0d;
            for (var j = 0; j < taps; j++)
            {
                var sourceIndex = MirrorIndex(i + j - half, signal.Count);
                sum += coeffs[j] * signal[sourceIndex];
            }
            result.Add(sum);
        }

        return result;
    }

    private static double[] CreateLowPassFirCoefficients(double sampleRate, double cutoff, int taps)
    {
        var coeffs = new double[taps];
        var center = taps / 2;
        var normalizedCutoff = cutoff / sampleRate;
        var sum = 0d;

        for (var i = 0; i < taps; i++)
        {
            var n = i - center;
            var sinc = n == 0
                ? 2d * normalizedCutoff
                : Math.Sin(2d * Math.PI * normalizedCutoff * n) / (Math.PI * n);
            var window = 0.54d - 0.46d * Math.Cos(2d * Math.PI * i / (taps - 1));
            coeffs[i] = sinc * window;
            sum += coeffs[i];
        }

        if (Math.Abs(sum) > double.Epsilon)
        {
            for (var i = 0; i < coeffs.Length; i++)
            {
                coeffs[i] /= sum;
            }
        }

        return coeffs;
    }

    private static int MirrorIndex(int index, int count)
    {
        if (count <= 1)
        {
            return 0;
        }

        while (index < 0 || index >= count)
        {
            if (index < 0)
            {
                index = -index;
            }
            if (index >= count)
            {
                index = 2 * count - 2 - index;
            }
        }

        return index;
    }
    /// <summary>
    /// 计算希尔伯特变换获取包络（优化版：自动补零到 2 的幂）
    /// 1. 补零到最近的 2^N
    /// 2. FFT
    /// 3. 正频率 *2（0 和 奈奎斯特除外）
    /// 4. 负频率置零
    /// 5. IFFT
    /// 6. 取前 N 点，恢复原长度
    /// </summary>
    public static double[] HilbertEnvelope(double[] input, bool positive = true)
    {
        var n = input.Length;
        var x = new Complex[n];
        var mValue = input.Mean();
        for (var i = 0; i < n; i++)
        {
            x[i] = new Complex(input[i] - mValue, 0);
        }

        Fourier.Forward(x, FourierOptions.Matlab);
        var h = new double[x.Length];
        h[0] = 1;
        for (var i = 1; i < n / 2; i++)
        {
            h[i] = 2;
        }

        if (n % 2 == 0)
        {
            h[n / 2] = 1;
        }

        for (var i = 0; i < x.Length; i++) x[i] *= h[i];
        Fourier.Inverse(x, FourierOptions.Matlab);
        x[0] = Complex.Zero;
        return positive ? x.Select(o => mValue + o.Magnitude).ToArray() : x.Select(o => mValue - o.Magnitude).ToArray();
    }
    /// <summary>
    /// 计算余弦相似度
    /// </summary>
    public static double CosineSimilarity(double[] arrayA, double[] arrayB)
    {
        if (arrayA == null || arrayB == null)
            throw new ArgumentNullException($"输入数组不能为空");

        if (arrayA.Length != arrayB.Length)
            throw new ArgumentException("两个数组的长度必须相同");

        // 创建向量
        var vecA = MathNet.Numerics.LinearAlgebra.Vector<double>.Build.DenseOfArray(arrayA);
        var vecB = MathNet.Numerics.LinearAlgebra.Vector<double>.Build.DenseOfArray(arrayB);
        // 计算点积
        var dotProduct = vecA.DotProduct(vecB);
        // 计算 L2 范数（模长）
        var normA = vecA.L2Norm();
        var normB = vecB.L2Norm();

        // 防止除以零
        if (normA == 0 || normB == 0)
            return 0.0;
        // 返回余弦相似度
        return dotProduct / (normA * normB);
    }
    /// <summary>
    /// 计算 SNR (能量法)
    /// </summary>
    public static (double snrDb, double detectionThreshold) CalcSnrAuto(double[] data, double thrFactor = 5.0d)
    {
        var n = data.Length;
        if (n < 10) return (0, 0);
        
        var envelope = HilbertEnvelope(data);
        var energy = envelope.Select(e => e * e).ToArray();
        
        var sortedEnergy = energy.Where(e => e > 0).OrderBy(x => x).ToArray();
        var median = sortedEnergy.Length > 0 ? sortedEnergy[sortedEnergy.Length / 2] : 0;
        var threshold = median * 2.0d;//能量阈值
        
        var signalRegions = new List<(int Start, int End)>();
        
        var inSignal = false;
        var regionStart = 0;
        var minSignalLen = Math.Max(3, n / 50);
        
        for (var i = 0; i < n; i++)
        {
            if (energy[i] >= threshold && !inSignal)
            {
                regionStart = i;
                inSignal = true;
            }
            else if (energy[i] < threshold && inSignal)
            {
                if (i - regionStart >= minSignalLen)
                {
                    signalRegions.Add((regionStart, i));
                }
                inSignal = false;
            }
        }
        
        if (inSignal)
        {
            if (n - regionStart >= minSignalLen)
            {
                signalRegions.Add((regionStart, n));
            }
        }
        var allRegions = signalRegions.OrderBy(r => r.Start).ToList();
        var finalNoiseRegions = new List<(int Start, int End)>();
        
        var lastEnd = 0;
        foreach (var r in allRegions)
        {
            if (r.Start > lastEnd)
                finalNoiseRegions.Add((lastEnd, r.Start));
            lastEnd = r.End;
        }
        if (lastEnd < n)
            finalNoiseRegions.Add((lastEnd, n));
        
        double signalEnergy = 0;
        var signalCount = 0;
        foreach (var r in signalRegions)
        {
            for (var i = r.Start; i < r.End; i++)
            {
                signalEnergy += energy[i];
                signalCount++;
            }
        }
        
        double noiseEnergy = 0;
        var noiseCount = 0;
        foreach (var r in finalNoiseRegions)
        {
            for (var i = r.Start; i < r.End; i++)
            {
                noiseEnergy += energy[i];
                noiseCount++;
            }
        }
        
        double snrDb;
        if (noiseEnergy > 0 && signalEnergy > 0 && noiseCount > 0)
        {
            var signalPower = signalEnergy / Math.Max(1, signalCount);
            var noisePower = noiseEnergy / Math.Max(1, noiseCount);
            snrDb = 10 * Math.Log10(signalPower / noisePower);
        }
        else if (signalEnergy > 0)
            snrDb = 30;
        else
            snrDb = -10;
        
        // 噪声水平：包络能量→原始信号 RMS (瑞利分布：σ = sqrt(E/2))
        var noiseLevel = Math.Sqrt(noiseEnergy / noiseCount / 2.0);  //
        // 检测阈值 = 噪声水平 × 系数 (默认 5σ，虚警率<0.0001%)
        var detectionThreshold = noiseLevel * thrFactor;
        return (snrDb, detectionThreshold);
    }
    /// <summary>
    /// 中值平滑
    /// </summary>
    public static double[] MedianSmooth(double[] input, int windowSize)
    {
        if (windowSize % 2 == 0) 
            windowSize++; // 必须奇数
        var half = windowSize / 2;
        var output = new double[input.Length];
    
        for (var i = 0; i < input.Length; i++)
        {
            var window = new List<double>();
            for (var j = -half; j <= half; j++)
            {
                var idx = i + j;
                if (idx < 0) idx = 0;
                else if (idx >= input.Length) idx = input.Length - 1;
                window.Add(input[idx]);
            }
            window.Sort();
            output[i] = window[half]; // 中位数
        }
        return output;
    }
    /// <summary>
    /// 平均平滑
    /// </summary>
    public static double[] MeanSmooth(double[] signal,int windowSize = 5)
    {
        var mss = new MovingStatistics(windowSize);
        var avgData = new double[signal.Length];
        for (var i = 0; i < signal.Length; i++)
        {
            mss.Push(signal[i]);
            avgData[i] = mss.Mean;
        }
        return avgData;
    }
    #region 信号区域检测
    /// <summary>
    /// 检测回波区域（返回多个回波）
    /// </summary>
    /// <param name="signal">输入信号</param>
    /// <param name="coarseThreshold">粗分割阈值（默认 25%）</param>
    /// <param name="threshold">合并和追溯阈值（默认 5%）</param>
    /// <param name="minWidth">最小持续点数（默认 20）</param>
    /// <returns>回波区域列表（按位置排序）</returns>
    public static List<EchoRegion> DetectRegions(
        double[] signal,
        double coarseThreshold = 0.25,
        double threshold = 0.05,
        int minWidth = 20)
    {
        if (signal == null || signal.Length < minWidth)
            return new List<EchoRegion>();
        
        // 1. 计算 Hilbert 包络
        var envelope = SignalProcessor.HilbertEnvelope(signal);
        
        // 2. 稳健归一化，避免单点尖峰把真实回波整体压低
        var envMax = GetRobustEnvelopeScale(envelope);
        if (envMax <= 0)
            return new List<EchoRegion>();
        
        var envNormalized = envelope.Select(e => e / envMax).ToArray();
        
        var smoothEnvelope = MeanSmooth(envNormalized, 5);
        var peaks = FindEchoPeaks(smoothEnvelope, coarseThreshold, threshold, minWidth);
        if (peaks.Count == 0)
            return new List<EchoRegion>();

        var echoPackets = MergePeaksToEchoPackets(smoothEnvelope, peaks, threshold, minWidth);
        var segments = new List<EchoRegion>();
        foreach (var packet in echoPackets)
        {
            var peakPos = FindPeakInRegion(smoothEnvelope, packet.Start, packet.End);

            // 回波包已由多个有效峰聚类得到，直接使用包边界，避免再按最高峰把多峰主回波切碎。
            var leftBound = packet.Start;
            var rightBound = packet.End;
            var nCycles = CalculateCycles(signal, leftBound, rightBound);

            segments.Add(new EchoRegion(
                Start: leftBound,
                End: rightBound,
                NCycles: nCycles,
                PeakEnergy: peakPos
            ));
        }
        return segments;
    }

    private readonly record struct EchoPeak(int Index, int Start, int End, double Peak, double Area);

    private readonly record struct EchoPacket(int Start, int End, double Peak, double Area, EchoPeak LastPeak);

    private static List<EchoPeak> FindEchoPeaks(double[] envelope, double peakThreshold, double boundaryThreshold,
        int minWidth)
    {
        var peaks = new List<EchoPeak>();
        var quietWidth = Math.Max(2, minWidth / 4);
        for (var i = 1; i < envelope.Length - 1; i++)
        {
            if (envelope[i] < peakThreshold || envelope[i] < envelope[i - 1] || envelope[i] <= envelope[i + 1])
            {
                continue;
            }

            var (start, end) = ExpandPeakBounds(envelope, i,
                Math.Max(boundaryThreshold, envelope[i] * boundaryThreshold), quietWidth);
            var width = end - start;
            if (width < minWidth)
            {
                continue;
            }

            var area = GetAreaValue(envelope, start, end);
            if (area < peakThreshold * minWidth * 0.35d)
            {
                continue;
            }

            peaks.Add(new EchoPeak(i, start, end, envelope[i], area));
        }

        return peaks;
    }

    private static (int Start, int End) ExpandPeakBounds(double[] envelope, int peakIndex, double threshold,
        int quietWidth)
    {
        var start = peakIndex;
        var belowThresholdCount = 0;
        for (var index = peakIndex; index >= 0; index--)
        {
            if (envelope[index] >= threshold)
            {
                belowThresholdCount = 0;
                start = index;
                continue;
            }

            belowThresholdCount++;
            start = index;
            if (belowThresholdCount >= quietWidth)
            {
                start = index + quietWidth;
                break;
            }
        }

        var end = peakIndex + 1;
        belowThresholdCount = 0;
        for (var index = peakIndex; index < envelope.Length; index++)
        {
            if (envelope[index] >= threshold)
            {
                belowThresholdCount = 0;
                end = index + 1;
                continue;
            }

            belowThresholdCount++;
            end = index + 1;
            if (belowThresholdCount >= quietWidth)
            {
                end = index - quietWidth + 1;
                break;
            }
        }

        return (start, end);
    }

    private static List<EchoPacket> MergePeaksToEchoPackets(double[] envelope, List<EchoPeak> peaks,
        double mergeThreshold, int minWidth)
    {
        var packets = new List<EchoPacket>();
        var current = new EchoPacket(peaks[0].Start, peaks[0].End, peaks[0].Peak, peaks[0].Area, peaks[0]);
        for (var i = 1; i < peaks.Count; i++)
        {
            var next = peaks[i];
            if (ShouldMergePeak(envelope, current.LastPeak, next, mergeThreshold, minWidth))
            {
                current = new EchoPacket(
                    Math.Min(current.Start, next.Start),
                    Math.Max(current.End, next.End),
                    Math.Max(current.Peak, next.Peak),
                    current.Area + next.Area,
                    next);
            }
            else
            {
                packets.Add(current);
                current = new EchoPacket(next.Start, next.End, next.Peak, next.Area, next);
            }
        }

        packets.Add(current);
        return packets;
    }

    private static bool ShouldMergePeak(double[] envelope, EchoPeak previousPeak, EchoPeak peak, double mergeThreshold,
        int minWidth)
    {
        var gap = peak.Start - previousPeak.End;
        if (gap < 0)
        {
            return true;
        }
        if (gap > minWidth)
        {
            return false;
        }

        var valleyVal = FindValleyBetween(envelope, previousPeak.End, peak.Start).Value;
        var peakRatio = Math.Min(previousPeak.Peak, peak.Peak) / Math.Max(previousPeak.Peak, peak.Peak);
        var areaRatio = Math.Min(previousPeak.Area, peak.Area) / Math.Max(previousPeak.Area, peak.Area);

        // 同一回波包允许多个峰；尖脉冲通常面积小，不满足面积比例。
        return peakRatio >= 0.30d
               && areaRatio >= 0.20d
               && (valleyVal >= mergeThreshold * 0.45d || gap <= minWidth / 2);
    }

    private static double GetRobustEnvelopeScale(double[] envelope)
    {
        var positiveValues = envelope.Where(v => v > 0).OrderBy(v => v).ToArray();
        if (positiveValues.Length == 0)
        {
            return 0;
        }
        var maxValue = positiveValues[^1];
        var percentileIndex = Math.Clamp((int)Math.Ceiling(positiveValues.Length * 0.98) - 1, 0, positiveValues.Length - 1);
        var p98Value = positiveValues[percentileIndex];
        return Math.Max(p98Value, maxValue * 0.25d);
    }

    private static double GetAreaValue(double[] values, int start, int end)
    {
        var safeStart = Math.Clamp(start, 0, values.Length);
        var safeEnd = Math.Clamp(end, safeStart, values.Length);
        var sum = 0d;
        for (var i = safeStart; i < safeEnd; i++)
        {
            sum += values[i];
        }
        return sum;
    }

    /// <summary>
    /// 找两区域之间的谷底位置和值
    /// </summary>
    private static (int Position, double Value) FindValleyBetween(
        double[] envelope,
        int region1End,
        int region2Start)
    {
        if (region2Start <= region1End + 1)
            return ((region1End + region2Start) / 2, 0);

        var minVal = double.MaxValue;
        var minPos = region1End;

        for (var i = region1End + 1; i < region2Start; i++)
        {
            if (envelope[i] < minVal)
            {
                minVal = envelope[i];
                minPos = i;
            }
        }

        return (minPos, minVal);
    }

    /// <summary>
    /// 在区域内找峰值位置
    /// </summary>
    private static int FindPeakInRegion(double[] envelope, int start, int end)
    {
        var maxVal = 0d;
        var maxPos = start;

        for (var i = start; i < end && i < envelope.Length; i++)
        {
            if (envelope[i] > maxVal)
            {
                maxVal = envelope[i];
                maxPos = i;
            }
        }

        return maxPos;
    }

    /// <summary>
    /// 通过过零点计算周期数
    /// </summary>
    private static double CalculateCycles(double[] signal, int start, int end)
    {
        if (start >= end || end >= signal.Length)
            return 0;
        
        // 计算直流偏置
        var segment = signal.Skip(start).Take(end - start).ToArray();
        var dcOffset = segment.Average();
        
        // 计算过零点数量
        var zeroCrossings = 0;
        for (var i = start + 1; i < end; i++)
        {
            var prev = signal[i - 1] - dcOffset;
            var curr = signal[i] - dcOffset;
            
            // 检测过零（上升沿或下降沿）
            if (prev * curr < 0)
            {
                zeroCrossings++;
            }
        }
        
        // 周期数 = 过零点数量 / 2
        return zeroCrossings / 2.0;
    }
    #endregion
}
