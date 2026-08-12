using System;

namespace FxSDK.Utility;

/// <summary>
/// Savitzky-Golay 平滑滤波器
/// 原理：局部多项式最小二乘拟合，保留信号特征的同时平滑噪声
/// </summary>
public static class SavitzkyGolayFilter
{
    /// <summary>
    /// SG 平滑滤波
    /// </summary>
    /// <param name="data">输入数据</param>
    /// <param name="windowSize">滑动窗口大小 (必须为奇数，>= 3)</param>
    /// <param name="polynomialOrder">多项式阶数 (必须 < windowSize )</param>
    /// <returns>平滑后的数据</returns>
    public static double[] Smooth(double[] data, int windowSize = 5, int polynomialOrder = 2)
    {
        if (data == null || data.Length == 0)
            throw new ArgumentException("输入数据不能为空");
        
        if (windowSize < 3)
            throw new ArgumentException("窗口大小必须 >= 3");
        
        if (windowSize % 2 == 0)
            throw new ArgumentException("窗口大小必须为奇数");
        
        if (polynomialOrder < 0 || polynomialOrder >= windowSize)
            throw new ArgumentException("多项式阶数必须 >= 0 且 < 窗口大小");
        
        var n = data.Length;
        var halfWindow = windowSize / 2;
        
        // 计算 SG 系数
        var coeffs = ComputeCoefficients(windowSize, polynomialOrder);
        
        // 应用滤波
        var result = new double[n];
        
        // 中间部分：使用完整窗口
        for (var i = halfWindow; i < n - halfWindow; i++)
        {
            double sum = 0;
            for (var j = -halfWindow; j <= halfWindow; j++)
                sum += coeffs[j + halfWindow] * data[i + j];
            result[i] = sum;
        }
        
        // 边界处理：使用对称延拓
        for (var i = 0; i < halfWindow; i++)
        {
            double sum = 0;
            for (var j = -halfWindow; j <= halfWindow; j++)
            {
                var idx = i + j;
                // 对称延拓
                if (idx < 0) idx = -idx;
                if (idx >= n) idx = 2 * n - 2 - idx;
                sum += coeffs[j + halfWindow] * data[idx];
            }
            result[i] = sum;
        }
        
        for (var i = n - halfWindow; i < n; i++)
        {
            double sum = 0;
            for (var j = -halfWindow; j <= halfWindow; j++)
            {
                var idx = i + j;
                // 对称延拓
                if (idx < 0) idx = -idx;
                if (idx >= n) idx = 2 * n - 2 - idx;
                sum += coeffs[j + halfWindow] * data[idx];
            }
            result[i] = sum;
        }
        
        return result;
    }
    
    /// <summary>
    /// 计算 SG 滤波系数
    /// </summary>
    private static double[] ComputeCoefficients(int windowSize, int polynomialOrder)
    {
        var halfWindow = windowSize / 2;
        var m = polynomialOrder;
        
        // 构建 Vandermonde 矩阵 A (windowSize × (m+1))
        // A[i,j] = i^j, i = -halfWindow ... halfWindow
        var a = new double[windowSize, m + 1];
        for (var i = 0; i < windowSize; i++)
        {
            var x = i - halfWindow;
            a[i, 0] = 1;
            for (var j = 1; j <= m; j++)
                a[i, j] = a[i, j - 1] * x;
        }
        
        // 计算 (A^T * A)^(-1) * A^T
        // 使用正规方程：(A^T * A) * c = A^T * y
        var ata = new double[m + 1, m + 1];
        for (var i = 0; i <= m; i++)
            for (var j = 0; j <= m; j++)
                for (var k = 0; k < windowSize; k++)
                    ata[i, j] += a[k, i] * a[k, j];
        
        // 矩阵求逆 (高斯 - 约旦消元)
        var ataInv = MatrixInverse(ata, m + 1);
        
        // 计算系数：c = (A^T * A)^(-1) * A^T
        // 对于平滑，我们只需要第 0 行 (常数项)
        var coeffs = new double[windowSize];
        for (var i = 0; i < windowSize; i++)
        {
            double sum = 0;
            for (var j = 0; j <= m; j++)
                sum += ataInv[0, j] * a[i, j];
            coeffs[i] = sum;
        }
        
        return coeffs;
    }
    
