using BoltPreload.Application.Alarm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BoltPreload.Infrastructure.Alarm;

/// <summary>
/// <see cref="IAlarmRuleProvider"/> 的 Postgres 实现：active 规则整表快照进内存
/// （规则总量最多几百条，不需要逐键缓存），bolt→group 映射一起加载，Resolve 纯字典查找。
/// <para>
/// 刷新失败保留旧快照——告警评估宁可用略旧的阈值也不能停。刚建档的螺栓在下一轮刷新前
/// 查不到 bolt→group 映射，这段窗口内按 global/兜底判，30 秒内自动补齐。
/// </para>
/// </summary>
public sealed class PostgresAlarmRuleProvider : IAlarmRuleProvider, IDisposable
{
    private readonly NpgsqlDataSource _ds;
    private readonly StaticThresholdAlarmOptions _factoryDefault;
    private readonly ILogger<PostgresAlarmRuleProvider> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private volatile Snapshot _snapshot = Snapshot.Empty;

    public PostgresAlarmRuleProvider(
        NpgsqlDataSource ds,
        IOptions<StaticThresholdAlarmOptions> factoryDefault,
        ILogger<PostgresAlarmRuleProvider> logger)
    {
        _ds = ds;
        _factoryDefault = factoryDefault.Value;
        _logger = logger;
    }

    public StaticThresholdAlarmOptions Resolve(long boltId)
    {
        var s = _snapshot;
        if (s.ByBolt.TryGetValue(boltId, out var boltRule))
            return boltRule;
        if (s.ByGroup.Count > 0
            && s.BoltGroup.TryGetValue(boltId, out var groupId)
            && s.ByGroup.TryGetValue(groupId, out var groupRule))
            return groupRule;
        return s.Global ?? _factoryDefault;
    }

    public void Dispose() => _refreshGate.Dispose();

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // 串行化刷新：端点批准后的显式刷新与后台定时刷新可能撞在一起，
        // 后到的等前一个装完再装，保证快照不回退到更旧的读取结果
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
        StaticThresholdAlarmOptions? global = null;
        var byGroup = new Dictionary<long, StaticThresholdAlarmOptions>();
        var byBolt = new Dictionary<long, StaticThresholdAlarmOptions>();

        await using (var cmd = _ds.CreateCommand(@"
            SELECT scope, group_id, bolt_id,
                   trigger_low_ratio, clear_low_ratio, trigger_high_ratio, clear_high_ratio,
                   trigger_consecutive, clear_consecutive, critical_deviation, preload_loss_ratio
            FROM alarm_rules
            WHERE review_state = 'active'"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var opt = new StaticThresholdAlarmOptions
                {
                    TriggerLowRatio = r.GetDecimal(3),
                    ClearLowRatio = r.GetDecimal(4),
                    TriggerHighRatio = r.GetDecimal(5),
                    ClearHighRatio = r.GetDecimal(6),
                    TriggerConsecutive = r.GetInt32(7),
                    ClearConsecutive = r.GetInt32(8),
                    CriticalDeviation = r.GetDecimal(9),
                    PreloadLossRatio = r.GetDecimal(10),
                };
                switch (r.GetString(0))
                {
                    case "bolt": byBolt[r.GetInt64(2)] = opt; break;
                    case "group": byGroup[r.GetInt64(1)] = opt; break;
                    default: global = opt; break;
                }
            }
        }

        // 只有存在组级规则时才需要 bolt→group 映射；bolts.group_id 非空（0001 建表约束）
        var boltGroup = new Dictionary<long, long>();
        if (byGroup.Count > 0)
        {
            await using var cmd = _ds.CreateCommand("SELECT id, group_id FROM bolts WHERE deleted_at IS NULL");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                boltGroup[r.GetInt64(0)] = r.GetInt64(1);
        }

        _logger.LogDebug("告警规则快照已刷新：global={HasGlobal} group 级 {GroupCount} 条 bolt 级 {BoltCount} 条",
            global is not null, byGroup.Count, byBolt.Count);
        return new Snapshot(global, byGroup, byBolt, boltGroup);
    }

    private sealed record Snapshot(
        StaticThresholdAlarmOptions? Global,
        IReadOnlyDictionary<long, StaticThresholdAlarmOptions> ByGroup,
        IReadOnlyDictionary<long, StaticThresholdAlarmOptions> ByBolt,
        IReadOnlyDictionary<long, long> BoltGroup)
    {
        public static readonly Snapshot Empty = new(
            null,
            new Dictionary<long, StaticThresholdAlarmOptions>(),
            new Dictionary<long, StaticThresholdAlarmOptions>(),
            new Dictionary<long, long>());
    }
}
