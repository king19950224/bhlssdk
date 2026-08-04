using BoltPreload.Application.Alarm;
using BoltPreload.Application.Devices.Acceptance;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Application.Diagnostics;
using BoltPreload.Application.Realtime;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Runtime;

/// <summary>接线指纹守卫配置（appsettings 的 Alarm:WiringGuard 节）。</summary>
public sealed class WiringGuardOptions
{
    public bool Enabled { get; set; } = true;
    public int CheckIntervalSeconds { get; set; } = 60;

    /// <summary>声时相对验收指纹的容差（ns）。要盖过温度漂移（钢栓约 ±200ns/±30°C）与
    /// 预紧力变化（松开→复紧约 ±数百 ns），只抓"换接了不同长度螺栓"级别的粗差（μs 级）。现场标定后可调。</summary>
    public double ToleranceNs { get; set; } = 500;

    /// <summary>比对窗口（分钟）：取最近 N 分钟测量均值，偏差必须持续整窗才算命中（防单点毛刺）。</summary>
    public int WindowMinutes { get; set; } = 5;

    /// <summary>窗口内最少有效回波样本数，不足不判（刚上电/信号断续时不误报）。</summary>
    public int MinSamples { get; set; } = 3;

    public int StartupGraceSeconds { get; set; } = 60;
}

/// <summary>
/// 接线指纹守卫：检修换接电缆后台账悄悄失真的最后防线。
/// <para>
/// 比对每个【已验收 + 已绑定】通道的近期声时均值与验收指纹（channels.accepted_tof_ns）：
/// 持续超差 → 触发 wiring_suspect 告警（疑似换接/接线变动），提示重新核对接线并重新验收；
/// 回到容差内（或重新验收刷新指纹）→ 自动闭合。
/// </para>
/// <para>
/// 能力边界（方案 §3.4）：能发现"接线变了"（不同长度螺栓声时差以 μs 计），
/// 不能证明"哪根是哪根"（同规格螺栓声时相近）——主识别永远靠点位表 + 接入验收流程。
/// 触发后【只告警不停发】：阈值未经现场标定前自动断数的风险大于收益；停发策略待真机验证后再议。
/// </para>
/// </summary>
public sealed class WiringGuardService : BackgroundService
{
    private readonly NpgsqlDataSource _ds;
    private readonly IAlarmSink _sink;
    private readonly IAlarmPublisher _publisher;
    private readonly ProbeScanState _probeScan;
    private readonly AcceptanceSessionState _acceptance;
    private readonly WiringGuardOptions _opt;
    private readonly BackgroundServiceHealth _health;
    private readonly ILogger<WiringGuardService> _logger;

    public WiringGuardService(
        NpgsqlDataSource ds, IAlarmSink sink, IAlarmPublisher publisher,
        ProbeScanState probeScan, AcceptanceSessionState acceptance,
        BackgroundServiceHealth health,
        IOptions<WiringGuardOptions> opt, ILogger<WiringGuardService> logger)
    {
        _ds = ds;
        _sink = sink;
        _publisher = publisher;
        _probeScan = probeScan;
        _acceptance = acceptance;
        _health = health;
        _opt = opt.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled) { _logger.LogInformation("WiringGuard 已禁用"); return; }
        _logger.LogInformation("接线指纹守卫启动：容差 {Tol}ns，窗口 {Win}min，每 {Interval}s 检查",
            _opt.ToleranceNs, _opt.WindowMinutes, _opt.CheckIntervalSeconds);

