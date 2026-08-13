using BoltPreload.Application.Alarm;
using BoltPreload.Application.Realtime;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Hubs;

/// <summary>
/// SignalR 告警推送。按 <see cref="IAlarmPublisher"/> 的约定失败不抛，不然推送出问题会反压入库主链；
/// 也因此留痕得记在这里，调用方那些 catch 轮不到。以前这里记 Debug，默认级别下等于没记。
/// </summary>
public sealed class SignalRAlarmPublisher : IAlarmPublisher
{
    private readonly IHubContext<MeasurementsHub> _hub;
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<SignalRAlarmPublisher> _logger;

    public SignalRAlarmPublisher(IHubContext<MeasurementsHub> hub, NpgsqlDataSource ds, ILogger<SignalRAlarmPublisher> logger)
    {
        _hub = hub;
        _ds = ds;
        _logger = logger;
    }

    public async Task PublishAsync(AlarmEvent ev, CancellationToken ct = default)
    {
        try
        {
            // 前端 critical 强制弹窗要能直接说"哪颗螺栓"（AppShell 会渲染 boltCode）。
            // 评估器事件出自采集热路径，只带 boltId 不带 code；告警本身低频，这里补查一次。
            // 查失败不阻断推送——boltCode 缺失时前端退化为只显示 message，告警本体不受影响。
            string? boltCode = null;
            try
            {
                await using var cmd = _ds.CreateCommand("SELECT code FROM bolts WHERE id = @id");
                cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, ev.BoltId);
                boltCode = await cmd.ExecuteScalarAsync(ct) as string;
            }
            catch (Exception lookupEx)
            {
                _logger.LogDebug(lookupEx, "推送前补查螺栓编号失败 bolt={Bolt}，弹窗将不带编号", ev.BoltId);
            }

            await _hub.Clients.All.SendAsync("OnAlarm", new
            {
                action = ev.Action.ToString().ToLowerInvariant(),
                boltId = ev.BoltId,
                boltCode,
                channelId = ev.ChannelId,
                type = ev.Type,
                severity = ev.Severity,
                ts = ev.Ts,
                preloadN = ev.PreloadN,
                preloadRatio = ev.PreloadRatio,
                targetPreloadN = ev.TargetPreloadN,
                thresholdLow = ev.ThresholdLowRatio,
                thresholdHigh = ev.ThresholdHighRatio,
                consecutive = ev.ConsecutiveCount,
                message = ev.Message
            }, ct);
        }
        catch (Exception ex)
        {
            // 不节流：告警只在状态机跳变时产生，本来就低频，每条都单独记一行
            _logger.LogWarning(ex,
                "告警实时推送失败，前端不会弹窗（告警已入库，可在告警中心查到）bolt={Bolt} type={Type}",
                ev.BoltId, ev.Type);
        }
    }
}
