using System.Text.Json;
using BoltPreload.Application.Notifications;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Infrastructure.Notifications;

/// <summary>
/// 发件箱派发核心：取到期 pending 行 → 按渠道分组风暴合并 → 适配器发送 → 成败落状态。
/// 独立于宿主服务（NotificationDispatcherService 只是 5 秒一次调 <see cref="ProcessDueAsync"/> 的壳），
/// 集成测试拿假适配器直接驱动本类。
/// <para>
/// 语义为至少一次：发送成功与置 sent 之间进程被杀，重启后会再发一次——通知宁可重复不可丢。
/// 风暴合并：同渠道本轮所有到期事件合成一条消息（设备掉线 12 颗螺栓同时 signal_lost，
/// 收到 1 条 12 行的消息而不是 12 条轰炸）；渠道级 min_interval_seconds 是第二道闸，
/// 间隔未到不算失败、不推进退避，只顺延 next_attempt_at。
/// </para>
/// </summary>
public sealed class NotificationDispatcher
{
    // 退避：第 1..5 次失败后的等待；第 6 次失败置 dead（页面可见、可手工重发）
    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60),
    };
    private const int MaxAttempts = 6;
    private const int MaxLinesPerMessage = 20;
    private const int BatchLimit = 200;

    // 与前端告警中心同一套叫法（Alarms.vue 的 SEV 表），同一条告警在页面和群消息里不该是两个词
    private static readonly Dictionary<string, string> SeverityLabels = new(StringComparer.Ordinal)
    {
        ["info"] = "提示",
        ["warn"] = "预警",
        ["critical"] = "严重",
    };

    private readonly NpgsqlDataSource _ds;
    private readonly Dictionary<string, INotificationTransport> _transports;
    private readonly ILogger<NotificationDispatcher> _logger;
    // 渠道级限流的内存态（重启即清，代价只是重启后第一条消息可能提前，可接受）
    private readonly Dictionary<long, DateTimeOffset> _lastSentAt = new();

    public NotificationDispatcher(
        NpgsqlDataSource ds,
        IEnumerable<INotificationTransport> transports,
        ILogger<NotificationDispatcher> logger)
    {
        _ds = ds;
        _transports = transports.ToDictionary(t => t.Kind, StringComparer.Ordinal);
        _logger = logger;
    }

    /// <summary>处理一轮到期行，返回本轮成功发出的行数。</summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        var due = await LoadDueAsync(ct);
        if (due.Count == 0)
            return 0;

        var sentRows = 0;
        foreach (var batch in due.GroupBy(r => r.ChannelId))
        {
            var rows = batch.ToList();
            var ch = rows[0];

            // 渠道被停用：不发也不算失败，5 分钟后再看（重新启用即续发，停着就一直顺延）
            if (!ch.ChannelEnabled)
            {
                await PostponeAsync(rows, DateTimeOffset.UtcNow.AddMinutes(5), ct);
                continue;
            }

            // 渠道级限流：间隔未到只顺延，不推进退避计数
            if (_lastSentAt.TryGetValue(ch.ChannelId, out var last))
            {
                var earliest = last.AddSeconds(ch.MinIntervalSeconds);
                if (earliest > DateTimeOffset.UtcNow)
                {
                    await PostponeAsync(rows, earliest, ct);
                    continue;
                }
            }

            if (!_transports.TryGetValue(ch.ChannelKind, out var transport))
            {
                // 渠道类型没有适配器（库里 kind 校验放行了 smtp/sms 预留值但本轮没实现）：按失败退避，
                // 而不是静默跳过——配置了收不到的渠道必须在发送记录里可见
                await MarkFailedAsync(rows, $"渠道类型 '{ch.ChannelKind}' 没有可用适配器", ct);
                continue;
            }

            var (title, lines) = BuildMessage(rows);
            try
            {
                await transport.SendAsync(new NotificationChannelInfo(ch.ChannelId, ch.ChannelName, ch.ChannelKind, ch.ConfigJson), title, lines, ct);
                _lastSentAt[ch.ChannelId] = DateTimeOffset.UtcNow;
                await MarkSentAsync(rows, ct);
                sentRows += rows.Count;
                _logger.LogInformation("通知已发出：渠道 {Channel} 合并 {Count} 条事件", ch.ChannelName, rows.Count);
            }
            catch (Exception ex)
            {
                await MarkFailedAsync(rows, ex.Message, ct);
                _logger.LogWarning(ex, "通知发送失败：渠道 {Channel}，{Count} 条事件进入退避", ch.ChannelName, rows.Count);
            }
        }
        return sentRows;
    }

    private sealed record DueRow(
        long Id, long ChannelId, string EventKind, string Payload, int Attempts,
        string ChannelName, string ChannelKind, string ConfigJson, int MinIntervalSeconds, bool ChannelEnabled);

    private async Task<List<DueRow>> LoadDueAsync(CancellationToken ct)
    {
        const string sql = @"
            SELECT o.id, o.channel_id, o.event_kind, o.payload_jsonb::text, o.attempts,
                   c.name, c.kind, c.config_jsonb::text, c.min_interval_seconds, c.enabled
            FROM notification_outbox o
            JOIN notification_channels c ON c.id = o.channel_id
            WHERE o.status = 'pending' AND o.next_attempt_at <= now()
            ORDER BY o.created_at
            LIMIT @limit";
        await using var cmd = _ds.CreateCommand(sql);
        cmd.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, BatchLimit);
        var rows = new List<DueRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new DueRow(
                r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
                r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt32(8), r.GetBoolean(9)));
        }
        return rows;
    }

    /// <summary>合并一批事件为一条消息。行内容直接用评估器生成的中文 message（自带类型与数值）。</summary>
    private static (string Title, IReadOnlyList<string> Lines) BuildMessage(List<DueRow> rows)
    {
        var lines = new List<string>();
        var maxSevRank = 0;
        var maxSevLabel = "提示";

        foreach (var row in rows.Take(MaxLinesPerMessage))
        {
            using var doc = JsonDocument.Parse(row.Payload);
            var root = doc.RootElement;
            var severity = root.TryGetProperty("severity", out var sev) ? sev.GetString() ?? "warn" : "warn";
            var sevLabel = SeverityLabels.GetValueOrDefault(severity, severity);
            var boltCode = root.TryGetProperty("boltCode", out var bc) ? bc.GetString() : null;
            var groupName = root.TryGetProperty("groupName", out var gn) ? gn.GetString() : null;
            var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : null;
            var ts = root.TryGetProperty("ts", out var t) && t.TryGetDateTimeOffset(out var dto) ? dto : (DateTimeOffset?)null;

            var rank = severity == "critical" ? 2 : severity == "warn" ? 1 : 0;
            // 恢复事件不参与"最高级别"（它是好消息），标题只反映在告的事
            if (row.EventKind == "trigger" && rank >= maxSevRank)
            {
                maxSevRank = rank;
                maxSevLabel = sevLabel;
            }

            var place = groupName is null ? boltCode : $"{groupName}/{boltCode}";
            var timeStr = ts?.ToLocalTime().ToString("MM-dd HH:mm") ?? "";
            lines.Add($"[{sevLabel}] {place} {message}（{timeStr}）");
        }
        if (rows.Count > MaxLinesPerMessage)
            lines.Add($"…另有 {rows.Count - MaxLinesPerMessage} 条，详见告警中心");

        var title = rows.Count == 1
            ? $"螺栓预紧力监测：{(rows[0].EventKind == "clear" ? "告警恢复" : $"{maxSevLabel}告警")}"
            : $"螺栓预紧力监测：{rows.Count} 条告警事件（最高级别 {maxSevLabel}）";
        return (title, lines);
    }

    private async Task MarkSentAsync(List<DueRow> rows, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(
            "UPDATE notification_outbox SET status = 'sent', sent_at = now(), last_error = NULL WHERE id = ANY(@ids)");
        cmd.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, rows.Select(r => r.Id).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task PostponeAsync(List<DueRow> rows, DateTimeOffset until, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(
            "UPDATE notification_outbox SET next_attempt_at = @until WHERE id = ANY(@ids)");
        cmd.Parameters.AddWithValue("until", NpgsqlDbType.TimestampTz, until);
        cmd.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, rows.Select(r => r.Id).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task MarkFailedAsync(List<DueRow> rows, string error, CancellationToken ct)
    {
        foreach (var row in rows)
        {
            var attempts = row.Attempts + 1;
            if (attempts >= MaxAttempts)
            {
                await using var cmd = _ds.CreateCommand(
                    "UPDATE notification_outbox SET status = 'dead', attempts = @n, last_error = @err WHERE id = @id");
                cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, attempts);
                cmd.Parameters.AddWithValue("err", NpgsqlDbType.Text, error);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, row.Id);
                await cmd.ExecuteNonQueryAsync(ct);
                _logger.LogError("通知重试超限置 dead：outbox #{Id} 渠道 {Channel}，最后错误：{Error}",
                    row.Id, row.ChannelName, error);
            }
            else
            {
                await using var cmd = _ds.CreateCommand(
                    "UPDATE notification_outbox SET attempts = @n, next_attempt_at = @next, last_error = @err WHERE id = @id");
                cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, attempts);
                cmd.Parameters.AddWithValue("next", NpgsqlDbType.TimestampTz, DateTimeOffset.UtcNow + Backoff[attempts - 1]);
                cmd.Parameters.AddWithValue("err", NpgsqlDbType.Text, error);
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, row.Id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
    }
}
