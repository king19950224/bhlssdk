namespace FxSDK.Models;

/// <summary>
/// 测量状态码。数据库和第三方接口仍使用对应的 short 数值。
/// </summary>
public enum MeasStatusCode : short
{
    Normal = 0,

    SignalTooSmall = -1,
    ThresholdNotReached = -2,
    SignalOverflow = -3,
    SimilarityLow = -4,
    UnknownSignalError = -5,
    SnrLow = -6,

    BelowLowerLimit = 11,
    AboveUpperLimit = 12,
}

public static class MeasStatusCodeExtensions
{
    /// <summary>
    /// 信号是否有效。超限值依然是有效测量结果，只是业务状态异常。
    /// </summary>
    public static bool IsSignalValid(this MeasStatusCode status) => (short)status >= 0;

    public static bool IsSignalInvalid(this MeasStatusCode status) => (short)status < 0;

    public static bool IsNormal(this MeasStatusCode status) => status == MeasStatusCode.Normal;

    public static bool IsLimitWarning(this MeasStatusCode status)
        => status is MeasStatusCode.BelowLowerLimit or MeasStatusCode.AboveUpperLimit;

    public static short ToCode(this MeasStatusCode status) => (short)status;

    public static MeasStatusCode FromZeroIndex(double zeroIndex)
        => zeroIndex >= 0 ? MeasStatusCode.Normal : (MeasStatusCode)Convert.ToInt16(zeroIndex);
}
