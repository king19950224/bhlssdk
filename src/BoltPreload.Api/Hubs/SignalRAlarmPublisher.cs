using BoltPreload.Application.Alarm;
using BoltPreload.Application.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace BoltPreload.Api.Hubs;

/// <summary>SignalR 告警推送。</summary>
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
            _logger.LogDebug(ex, "SignalR 告警推送失败 bolt={Bolt}", ev.BoltId);
        }
    }
}
