namespace BoltPreload.Application.Alarm;

/// <summary>
/// 告警事件持久化抽象。
/// 让 evaluator 和 db 解耦，方便单测和未来切换存储后端。
/// </summary>
public interface IAlarmSink
{
    /// <summary>
    /// 持久化一个告警事件。Trigger 写新行；Clear 更新对应 type 当前开启告警的 cleared_at。
    /// <para><b>契约（C2）：持久化失败必须抛出异常</b>，不得静默吞——调用方需据此回滚状态机或重试，
    /// 否则会"状态机已 active 但 DB 无告警"导致永久漏报。</para>
    /// </summary>
    Task PersistAsync(AlarmEvent ev, CancellationToken ct = default);

    /// <summary>
    /// 去重补写一条 critical 告警（C1，WAL 重放兜底用）：仅当同 (bolt_id, type) 当前
    /// <b>无未闭合告警</b>时才写入，避免重放多条越限测量产生重复告警。返回是否实际写入了新行。
    /// </summary>
    Task<bool> PersistCriticalIfAbsentAsync(AlarmEvent ev, CancellationToken ct = default);

    /// <summary>启动时拉取所有未闭合告警（用于重建评估器内存状态）</summary>
    Task<IReadOnlyList<(long BoltId, string Type)>> LoadOpenAsync(CancellationToken ct = default);
}
