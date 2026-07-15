using BoltPreload.Application.Devices;
using BoltPreload.Application.Realtime;
using BoltPreload.Fx;
using BoltPreload.Fx.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoltPreload.Application.Ingestion;

/// <summary>
/// 从 IFxConnector 消费数据，写入数据库。
/// 一个 IFxConnector 对应一个设备，对应一个 IngestionService 任务。
/// </summary>
/// <remarks>
/// Phase 1 简化版：单连接、同步消费、单条入库。
/// Phase 2+ 改：批量 + WAL + 多连接管理（按方案 §5.2 ConnectorService）。
/// </remarks>
public sealed class IngestionService : BackgroundService
{
    private readonly IDeviceConnectorManager _deviceManager;
    private readonly IMeasurementSink _sink;
    private readonly IMeasurementPublisher _publisher;
    private readonly IMeasurementWal _wal;
    private readonly ChannelPresenceTracker _presence;
    private readonly ILogger<IngestionService> _logger;

    public IngestionService(
        IDeviceConnectorManager deviceManager,
        IMeasurementSink sink,
        IMeasurementPublisher publisher,
        IMeasurementWal wal,
        ChannelPresenceTracker presence,
        ILogger<IngestionService> logger)
    {
        _deviceManager = deviceManager;
        _sink = sink;
        _publisher = publisher;
        _wal = wal;
        _presence = presence;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runtimes = _deviceManager.Runtimes.ToArray();
        _logger.LogInformation("IngestionService 启动 runtimes={Count}", runtimes.Length);

        var tasks = runtimes.Select(runtime => RunRuntimeAsync(runtime, stoppingToken)).ToArray();
        await Task.WhenAll(tasks);
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
            await WaitConnectedAsync(connector, runtime.DeviceId, stoppingToken);
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

    /// <summary>
    /// 轮询等待 connector 进入 Connected 状态，最多等 5 秒。
    /// 旧版本用 Task.Delay(1s) 硬等，对慢机器不准 / 对快机器浪费。
    /// </summary>
    private async Task WaitConnectedAsync(IFxConnector connector, long deviceId, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            if (connector.State == FxConnectorState.Connected) return;
            await Task.Delay(50, ct);
        }
        _logger.LogWarning("等待 connector Connected 超时 device={DeviceId} state={State}",
            deviceId, connector.State);
    }

    private void ObservePresence(FxDecodedEnvelope envelope)
    {
        switch (envelope.ResultType)
        {
            case FxResultType.ChannelResult when envelope.ChannelResult is not null:
                _presence.Observe(envelope.DeviceId, envelope.ChannelResult.Channel,
                    envelope.ChannelResult.EchoTimeNs is not null, envelope.ReceiveTime);
                break;
            case FxResultType.MultiChannelResult when envelope.MultiChannelResult is not null:
                foreach (var (chNo, cr) in envelope.MultiChannelResult.Channels)
                    _presence.Observe(envelope.DeviceId, chNo, cr.EchoTimeNs is not null, envelope.ReceiveTime);
                break;
        }
    }

    private async Task SafePublishAsync(FxDecodedEnvelope envelope, CancellationToken ct)
    {
        try { await _publisher.PublishAsync(envelope, ct); }
        catch (OperationCanceledException) { /* 进程退出 */ }
        catch (Exception ex) { _logger.LogDebug(ex, "实时推送失败 device={DeviceId}", envelope.DeviceId); }
    }
}
