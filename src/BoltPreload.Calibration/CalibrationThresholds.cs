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

    // ===== 采样稳定性护栏（calibration-hardening-design §2）=====
    //
    // 标定输入取的是"窗口平均声时"，窗口里混进拧紧过程/松弛尾巴的数据，平均值会落在
    // 两个受力状态之间，K_L 跟着错——2026-08-12 真机实测里四个通道标出的 K_L 相差 20 倍，
    // 根因全是这一个。两条判据必须同时达标：
    //   stddev 抓"信号本身不稳"（耦合差/干扰/正在扳动）；
    //   前后半窗均值差抓"单调趋势"（爬坡/松弛/混档——实测松弛中的窗口 stddev 只有 0.53ns
    //   能溜过第一条，半窗差 1.2ns 才拦得住，所以缺一不可）。
    // 0.6ns 的论证：真稳态实测 stddev≈0.23ns、半窗差<0.3ns（放行余量足）；折算到力
    // （按正确量级 K_L≈1.4e-3 ns/N）约 0.4kN，相对扭矩法 ±30% 固有不确定度可忽略——收更紧没有意义。

    /// <summary>采样窗口内声时标准差上限（ns）：超过视为信号不稳，拒绝用作标定输入。</summary>
    public const decimal EchoStddevMaxNs = 0.6m;

    /// <summary>前半窗与后半窗平均声时之差上限（ns）：超过说明窗口内有单调趋势（还没稳），拒绝。</summary>
    public const decimal EchoHalfWindowDriftMaxNs = 0.6m;

    // 在役置零（expectZeroLoad=false）：不拧螺栓、只记当前为相对原点。
    // 0.6ns 是实验室拧松/拧紧窗口用的——比设备量化步长（IotR24 回波声时 0.625ns）还紧，
    // 真机现场 sd 常态 1~2.5ns（CH1 实测），0.6 会把向导永远锁在"请保持静置等稳"。
    // 3ns / 5ns 仍能拦住有人正在扳动（约数 kN 的单调变化），放过探头自然抖动。
    /// <summary>在役置零允许的窗口标准差上限（ns）。</summary>
    public const decimal InServiceEchoStddevMaxNs = 3.0m;

    /// <summary>在役置零允许的半窗趋势上限（ns）。</summary>
    public const decimal InServiceEchoHalfWindowDriftMaxNs = 5.0m;
}
