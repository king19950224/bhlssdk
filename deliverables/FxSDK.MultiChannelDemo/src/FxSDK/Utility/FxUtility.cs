using System.Diagnostics;

namespace FxSDK.Utility;

public static class FxUtility
{
    /// <summary>
    /// 开发者模式
    /// </summary>
    public static bool DeveloperMode { get; set; }
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