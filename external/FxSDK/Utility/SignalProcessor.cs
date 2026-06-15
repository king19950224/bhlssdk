using System.Numerics;
using MathNet.Filtering;
using MathNet.Numerics.IntegralTransforms;
using MathNet.Numerics.Interpolation;
using MathNet.Numerics.Statistics;

namespace FxSDK.Utility;

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
        return CubicResample(originalData, originalInterval, newInterval);
    }
    private static List<double> CubicResample(List<double> originalData, 
        decimal originalInterval, decimal newInterval)
    {
        var xValues = new List<double>();
        for (var i = 0; i < originalData.Count; i++)
        {
            xValues.Add(i * (double) originalInterval);
        }
        var interpolator = CubicSpline.InterpolateNatural(xValues, originalData);
        //先确认点数 然后重采样 
        var newPointCount = (int)Math.Ceiling(originalData.Count * originalInterval / newInterval);
        var interpolatedData = new List<double>();
        var x = 0d;
        for(var i = 0; i < newPointCount; i++)
        {
            var y = interpolator.Interpolate(x);
            interpolatedData.Add(y);
            x += (double) newInterval;
        }
        return interpolatedData;
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
    /// 计算 SNR
    /// </summary>
    public static (double snrDb,double noiseLevel) CalcSnrAuto(double[] data, double thrFactor = 3.0d)
    {
        //1: 使用 Hilbert 变换提取包络
        var envelope = HilbertEnvelope(data);
        //2. 估算噪声水平
        var envMedian = envelope.Median();
        var lowValues = envelope.Where(x => x < envMedian).ToArray();//低值(噪声)数组
        
        //mad方法
        var lowMedian = lowValues.Median();
        var mad = lowValues.Select(x => Math.Abs(x - lowMedian)).Median();
        var threshold = thrFactor * mad / 0.6745; // 初始阈值，thrFactor≈4~5
        
        //3. 检测超过阈值的连续区域
        var signalRegions = GetSignalRangesByThreshold(envelope, threshold);
        //4. 提取信号数据和噪声数据
        var signalSamples = new List<double>();
        var noiseSamples = new List<double>();
        var sortedRegions = signalRegions.OrderBy(r => r.start).ToList();
        var lastEnd = -1;
        // 提取信号
        foreach (var (start, end) in sortedRegions)
        {
            var s = Math.Max(0, start);
            var e = Math.Min(data.Length - 1, end);
            if (s <= e)
            {
                signalSamples.AddRange(data.Skip(s).Take(e - s + 1));
            }
            lastEnd = Math.Max(lastEnd, end);
        }
        // 提取噪声（批量）
        var prevEnd = -1;
        foreach (var (start, end) in sortedRegions)
        {
            if (start > prevEnd + 1)
            {
                var s = prevEnd + 1;
                var e = start - 1;
                if (s < data.Length && s <= e)
                {
                    noiseSamples.AddRange(data.Skip(s).Take(e - s + 1));
                } 
            }
            prevEnd = end;
        }
        //最后一段噪声
        if (prevEnd + 1 < data.Length)
        {
            noiseSamples.AddRange(data.Skip(prevEnd + 1).Take(data.Length - (prevEnd + 1)));
        }
        //5. 计算噪声平均功率
        var noiseMean = noiseSamples.Average();
        var noisePower = noiseSamples.Select(x => Math.Pow(x - noiseMean, 2)).Average();
        //6. 计算信号平均功率
        var signalPower = signalSamples.Select(x => Math.Pow(x, 2)).Average();
        // 步骤7: 计算 SNR (dB)
        var snrDb = noisePower > 1e-10 ? 10 * Math.Log10(signalPower / noisePower) : double.PositiveInfinity;
        var noiseLevel = GetNoiseScale(snrDb) * Math.Sqrt(noisePower);
        
        return (snrDb, noiseLevel);
    }
    /// <summary>
    /// 动态计算检测阈值（防止高 SNR 误报）系数
    /// </summary>
    private static double GetNoiseScale(double snrDb)
    {
        // S型函数：SNR 越高，scale 越大
        var baseScale = 6;     // 低 SNR 最小值
        var ceiling = 4.2;       // 高 SNR 饱和值
        var midSnr = 16.0;       // 中点
        var sharpness = 0.35;     // 增长速率

        var effectiveSnr = Math.Min(snrDb, 24.0); // 高 SNR 截止
        var scale = baseScale + (ceiling - baseScale) / (1 + Math.Exp(-sharpness * (effectiveSnr - midSnr)));
        // 添加下限保护，确保不会太小
        scale = Math.Max(2.0, scale);
        return scale;
    } 
    /// <summary>
    /// 检测信号中超出阈值的连续区域列表
    /// </summary>
    private static List<(int start, int end)> GetSignalRangesByThreshold(double[] data, double threshold)
    {
        var list = new List<(int start, int end)>();
        var start = -1;
        for (var i = 0; i < data.Length; i++)
        {
            if (start == -1 && data[i] >= threshold)//识别开始
            {
                start = i;
            }
            else if (start != -1 && data[i] < threshold)//识别结束
            {
                if (i - start > 2) // 忽略太窄的脉冲
                    list.Add((start, i - 1));
                start = -1;
            }
        }
        //如果最后的信号也大于阈值
        if (start != -1 && data.Length - start > 2)
            list.Add((start, data.Length - 1));

        return list;
    }
    /// <summary>
    /// 小波降噪
    /// </summary>
    public static double[] DenoiseSignal(double[] signal, Wavelet wavelet, int level)
    {
        var originalLen = signal.Length;
        // 截断到 2 的 N 次方
        var maxExp = (int)Math.Floor(Math.Log(originalLen, 2)); // log2(N)
        var powerOfTwoLen = (int)Math.Pow(2, maxExp);
        var truncated = new double[powerOfTwoLen];
        Array.Copy(signal, truncated, powerOfTwoLen);
        // 1. 正向小波变换
        WaveletTransform.Forward1D(truncated, out var coefs, wavelet, level);
        // 2. 获取各层细节系数并阈值处理
        var len = truncated.Length;
        for (var lev = 1; lev <= level; lev++)
        {
            // 获取第 lev 层的细节系数
            var detailLen = len >> lev;
            var detail = new double[detailLen];
            Array.Copy(coefs, detailLen, detail, 0, detailLen);

            // 估计噪声标准差
            var sigma = Mad(detail) / 0.6745;

            // === 分层设置低阈值 ===
            var universal = sigma * Math.Sqrt(2 * Math.Log(len));
    
            // 分层缩放因子（针对超声信号优化）
            var alpha = lev switch
            {
                1 => 0.3,   // 第1层：高频细节，含回波前沿 → 保留最多
                2 => 0.6,   // 第2层：中频 → 中等去噪
                3 => 0.8,   // 第3层：低频细节 → 可稍强去噪
                _ => 0.9    // 第4层及以上：接近噪声 → 接近原始
            };

            var lambda = universal * alpha;

            // 软阈值处理
            detail = WaveletTransform.SoftThreshold(detail, lambda);

            // 写回
            Array.Copy(detail, 0, coefs, detailLen, detailLen);
        }
        // 3. 逆变换重构
        WaveletTransform.Inverse1D(coefs, out var denoised, wavelet, level);
        return denoised;
    }
    // 辅助函数：MAD（Median Absolute Deviation）
    private static double Mad(double[] x)
    {
        var sorted = x.OrderBy(v => v).ToArray();
        var median = sorted[sorted.Length / 2];
        var deviations = x.Select(v => Math.Abs(v - median)).ToArray();
        Array.Sort(deviations);
        return deviations[deviations.Length / 2];
    }
    /// <summary>
    /// 轻量高斯平滑
    /// </summary>
    public static double[] LightGaussianSmooth(double[] y, double gradientThreshold = 0.1)
    {
        var smoothed = new double[y.Length];
        var grad = new double[y.Length];

        // 边界复制
        smoothed[0] = y[0];
        smoothed[^1] = y[^1];
        grad[0] = grad[^1] = double.MaxValue; // 强制不平滑边界

        // 计算梯度（更鲁棒）
        for (var i = 1; i < y.Length - 1; i++)
        {
            var d1 = Math.Abs(y[i] - y[i - 1]);
            var d2 = Math.Abs(y[i + 1] - y[i]);
            grad[i] = Math.Max(d1, d2);
        }

        // 自适应平滑（5点窗口）
        for (var i = 1; i < y.Length - 1; i++)
        {
            if (grad[i] < gradientThreshold)
            {
                double sum = 0;
                var count = 0;
                for (var j = -2; j <= 2; j++)
                {
                    var idx = i + j;
                    if (idx < 0 || idx >= y.Length) 
                        continue;
                    sum += y[idx];
                    count++;
                }
                smoothed[i] = sum / count;
            }
            else
            {
                smoothed[i] = y[i];
            }
        }
        return smoothed;
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
}