    /// <summary>
    /// 矩阵求逆 (高斯 - 约旦消元法)
    /// </summary>
    private static double[,] MatrixInverse(double[,] matrix, int n)
    {
        // 创建增广矩阵 [A|I]
        var augmented = new double[n, 2 * n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
                augmented[i, j] = matrix[i, j];
            augmented[i, n + i] = 1;
        }
        
        // 高斯 - 约旦消元
        for (var i = 0; i < n; i++)
        {
            // 找主元
            var maxRow = i;
            for (int k = i + 1; k < n; k++)
                if (Math.Abs(augmented[k, i]) > Math.Abs(augmented[maxRow, i]))
                    maxRow = k;
            
            // 交换行
            if (maxRow != i)
            {
                for (var j = 0; j < 2 * n; j++)
                {
                    (augmented[i, j], augmented[maxRow, j]) = (augmented[maxRow, j], augmented[i, j]);
                }
            }
            
            // 缩放主元行
            var pivot = augmented[i, i];
            if (Math.Abs(pivot) < 1e-12)
                throw new InvalidOperationException("矩阵奇异，无法求逆");
            
            for (var j = 0; j < 2 * n; j++)
                augmented[i, j] /= pivot;
            
            // 消去其他行
            for (var k = 0; k < n; k++)
            {
                if (k != i)
                {
                    var factor = augmented[k, i];
                    for (var j = 0; j < 2 * n; j++)
                        augmented[k, j] -= factor * augmented[i, j];
                }
            }
        }
        
        // 提取逆矩阵
        var result = new double[n, n];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                result[i, j] = augmented[i, n + j];
        
        return result;
    }
    
    /// <summary>
    /// SG 平滑并计算导数
    /// </summary>
    /// <param name="data">输入数据</param>
    /// <param name="windowSize">窗口大小(必须是奇数)</param>
    /// <param name="polynomialOrder">多项式阶数</param>
    /// <param name="derivative">导数阶数 (0=平滑，1=一阶导，2=二阶导)</param>
    /// <param name="sampleInterval">采样间隔 (用于导数计算)</param>
    /// <returns>平滑后的数据或导数</returns>
    public static double[] SmoothWithDerivative(double[] data, int windowSize = 5, int polynomialOrder = 2, 
        int derivative = 0, double sampleInterval = 1.0)
    {
        if (data == null || data.Length == 0)
            throw new ArgumentException("输入数据不能为空");
        
        if (windowSize < 3 || windowSize % 2 == 0)
            throw new ArgumentException("窗口大小必须为奇数且 >= 3");
        
        if (polynomialOrder < derivative || polynomialOrder >= windowSize)
            throw new ArgumentException("多项式阶数必须 >= 导数阶数且 < 窗口大小");
        
        var n = data.Length;
        var halfWindow = windowSize / 2;
        
        // 计算导数系数
        var coeffs = ComputeDerivativeCoefficients(windowSize, polynomialOrder, derivative, sampleInterval);
        
        var result = new double[n];
        
        // 中间部分
        for (var i = halfWindow; i < n - halfWindow; i++)
        {
            double sum = 0;
            for (var j = -halfWindow; j <= halfWindow; j++)
                sum += coeffs[j + halfWindow] * data[i + j];
            result[i] = sum;
        }
        
        // 边界处理 (对称延拓)
        for (var i = 0; i < halfWindow; i++)
        {
            double sum = 0;
            for (var j = -halfWindow; j <= halfWindow; j++)
            {
                var idx = i + j;
                if (idx < 0) idx = -idx;
                if (idx >= n) idx = 2 * n - 2 - idx;
                sum += coeffs[j + halfWindow] * data[idx];
            }
            result[i] = sum;
        }
        
        for (var i = n - halfWindow; i < n; i++)
        {
            double sum = 0;
            for (var j = -halfWindow; j <= halfWindow; j++)
            {
                var idx = i + j;
                if (idx < 0) idx = -idx;
                if (idx >= n) idx = 2 * n - 2 - idx;
                sum += coeffs[j + halfWindow] * data[idx];
            }
            result[i] = sum;
        }
        
        return result;
    }
    
    /// <summary>
    /// 计算导数系数
    /// </summary>
    private static double[] ComputeDerivativeCoefficients(int windowSize, int polynomialOrder, 
        int derivative, double sampleInterval)
    {
        var halfWindow = windowSize / 2;
        var m = polynomialOrder;
        
        // 构建 Vandermonde 矩阵
        var a = new double[windowSize, m + 1];
        for (var i = 0; i < windowSize; i++)
        {
            var x = i - halfWindow;
            a[i, 0] = 1;
            for (var j = 1; j <= m; j++)
                a[i, j] = a[i, j - 1] * x;
        }
        
        // 计算 (A^T * A)^(-1)
        var ata = new double[m + 1, m + 1];
        for (var i = 0; i <= m; i++)
        for (var j = 0; j <= m; j++)
        for (var k = 0; k < windowSize; k++)
            ata[i, j] += a[k, i] * a[k, j];
        
        var ataInv = MatrixInverse(ata, m + 1);
        
        // 计算导数系数
        // 对于 d 阶导数，需要乘以 d! / (dt^d)
        double factorial = 1;
        for (var i = 2; i <= derivative; i++)
            factorial *= i;
        
        var scale = factorial / Math.Pow(sampleInterval, derivative);
        
        var coeffs = new double[windowSize];
        for (var i = 0; i < windowSize; i++)
        {
            double sum = 0;
            for (var j = derivative; j <= m; j++)
                sum += ataInv[derivative, j] * a[i, j];
            coeffs[i] = sum * scale;
        }
        
        return coeffs;
    }
}
