/// <summary>
/// 单颗螺栓的状态口径 —— 全系统只有这一份。服务端每帧判一次，帧、历史采样、事件、页面显示都照抄它，
/// 页面不再自己重算（之前页面和历史库各判一遍，页面知道"探头无信号"而历史库只会记"检测异常"，
/// 业主看到的和报表里写的对不上）。
/// </summary>
internal static class Health
{
    public const string Ok = "ok";             // 正常
    public const string Warn = "warn";         // 预紧力偏低（< 额定 × warnRatio）
    public const string Loose = "loose";       // 松动报警（< 额定 × alarmRatio）
    public const string Fault = "fault";       // 检测异常：设备这帧没解出声时
    public const string NoProbe = "noprobe";   // 探头无信号：波形里连续多帧没有像样的回波（拔了/接错/耦合失效）
    public const string Shift = "shift";       // 基线失配：声时偏离置零基线 > 2 µs，探头挪过或重新耦合过
    public const string Uncal = "uncal";       // 未标定：测得到声时差但没有 .ucf，算不出力值
    public const string BadBase = "badbase";   // 基线可疑：算出来的预紧力明显为负，基线或额定值有误
    public const string Rezero = "rezero";     // 事件专用：这一路的参考声时变了（现场重新置零）

    /// <summary>
    /// 判定顺序：先看测不测得到（shift / noprobe / fault），再看有没有标定（uncal），
    /// 再看力值是否物理上可信（badbase），最后才拿力值比阈值（loose / warn / ok）。
    /// 没有标定文件时绝不给出"正常/偏低/松动"这种需要力值才能下的结论；没有额定值时不判偏低/松动。
    /// </summary>
    /// <param name="normal">设备状态字为 Normal。</param>
    /// <param name="mismatch">声时差超出物理上限（HistoryStore.IsBaselineMismatch）。</param>
    /// <param name="presence">在位识别结论：probe / none / unknown。</param>
    /// <param name="kn">服务端算好的预紧力（CalPlan.ForceKn），未标定/估不出为空。</param>
    public static string Judge(bool normal, bool mismatch, string presence, CalPlan plan, ushort ch, decimal? kn)
    {
        if (mismatch)
        {
            return Shift;
        }

        if (presence == "none")
        {
            return NoProbe;
        }

        if (!normal)
        {
            return Fault;
        }

        if (plan.CalFor(ch) is null)
        {
            return Uncal;
        }

        var rated = plan.RatedFor(ch);
        if (kn is { } f)
        {
            // 预紧力物理上不会是负的。锚定口径下为负 = 较锚定时丢了超过 100% 的假定预紧力；自由口径下为负 = k·Δt+b < 0。
            // 两种都只可能是基线不对（置零后拧过、探头挪过）或额定值给错了——这是运维要处理的设置问题，
            // 不是业主该看到的"松动报警"。留一点噪声余量：额定的 10%（没额定值时 10 kN）
            if (f < -NegativeTolerance(rated))
            {
                return BadBase;
            }

            if (rated > 0)
            {
                if (f < rated * plan.AlarmRatio)
                {
                    return Loose;
                }

                if (f < rated * plan.WarnRatio)
                {
                    return Warn;
                }
            }
        }

        return Ok;
    }

    public static decimal NegativeTolerance(decimal rated) => rated > 0 ? rated * 0.1m : 10m;

    /// <summary>需要立刻记事件、不做 10 秒去抖的状态：恶化成报警/异常的一刻就得留痕。</summary>
    public static bool IsUrgent(string health) => health is Loose or Fault or Shift or NoProbe or BadBase;
}
