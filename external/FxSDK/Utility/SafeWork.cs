

namespace FxSDK.Utility;

public class SafeWork
{
    public static decimal? ToNullableDecimal(double? value)
    {
        if (!value.HasValue)
            return null;
        var d = value.Value;
        if (d > (double)decimal.MaxValue || d < (double)decimal.MinValue)
            return null;
        try
        {
            return (decimal)d;
        }
        catch
        {
            return null;
        }
    }
    /// <summary>
    /// 安全地将对象解析为双精度浮点数
    /// </summary>
    /// <param name="value">传入的值 (可能是 string, int, DBNull, 或 null)</param>
    /// <param name="result">输出的数值</param>
    /// <returns>解析是否成功</returns>
    public static bool SafeParseDouble(object? value, out double result)
    {
        result = 0;
        // 1. 处理 null 和 DBNull
        if (value == null || value == DBNull.Value)
            return false;
        // 2. 处理空字符串
        var strValue = value.ToString()?.Trim();
        
        if (string.IsNullOrEmpty(strValue))
            return false;
        // 3. 尝试转换
        return double.TryParse(strValue, out result);
    }
}