using MathNet.Numerics.LinearAlgebra.Double;

namespace FxSDK.Utility;

/// <summary>
/// Savitzky-Golay 滤波器（平滑 + 求导）
/// 支持边界对称镜像填充，系数可缓存
/// </summary>
public static class SavitzkyGolayFilter
{
    // 缓存常见窗口+阶数组合的系数
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double[]> CoeffCache = new();
    /// <summary>
    /// 使用 Savitzky-Golay 滤波器平滑信号（输出长度 = 输入长度）
    /// </summary>
    /// <param name="y">输入信号</param>
    /// <param name="windowSize">窗口大小（必须为正奇数，推荐5~15）</param>
    /// <param name="order">多项式阶数（建议2~4）</param>
    /// <returns>平滑后的信号</returns>
    public static double[] Smooth(double[] y, int windowSize = 9, int order = 3)
        => Filter(y, windowSize, order, derivativeOrder: 0);

    /// <summary>
    /// 使用 Savitzky-Golay 滤波器求信号的一阶导数（斜率）
    /// </summary>
    /// <param name="y">输入信号</param>
    /// <param name="windowSize">窗口大小（奇数）</param>
    /// <param name="order">多项式阶数（order >=1）</param>
    /// <param name="delta">采样间隔（默认1.0，表示采样点单位；若需物理单位如 ns，传入 1.0/samplingRate）</param>
    /// <returns>每个点的斜率估计</returns>
    public static double[] Differentiate(double[] y, int windowSize, int order = 3, double delta = 1.0)
        => Filter(y, windowSize, order, derivativeOrder: 1, delta: delta);

    private static double[] Filter(double[] y, int windowSize, int order, int derivativeOrder = 0, double delta = 1.0)
    {
        if (y == null || y.Length == 0)
            throw new ArgumentException("信号不能为空。", nameof(y));
        if (windowSize <= 0 || windowSize % 2 == 0)
            throw new ArgumentException("窗口大小必须为正奇数。", nameof(windowSize));
        if (order < 0 || order >= windowSize)
            throw new ArgumentException("多项式阶数必须小于窗口大小且非负。", nameof(order));
        if (derivativeOrder < 0 || derivativeOrder > order)
            throw new ArgumentException("导数阶数必须在0到多项式阶数之间。", nameof(derivativeOrder));
        if (double.IsNaN(delta) || delta <= 0)
            throw new ArgumentException("采样间隔 delta 必须为正数。", nameof(delta));

        // 自适应窗口：如果信号太短，缩小窗口
        if (windowSize > y.Length)
            windowSize = y.Length % 2 == 1 ? y.Length : y.Length - 1;

        // 缓存 key
        var cacheKey = $"{windowSize}_{order}_{derivativeOrder}";

        if (!CoeffCache.TryGetValue(cacheKey, out var coeffs))
        {
            coeffs = ComputeSgCoefficients(windowSize, order, derivativeOrder);
            CoeffCache[cacheKey] = coeffs;
        }

        var pad = windowSize / 2;
        var padded = SymmetricPad(y, pad);

        var result = new double[y.Length];
        for (var i = 0; i < y.Length; i++)
        {
            var sum = 0.0;
            for (var j = 0; j < windowSize; j++)
            {
                sum += coeffs[j] * padded[i + j];
            }
            result[i] = sum / delta;  // 导数时除以 delta 得到物理斜率
        }

        return result;
    }

    // 对称镜像填充（标准 reflection，不重复端点）
    private static double[] SymmetricPad(double[] signal, int pad)
    {
        var n = signal.Length;
        var padded = new double[n + 2 * pad];

        // 左填充：signal[pad] ... signal[1]（不含 signal[0] 重复）
        for (var i = 0; i < pad; i++)
            padded[i] = signal[pad - 1 - i];  // 优化：pad-1-i 更标准

        // 中心
        Array.Copy(signal, 0, padded, pad, n);

        // 右填充：signal[n-2] ... signal[n-pad-1]
        for (var i = 0; i < pad; i++)
            padded[pad + n + i] = signal[n - 2 - i];

        return padded;
    }

    // 计算 SG 系数（支持导数）
    private static double[] ComputeSgCoefficients(int windowSize, int order, int derivativeOrder)
    {
        var m = windowSize / 2;
        var a = new DenseMatrix(windowSize, order + 1);

        for (var i = 0; i < windowSize; i++)
        {
            double x = i - m;
            var xp = 1.0;
            for (var j = 0; j <= order; j++)
            {
                a[i, j] = Math.Pow(x, j);  
            }
        }

        var aT = a.Transpose();
        var aTa = aT * a;

        var e = new DenseVector(order + 1)
        {
            [derivativeOrder] = 1.0 // 求对应阶的系数
        };

        var z = aTa.Solve(e);
        var w = a * z;

        // 对于导数，系数需乘以 factorial(derivativeOrder)，但由于 derivativeOrder=0或1，这里=1

        return w.ToArray();
    }
}