using BoltPreload.Application.Alarm;
using BoltPreload.Application.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace BoltPreload.Api.Hubs;

/// <summary>
/// SignalR 告警推送。按 <see cref="IAlarmPublisher"/> 的约定失败不抛，不然推送出问题会反压入库主链；
/// 也因此留痕得记在这里，调用方那些 catch 轮不到。以前这里记 Debug，默认级别下等于没记。
/// </summary>
public sealed class SignalRAlarmPublisher : IAlarmPublisher
{
    private readonly IHubContext<MeasurementsHub> _hub;
    private readonly ILogger<SignalRAlarmPublisher> _logger;

    public SignalRAlarmPublisher(IHubContext<MeasurementsHub> hub, ILogger<SignalRAlarmPublisher> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task PublishAsync(AlarmEvent ev, CancellationToken ct = default)
    {
        try
        {
            await _hub.Clients.All.SendAsync("OnAlarm", new
            {
                action = ev.Action.ToString().ToLowerInvariant(),
                boltId = ev.BoltId,
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
