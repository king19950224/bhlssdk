using BoltPreload.Application.Alarm;

namespace BoltPreload.Application.Notifications;

/// <summary>
/// 告警事件 → 通知渠道的匹配（notification_rules 表）。
/// <para>
/// 在告警落库事务里被调用（PostgresAlarmSink），不许查库：实现持有渠道/规则/螺栓映射的
/// 内存快照，NotificationDispatcherService 定期刷新、配置端点变更后显式刷新。
/// 快照未加载或一条规则不中时返回 null，发件箱一行都不写——热路径零额外开销。
/// </para>
/// </summary>
public interface INotificationRouter
{
    /// <summary>
    /// 匹配该事件要通知的渠道。null = 无需通知。
    /// Clear 事件仅命中 notify_on_clear 的规则（不做级别过滤，规则勾了就发）；
    /// critical 事件无视规则的静默窗。
    /// </summary>
    NotificationMatch? Match(AlarmEvent ev);

    /// <summary>重建渠道/规则/螺栓映射快照。失败保留旧快照并抛出。</summary>
    Task RefreshAsync(CancellationToken ct = default);
}

/// <summary>
/// 匹配结果：去重后的渠道 ID 集合 + 事件快照要用的螺栓展示信息
/// （payload 入队即定格，事后改名/删档不影响已入队的通知内容）。
/// </summary>
public sealed record NotificationMatch(IReadOnlyList<long> ChannelIds, string BoltCode, string? GroupName);
