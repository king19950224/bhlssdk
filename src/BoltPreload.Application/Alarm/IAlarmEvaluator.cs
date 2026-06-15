namespace BoltPreload.Application.Alarm;

/// <summary>
/// 告警评估器。
/// <para>
/// 在 hot path 调用，结合内存状态机决定是否触发 / 闭合告警。
/// 实现要点（GB/T 41261-2022 / ISA-18.2）：
/// - <b>Deadband</b>：触发阈值和恢复阈值不重合，避免在临界值附近抖动；
/// - <b>On-delay</b>：连续 N 条都越限才触发，防止单条噪声触发；
/// - <b>Off-delay</b>：连续 M 条都恢复才闭合；
/// - <b>状态机持久化</b>：Phase 1 走内存（重启后从 DB 当前开启告警重建）；Phase 2 改 Redis。
/// </para>
/// </summary>
public interface IAlarmEvaluator
{
    /// <summary>
    /// 评估一次测量，可能产生 0~N 个告警事件（触发 / 闭合）。
    /// 实现必须线程安全（多个 ingestion task 可能并发调用）。
    /// </summary>
    IReadOnlyList<AlarmEvent> Evaluate(AlarmInput input);
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
