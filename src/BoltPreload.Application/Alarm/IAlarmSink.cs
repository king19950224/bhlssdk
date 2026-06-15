namespace BoltPreload.Application.Alarm;

/// <summary>
/// 告警事件持久化抽象。
/// 让 evaluator 和 db 解耦，方便单测和未来切换存储后端。
/// </summary>
public interface IAlarmSink
{
    /// <summary>
    /// 持久化一个告警事件。Trigger 写新行；Clear 更新对应 type 当前开启告警的 cleared_at。
    /// </summary>
    Task PersistAsync(AlarmEvent ev, CancellationToken ct = default);

    /// <summary>启动时拉取所有未闭合告警（用于重建评估器内存状态）</summary>
    Task<IReadOnlyList<(long BoltId, string Type)>> LoadOpenAsync(CancellationToken ct = default);
}
