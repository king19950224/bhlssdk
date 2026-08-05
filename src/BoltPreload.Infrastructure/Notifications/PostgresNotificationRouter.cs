using BoltPreload.Application.Alarm;
using BoltPreload.Application.Notifications;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BoltPreload.Infrastructure.Notifications;

/// <summary>
/// <see cref="INotificationRouter"/> 的 Postgres 实现：启用渠道的启用规则 + bolt→(code, group)
/// 映射整表进内存快照，Match 纯内存判定。刷新失败保留旧快照。
/// 刚建档的螺栓在下一轮刷新前查不到映射，此时按"入队但螺栓显示为 bolt#id、部位过滤视为不命中组规则"
/// 处理——宁可少发一条组规则通知，也不因映射缺失把告警通知整个吞掉（全站规则照发）。
/// </summary>
public sealed class PostgresNotificationRouter : INotificationRouter, IDisposable
{
    private static readonly Dictionary<string, int> SeverityRank = new(StringComparer.Ordinal)
    {
        ["info"] = 0,
        ["warn"] = 1,
        ["critical"] = 2,
    };

    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<PostgresNotificationRouter> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private volatile Snapshot _snapshot = Snapshot.Empty;

    public PostgresNotificationRouter(NpgsqlDataSource ds, ILogger<PostgresNotificationRouter> logger)
    {
        _ds = ds;
        _logger = logger;
    }

    public void Dispose() => _refreshGate.Dispose();

    public NotificationMatch? Match(AlarmEvent ev)
    {
        var s = _snapshot;
        if (s.Rules.Count == 0)
            return null;

        s.Bolts.TryGetValue(ev.BoltId, out var bolt);
        var severityRank = SeverityRank.TryGetValue(ev.Severity, out var r) ? r : SeverityRank["warn"];
        // 静默窗判定用"入队此刻"的服务器本地时间：值班静默关心的是什么时候被吵，不是事件发生在几点
        var localNow = DateTime.Now.TimeOfDay;

        List<long>? channels = null;
        foreach (var rule in s.Rules)
        {
            if (ev.Action == AlarmAction.Clear)
            {
                if (!rule.NotifyOnClear) continue;
            }
            else
            {
                if (severityRank < rule.MinSeverityRank) continue;
                // critical 无视静默窗：夜里两点的脱落就是要把人叫醒
                if (ev.Severity != "critical" && rule.InQuietWindow(localNow)) continue;
            }

            if (rule.AlarmTypes is not null && !rule.AlarmTypes.Contains(ev.Type)) continue;
            if (rule.GroupId is not null && (bolt is null || bolt.GroupId != rule.GroupId)) continue;

            channels ??= new List<long>();
            if (!channels.Contains(rule.ChannelId))
                channels.Add(rule.ChannelId);
        }

        if (channels is null)
            return null;
        return new NotificationMatch(channels, bolt?.Code ?? $"bolt#{ev.BoltId}", bolt?.GroupName);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            _snapshot = await LoadAsync(ct);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        var rules = new List<Rule>();
        await using (var cmd = _ds.CreateCommand(@"
            SELECT r.channel_id, r.min_severity, r.alarm_types, r.group_id, r.notify_on_clear,
                   r.quiet_start, r.quiet_end
            FROM notification_rules r
            JOIN notification_channels c ON c.id = r.channel_id AND c.enabled
            WHERE r.enabled"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rules.Add(new Rule(
                    ChannelId: reader.GetInt64(0),
                    MinSeverityRank: SeverityRank.TryGetValue(reader.GetString(1), out var rank) ? rank : 1,
                    AlarmTypes: reader.IsDBNull(2) ? null : new HashSet<string>(reader.GetFieldValue<string[]>(2), StringComparer.Ordinal),
                    GroupId: reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    NotifyOnClear: reader.GetBoolean(4),
                    QuietStart: reader.IsDBNull(5) ? null : reader.GetFieldValue<TimeSpan>(5),
                    QuietEnd: reader.IsDBNull(6) ? null : reader.GetFieldValue<TimeSpan>(6)));
            }
        }

        // 一条规则都没有（常态：功能未配置）就不必加载螺栓映射
        var bolts = new Dictionary<long, BoltInfo>();
        if (rules.Count > 0)
        {
            await using var cmd = _ds.CreateCommand(@"
                SELECT b.id, b.code, b.group_id, mg.name
                FROM bolts b
                JOIN monitor_groups mg ON mg.id = b.group_id
                WHERE b.deleted_at IS NULL");
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                bolts[reader.GetInt64(0)] = new BoltInfo(reader.GetString(1), reader.GetInt64(2), reader.GetString(3));
        }

        _logger.LogDebug("通知路由快照已刷新：规则 {RuleCount} 条（仅启用渠道），螺栓映射 {BoltCount} 条",
            rules.Count, bolts.Count);
        return new Snapshot(rules, bolts);
    }

    private sealed record Rule(
        long ChannelId,
        int MinSeverityRank,
        HashSet<string>? AlarmTypes,
        long? GroupId,
        bool NotifyOnClear,
        TimeSpan? QuietStart,
        TimeSpan? QuietEnd)
    {
        /// <summary>quiet_start &gt; quiet_end 表示跨零点窗（如 22:00–06:00）。</summary>
        public bool InQuietWindow(TimeSpan localNow)
        {
            if (QuietStart is not { } start || QuietEnd is not { } end)
                return false;
            return start <= end
                ? localNow >= start && localNow < end
                : localNow >= start || localNow < end;
        }
    }

    private sealed record BoltInfo(string Code, long GroupId, string GroupName);

    private sealed record Snapshot(IReadOnlyList<Rule> Rules, IReadOnlyDictionary<long, BoltInfo> Bolts)
    {
        public static readonly Snapshot Empty = new(Array.Empty<Rule>(), new Dictionary<long, BoltInfo>());
    }
}