        try { await Task.Delay(TimeSpan.FromSeconds(_opt.StartupGraceSeconds), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, _opt.CheckIntervalSeconds)));
        while (true)
        {
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) { return; }

            _health.Beat("wiring-guard", TimeSpan.FromSeconds(Math.Max(10, _opt.CheckIntervalSeconds)));
            try { await CheckAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogError(ex, "接线指纹守卫检查异常"); }
        }
    }

    // internal 供集成测试直调单轮检查
    internal async Task CheckAsync(CancellationToken ct)
    {
        // 1) 当前开启的 wiring_suspect 告警
        var open = new HashSet<long>();
        await using (var cmd = _ds.CreateCommand(
            "SELECT DISTINCT bolt_id FROM alarms WHERE type='wiring_suspect' AND cleared_at IS NULL"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) open.Add(r.GetInt64(0));

        // 2) 候选：已验收（有指纹）+ 已绑定螺栓的通道，取窗口内声时均值
        await using var q = _ds.CreateCommand(@"
            SELECT b.id, b.code, c.id AS channel_id, c.device_id, c.channel_no,
                   c.accepted_tof_ns, w.avg_tof, w.n, w.span_sec
            FROM bolts b
            JOIN channels c ON c.id = b.channel_id AND c.deleted_at IS NULL
            JOIN LATERAL (
                SELECT AVG(echo_time_ns) AS avg_tof,
                       COUNT(echo_time_ns) AS n,
                       EXTRACT(EPOCH FROM (max(ts) - min(ts)))::double precision AS span_sec
                FROM measurements m
                WHERE m.channel_id = c.id
                  AND m.ts > now() - (@win * interval '1 minute')
                  AND m.echo_time_ns IS NOT NULL
            ) w ON true
            WHERE b.deleted_at IS NULL AND b.enabled AND c.accepted_tof_ns IS NOT NULL");
        q.Parameters.AddWithValue("win", NpgsqlDbType.Integer, Math.Max(1, _opt.WindowMinutes));

        var rows = new List<(long BoltId, string Code, long ChannelId, long DeviceId, short ChannelNo,
                             decimal Fingerprint, decimal? AvgTof, long Samples, double SpanSec)>();
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rows.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt16(4),
                          r.GetDecimal(5),
                          r.IsDBNull(6) ? null : r.GetDecimal(6),
                          r.IsDBNull(7) ? 0 : r.GetInt64(7),
                          r.IsDBNull(8) ? 0 : r.GetDouble(8)));

        foreach (var row in rows)
        {
            // 扫描/验收会话期间该设备通道集被临时改写，测量断续，不判（恢复后下一轮自然接上）
            if (_probeScan.IsRunning(row.DeviceId) || _acceptance.IsActive(row.DeviceId))
                continue;

            // 样本不足（无数据/刚恢复/窗口太短）：既不触发也不闭合——没有证据就不动状态
            if (row.AvgTof is not { } avg || row.Samples < _opt.MinSamples
                || row.SpanSec < Math.Max(1, _opt.WindowMinutes) * 30)  // 窗口跨度至少过半
                continue;

            var deviationNs = (double)Math.Abs(avg - row.Fingerprint);
            var hit = deviationNs > _opt.ToleranceNs;
            var isOpen = open.Contains(row.BoltId);

            try
            {
                if (hit && !isOpen)
                {
                    if (await IsShelvedAsync(row.BoltId, ct))
                    {
                        _logger.LogInformation("wiring_suspect 被维护抑制(shelve)拦截 bolt={Bolt} ch=CH{Ch}", row.Code, row.ChannelNo);
                        continue;
                    }
                    var ev = MakeEvent(AlarmAction.Trigger, row.BoltId, row.ChannelId, "warn",
                        $"疑似接线变动：{row.Code}（CH{row.ChannelNo}）近 {_opt.WindowMinutes} 分钟声时均值 {avg:F0}ns " +
                        $"偏离验收指纹 {row.Fingerprint:F0}ns 达 {deviationNs:F0}ns（容差 {_opt.ToleranceNs:F0}ns）。" +
                        "请核对该通道电缆是否被换接/移位；确认接线无误（如更换过螺栓/重新装配）请重新验收以刷新指纹");
                    await _sink.PersistAsync(ev, ct);
                    // 兜底：推送实现按 IAlarmPublisher 约定不抛，真抛了说明实现坏了，不能连带把巡检循环打断
                    try { await _publisher.PublishAsync(ev, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "告警推送实现抛异常 bolt={Bolt} ch=CH{Ch}", row.Code, row.ChannelNo); }
                    _logger.LogWarning("接线可疑告警：bolt={Bolt} ch=CH{Ch} 偏差 {Dev:F0}ns", row.Code, row.ChannelNo, deviationNs);
                }
                else if (!hit && isOpen)
                {
                    var ev = MakeEvent(AlarmAction.Clear, row.BoltId, row.ChannelId, "info",
                        $"接线指纹恢复一致：{row.Code}（CH{row.ChannelNo}）声时回到验收指纹容差内");
                    await _sink.PersistAsync(ev, ct);
                    // 兜底：推送实现按 IAlarmPublisher 约定不抛，真抛了说明实现坏了，不能连带把巡检循环打断
                    try { await _publisher.PublishAsync(ev, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "告警推送实现抛异常 bolt={Bolt} ch=CH{Ch}", row.Code, row.ChannelNo); }
                    _logger.LogInformation("接线可疑告警闭合：bolt={Bolt} ch=CH{Ch}", row.Code, row.ChannelNo);
                }
                else if (hit && isOpen)
                {
                    await using var touch = _ds.CreateCommand(
                        "UPDATE alarms SET last_active_at = now() WHERE bolt_id = @b AND type = 'wiring_suspect' AND cleared_at IS NULL");
                    touch.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, row.BoltId);
                    await touch.ExecuteNonQueryAsync(ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "wiring_suspect 告警写入失败 bolt={Bolt} ch=CH{Ch}（下一轮重试）", row.Code, row.ChannelNo);
            }
        }
    }

    /// <summary>生效中的维护抑制（与 SignalHealthService.IsShelvedAsync 同一口径）。</summary>
    private async Task<bool> IsShelvedAsync(long boltId, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(@"
            SELECT 1 FROM alarm_shelvings
            WHERE cancelled_at IS NULL AND expires_at > now()
              AND (bolt_id IS NULL OR bolt_id = @bolt)
              AND (alarm_type IS NULL OR alarm_type = 'wiring_suspect')
            LIMIT 1");
        cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, boltId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static AlarmEvent MakeEvent(AlarmAction action, long boltId, long channelId, string severity, string message) =>
        new(action, boltId, channelId, "wiring_suspect", severity, DateTimeOffset.UtcNow,
            PreloadN: null, PreloadRatio: null, TargetPreloadN: null,
            ThresholdLowRatio: 0m, ThresholdHighRatio: 0m, ConsecutiveCount: 0, Message: message);
}
