using BoltPreload.Application.Alarm;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Infrastructure.Alarm;

/// <summary>
/// PostgreSQL 告警持久化。
/// <para>
/// 不开事务（告警量小，单条 INSERT/UPDATE 足够）。
/// 同一 (bolt_id, type) 的开启告警唯一性由应用层保证（evaluator 状态机不会重复 trigger）。
/// </para>
/// </summary>
public sealed class PostgresAlarmSink : IAlarmSink
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresAlarmSink> _logger;

    public PostgresAlarmSink(NpgsqlDataSource dataSource, ILogger<PostgresAlarmSink> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task PersistAsync(AlarmEvent ev, CancellationToken ct = default)
    {
        // 契约（C2）：失败不再 catch-all 吞掉，直接抛给调用方，由其回滚状态机 / 重试，杜绝静默漏报。
        if (ev.Action == AlarmAction.Trigger)
        {
            const string insertSql = @"
                INSERT INTO alarms (
                    bolt_id, channel_id, type, severity, triggered_at,
                    preload_n_at_trigger, preload_ratio_at_trigger, target_preload_n,
                    threshold_low_ratio, threshold_high_ratio,
                    consecutive_count, message
                ) VALUES (
                    @bolt, @chan, @type, @sev, @ts,
                    @pn, @pr, @target,
                    @lo, @hi,
                    @cnt, @msg
                )";

            await using var cmd = _dataSource.CreateCommand(insertSql);
            cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, ev.BoltId);
            cmd.Parameters.AddWithValue("chan", NpgsqlDbType.Bigint, ev.ChannelId);
            cmd.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, ev.Type);
            cmd.Parameters.AddWithValue("sev", NpgsqlDbType.Varchar, ev.Severity);
            cmd.Parameters.AddWithValue("ts", NpgsqlDbType.TimestampTz, ev.Ts);
            cmd.Parameters.AddWithValue("pn", NpgsqlDbType.Numeric, (object?)ev.PreloadN ?? DBNull.Value);
            cmd.Parameters.AddWithValue("pr", NpgsqlDbType.Numeric, (object?)ev.PreloadRatio ?? DBNull.Value);
            cmd.Parameters.AddWithValue("target", NpgsqlDbType.Numeric, (object?)ev.TargetPreloadN ?? DBNull.Value);
            cmd.Parameters.AddWithValue("lo", NpgsqlDbType.Numeric, ev.ThresholdLowRatio);
            cmd.Parameters.AddWithValue("hi", NpgsqlDbType.Numeric, ev.ThresholdHighRatio);
            cmd.Parameters.AddWithValue("cnt", NpgsqlDbType.Integer, ev.ConsecutiveCount);
            cmd.Parameters.AddWithValue("msg", NpgsqlDbType.Text, ev.Message);

            await cmd.ExecuteNonQueryAsync(ct);
            _logger.LogWarning("告警触发 bolt={Bolt} type={Type} severity={Sev} ratio={Ratio}",
                ev.BoltId, ev.Type, ev.Severity, ev.PreloadRatio);
        }
        else // Clear
        {
            const string clearSql = @"
                UPDATE alarms SET cleared_at = @ts, cleared_reason = 'auto_recovered'
                WHERE bolt_id = @bolt AND type = @type AND cleared_at IS NULL";

            await using var cmd = _dataSource.CreateCommand(clearSql);
            cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, ev.BoltId);
            cmd.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, ev.Type);
            cmd.Parameters.AddWithValue("ts", NpgsqlDbType.TimestampTz, ev.Ts);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("告警恢复 bolt={Bolt} type={Type} affected={N}", ev.BoltId, ev.Type, n);
        }
    }

    public async Task<bool> PersistCriticalIfAbsentAsync(AlarmEvent ev, CancellationToken ct = default)
    {
        // 去重补写（C1，WAL 重放兜底）：同 (bolt_id, type) 已有未闭合告警则跳过；
        // 用 INSERT ... SELECT ... WHERE NOT EXISTS 做幂等插入，避免重放多条越限测量刷重复告警。
        const string sql = @"
            INSERT INTO alarms (
                bolt_id, channel_id, type, severity, triggered_at,
                preload_n_at_trigger, preload_ratio_at_trigger, target_preload_n,
                threshold_low_ratio, threshold_high_ratio,
                consecutive_count, message
            )
            SELECT
                @bolt, @chan, @type, @sev, @ts,
                @pn, @pr, @target,
                @lo, @hi,
                @cnt, @msg
            WHERE NOT EXISTS (
                SELECT 1 FROM alarms
                WHERE bolt_id = @bolt AND type = @type AND cleared_at IS NULL)";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, ev.BoltId);
        cmd.Parameters.AddWithValue("chan", NpgsqlDbType.Bigint, ev.ChannelId);
        cmd.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, ev.Type);
        cmd.Parameters.AddWithValue("sev", NpgsqlDbType.Varchar, ev.Severity);
        cmd.Parameters.AddWithValue("ts", NpgsqlDbType.TimestampTz, ev.Ts);
        cmd.Parameters.AddWithValue("pn", NpgsqlDbType.Numeric, (object?)ev.PreloadN ?? DBNull.Value);
        cmd.Parameters.AddWithValue("pr", NpgsqlDbType.Numeric, (object?)ev.PreloadRatio ?? DBNull.Value);
        cmd.Parameters.AddWithValue("target", NpgsqlDbType.Numeric, (object?)ev.TargetPreloadN ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lo", NpgsqlDbType.Numeric, ev.ThresholdLowRatio);
        cmd.Parameters.AddWithValue("hi", NpgsqlDbType.Numeric, ev.ThresholdHighRatio);
        cmd.Parameters.AddWithValue("cnt", NpgsqlDbType.Integer, ev.ConsecutiveCount);
        cmd.Parameters.AddWithValue("msg", NpgsqlDbType.Text, ev.Message);

        var n = await cmd.ExecuteNonQueryAsync(ct);
        if (n > 0)
            _logger.LogWarning("WAL 重放兜底补发 critical 告警 bolt={Bolt} type={Type} ratio={Ratio}",
                ev.BoltId, ev.Type, ev.PreloadRatio);
        return n > 0;
    }

    public async Task<IReadOnlyList<(long BoltId, string Type)>> LoadOpenAsync(CancellationToken ct = default)
    {
        const string sql = @"
            SELECT DISTINCT bolt_id, type FROM alarms WHERE cleared_at IS NULL";
        await using var cmd = _dataSource.CreateCommand(sql);
        var items = new List<(long, string)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            items.Add((r.GetInt64(0), r.GetString(1)));
        }
        return items;
    }
}
