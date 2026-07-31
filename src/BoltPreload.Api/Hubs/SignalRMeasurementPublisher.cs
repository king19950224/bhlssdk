using BoltPreload.Application.Realtime;
using BoltPreload.Fx.Models;
using Microsoft.AspNetCore.SignalR;

namespace BoltPreload.Api.Hubs;

/// <summary>
/// 基于 SignalR 的实时测量发布者。
/// <para>
/// 把内部 <see cref="FxDecodedEnvelope"/> 转成对外 DTO 后推到客户端，
/// 隐藏 SDK/内部类型，避免外部依赖 BoltPreload.Fx 程序集。
/// </para>
/// <para>
/// 背压防护：采集热路径对每条测量 fire-and-forget 调用本类，若直接 await SendAsync，
/// 客户端慢/断线重连时 in-flight 发送任务会无界堆积反压采集。改为【有界队列 + 单泵】：
/// PublishAsync 只做非阻塞入队（队列满丢最旧——实时推送只关心最新值，轮询兜底补齐），
/// 由常驻泵任务串行发送，把推送速率与采集速率解耦。
/// </para>
/// </summary>
public sealed class SignalRMeasurementPublisher : IMeasurementPublisher, IDisposable
{
    private const int QueueCapacity = 512;

    private readonly IHubContext<MeasurementsHub> _hub;
    private readonly ILogger<SignalRMeasurementPublisher> _logger;
    private readonly System.Threading.Channels.Channel<FxDecodedEnvelope> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private long _droppedCount;

    public SignalRMeasurementPublisher(IHubContext<MeasurementsHub> hub, ILogger<SignalRMeasurementPublisher> logger)
    {
        _hub = hub;
        _logger = logger;
        _queue = System.Threading.Channels.Channel.CreateBounded<FxDecodedEnvelope>(
            new System.Threading.Channels.BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });
        _pump = Task.Run(PumpAsync);
    }

    public Task PublishAsync(FxDecodedEnvelope envelope, CancellationToken ct = default)
    {
        // DropOldest 模式下 TryWrite 恒成功（挤掉最旧条目）；计数仅用于观测推送拥塞
        if (!_queue.Writer.TryWrite(envelope))
            Interlocked.Increment(ref _droppedCount);
        return Task.CompletedTask;
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var envelope in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    await SendAsync(envelope, _cts.Token);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    // 单条推送失败不影响后续（客户端断线等瞬态问题）
                    _logger.LogDebug(ex, "SignalR 推送失败 device={DeviceId}", envelope.DeviceId);
                }
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    private async Task SendAsync(FxDecodedEnvelope envelope, CancellationToken ct)
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

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { _pump.Wait(TimeSpan.FromSeconds(2)); } catch { /* 停机尽力 */ }
        _cts.Dispose();
    }
}
