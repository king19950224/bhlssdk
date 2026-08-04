using BoltPreload.Application.Alarm;
using BoltPreload.Application.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace BoltPreload.Api.Hubs;

/// <summary>
/// SignalR 告警推送。
/// <para>
/// 按 <see cref="IAlarmPublisher"/> 的约定失败不抛（不能让推送问题反压入库主链），
/// 因此<b>可见性由这里负责</b>：调用方写的那些 Warning 处理器按契约根本轮不到。
/// 曾经这里记 Debug，默认级别下等于没记——告警落了库，大屏不弹窗，日志里一片安静，
/// 值班看到的就是"没有告警"。
/// </para>
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
            // 不节流：告警只在状态机跳变时产生，本身就是低频事件，每条推丢都值得单独留一行
            _logger.LogWarning(ex,
                "告警实时推送失败，前端不会弹窗（告警已入库，可在告警中心查到）bolt={Bolt} type={Type}",
                ev.BoltId, ev.Type);
        }
    }
}
