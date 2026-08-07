using System.Collections.Concurrent;
using BoltPreload.Application.Alarm;
using BoltPreload.Application.Devices;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Application.Diagnostics;
using BoltPreload.Application.Realtime;
using BoltPreload.Fx;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Runtime;

/// <summary>设备校时与主机时钟漂移监视（appsettings 的 Alarm:TimeSync 节）。</summary>
public sealed class TimeSyncOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>周期校时间隔（小时）。plan §9.8：连上一次 + 每天定期。</summary>
    public int IntervalHours { get; set; } = 24;
    /// <summary>主机时钟跳变检查间隔（秒）。</summary>
    public int HostCheckIntervalSeconds { get; set; } = 60;
    /// <summary>
    /// 主机时钟跳变阈值（秒）：两次检查之间 |实际流逝 − 期望间隔| 超过此值即视为 NTP/系统时间错乱。
    /// FMEA RPN 70 的缓解措施之一；默认 30s，盖过正常调度抖动。
    /// </summary>
    public int HostDriftThresholdSeconds { get; set; } = 30;
    /// <summary>连续校时失败多少次后对代表螺栓触发 clock_drift（设备对时寄存器写失败）。</summary>
    public int SyncFailThreshold { get; set; } = 3;
    public int StartupGraceSeconds { get; set; } = 60;
}

/// <summary>
/// 设备校时 + 主机时钟漂移告警（plan §9.8 / §9.15.2 FMEA RPN 70）。
/// <para>
/// 厂家 SDK 无 SyncTime，也读不回设备钟：本服务做两件事——
/// (1) 对已连接真机周期下发 <see cref="IFxConnector.SyncDeviceTimeAsync"/>；
/// (2) 监视工控机自身 UTC 时钟跳变（NTP 失效 / 手动改时），触发 <c>clock_drift</c>。
/// 帧时间戳当前用主机时间解码，设备钟漂不会直接带偏 measurements.ts，
/// 但主机钟乱了就会——所以漂移告警盯的是主机侧。
/// </para>
/// </summary>
public sealed class TimeSyncService : BackgroundService
{
    private readonly IDeviceConnectorManager _manager;
    private readonly NpgsqlDataSource _ds;
    private readonly IAlarmSink _sink;
    private readonly IAlarmPublisher _publisher;
    private readonly ProbeScanState _probeScan;
    private readonly TimeSyncOptions _opt;
    private readonly BackgroundServiceHealth _health;
    private readonly ILogger<TimeSyncService> _logger;

    private DateTimeOffset? _lastHostTick;
    private DateTimeOffset _lastDeviceSyncAt = DateTimeOffset.MinValue;
    private readonly ConcurrentDictionary<long, int> _syncFailStreak = new();
    private bool _hostDriftActive;

    public TimeSyncService(
        IDeviceConnectorManager manager,
        NpgsqlDataSource ds,
        IAlarmSink sink,
        IAlarmPublisher publisher,
        ProbeScanState probeScan,
        IOptions<TimeSyncOptions> opt,
        BackgroundServiceHealth health,
        ILogger<TimeSyncService> logger)
    {
        _manager = manager;
        _ds = ds;
        _sink = sink;
        _publisher = publisher;
        _probeScan = probeScan;
        _opt = opt.Value;
        _health = health;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled)
        {
            _logger.LogInformation("TimeSync 已禁用");
            return;
        }

        _logger.LogInformation(
            "TimeSync 启动：设备校时每 {Hours}h，主机时钟每 {Host}s 检查（跳变阈值 {Drift}s）",
            Math.Max(1, _opt.IntervalHours),
            Math.Max(15, _opt.HostCheckIntervalSeconds),
            _opt.HostDriftThresholdSeconds);

