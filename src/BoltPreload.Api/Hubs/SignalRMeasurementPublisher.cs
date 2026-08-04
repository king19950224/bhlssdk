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
    private long _sendFailures;
    private long _lastWarnTicks;

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
        // DropOldest：队满时挤掉最旧一条。实时推送要的是最新值，积压的旧帧对界面没意义，
        // 这里丢弃是对的（与采集主链的 BackpressureWriter 刻意相反，那边一条都不能丢）。
        // 该模式下 TryWrite 恒成功，无需判返回值 —— 原先据此累加的丢弃计数永远是 0，也无人读取。
        _queue.Writer.TryWrite(envelope);
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
                    // 单条推送失败不影响后续（客户端断线等瞬态问题），但必须留痕：
                    // 数据仍然落库、告警判定也照常，只是大屏和操作台从此不再刷新，
                    // 现场看到的是一屏冻住的旧数字。这正是最需要被看见的一类失效。
                    WarnSendFailureThrottled(ex, envelope.DeviceId);
                }
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    /// <summary>
    /// 发送失败告警，每秒最多一条。
    /// <para>
    /// 必须节流：50Hz × N 通道下推送一旦持续失败，不节流会把日志刷爆。
    /// 也必须记在这里——<see cref="PublishAsync"/> 只入队不发送，异常出在泵任务里，
    /// 上层调用方的 catch 按契约（实现不抛）永远不会执行到。
    /// </para>
    /// </summary>
    private void WarnSendFailureThrottled(Exception ex, long deviceId)
    {
        var total = Interlocked.Increment(ref _sendFailures);
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastWarnTicks);
        if (now - last < TimeSpan.TicksPerSecond) return;
        if (Interlocked.CompareExchange(ref _lastWarnTicks, now, last) != last) return;
        _logger.LogWarning(ex,
            "实时推送失败，前端将停止刷新（数据已入库，不影响告警判定）device={DeviceId} 累计失败={Total}",
            deviceId, total);
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
