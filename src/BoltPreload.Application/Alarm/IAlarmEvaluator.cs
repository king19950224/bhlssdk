namespace BoltPreload.Application.Alarm;

/// <summary>
/// 告警评估器。
/// <para>
/// 在 hot path 调用，结合内存状态机决定是否触发 / 闭合告警。
/// 实现要点（GB/T 41261-2022 / ISA-18.2）：
/// - <b>Deadband</b>：触发阈值和恢复阈值不重合，避免在临界值附近抖动；
/// - <b>On-delay</b>：连续 N 条都越限才触发，防止单条噪声触发；
/// - <b>Off-delay</b>：连续 M 条都恢复才闭合；
/// - <b>状态机存储</b>：内存态（进程启动时从 DB 当前开启告警重建，见 Program 启动流程）；
///   单站部署下无需外部存储，多实例部署时才需要考虑共享存储。
/// </para>
/// </summary>
public interface IAlarmEvaluator
{
    /// <summary>
    /// 评估一次测量，可能产生 0~N 个告警事件（触发 / 闭合）。
    /// 实现必须线程安全（多个 ingestion task 可能并发调用）。
    /// </summary>
    IReadOnlyList<AlarmEvent> Evaluate(AlarmInput input);

    /// <summary>
    /// 无状态安全评估（C1）：仅判断当前样本是否处于"立即危险"的 critical 档（预紧力脱落 / 严重越限），
    /// <b>不读写内部状态机</b>。用于 WAL 重放等"不能喂有状态状态机"的场景做兜底补判——
    /// DB 故障窗口内落 WAL 的测量在补写时若处于危险态，必须仍能补发告警，否则会永久漏报。
    /// 返回 <c>null</c> 表示当前样本未达 critical 危险档。
    /// </summary>
    AlarmEvent? EvaluateCriticalStateless(AlarmInput input);

    /// <summary>
    /// 回滚最近一次 <see cref="Evaluate"/> 产生的状态转移（C2）。
    /// 当告警<b>持久化失败</b>时由调用方触发：使状态机回到"差一格即可重新触发/恢复"的保守态，
    /// 让后续测量能立即重试，杜绝"状态机已置 active 但 DB 无对应告警行"导致的永久漏报。
    /// 实现必须线程安全。
    /// </summary>
    void RollbackTransition(AlarmEvent ev);
}

/// <summary>评估器输入</summary>
public sealed record AlarmInput(
    long BoltId,
    long ChannelId,
    DateTimeOffset Ts,
    decimal? PreloadN,
    decimal? PreloadRatio,
    decimal? TargetPreloadN,
    short SdkStatusCode);

/// <summary>
/// 告警事件（评估器产出）。Persistence 层和 publisher 层分别处理。
/// </summary>
/// <param name="Action">Trigger=新触发；Clear=自动恢复</param>
/// <param name="BoltId">螺栓 ID</param>
/// <param name="ChannelId">通道 ID</param>
/// <param name="Type">告警类型（preload_low / preload_high / signal_lost）</param>
/// <param name="Severity">严重程度（info / warn / critical）</param>
/// <param name="Ts">事件时间</param>
/// <param name="PreloadN">触发时预紧力快照</param>
/// <param name="PreloadRatio">触发时 ratio 快照</param>
/// <param name="TargetPreloadN">目标预紧力</param>
/// <param name="ThresholdLowRatio">下限阈值</param>
/// <param name="ThresholdHighRatio">上限阈值</param>
/// <param name="ConsecutiveCount">触发时连续越限条数（on-delay 累积值）</param>
/// <param name="Message">人话描述</param>
public sealed record AlarmEvent(
    AlarmAction Action,
    long BoltId,
    long ChannelId,
    string Type,
    string Severity,
    DateTimeOffset Ts,
    decimal? PreloadN,
    decimal? PreloadRatio,
    decimal? TargetPreloadN,
    decimal ThresholdLowRatio,
    decimal ThresholdHighRatio,
    int ConsecutiveCount,
    string Message);

public enum AlarmAction { Trigger, Clear }
