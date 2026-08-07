using BoltPreload.Application.Devices;
using BoltPreload.Application.Diagnostics;
using BoltPreload.Application.Realtime;
using BoltPreload.Fx;
using BoltPreload.Fx.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoltPreload.Application.Ingestion;

/// <summary>
/// 从 IFxConnector 消费数据，写入数据库。
/// 一个 IFxConnector 对应一个设备，对应一条消费任务。
/// </summary>
/// <remarks>
/// supervisor 对账模式：不再启动时取一次运行时数组，而是每 2 秒与
/// <see cref="IDeviceConnectorManager.Runtimes"/> 对账——manager 有而任务表没有的起任务，
/// 任务表有而 manager 没有的取消、等退出、释放连接器。设备热增删的接缝在 manager 写侧，
/// 本服务只认对账结果。启动首轮对账起出的任务集与旧的"取一次数组"实现完全相同。
/// 每设备一条独立消费循环并发运行（单设备异常退避重启，不拖垮其它设备）；
/// 入库失败与背压溢出走 WAL 兜底，由 <c>WalReplayService</c> 重放补写。
/// </remarks>
public sealed class IngestionService : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(15);

    private readonly IDeviceConnectorManager _deviceManager;
    private readonly IMeasurementSink _sink;
    private readonly IMeasurementPublisher _publisher;
    private readonly IMeasurementWal _wal;
    private readonly ChannelPresenceTracker _presence;
    private readonly IngestionTaskRegistry _registry;
    private readonly BackgroundServiceHealth _health;
    private readonly ILogger<IngestionService> _logger;

    public IngestionService(
        IDeviceConnectorManager deviceManager,
        IMeasurementSink sink,
        IMeasurementPublisher publisher,
        IMeasurementWal wal,
        ChannelPresenceTracker presence,
        IngestionTaskRegistry registry,
        BackgroundServiceHealth health,
        ILogger<IngestionService> logger)
    {
        _deviceManager = deviceManager;
        _sink = sink;
        _publisher = publisher;
        _wal = wal;
        _presence = presence;
        _registry = registry;
        _health = health;
        _logger = logger;
    }

    private sealed record RunningRuntime(DeviceRuntimeContext Runtime, CancellationTokenSource Cts, Task Task);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new Dictionary<long, RunningRuntime>();
        _logger.LogInformation("IngestionService 启动（supervisor 对账模式）runtimes={Count}", _deviceManager.Runtimes.Count);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // 心跳：本服务停摆 = 全部设备停止采集，属安全相关的静默失效，
                // 必须能在 /api/health/ready 看出来（宿主的 Ignore 策略让进程照常存活）
                _health.Beat("ingestion", ReconcileInterval);

                // 单轮对账的异常一律不许冒出循环：冒出去就是 ExecuteAsync 正常返回、
                // 本服务被宿主按 Ignore 悄悄注销 —— 全站停采而进程看着还活着。
                // 单设备的异常由 RunRuntimeAsync 的兜底重启循环负责，这里挡的是对账动作本身。
                try
                {
                    StartMissing(running, stoppingToken);
                    await StopOrphanedAsync(running);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "采集对账轮次异常，{Interval}s 后重试（已在跑的消费任务不受影响）",
                        ReconcileInterval.TotalSeconds);
                }

                try
                {
                    await Task.Delay(ReconcileInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            // 停机：取消全部任务并等它们走完 finally（DisconnectAsync 收尾）。
            // 连接器的释放不在这里做——字典里的运行时由 DeviceConnectorManager.DisposeAsync 统一释放，
            // 与重构前一致；本服务只释放"被摘出字典的孤儿"（见 StopOrphanedAsync）。
            foreach (var r in running.Values) r.Cts.Cancel();
            // RunRuntimeAsync 吞掉一切非取消异常（兜底重启循环），取消路径正常 return，这里不会抛
            await Task.WhenAll(running.Values.Select(r => r.Task));
            foreach (var r in running.Values) r.Cts.Dispose();
            _logger.LogInformation("IngestionService 停机，{Count} 条消费任务已退出", running.Count);
        }
    }

    /// <summary>manager 有而任务表没有的设备：起消费任务（启动首轮 = 全量起动，此后 = 热加的设备）。</summary>
    private void StartMissing(Dictionary<long, RunningRuntime> running, CancellationToken stoppingToken)
    {
        foreach (var runtime in _deviceManager.Runtimes)
        {
            if (running.ContainsKey(runtime.DeviceId))
                continue;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var task = RunRuntimeAsync(runtime, cts.Token);
            running[runtime.DeviceId] = new RunningRuntime(runtime, cts, task);
            _registry.MarkStarted(runtime.DeviceId);
        }
    }

    /// <summary>
    /// 任务表有而 manager 没有的设备（被禁用/删除/重启连接）：取消任务、等退出、释放连接器。
    /// 连接器必须在这里释放——它已不在 manager 字典里，停机路径不会再碰它，
    /// 不释放就是半开 TCP 会话挂到进程退出（设备端可能拒绝下一次连接）。
    /// </summary>
    private async Task StopOrphanedAsync(Dictionary<long, RunningRuntime> running)
    {
        List<long>? orphaned = null;
        var current = new HashSet<long>(_deviceManager.Runtimes.Select(r => r.DeviceId));
        foreach (var id in running.Keys)
        {
            if (!current.Contains(id))
                (orphaned ??= []).Add(id);
        }
        if (orphaned is null)
            return;

        foreach (var id in orphaned)
        {
            var r = running[id];
            r.Cts.Cancel();
            try
            {
                await r.Task.WaitAsync(StopGrace);
            }
            catch (TimeoutException)
            {
                // 任务卡死也照常继续释放：多半卡在 DB 写入或 SDK 调用上，
                // 释放连接器会让阻塞的 IO 抛错收场，比无限等强
                _logger.LogWarning("设备 {DeviceId} 的消费任务 {Grace}s 内未退出，强制释放连接器", id, StopGrace.TotalSeconds);
            }
            catch (Exception ex)
            {
                // 任务以异常收场时 WaitAsync 会把该异常重抛出来。不接住的话摘除流程从这里断：
                // 连接器不释放（半开 TCP 会话留到进程退出）、running 里的条目不删，
                // 于是每 2 秒对账都把它当孤儿重试一遍，异常一路冒到 ExecuteAsync 之外 ——
                // 配 BackgroundServiceExceptionBehavior.Ignore 就是本服务静默退出、全部设备停采。
                // RunRuntimeAsync 的兜底循环理论上吞掉一切非取消异常，真漏出来也只是这一条任务的事，
                // 记下来继续把摘除做完。
                _logger.LogWarning(ex, "设备 {DeviceId} 的消费任务以异常退出，继续摘除", id);
            }
            try
            {
                await r.Runtime.Connector.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "释放被摘除设备的连接器失败 device={DeviceId}", id);
            }
            r.Cts.Dispose();
            running.Remove(id);
            _registry.MarkStopped(id);
            _logger.LogInformation("设备 {DeviceId} 已摘出采集（任务退出、连接器释放）", id);
        }
    }

    private async Task RunRuntimeAsync(DeviceRuntimeContext runtime, CancellationToken stoppingToken)
    {
        // H3：把背压溢出（通道满被挤掉的最旧测量）接到 WAL 兜底——由 WalReplayService 重放补写，不再静默丢失。
        // WAL 未启用时保持 null（仅计数丢弃，经 connector.DroppedCount 与 /api/runtime/devices 暴露）。
        if (_wal.Enabled)
            runtime.Connector.OverflowSink = envelope => _wal.AppendAsync(envelope, stoppingToken);

        // M1：兜底重启循环。单设备的连接 / 消费循环抛意外异常时退避后重启该设备的采集，
        // 既不拖垮其它设备（Task.WhenAll 不因单 task 失败而整体失败），也不让 IngestionService 静默退出。
        // 旧实现：消费循环外的代码（MarkEnvelope / 日志）一旦抛异常会冒泡到 WhenAll → ExecuteAsync 退出
        // = 全部设备停采，而 BackgroundServiceExceptionBehavior.Ignore 让进程"看起来还活着" → 静默全局停采。
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunRuntimeOnceAsync(runtime, stoppingToken);
                return; // 正常结束（通常是 stoppingToken 取消令 ReadAllAsync 收尾）
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                var delay = TimeSpan.FromSeconds(Math.Min(30, 3 * attempt));
                _logger.LogError(ex,
                    "Ingestion runtime 异常退出 device={DeviceId}，{Delay}s 后兜底重启（第 {Attempt} 次）—— 其它设备不受影响",
                    runtime.DeviceId, delay.TotalSeconds, attempt);
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task RunRuntimeOnceAsync(DeviceRuntimeContext runtime, CancellationToken stoppingToken)
    {
        var connector = runtime.Connector;
        var runtimeStatus = runtime.RuntimeStatus;
        _logger.LogInformation("Ingestion runtime 启动 device={DeviceId} name={Name}", runtime.DeviceId, runtime.Name);

        // 首次连接失败不再静默 return — Watchdog 会接管重连，期间继续 ReadAllAsync 等数据；
        // 否则 runtime task 退出后整个 IngestionService.ExecuteAsync 会"看起来成功"，
        // Host 当作正常退出，但其实根本没在采集。
        try
        {
            await connector.ConnectAsync(stoppingToken);
            if (!await FxConnectorWait.UntilConnectedAsync(connector, stoppingToken))
                _logger.LogWarning("等待 connector Connected 超时 device={DeviceId} state={State}",
                    runtime.DeviceId, connector.State);
            await connector.SetWorkModeAsync(FxWorkMode.MultiMeas, channel: 0, address: runtime.Options.Address, stoppingToken);
            _logger.LogInformation("已下发 MultiMeas device={DeviceId}", runtime.DeviceId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ingestion runtime 启动连接 / 下发模式失败 device={DeviceId}，等待 Watchdog 接管", runtime.DeviceId);
        }

        // 消费循环：哪怕首次连接失败，也进入这里，Watchdog 重连成功后数据会从 Channel 流过来
        try
        {
            await foreach (var envelope in connector.Results.ReadAllAsync(stoppingToken))
            {
                // 新鲜度按【收包时刻】(ReceiveTime，connector 收到即打的 UtcNow) 计，而非出队/入库时刻。
                // 否则设备已断但 Channel 仍有积压时，消费线程会用"当前时间"持续刷新 LastChannelResultAt，
                // 让看门狗误以为"仍有数据"而延迟重连（积压越大延迟越久）。
                runtimeStatus.MarkEnvelope(
                    envelope.ReceiveTime,
                    envelope.ResultType is FxResultType.ChannelResult or FxResultType.MultiChannelResult);

                // 探头热插拔在位判定（纯内存计数，不做 IO；事件由 ChannelPresenceService 异步消费）
                ObservePresence(envelope);

                if (envelope.ResultType == FxResultType.DeviceParameter && envelope.DeviceParameter is not null)
                {
                    var dp = envelope.DeviceParameter;
                    _logger.LogInformation(
                        "收到 DeviceParameter device={DeviceId} addr={Addr} ch={Ch} param={Param} value={Value}",
                        envelope.DeviceId, dp.Address, dp.Channel, dp.ParamName, dp.ParamValue);
                }

                var persisted = false;
                // 标记"正在写库"：让 Watchdog 在慢 DB 写入期间不要把该设备误判为"无数据"而重连（见 finally 清标记）
                runtimeStatus.MarkWriteStart();
                try
                {
                    await _sink.WriteAsync(envelope, stoppingToken);
                    persisted = true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 不抛出——避免单条坏写拖垮整个采集循环；累计失败计数让丢数可观测
                    //（经 /api/runtime/devices 暴露）。测量类信封写失败时落 WAL 兜底，
                    // DB 恢复后由 WalReplayService 重放补写（重放不触发告警）。
                    runtimeStatus.MarkWriteFailure();

                    var isMeasurement = envelope.ResultType
                        is FxResultType.ChannelResult or FxResultType.MultiChannelResult;

                    if (isMeasurement && _wal.Enabled)
                    {
                        try
                        {
                            await _wal.AppendAsync(envelope, stoppingToken);
                            _logger.LogError(ex,
                                "入库写失败，已落 WAL 待重放 device={DeviceId} ts={Ts} 累计写失败={Failures} WAL待重放={Pending}",
                                envelope.DeviceId, envelope.ReceiveTime, runtimeStatus.WriteFailureCount, _wal.PendingCount);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception walEx)
                        {
                            _logger.LogError(walEx,
                                "入库写失败且 WAL 落盘也失败，measurement 丢失 device={DeviceId} ts={Ts} 累计写失败={Failures}",
                                envelope.DeviceId, envelope.ReceiveTime, runtimeStatus.WriteFailureCount);
                        }
                    }
                    else
                    {
                        _logger.LogError(ex,
                            "入库写失败 device={DeviceId} ts={Ts} 累计写失败={Failures}（非测量信封或 WAL 未启用，不兜底）",
                            envelope.DeviceId, envelope.ReceiveTime, runtimeStatus.WriteFailureCount);
                    }
                }
                finally
                {
                    runtimeStatus.MarkWriteEnd();
                }

                // 实时推送：仅在【成功落库】后推送，避免前端展示从未持久化的数据（数据完整性）。
                // 入库失败已落 WAL 的数据，由 WalReplayService 重放补写（重放不推送）；
                // 宁可实时通道短暂缺一条（前端 REST 轮询/历史仍可见），也不推未入库的数据。
                if (persisted)
                    _ = SafePublishAsync(envelope, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Ingestion runtime 收到取消请求 device={DeviceId}", runtime.DeviceId);
        }
        finally
        {
            await connector.DisconnectAsync(CancellationToken.None);
            _logger.LogInformation("Ingestion runtime 退出 device={DeviceId}", runtime.DeviceId);
        }
    }

    private void ObservePresence(FxDecodedEnvelope envelope)
    {
        switch (envelope.ResultType)
        {
            case FxResultType.ChannelResult when envelope.ChannelResult is not null:
                _presence.Observe(envelope.DeviceId, envelope.ChannelResult.Channel,
                    (double?)envelope.ChannelResult.EchoTimeNs, envelope.ReceiveTime);
                break;
            case FxResultType.MultiChannelResult when envelope.MultiChannelResult is not null:
                foreach (var (chNo, cr) in envelope.MultiChannelResult.Channels)
                    _presence.Observe(envelope.DeviceId, chNo, (double?)cr.EchoTimeNs, envelope.ReceiveTime);
                break;
        }
    }

    private long _publishFailures;
    private long _lastPublishWarnTicks;

    private async Task SafePublishAsync(FxDecodedEnvelope envelope, CancellationToken ct)
    {
        try { await _publisher.PublishAsync(envelope, ct); }
        catch (OperationCanceledException) { /* 进程退出 */ }
        catch (Exception ex) { WarnPublishFailureThrottled(ex, envelope.DeviceId); }
    }

    /// <summary>
    /// 实时推送失败告警（节流：每秒最多一条，写法参照 FxSdkConnector 的背压告警）。
    /// 这是 <see cref="IMeasurementPublisher"/> 约定失败不抛的兜底：SignalR 实现只在 PublishAsync 里入队，
    /// 发送异常出在它自己的泵任务里、由它自己记。换成会抛的实现这条才会响，口径和节流都跟着一致。
    /// </summary>
    private void WarnPublishFailureThrottled(Exception ex, long deviceId)
    {
        var total = Interlocked.Increment(ref _publishFailures);
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastPublishWarnTicks);
        if (now - last < TimeSpan.TicksPerSecond) return;
        if (Interlocked.CompareExchange(ref _lastPublishWarnTicks, now, last) != last) return;
        _logger.LogWarning(ex,
            "实时推送失败，前端将停止刷新（数据已入库，不影响告警判定）device={DeviceId} 累计失败={Total}",
            deviceId, total);
    }
}
