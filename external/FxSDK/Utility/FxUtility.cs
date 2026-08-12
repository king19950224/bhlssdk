using System.Diagnostics;

namespace FxSDK.Utility;

public enum TempTauProfile
{
    Direct = 0,
    Fast = 1,
    Balanced = 2,
    Stable = 3,
    Conservative = 4
}

public static class FxUtility
{
    /// <summary>
    /// 开发者模式
    /// </summary>
    public static bool DeveloperMode { get; set; }
    /// <summary>
    /// 默认最小有效温度
    /// </summary>
    public const decimal DefaultMinTemp = -40M;
    /// <summary>
    /// 默认最大有效温度
    /// </summary>
    public const decimal DefaultMaxTemp = 120M;
    /// <summary>
    /// 当前生效的最小温度
    /// </summary>
    public static decimal MinTemp { get; set; } = DefaultMinTemp;
    /// <summary>
    /// 当前生效的最大温度
    /// </summary>
    public static decimal MaxTemp { get; set; } = DefaultMaxTemp;
    /// <summary>
    /// 默认温度 Tau 策略。
    /// </summary>
    public static TempTauProfile TempTauProfile { get; set; } = TempTauProfile.Balanced;
    /// <summary>
    /// 通道没有自身温度 Tau 系数时，按全局策略解析默认值。
    /// </summary>
    public static decimal ResolveTempTauSeconds(decimal? channelTauSeconds)
    {
        return channelTauSeconds.HasValue
            ? Math.Max(0M, channelTauSeconds.Value)
            : GetDefaultTempTauSeconds(TempTauProfile);
    }
    /// <summary>
    /// 获取温度 Tau 策略对应的一阶响应时间常数，单位秒。
    /// </summary>
    public static decimal GetDefaultTempTauSeconds(TempTauProfile profile)
    {
        return profile switch
        {
            TempTauProfile.Direct => 0M,
            TempTauProfile.Fast => 60M,
            TempTauProfile.Stable => 480M,
            TempTauProfile.Conservative => 900M,
            _ => 180M
        };
    }
    public const string Delay500Ms = "#Fx-Delay-500#";
    public const string Delay100Ms = "#Fx-Delay-100#";
    public const string Delay50Ms = "#Fx-Delay-50#";
    public const string Delay10Ms = "#Fx-Delay-10#";
    /// <summary>
    /// 休眠一个时间
    /// </summary>
    /// <param name="ms">毫秒</param>
    public static void Sleep(int ms)
    {
        var sw = new Stopwatch();
        sw.Start();
        Thread.Sleep(ms);
        while (sw.ElapsedMilliseconds < ms)
        {
            var timeout = ms - (int)sw.ElapsedMilliseconds;
            Thread.Sleep(timeout >= 0 ? timeout : 0);
        }
        sw.Stop();
    }
    
}
