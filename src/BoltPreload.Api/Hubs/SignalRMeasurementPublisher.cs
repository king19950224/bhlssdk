using BoltPreload.Application.Alarm;
using BoltPreload.Application.Realtime;
using BoltPreload.Fx.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

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

/// <summary>
/// 基于 SignalR 的实时测量发布者。
/// <para>
/// 把内部 <see cref="FxDecodedEnvelope"/> 转成对外 DTO 后推到客户端，
/// 隐藏 SDK/内部类型，避免外部依赖 BoltPreload.Fx 程序集。
/// </para>
/// </summary>
public sealed class SignalRMeasurementPublisher : IMeasurementPublisher
{
    private readonly IHubContext<MeasurementsHub> _hub;
    private readonly ILogger<SignalRMeasurementPublisher> _logger;

    public SignalRMeasurementPublisher(IHubContext<MeasurementsHub> hub, ILogger<SignalRMeasurementPublisher> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task PublishAsync(FxDecodedEnvelope envelope, CancellationToken ct = default)
    {
        try
        {
            switch (envelope.ResultType)
            {
                case FxResultType.ChannelResult when envelope.ChannelResult is not null:
                    await _hub.Clients.All.SendAsync("OnMeasurement", new
                    {
                        type = "channel",
                        deviceId = envelope.DeviceId,
                        ts = envelope.ReceiveTime,
                        channel = envelope.ChannelResult.Channel,
                        statusCode = envelope.ChannelResult.StatusCode,
                        echoTimeNs = envelope.ChannelResult.EchoTimeNs,
                        spTempC = envelope.ChannelResult.SpTempC,
                        gain = envelope.ChannelResult.Gain,
                        hasWave = envelope.ChannelResult.EchoWave is not null
                    }, ct);
                    break;

                case FxResultType.MultiChannelResult when envelope.MultiChannelResult is not null:
                    foreach (var (chNo, cr) in envelope.MultiChannelResult.Channels)
                    {
                        await _hub.Clients.All.SendAsync("OnMeasurement", new
                        {
                            type = "channel",
                            deviceId = envelope.DeviceId,
                            ts = envelope.ReceiveTime,
                            channel = chNo,
                            statusCode = cr.StatusCode,
                            echoTimeNs = cr.EchoTimeNs,
                            spTempC = cr.SpTempC,
                            gain = cr.Gain,
                            hasWave = cr.EchoWave is not null
                        }, ct);
                    }
                    break;

                case FxResultType.DeviceParameter when envelope.DeviceParameter is not null:
                    await _hub.Clients.All.SendAsync("OnDeviceParameter", new
                    {
                        deviceId = envelope.DeviceId,
                        ts = envelope.ReceiveTime,
                        address = envelope.DeviceParameter.Address,
                        channel = envelope.DeviceParameter.Channel,
                        paramName = envelope.DeviceParameter.ParamName,
                        valueText = envelope.DeviceParameter.ParamValue?.ToString()
                    }, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            // 推送失败不阻塞主流程
            _logger.LogDebug(ex, "SignalR 推送失败 device={DeviceId}", envelope.DeviceId);
        }
    }
}
