using System.Collections.Concurrent;
using BoltPreload.Application.Devices;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Fx;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoltPreload.Application.Ingestion;

public sealed class IngestionWatchdogOptions
{
    public bool Enabled { get; set; } = true;
    public int CheckIntervalSeconds { get; set; } = 10;
    public int NoDataTimeoutSeconds { get; set; } = 60;
    public int StartupGraceSeconds { get; set; } = 30;
    public int ReconnectCooldownSeconds { get; set; } = 30;
    public ushort Address { get; set; } = 1;
    public FxWorkMode WorkMode { get; set; } = FxWorkMode.MultiMeas;
}

/// <summary>
/// 采集看门狗：连接断开或长时间无 ChannelResult 时自动重连。
/// </summary>
public sealed class IngestionWatchdogService : BackgroundService
{
    private readonly IDeviceConnectorManager _deviceManager;
    private readonly IngestionWatchdogOptions _options;
    private readonly ILogger<IngestionWatchdogService> _logger;
    private readonly ProbeScanState _probeScanState;
    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastReconnectAttempts = new();

    public IngestionWatchdogService(
        IDeviceConnectorManager deviceManager,
        IOptions<IngestionWatchdogOptions> options,
        ProbeScanState probeScanState,
        ILogger<IngestionWatchdogService> logger)
    {
        _deviceManager = deviceManager;
        _options = options.Value;
        _probeScanState = probeScanState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("IngestionWatchdog 已禁用");
            return;
        }

        _logger.LogInformation(
            "IngestionWatchdog 启动 runtimes={Count}, noData={NoData}s, interval={Interval}s",
            _deviceManager.Runtimes.Count, _options.NoDataTimeoutSeconds, _options.CheckIntervalSeconds);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.StartupGraceSeconds), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.CheckIntervalSeconds));
        // 顶层 try/catch 保护 — 单次检查异常不能让 BackgroundService 整个挂掉
        // （默认 BackgroundServiceExceptionBehavior=StopHost 会停整个 app，对 7×24 系统是灾难）
        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
            }
            catch (OperationCanceledException) { return; }

            foreach (var runtime in _deviceManager.Runtimes)
            {
                try
                {
                    await CheckRuntimeAsync(runtime, stoppingToken);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Watchdog 检查 runtime 抛异常 device={DeviceId}，继续下一个", runtime.DeviceId);
                }
            }
        }
    }

    private async Task CheckRuntimeAsync(DeviceRuntimeContext runtime, CancellationToken ct)
    {
        // 扫描期：通道扫描器独占工作模式，Watchdog 必须完全跳过该设备，
        // 否则 60s 单通道无数据会被强制 Reconnect + MultiMeas，打断扫描。
        if (_probeScanState.IsRunning(runtime.DeviceId))
            return;

        var now = DateTimeOffset.UtcNow;
        var connector = runtime.Connector;
        var status = runtime.RuntimeStatus;
        var last = status.LastChannelResultAt;
        var noDataFor = last is null ? TimeSpan.MaxValue : now - last.Value;

        var disconnected = connector.State != FxConnectorState.Connected;
        var stale = noDataFor > TimeSpan.FromSeconds(_options.NoDataTimeoutSeconds);

        if (!disconnected && !stale)
            return;

        var lastAttempt = _lastReconnectAttempts.GetOrAdd(runtime.DeviceId, DateTimeOffset.MinValue);
        if (now - lastAttempt < TimeSpan.FromSeconds(_options.ReconnectCooldownSeconds))
            return;

        _lastReconnectAttempts[runtime.DeviceId] = now;
        _logger.LogWarning(
            "采集看门狗触发重连 device={DeviceId}, state={State}, lastChannelResult={Last}, noDataFor={NoDataFor}s",
            runtime.DeviceId, connector.State, last, last is null ? -1 : (int)noDataFor.TotalSeconds);

        try
        {
            await connector.ReconnectAsync(ct);
            await WaitConnectedAsync(connector, runtime.DeviceId, ct);
            await connector.SetWorkModeAsync(_options.WorkMode, channel: 0, address: runtime.Options.Address, ct);

            _logger.LogInformation(
                "采集看门狗重连完成 device={DeviceId}, mode={Mode}",
                runtime.DeviceId, _options.WorkMode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "采集看门狗重连失败 device={DeviceId}", runtime.DeviceId);
        }
    }

    private async Task WaitConnectedAsync(IFxConnector connector, long deviceId, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            if (connector.State == FxConnectorState.Connected) return;
            await Task.Delay(50, ct);
        }
        _logger.LogWarning("重连后等待 Connected 超时 device={DeviceId} state={State}", deviceId, connector.State);
    }
}
