namespace BoltPreload.Calibration;

/// <summary>
/// 标定拟合优度门槛，全项目唯一一份（plan §8.5 / 附录 B 步骤 5）。
/// <para>
/// 0.98（合格）/ 0.95（拒绝下限）是工程经验阈值 —— GB/T 43232 对拟合 R² 无规定，
/// 取值依据程芳玲 2024 实测（纵波 R²=0.9998）+ 通用测量学经验。
/// </para>
/// <list type="bullet">
/// <item>R² ≥ 0.98：通过</item>
/// <item>0.95 ≤ R² &lt; 0.98：警告，可作"现场临时"标定但 passed=false，建议补采载荷点重拟合</item>
/// <item>R² &lt; 0.95：拒绝，线性度不达标，必须补点或全部重做</item>
/// </list>
/// <para>
/// 收口之前这两个数在标定端点里有两份 const、验收报告里还有四处字面量。
/// 红线要"可追溯"，就不能允许散落点各改各的。
/// </para>
/// </summary>
public static class CalibrationThresholds
{
    /// <summary>合格线：R² 达到即 passed=true。</summary>
    public const decimal R2Pass = 0.98m;

    /// <summary>拒绝下限：低于此值直接拒绝应用为标定。</summary>
    public const decimal R2Reject = 0.95m;

    /// <summary>
    /// <see cref="R2Pass"/> 的 double 形式。拟合结果 <see cref="ICalibrationFitter"/>.RSquared
    /// 是 double，而端点里算的是 decimal，两种类型都要用到同一条线。
    /// </summary>
    public const double R2PassDouble = 0.98;

    /// <summary><see cref="R2Reject"/> 的 double 形式。</summary>
    public const double R2RejectDouble = 0.95;
}
