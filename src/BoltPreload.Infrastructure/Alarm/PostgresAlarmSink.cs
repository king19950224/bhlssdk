using System.Text.Json;
using BoltPreload.Application.Alarm;
using BoltPreload.Application.Notifications;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Infrastructure.Alarm;

/// <summary>
/// PostgreSQL 告警持久化 + 出站通知入队。
/// <para>
/// 通知路由命中时，告警行与 notification_outbox 行在同一事务写入——落库的告警必然有对应的
/// 发送记录，进程重启/对端不可用都丢不掉（派发与重试是 NotificationDispatcherService 的事）。
/// 路由没命中（含功能未配置的常态）保持单条命令，热路径零额外开销。
/// 同一 (bolt_id, type) 的开启告警唯一性由应用层保证（evaluator 状态机不会重复 trigger）。
/// </para>
/// </summary>
public sealed class PostgresAlarmSink : IAlarmSink
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresAlarmSink> _logger;
    private readonly INotificationRouter? _router;

    public PostgresAlarmSink(
        NpgsqlDataSource dataSource,
        ILogger<PostgresAlarmSink> logger,
        INotificationRouter? router = null)
    {
        _dataSource = dataSource;
        _logger = logger;
        _router = router;
    }

    private const string InsertAlarmSql = @"
        INSERT INTO alarms (
            bolt_id, channel_id, type, severity, triggered_at,
            preload_n_at_trigger, preload_ratio_at_trigger, target_preload_n,
            threshold_low_ratio, threshold_high_ratio,
            consecutive_count, message, last_active_at
        ) VALUES (
            @bolt, @chan, @type, @sev, @ts,
            @pn, @pr, @target,
            @lo, @hi,
            @cnt, @msg, @ts
        )
        RETURNING id";

    public async Task PersistAsync(AlarmEvent ev, CancellationToken ct = default)
    {
        // 契约（C2）：失败不再 catch-all 吞掉，直接抛给调用方，由其回滚状态机 / 重试，杜绝静默漏报。
        var match = _router?.Match(ev);

        if (ev.Action == AlarmAction.Trigger)
        {
            // last_active_at 初始化为触发时刻，此后由"条件仍命中"路径刷新（测量管线节流刷 / SignalHealth 每轮刷）
            if (match is null)
            {
                await using var cmd = _dataSource.CreateCommand(InsertAlarmSql);
                BindTriggerParams(cmd, ev);
                await cmd.ExecuteScalarAsync(ct);
            }
            else
            {
                await using var conn = await _dataSource.OpenConnectionAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                long alarmId;
                await using (var cmd = new NpgsqlCommand(InsertAlarmSql, conn, tx))
                {
                    BindTriggerParams(cmd, ev);
                    alarmId = (long)(await cmd.ExecuteScalarAsync(ct))!;
                }
                await EnqueueOutboxAsync(conn, tx, alarmId, "trigger", ev, match, ct);
                await tx.CommitAsync(ct);
            }

            _logger.LogWarning("告警触发 bolt={Bolt} type={Type} severity={Sev} ratio={Ratio} notify={Channels}",
                ev.BoltId, ev.Type, ev.Severity, ev.PreloadRatio, match?.ChannelIds.Count ?? 0);
        }
        else if (ev.Action == AlarmAction.Escalate)
        {
            // 只升不降：已开启行的 severity 随恶化更新，让静默窗豁免 critical 等逻辑能看到真实等级
            const string escalateSql = @"
                UPDATE alarms
                   SET severity = @sev,
                       message = COALESCE(@msg, message),
                       last_active_at = @ts
                 WHERE bolt_id = @bolt AND type = @type AND cleared_at IS NULL
                   AND CASE severity
                         WHEN 'critical' THEN 3 WHEN 'warn' THEN 2 WHEN 'info' THEN 1 ELSE 0
                       END
                     < CASE @sev
                         WHEN 'critical' THEN 3 WHEN 'warn' THEN 2 WHEN 'info' THEN 1 ELSE 0
                       END
                RETURNING id";
            await using var cmd = _dataSource.CreateCommand(escalateSql);
            cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, ev.BoltId);
            cmd.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, ev.Type);
            cmd.Parameters.AddWithValue("sev", NpgsqlDbType.Varchar, ev.Severity);
            cmd.Parameters.AddWithValue("msg", NpgsqlDbType.Text, (object?)ev.Message ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ts", NpgsqlDbType.TimestampTz, ev.Ts);
            var id = await cmd.ExecuteScalarAsync(ct);
            _logger.LogWarning("告警升级 bolt={Bolt} type={Type} severity={Sev} alarmId={Id}",
                ev.BoltId, ev.Type, ev.Severity, id);
        }
        else // Clear
        {
            // 同 (bolt_id, type) 同时最多一条开启告警（0011 的 partial unique 索引兜着），RETURNING 至多一行
            const string clearSql = @"
                UPDATE alarms SET cleared_at = @ts, cleared_reason = 'auto_recovered'
                WHERE bolt_id = @bolt AND type = @type AND cleared_at IS NULL
                RETURNING id";

            long? alarmId = null;
            if (match is null)
            {
                await using var cmd = _dataSource.CreateCommand(clearSql);
                BindClearParams(cmd, ev);
                alarmId = (long?)await cmd.ExecuteScalarAsync(ct);
            }
            else
            {
                await using var conn = await _dataSource.OpenConnectionAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var cmd = new NpgsqlCommand(clearSql, conn, tx))
                {
                    BindClearParams(cmd, ev);
                    alarmId = (long?)await cmd.ExecuteScalarAsync(ct);
                }
                // 没有开启中的告警行（重复 Clear 等）就没有可通知的事件，事务里也不写发件箱
                if (alarmId is not null)
                    await EnqueueOutboxAsync(conn, tx, alarmId.Value, "clear", ev, match, ct);
                await tx.CommitAsync(ct);
            }

            _logger.LogInformation("告警恢复 bolt={Bolt} type={Type} affected={N}", ev.BoltId, ev.Type, alarmId is null ? 0 : 1);
        }
    }

    public async Task<bool> PersistCriticalIfAbsentAsync(AlarmEvent ev, CancellationToken ct = default)
    {
        // 去重补写（C1，WAL 重放兜底）：同 (bolt_id, type) 已有未闭合告警则跳过；
        // 用 INSERT ... SELECT ... WHERE NOT EXISTS 做幂等插入，避免重放多条越限测量刷重复告警。
        // 补发的告警恰恰是断网窗口里漏掉的，同样入发件箱通知。
        const string sql = @"
            INSERT INTO alarms (
                bolt_id, channel_id, type, severity, triggered_at,
                preload_n_at_trigger, preload_ratio_at_trigger, target_preload_n,
                threshold_low_ratio, threshold_high_ratio,
                consecutive_count, message, last_active_at
            )
            SELECT
                @bolt, @chan, @type, @sev, @ts,
                @pn, @pr, @target,
                @lo, @hi,
                @cnt, @msg, @ts
            WHERE NOT EXISTS (
                SELECT 1 FROM alarms
                WHERE bolt_id = @bolt AND type = @type AND cleared_at IS NULL)
            RETURNING id";

        var match = _router?.Match(ev);
        long? alarmId;
        if (match is null)
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            BindTriggerParams(cmd, ev);
            alarmId = (long?)await cmd.ExecuteScalarAsync(ct);
        }
        else
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var cmd = new NpgsqlCommand(sql, conn, tx))
            {
                BindTriggerParams(cmd, ev);
                alarmId = (long?)await cmd.ExecuteScalarAsync(ct);
            }
            if (alarmId is not null)
                await EnqueueOutboxAsync(conn, tx, alarmId.Value, "trigger", ev, match, ct);
            await tx.CommitAsync(ct);
        }

        if (alarmId is not null)
            _logger.LogWarning("WAL 重放兜底补发 critical 告警 bolt={Bolt} type={Type} ratio={Ratio}",
                ev.BoltId, ev.Type, ev.PreloadRatio);
        return alarmId is not null;
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

    private static void BindTriggerParams(NpgsqlCommand cmd, AlarmEvent ev)
    {
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
    }

    private static void BindClearParams(NpgsqlCommand cmd, AlarmEvent ev)
    {
        cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, ev.BoltId);
        cmd.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, ev.Type);
        cmd.Parameters.AddWithValue("ts", NpgsqlDbType.TimestampTz, ev.Ts);
    }

    /// <summary>
    /// 发件箱入队（与告警行同事务）：每个命中渠道一行，payload 为事件快照——
    /// 发送时不回查业务表，螺栓事后改名/删除不影响已入队通知的内容。
    /// </summary>
    private static async Task EnqueueOutboxAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        long alarmId, string eventKind, AlarmEvent ev, NotificationMatch match, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            action = eventKind,
            type = ev.Type,
            severity = ev.Severity,
            boltId = ev.BoltId,
            boltCode = match.BoltCode,
            groupName = match.GroupName,
            preloadN = ev.PreloadN,
            preloadRatio = ev.PreloadRatio,
            targetPreloadN = ev.TargetPreloadN,
            ts = ev.Ts,
            message = ev.Message,
        });

        foreach (var channelId in match.ChannelIds)
        {
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO notification_outbox (alarm_id, channel_id, event_kind, payload_jsonb)
                VALUES (@alarm, @chan, @kind, @payload)", conn, tx);
            cmd.Parameters.AddWithValue("alarm", NpgsqlDbType.Bigint, alarmId);
            cmd.Parameters.AddWithValue("chan", NpgsqlDbType.Bigint, channelId);
            cmd.Parameters.AddWithValue("kind", NpgsqlDbType.Varchar, eventKind);
            cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
