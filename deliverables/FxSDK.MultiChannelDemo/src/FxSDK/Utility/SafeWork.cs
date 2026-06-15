

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
}