using BoltPreload.Application.Alarm;
using BoltPreload.Application.Realtime;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Runtime;

public sealed class SignalHealthOptions
{
    public bool Enabled { get; set; } = true;
    public int CheckIntervalSeconds { get; set; } = 20;
    /// <summary>已在监测的螺栓超过此秒数无任何测量 → 判信号丢失（探头脱落/接头松/设备掉线）。</summary>
    public int NoSignalSeconds { get; set; } = 120;
    /// <summary>"在监测中"的判定窗口：近 N 分钟内有过测量的螺栓才纳入信号监控（排除从未接入的占位螺栓）。</summary>
    public int MonitoredWindowMinutes { get; set; } = 360;
    public int StartupGraceSeconds { get; set; } = 45;
}

/// <summary>
/// 信号健康看门狗：安全监测系统不能在自己的传感器失效时"静默"。
/// <para>
/// 预紧力告警只在已标定通道(有 ratio)才触发；未标定/调试期通道一旦探头脱落、接头松动或设备掉线，
/// 旧逻辑完全无告警 = 静默失效（危险）。本服务对"在监测中"的螺栓做<b>无回波超时</b>判定，
/// 产生/闭合 <c>signal_lost</c> 告警(落库 + 实时推送)，不依赖标定即可工作；设备整体掉线时其名下螺栓会一并告警。
/// </para>
/// </summary>
public sealed class SignalHealthService : BackgroundService
{
    private readonly NpgsqlDataSource _ds;
    private readonly IAlarmSink _sink;
    private readonly IAlarmPublisher _publisher;
    private readonly SignalHealthOptions _opt;
    private readonly ILogger<SignalHealthService> _logger;

    public SignalHealthService(
        NpgsqlDataSource ds, IAlarmSink sink, IAlarmPublisher publisher,
        IOptions<SignalHealthOptions> opt, ILogger<SignalHealthService> logger)
    {
        _ds = ds;
        _sink = sink;
        _publisher = publisher;
        _opt = opt.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled) { _logger.LogInformation("SignalHealth 已禁用"); return; }
        _logger.LogInformation("SignalHealth 启动：{Interval}s 检查一次，无回波 {NoSig}s 判信号丢失", _opt.CheckIntervalSeconds, _opt.NoSignalSeconds);

        try { await Task.Delay(TimeSpan.FromSeconds(_opt.StartupGraceSeconds), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_opt.CheckIntervalSeconds));
        while (true)
        {
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) { return; }
            try { await CheckAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogError(ex, "SignalHealth 检查异常"); }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        // 1) 当前已开启的 signal_lost 告警（按 bolt 去重）
        var open = new HashSet<long>();
        await using (var cmd = _ds.CreateCommand("SELECT DISTINCT bolt_id FROM alarms WHERE type='signal_lost' AND cleared_at IS NULL"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) open.Add(r.GetInt64(0));

        // 2) 监测中的螺栓（近 MonitoredWindow 有过测量）+ 距最近一条测量的秒数
        await using var q = _ds.CreateCommand(@"
            SELECT b.id, b.channel_id, b.code,
                   EXTRACT(EPOCH FROM (now() - mx.last_ts))::double precision AS idle_secs
            FROM bolts b
            JOIN LATERAL (SELECT max(ts) AS last_ts FROM measurements m WHERE m.bolt_id = b.id) mx ON true
            WHERE b.deleted_at IS NULL AND b.enabled
              AND mx.last_ts IS NOT NULL
              AND mx.last_ts > now() - (@win * interval '1 minute')");
        q.Parameters.AddWithValue("win", NpgsqlDbType.Integer, _opt.MonitoredWindowMinutes);

        var rows = new List<(long boltId, long channelId, string code, double idle)>();
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rows.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? 0 : r.GetDouble(3)));

        foreach (var (boltId, channelId, code, idle) in rows)
        {
            // 单个螺栓的告警持久化失败不应中断本轮其它螺栓的检查。PersistAsync 失败现在会抛（不再静默吞），
            // 这里捕获并记录；下一轮重新查询 open 集合后会自动重试（signal_lost 未入库则仍判 silent）。
            try
            {
                var silent = idle > _opt.NoSignalSeconds;
                var isOpen = open.Contains(boltId);
                if (silent && !isOpen)
                {
                    var ev = MakeEvent(AlarmAction.Trigger, boltId, channelId, "warn",
                        $"通道信号丢失：{code} 已 {(int)idle}s 无回波数据（探头脱落 / 接头松动 / 设备异常?）");
                    await _sink.PersistAsync(ev, ct);
                    try { await _publisher.PublishAsync(ev, ct); } catch { /* 推送尽力 */ }
                    _logger.LogWarning("信号丢失告警：bolt={Bolt} ch={Ch} 已 {Idle}s 无回波", code, channelId, (int)idle);
                }
                else if (!silent && isOpen)
                {
                    var ev = MakeEvent(AlarmAction.Clear, boltId, channelId, "info", $"信号已恢复：{code} 回波重新上报");
                    await _sink.PersistAsync(ev, ct);
                    try { await _publisher.PublishAsync(ev, ct); } catch { /* 推送尽力 */ }
                    _logger.LogInformation("信号恢复，闭合 signal_lost：bolt={Bolt} ch={Ch}", code, channelId);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "signal_lost 告警写入失败 bolt={Bolt} ch={Ch}（下一轮重试）", code, channelId);
            }
        }
    }

    private static AlarmEvent MakeEvent(AlarmAction action, long boltId, long channelId, string severity, string message) =>
        new(action, boltId, channelId, "signal_lost", severity, DateTimeOffset.UtcNow,
            PreloadN: null, PreloadRatio: null, TargetPreloadN: null,
            ThresholdLowRatio: 0m, ThresholdHighRatio: 0m, ConsecutiveCount: 0, Message: message);
}
