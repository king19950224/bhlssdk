using BoltPreload.Application.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace BoltPreload.Api.Hubs;

/// <summary>SignalR 通道在位（探头热插拔）推送。频率极低，直接 SendAsync 不需要队列。</summary>
public sealed class SignalRChannelPresencePublisher : IChannelPresencePublisher
{
    private readonly IHubContext<MeasurementsHub> _hub;
    private readonly ILogger<SignalRChannelPresencePublisher> _logger;

    public SignalRChannelPresencePublisher(IHubContext<MeasurementsHub> hub, ILogger<SignalRChannelPresencePublisher> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task PublishAsync(
        BoltPreload.Application.Ingestion.ChannelPresenceChange change,
        long? channelId, long? boltId, string? boltCode,
        CancellationToken ct = default)
    {
        try
        {
            await _hub.Clients.All.SendAsync("OnChannelPresence", new
            {
                deviceId = change.DeviceId,
                channelNo = (int)change.ChannelNo,
                present = change.Present,
                ts = change.At,
                initial = change.Initial,
                channelId,
                boltId,
                boltCode
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SignalR 在位推送失败 device={Device} ch={Ch}", change.DeviceId, change.ChannelNo);
        }
    }
}
