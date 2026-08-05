namespace BoltPreload.Application.Alarm;

/// <summary>
/// 按螺栓解析生效的静态阈值告警参数（alarm_rules 表，三级 scope）。
/// <para>
/// 评估在测量热路径上（每条测量一次），<see cref="Resolve"/> 不许查库：实现持有整表内存快照，
/// 后台定期刷新（AlarmRuleRefreshService，30 秒一轮），规则批准/驳回时由端点调
/// <see cref="RefreshAsync"/> 立即生效。生效顺序：bolt 覆盖 &gt; group 覆盖 &gt; global &gt;
/// appsettings 出厂兜底——库里一条 active 规则都没有时行为与阈值进库前完全相同。
/// </para>
/// </summary>
public interface IAlarmRuleProvider
{
    /// <summary>解析该螺栓当前生效的阈值参数。线程安全，纯内存字典查找。</summary>
    StaticThresholdAlarmOptions Resolve(long boltId);

    /// <summary>重建规则快照。加载失败时保留旧快照并抛出，调用方决定是否向用户暴露。</summary>
    Task RefreshAsync(CancellationToken ct = default);
}
