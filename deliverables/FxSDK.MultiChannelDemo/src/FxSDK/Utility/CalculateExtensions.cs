using System.Text;
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
    public const decimal DefaultTempCoeff = 0.00010269M;//默认温度补偿系数
    public const decimal StandardTemperature = 22.22M; //基准温度
    public const double NoiseWindowNs = 1500;//噪声端窗口 1500ns
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
    /// <param name="midResults">中间值(x1,y1,x2,y2)</param>
    /// <param name="noiseBase">噪声基线</param>
    /// <param name="threshold">阈值</param>
    public static double CalcZeroIndexByThrSignal(List<double> signal, decimal signalInterval, out decimal[] midResults ,double noiseBase = 50d, 
        short threshold = 0)
    {
        midResults = new[] {0m, 0m, 0m, 0m};
        //1. 根据幅值判断信号是否有效
        var isValidSignal = signal.Any(s => Math.Abs(s) >= noiseBase);
        if (!isValidSignal)
        {
            return -1;//无效信号状态码
        }
        var dynamicThreshold = threshold == 0 ? signal.Max() * 0.6d : threshold;
        var edgeType = dynamicThreshold < 0 ? ThresholdEdgeType.Falling : ThresholdEdgeType.Rising;
        //2. 在阈值前后找 时间轴正向和反向过零点 将反向到正向过零点之间信号取出进行插值
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
        midResults[0] = usStartIdx + calcThrCrossIdx * CalculationInterval / signalInterval; 
        midResults[1] = (decimal)dynamicThreshold;
        
        var calcZeroIdxTuple = FindPositiveZeroCrossing(calcSignal.ToArray(), calcThrCrossIdx);
        //信号过阈值 时间轴正向过零点索引
        var calcZeroIdx = Convert.ToDecimal(calcZeroIdxTuple.Item1);
        if (calcZeroIdxTuple.Item1 > 0)
        {
            var calcCurrentIdx = calcZeroIdxTuple.Item1;
            var calcPrevIdx = calcZeroIdxTuple.Item1 - 1;
            calcZeroIdx = CalcZeroCrossingIndex(calcPrevIdx, calcSignal[calcPrevIdx], calcCurrentIdx,calcSignal[calcCurrentIdx]);
        }
        var zeroIndex = usStartIdx + calcZeroIdx * CalculationInterval / signalInterval;
        midResults[2] = zeroIndex;
        return Convert.ToDouble(zeroIndex);
    }
    /// <summary>
    /// 计算回波时间索引(计算过零含小数索引)-首峰法
    /// -1 表示信号过小
    /// -2 阈值异常
    /// -4 信噪比低
    /// </summary>
    /// <param name="signal">信号</param>
    /// <param name="signalInterval">信号点间隔(ns)</param>
    /// <param name="midResults">中间值</param>
    /// <param name="noiseBase">噪声基线</param>
    public static double CalcZeroIndexAuto(List<double> signal, decimal signalInterval, out decimal[] midResults, double noiseBase = 50d)
    {
        midResults = new[] {0m, 0m};
        //1. 根据幅值判断信号是否有效(先用经验值判断)
        var isValidSignal = signal.Any(s => Math.Abs(s) >= noiseBase);
        if (!isValidSignal)
        {
            return -1;//无效信号状态码
        }
        // 2. 噪声估计：使用前1500ns（安全区域，避开回波）
        var noiseSamples = (int)(NoiseWindowNs / (double)signalInterval); 
        noiseSamples = Math.Min(noiseSamples, signal.Count / 4);
        noiseSamples = Math.Max(noiseSamples, 30); // 至少30点
        var noiseRegion = signal.Take(noiseSamples).ToArray();
        var median = noiseRegion.Median();
        var mad = noiseRegion.Select(x => Math.Abs(x - median)).ToArray().Median();
        var noiseRms = 1.4826 * mad;//
        var noiseLevel = 5 * noiseRms; // 5σ 阈值
        //3. 查找首个过阈值的位置索引
        var firstThrCrossIdx = FindForwardThresholdCrossing(signal.ToArray(), noiseLevel);
        if (firstThrCrossIdx == -1)
            return -2;//无效信号
        //4.计算包络并平滑
        var envSignal= SignalProcessor.HilbertEnvelope(signal.ToArray());
        var smoothedEnvSignal = SavitzkyGolayFilter.Smooth(envSignal);
        //5.计算包络梯度(5点stencil)
        var gradient = new double[smoothedEnvSignal.Length];
        for (var i = 2; i < smoothedEnvSignal.Length - 2; i++)
        {
            gradient[i] = (-smoothedEnvSignal[i+2] + 8*smoothedEnvSignal[i+1] 
                - 8*smoothedEnvSignal[i-1] + smoothedEnvSignal[i-2]) / 12.0;
        }
        //6. 从大于包络大于5ς开始搜索 到梯度为负数结束
        var firstPeakIdx = -1;
        var maxGrad = double.MinValue;
        var searchEnd = gradient.Length - 1;
        for (var i = firstThrCrossIdx; i <= searchEnd; i++)
        {
            if (gradient[i] <= 0)
            {
                break;
            }
            if (gradient[i] > maxGrad)
            {
                maxGrad = gradient[i];
                firstPeakIdx = i;
            }
        }

        if (firstPeakIdx == -1) return -2;
        
        midResults[0] = firstPeakIdx;//第一个回波 波峰位置
        midResults[1] = (decimal)smoothedEnvSignal[firstPeakIdx];//第一个回波峰值
        //7. 计算firstPeakIdx所在的整us 并插值 2us 的数据
        var usSpan = (int) (1000 / signalInterval); //1us多少个离散点
        var usStartIdx = (int) Math.Floor(firstPeakIdx / (decimal) usSpan) * usSpan;
        var usEndIdx = usStartIdx + 2 * usSpan; 
        if (usEndIdx >= signal.Count) usEndIdx = signal.Count - 1;
        var rangeSignal = signal.GetRange(usStartIdx, usEndIdx - usStartIdx);
        var calcSignal = SignalProcessor.InterpolateAndResample(rangeSignal, signalInterval, CalculationInterval);
        //8. 计算过阈值后的上升沿时间轴正向 过零点索引(从首峰索引开始找)
        var calcFirstPeakIdx = (firstPeakIdx - usStartIdx) * signalInterval  / CalculationInterval;
        var calcZeroIdxTuple = FindPositiveZeroCrossing(calcSignal.ToArray(), (int)calcFirstPeakIdx);
        //信号过阈值 时间轴正向过零点索引
        var calcZeroIdx = Convert.ToDecimal(calcZeroIdxTuple.Item1);
        return Convert.ToDouble(usStartIdx + calcZeroIdx * CalculationInterval / signalInterval);
    }
    /// <summary>
    /// 计算回波时间索引(计算过零含小数索引)-动态特征
    /// -1 表示信号过小
    /// -2 阈值异常
    /// </summary>
    /// <param name="signal">信号</param>
    /// <param name="signalInterval">信号点间隔(ns)</param>
    /// <param name="midResults">中间值</param>
    /// <param name="noiseBase">噪声基线</param>
    /// <param name="dynThr">小数阈值小于0.5</param>
    public static double CalcZeroIndexByEnv(List<double> signal, decimal signalInterval, out decimal[] midResults, double noiseBase = 50d
        , double dynThr = 0.3d)
    {
        midResults = new[] {0m, 0m, 0m};
        //1. 根据幅值判断信号是否有效
        var isValidSignal = signal.Any(s => Math.Abs(s) >= noiseBase);
        if (!isValidSignal)
        {
            return -1;//无效信号状态码
        }
        //2. 获取正向包络信号
        var envSignal= SignalProcessor.HilbertEnvelope(signal.ToArray());
        var smoothedEnvSignal = GetSmoothingData(envSignal);
        //3. 计算包络信号过阈值的索引(峰值最大点)
        var envMax = smoothedEnvSignal.Max();
        //3.1 先找45%确定第一个包络
        var envDetectThr = envMax * 0.45;//特征检测线
        var envDetectThrCrossIdx = FindForwardThresholdCrossing(smoothedEnvSignal, envDetectThr);//正向上升沿
        //3.2 从45%索引点前找过阈值点
        var envThr = envMax * dynThr;
        var thrCrossIdx = FindBackwardThresholdCrossing(smoothedEnvSignal, envThr, ThresholdEdgeType.Falling, envDetectThrCrossIdx);//反向下降沿
        //4. 取前后各 1us 进行插值
        var usSpan = (int) (1000 / signalInterval); //1us多少个离散点
        var usStartIdx = (int) Math.Floor(thrCrossIdx / (decimal) usSpan) * usSpan;
        var usEndIdx = usStartIdx + 2 * usSpan; //只进行前后共2个us的点进行插值
        if (usEndIdx >= smoothedEnvSignal.Length) usEndIdx = smoothedEnvSignal.Length - 1;
        var rangeSignal = smoothedEnvSignal.ToList().GetRange(usStartIdx, usEndIdx - usStartIdx);
        //5. 插值通过阈值进行高精度计算
        var calcSignal = SignalProcessor.InterpolateAndResample(rangeSignal, signalInterval, CalculationInterval);
        var calcThrCrossIdx = FindForwardThresholdCrossing(calcSignal.ToArray(), envThr);
        
        midResults[0] = usStartIdx + calcThrCrossIdx * CalculationInterval / signalInterval; 
        midResults[1] = (decimal)envThr;
        midResults[2] = (decimal) envDetectThr;
        
        //6. 直接使用包络过阈值点作为测量结果
        return Convert.ToDouble(usStartIdx + calcThrCrossIdx * CalculationInterval / signalInterval);
    }
    /// <summary>
    /// 计算回波时间索引(计算过零含小数索引)-波谷法
    /// -1 表示信号过小
    /// </summary>
    /// <param name="signal">信号</param>
    /// <param name="midResults">中间值</param>
    /// <param name="noiseBase">噪声基线</param>
    public static double CalcZeroIndexByAutoValley(List<double> signal, out decimal[] midResults, double noiseBase = 10d)
    {
        midResults = new[] {0m, 0m, 0m, 0m};
        //1. 根据幅值判断信号是否有效
        var isValidSignal = signal.Any(s => Math.Abs(s) >= noiseBase);
        if (!isValidSignal)
        {
            return -1;//无效信号状态码
        }
        //2. 找最小波谷,然后找上升沿过零点
        var (minIndex,_) = FindMinMax(signal.ToArray());
        midResults[0] = minIndex; midResults[1] = (decimal) signal[minIndex];
        var zeroIndexTuple = FindPositiveZeroCrossing(signal.ToArray(), minIndex);
        var calcZeroIdx = Convert.ToDecimal(zeroIndexTuple.Item1);
        if (zeroIndexTuple.Item1 > 0)
        {
            var calcCurrentIdx = zeroIndexTuple.Item1;
            var calcPrevIdx = zeroIndexTuple.Item1 - 1;
            calcZeroIdx = CalcZeroCrossingIndex(calcPrevIdx, signal[calcPrevIdx], calcCurrentIdx,signal[calcCurrentIdx]);
        }
        midResults[2] = calcZeroIdx;
        return Convert.ToDouble(calcZeroIdx);
    }
    /// <summary>
    /// 计算回波时间索引(计算过零含小数索引)-位置法
    /// -1 表示信号过小
    /// </summary>
    /// <param name="signal">信号</param>
    /// <param name="posIndex">信号</param>
    /// <param name="midResults">中间值</param>
    /// <param name="noiseBase">噪声基线</param>
    public static double CalcZeroIndexByPosition(List<double> signal,int posIndex, out decimal[] midResults, double noiseBase = 10d)
    {
        midResults = new[] {0m, 0m, 0m, 0m};
        //1. 根据幅值判断信号是否有效
        var isValidSignal = signal.Any(s => Math.Abs(s) >= noiseBase);
        if (!isValidSignal)
        {
            return -1;//无效信号状态码
        }
        //2. 根据位置找上升沿过零点
        if (posIndex > 0)
        {
            midResults[0] = posIndex; 
            midResults[1] = (decimal) signal[posIndex];
        }

        var zeroIndexTuple = FindPositiveZeroCrossing(signal.ToArray(), posIndex);
        var calcZeroIdx = Convert.ToDecimal(zeroIndexTuple.Item1);
        if (zeroIndexTuple.Item1 > 0)
        {
            var calcCurrentIdx = zeroIndexTuple.Item1;
            var calcPrevIdx = zeroIndexTuple.Item1 - 1;
            calcZeroIdx = CalcZeroCrossingIndex(calcPrevIdx, signal[calcPrevIdx], calcCurrentIdx,signal[calcCurrentIdx]);
        }
        midResults[2] = calcZeroIdx;
        return Convert.ToDouble(calcZeroIdx);
    }
    /// <summary>
    /// 计算回波时间索引(计算过零含小数索引)-阈值法
    /// -1 表示信号过小
    /// -2 阈值异常
    /// </summary>
    /// <param name="signal">信号</param>
    /// <param name="midResults">中间值(x1,y1,x2,y2)</param>
    /// <param name="noiseBase">噪声基线</param>
    /// <param name="threshold">阈值</param>
    public static double CalcZeroIndexBySimpleThr(List<double> signal, out decimal[] midResults ,double noiseBase = 10d, short threshold = 0)
    {
        midResults = new[] {0m, 0m, 0m, 0m};
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
        midResults[0] = thrCrossIdx; midResults[1] = (decimal) signal[thrCrossIdx];
        var zeroIndexTuple = FindPositiveZeroCrossing(signal.ToArray(), thrCrossIdx);
        var calcZeroIdx = Convert.ToDecimal(zeroIndexTuple.Item1);
        if (zeroIndexTuple.Item1 > 0)
        {
            var calcCurrentIdx = zeroIndexTuple.Item1;
            var calcPrevIdx = zeroIndexTuple.Item1 - 1;
            calcZeroIdx = CalcZeroCrossingIndex(calcPrevIdx, signal[calcPrevIdx], calcCurrentIdx,signal[calcCurrentIdx]);
        }
        midResults[2] = calcZeroIdx;
        return Convert.ToDouble(calcZeroIdx);
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
    /// 获取 平滑数据
    /// </summary>
    public static double[] GetSmoothingData(double[] signal,int windowSize = 5)
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