        try { await Task.Delay(TimeSpan.FromSeconds(_opt.StartupGraceSeconds), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var hostInterval = TimeSpan.FromSeconds(Math.Max(15, _opt.HostCheckIntervalSeconds));
        using var timer = new PeriodicTimer(hostInterval);
        while (true)
        {
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) { return; }

            _health.Beat("time-sync", hostInterval);
            try
            {
                await CheckHostClockAsync(hostInterval, stoppingToken);
                await SyncDevicesIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogError(ex, "TimeSync 一轮异常"); }
        }
    }

    private async Task CheckHostClockAsync(TimeSpan expected, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastHostTick is { } last)
        {
            var actual = (now - last).TotalSeconds;
            var drift = Math.Abs(actual - expected.TotalSeconds);
            var jumped = drift > _opt.HostDriftThresholdSeconds;

            if (jumped && !_hostDriftActive)
            {
                _logger.LogError(
                    "检测到主机时钟跳变：期望间隔 {Expected:F0}s，实际 {Actual:F0}s，偏差 {Drift:F0}s（查 NTP/W32Time）",
                    expected.TotalSeconds, actual, drift);
                await RaiseHostDriftAsync(
                    $"工控机系统时间跳变 {drift:F0} 秒（期望间隔 {expected.TotalSeconds:F0}s，实际 {actual:F0}s）。请检查 NTP/W32Time。",
                    ct);
                _hostDriftActive = true;
            }
            else if (!jumped && _hostDriftActive)
            {
                await ClearHostDriftAsync(ct);
                _hostDriftActive = false;
                _logger.LogInformation("主机时钟跳变已恢复（连续检查间隔恢复正常）");
            }
        }
        _lastHostTick = now;
    }

    private async Task SyncDevicesIfDueAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, _opt.IntervalHours));
        if (DateTimeOffset.UtcNow - _lastDeviceSyncAt < interval) return;

        foreach (var rt in _manager.Runtimes)
        {
            if (ct.IsCancellationRequested) return;
            if (!string.Equals(rt.Options.Mode, "Real", StringComparison.OrdinalIgnoreCase)) continue;
            if (rt.Connector.State != FxConnectorState.Connected) continue;
            if (_probeScan.IsRunning(rt.DeviceId)) continue;

            try
            {
                await rt.Connector.SyncDeviceTimeAsync(ct);
                _syncFailStreak[rt.DeviceId] = 0;
                await ClearDeviceSyncFailAsync(rt.DeviceId, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var n = _syncFailStreak.AddOrUpdate(rt.DeviceId, 1, (_, v) => v + 1);
                _logger.LogWarning(ex, "周期校时失败 device={Dev} streak={N}", rt.DeviceId, n);
                if (n >= Math.Max(1, _opt.SyncFailThreshold))
                    await RaiseDeviceSyncFailAsync(rt.DeviceId,
                        $"设备校时连续失败 {n} 次（MeasConfig 对时下发）。请检查设备连接与型号是否支持对时寄存器。",
                        ct);
            }
        }

        _lastDeviceSyncAt = DateTimeOffset.UtcNow;
    }

    private async Task RaiseHostDriftAsync(string message, CancellationToken ct)
    {
        // 主机级问题：每个已连接设备挂一条代表螺栓告警，避免一颗螺栓刷几十条同源告警
        foreach (var rt in _manager.Runtimes)
        {
            if (rt.Connector.State != FxConnectorState.Connected) continue;
            var anchor = await FindAnchorBoltAsync(rt.DeviceId, ct);
            if (anchor is null) continue;
            await TriggerIfOpenMissingAsync(anchor.Value.BoltId, anchor.Value.ChannelId, message, ct);
        }
    }

    private async Task ClearHostDriftAsync(CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(@"
            SELECT a.bolt_id, a.channel_id FROM alarms a
            WHERE a.type = 'clock_drift' AND a.cleared_at IS NULL
              AND a.message LIKE '工控机系统时间跳变%'");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<(long BoltId, long ChannelId)>();
        while (await r.ReadAsync(ct))
            list.Add((r.GetInt64(0), r.GetInt64(1)));
        await r.DisposeAsync();

        foreach (var (boltId, channelId) in list)
            await PersistAsync(MakeEvent(AlarmAction.Clear, boltId, channelId, "主机时钟已恢复稳定"), ct);
    }

    private async Task RaiseDeviceSyncFailAsync(long deviceId, string message, CancellationToken ct)
    {
        var anchor = await FindAnchorBoltAsync(deviceId, ct);
        if (anchor is null) return;
        await TriggerIfOpenMissingAsync(anchor.Value.BoltId, anchor.Value.ChannelId, message, ct);
    }

    private async Task ClearDeviceSyncFailAsync(long deviceId, CancellationToken ct)
    {
        var anchor = await FindAnchorBoltAsync(deviceId, ct);
        if (anchor is null) return;
        if (!await IsOpenAsync(anchor.Value.BoltId, ct)) return;
        // 只清"校时失败"类，不动主机跳变告警
        await using var cmd = _ds.CreateCommand(@"
            SELECT 1 FROM alarms
            WHERE bolt_id = @b AND type = 'clock_drift' AND cleared_at IS NULL
              AND message LIKE '设备校时连续失败%' LIMIT 1");
        cmd.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, anchor.Value.BoltId);
        if (await cmd.ExecuteScalarAsync(ct) is null) return;
        await PersistAsync(MakeEvent(AlarmAction.Clear, anchor.Value.BoltId, anchor.Value.ChannelId, "设备校时已成功"), ct);
    }

    private async Task TriggerIfOpenMissingAsync(long boltId, long channelId, string message, CancellationToken ct)
    {
        if (await IsShelvedAsync(boltId, ct)) return;
        if (await IsOpenAsync(boltId, ct))
        {
            await using var touch = _ds.CreateCommand(@"
                UPDATE alarms SET last_active_at = now()
                WHERE bolt_id = @b AND type = 'clock_drift' AND cleared_at IS NULL");
            touch.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
            await touch.ExecuteNonQueryAsync(ct);
            return;
        }
        await PersistAsync(MakeEvent(AlarmAction.Trigger, boltId, channelId, message), ct);
    }

    private async Task<(long BoltId, long ChannelId)?> FindAnchorBoltAsync(long deviceId, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(@"
            SELECT b.id, c.id
            FROM channels c
            JOIN bolts b ON b.id = c.bolt_id AND b.deleted_at IS NULL
            WHERE c.device_id = @d AND c.deleted_at IS NULL AND c.bolt_id IS NOT NULL
            ORDER BY c.channel_no
            LIMIT 1");
        cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetInt64(0), r.GetInt64(1));
    }

    private async Task<bool> IsOpenAsync(long boltId, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(
            "SELECT 1 FROM alarms WHERE bolt_id = @b AND type = 'clock_drift' AND cleared_at IS NULL LIMIT 1");
        cmd.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private async Task<bool> IsShelvedAsync(long boltId, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(@"
            SELECT 1 FROM alarm_shelvings
            WHERE cancelled_at IS NULL AND expires_at > now()
              AND (bolt_id IS NULL OR bolt_id = @bolt)
              AND (alarm_type IS NULL OR alarm_type = 'clock_drift')
            LIMIT 1");
        cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, boltId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private async Task PersistAsync(AlarmEvent ev, CancellationToken ct)
    {
        try
        {
            await _sink.PersistAsync(ev, ct);
            _ = SafePublishAsync(ev, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "clock_drift 告警写入失败 bolt={Bolt}", ev.BoltId);
        }
    }

    private async Task SafePublishAsync(AlarmEvent ev, CancellationToken ct)
    {
        try { await _publisher.PublishAsync(ev, ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "clock_drift 推送失败"); }
    }

    private static AlarmEvent MakeEvent(AlarmAction action, long boltId, long channelId, string message) =>
        new(action, boltId, channelId, "clock_drift", "warn", DateTimeOffset.UtcNow,
            PreloadN: null, PreloadRatio: null, TargetPreloadN: null,
            ThresholdLowRatio: 0m, ThresholdHighRatio: 0m, ConsecutiveCount: 0, Message: message);
}
