namespace FxDevice;

/// <summary>单颗螺栓的状态口径。大屏和桌面端共用这一份。</summary>
public static class Health
{
    public const string Ok = "ok";             // 正常
    public const string Warn = "warn";         // 预紧力偏低（< 额定 × warnRatio）
    public const string Loose = "loose";       // 松动报警（< 额定 × alarmRatio）
    public const string Fault = "fault";       // 检测异常：设备这帧没解出声时
    public const string NoProbe = "noprobe";   // 探头无信号：波形里连续多帧没有像样的回波（拔了/接错/耦合失效）
    public const string Shift = "shift";       // 基线失配：声时偏离置零基线 > 2 µs，探头挪过或重新耦合过
    public const string Uncal = "uncal";       // 未标定：测得到声时差但没有 .ucf，算不出力值
    public const string Unrated = "unrated";   // 未配额定：有标定、测得到变化量，但没有额定预紧力，判不了偏低/松动
    public const string BadBase = "badbase";   // 基线可疑：算出来的预紧力明显为负或远超额定，基线/参数/额定值有误
    public const string Rezero = "rezero";     // 事件专用：这一路的参考声时变了（现场重新置零）

    /// <summary>声时差超过 2000 ns 记基线失配。预紧力对应的声时差通常几十到几百 ns。</summary>
    public const decimal MaxPlausibleDeltaTofNs = 2000m;

    public static bool IsBaselineMismatch(decimal? deltaTof) => deltaTof is { } d && Math.Abs(d) > MaxPlausibleDeltaTofNs;

    /// <summary>估算预紧力超过额定的这个倍数记基线可疑。</summary>
    public const decimal OverRangeRatio = 1.3m;

    /// <summary>顺序：失配、无信号、异常、未标定、力值不可信、未配额定，然后才和阈值比。没有标定或额定时不返回「正常」。</summary>
    /// <param name="normal">设备状态字为 Normal。</param>
    /// <param name="mismatch">声时差超出物理上限（IsBaselineMismatch）。</param>
    /// <param name="presence">在位识别结论：probe / none / unknown。</param>
    /// <param name="seenProbe">这一路曾经判过有探头。没见过就不是「被拔出」。</param>
    /// <param name="estKn">估算预紧力（CalPlan.EstKn）。</param>
    public static string Judge(bool normal, bool mismatch, string presence, CalPlan plan, ushort ch, decimal? estKn, bool seenProbe)
    {
        if (mismatch)
        {
            return Shift;
        }

        if (presence == "none")
        {
            return seenProbe ? NoProbe : "pending";
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
        if (estKn is { } f)
        {
            // 低于 -10% 额定（无额定时 -10 kN）记基线可疑
            if (f < -NegativeTolerance(rated))
            {
                return BadBase;
            }

            // 超过额定 130% 视为基线有误，常见于改了回波窗没重新置零
            if (rated > 0 && f > rated * OverRangeRatio)
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

        // 没有额定值不返回「正常」
        if (rated <= 0)
        {
            return Unrated;
        }

        return Ok;
    }

    public static decimal NegativeTolerance(decimal rated) => rated > 0 ? rated * 0.1m : 10m;

    /// <summary>这些状态立刻记事件。</summary>
    public static bool IsUrgent(string health) => health is Loose or Fault or Shift or NoProbe or BadBase;

    /// <summary>状态词（报表、CSV、桌面端列表共用；页面上那份 HEALTH_TEXT 是同一套词）。</summary>
    public static string Text(string health) => health switch
    {
        Ok => "正常",
        Warn => "预紧力偏低",
        Loose => "松动报警",
        Fault => "检测异常",
        NoProbe => "探头无信号",
        Shift => "基线失配",
        BadBase => "基线可疑",
        Uncal => "未标定",
        Unrated => "未配额定",
        Rezero => "基线重置",
        "pending" => "未接入",
        _ => health,
    };
}
