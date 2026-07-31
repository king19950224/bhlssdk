using BoltPreload.Application.Alarm;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Application.Diagnostics;
using BoltPreload.Application.Realtime;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Runtime;

/// <summary>信号丢失告警配置（appsettings 的 Alarm:SignalHealth 节）。</summary>
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
    private readonly ProbeScanState _probeScan;
    private readonly SignalHealthOptions _opt;
    private readonly BackgroundServiceHealth _health;
    private readonly ILogger<SignalHealthService> _logger;

    public SignalHealthService(
        NpgsqlDataSource ds, IAlarmSink sink, IAlarmPublisher publisher,
        ProbeScanState probeScan, BackgroundServiceHealth health,
        IOptions<SignalHealthOptions> opt, ILogger<SignalHealthService> logger)
    {
        _ds = ds;
        _sink = sink;
        _publisher = publisher;
        _probeScan = probeScan;
        _health = health;
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
            // 心跳：本服务停摆意味着探头脱落不再产生 signal_lost 告警，属安全相关静默失效，
            // 必须能在 /api/health/ready 看出来
            _health.Beat("signal-health", TimeSpan.FromSeconds(_opt.CheckIntervalSeconds));
            try { await CheckAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogError(ex, "SignalHealth 检查异常"); }
        }
    }

    // internal 供集成测试直调单轮检查（BoltPreload.Tests，见 csproj InternalsVisibleTo）
    internal async Task CheckAsync(CancellationToken ct)
    {
        // 1) 当前已开启的 signal_lost 告警（按 bolt 去重）
        var open = new HashSet<long>();
        await using (var cmd = _ds.CreateCommand("SELECT DISTINCT bolt_id FROM alarms WHERE type='signal_lost' AND cleared_at IS NULL"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) open.Add(r.GetInt64(0));

        // 2) 监测中的螺栓（近 MonitoredWindow 有过测量）+ 距最近一条测量的秒数
        await using var q = _ds.CreateCommand(@"
            SELECT b.id, b.channel_id, b.code,
                   EXTRACT(EPOCH FROM (now() - mx.last_ts))::double precision AS idle_secs,
                   c.device_id
            FROM bolts b
            LEFT JOIN channels c ON c.id = b.channel_id
            JOIN LATERAL (SELECT max(ts) AS last_ts FROM measurements m WHERE m.bolt_id = b.id) mx ON true
            WHERE b.deleted_at IS NULL AND b.enabled
              AND mx.last_ts IS NOT NULL
              AND mx.last_ts > now() - (@win * interval '1 minute')");
        q.Parameters.AddWithValue("win", NpgsqlDbType.Integer, _opt.MonitoredWindowMinutes);

        var rows = new List<(long boltId, long channelId, string code, double idle, long? deviceId)>();
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rows.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? 0 : r.GetDouble(3),
                          r.IsDBNull(4) ? (long?)null : r.GetInt64(4)));

        foreach (var (boltId, channelId, code, idle, deviceId) in rows)
        {
            // 扫描期：该设备通道被扫描器独占工作模式、正常测量暂停，跳过以免误报 signal_lost
            // （与 IngestionWatchdogService / WaveSamplingService 同一不变量）。
            if (deviceId is { } dev && _probeScan.IsRunning(dev))
                continue;

            // 单个螺栓的告警持久化失败不应中断本轮其它螺栓的检查。PersistAsync 失败现在会抛（不再静默吞），
            // 这里捕获并记录；下一轮重新查询 open 集合后会自动重试（signal_lost 未入库则仍判 silent）。
            try
            {
                var silent = idle > _opt.NoSignalSeconds;
                var isOpen = open.Contains(boltId);
                if (silent && !isOpen)
                {
                    // 维护抑制（shelve）：换探头等维护窗口内拔线必然静默，命中生效抑制时不触发。
                    // 此前抑制检查只在测量管线（PostgresMeasurementSink），signal_lost 从这里直发、完全绕过
                    // ——而"更换探头"恰是抑制的标准场景。本路径无内存状态机，跳过即可，无需回滚。
                    if (await IsShelvedAsync(boltId, ct))
                    {
                        _logger.LogInformation("signal_lost 被维护抑制(shelve)拦截 bolt={Bolt} ch={Ch}", code, channelId);
                        continue;
                    }
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
                else if (silent && isOpen)
                {
                    // 条件仍在命中（依旧静默）：刷新 last_active_at，让"开启中的告警是否仍在持续命中"可判断。
                    await using var touch = _ds.CreateCommand(
                        "UPDATE alarms SET last_active_at = now() WHERE bolt_id = @b AND type = 'signal_lost' AND cleared_at IS NULL");
                    touch.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
                    await touch.ExecuteNonQueryAsync(ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "signal_lost 告警写入失败 bolt={Bolt} ch={Ch}（下一轮重试）", code, channelId);
            }
        }
    }

    /// <summary>是否存在命中该 bolt 的 signal_lost 生效抑制（未取消且未过期；bolt_id / alarm_type 为 NULL 表示通配），
    /// 与 PostgresMeasurementSink.IsAlarmShelvedAsync 同一判定口径。</summary>
    private async Task<bool> IsShelvedAsync(long boltId, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(@"
            SELECT 1 FROM alarm_shelvings
            WHERE cancelled_at IS NULL AND expires_at > now()
              AND (bolt_id IS NULL OR bolt_id = @bolt)
              AND (alarm_type IS NULL OR alarm_type = 'signal_lost')
            LIMIT 1");
        cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, boltId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static AlarmEvent MakeEvent(AlarmAction action, long boltId, long channelId, string severity, string message) =>
        new(action, boltId, channelId, "signal_lost", severity, DateTimeOffset.UtcNow,
            PreloadN: null, PreloadRatio: null, TargetPreloadN: null,
            ThresholdLowRatio: 0m, ThresholdHighRatio: 0m, ConsecutiveCount: 0, Message: message);
}
