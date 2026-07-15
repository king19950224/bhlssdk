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
    /// <summary>建连/重连(Connecting/Reconnecting)在此宽限内不被看门狗干预，避免打断进行中的连接 + provisioning
    /// 形成重连风暴；仅当滞留超过该值（疑似 OnConnected 未回调卡死）才强制重连。</summary>
    public int ConnectGraceSeconds { get; set; } = 45;
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
    // 设备"自何时起处于已连接"（断开即清）。用于给刚（重）连上的设备完整无数据宽限，
    // 避免 LastChannelResultAt 尚为 null / 仍是断开前旧值时被判无穷陈旧而误重连。
    private readonly ConcurrentDictionary<long, DateTimeOffset> _connectedSince = new();
    // 设备"自何时起处于建连中(Connecting/Reconnecting)"。给进行中的连接 + provisioning 一段宽限，
    // 避免看门狗对着 in-flight 连接再发 ReconnectAsync 打断它、形成重连风暴。
    private readonly ConcurrentDictionary<long, DateTimeOffset> _inFlightSince = new();

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
        var now = DateTimeOffset.UtcNow;
        var connector = runtime.Connector;
        var status = runtime.RuntimeStatus;
        var state = connector.State;

        // 记录各阶段起点（GetOrAdd 不覆盖，仅记首次观察到的时刻）：
        //  - Connected：记 connectedSince，清 inFlightSince；
        //  - Connecting/Reconnecting：记 inFlightSince，清 connectedSince；
        //  - Idle/Disconnected/Faulted：两者都清（下次进入某阶段重新计时）。
        DateTimeOffset? connectedSince = null, inFlightSince = null;
        switch (state)
        {
            case FxConnectorState.Connected:
                _inFlightSince.TryRemove(runtime.DeviceId, out _);
                connectedSince = _connectedSince.GetOrAdd(runtime.DeviceId, now);
                break;
            case FxConnectorState.Connecting:
            case FxConnectorState.Reconnecting:
                _connectedSince.TryRemove(runtime.DeviceId, out _);
                inFlightSince = _inFlightSince.GetOrAdd(runtime.DeviceId, now);
                break;
            default:
                _connectedSince.TryRemove(runtime.DeviceId, out _);
                _inFlightSince.TryRemove(runtime.DeviceId, out _);
                break;
        }

        if (!ShouldReconnect(
                _probeScanState.IsRunning(runtime.DeviceId), state, status.IsWriting,
                status.LastChannelResultAt, connectedSince, inFlightSince, now,
                _options.NoDataTimeoutSeconds, _options.ConnectGraceSeconds))
            return;

        // 冷却节流：避免高频重连风暴
        var lastAttempt = _lastReconnectAttempts.GetOrAdd(runtime.DeviceId, DateTimeOffset.MinValue);
        if (now - lastAttempt < TimeSpan.FromSeconds(_options.ReconnectCooldownSeconds))
            return;

        _lastReconnectAttempts[runtime.DeviceId] = now;
        _logger.LogWarning(
            "采集看门狗触发重连 device={DeviceId}, state={State}, lastChannelResult={Last}",
            runtime.DeviceId, connector.State, status.LastChannelResultAt);

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

    /// <summary>
    /// 纯判定：是否应触发重连（不含冷却节流）。抽出为可测方法，覆盖：
    /// 扫描期跳过、建连中(Connecting/Reconnecting)给宽限不打断、慢写库(IsWriting)期间跳过、
    /// 刚(重)连的 null/旧时间戳宽限、按本次连接会话计"无数据时长"。
    /// </summary>
    internal static bool ShouldReconnect(
        bool probeScanRunning, FxConnectorState state, bool isWriting,
        DateTimeOffset? lastChannelResultAt, DateTimeOffset? connectedSince, DateTimeOffset? inFlightSince,
        DateTimeOffset now, int noDataTimeoutSeconds, int connectGraceSeconds)
    {
        if (probeScanRunning) return false;   // 扫描期独占工作模式，跳过

        switch (state)
        {
            // 建连中：进行中的 ConnectAsync + provisioning，给宽限不打断；仅疑似卡死(超宽限)才强制重连。
            // inFlightSince 为空表示刚进入该阶段（本轮才观察到），按仍在宽限内处理。
            case FxConnectorState.Connecting:
            case FxConnectorState.Reconnecting:
                return inFlightSince is { } inFlight && now - inFlight > TimeSpan.FromSeconds(connectGraceSeconds);

            // 明确未连接（初始/断开/故障）→ 重连。
            case FxConnectorState.Idle:
            case FxConnectorState.Disconnected:
            case FxConnectorState.Faulted:
                return true;

            case FxConnectorState.Connected:
                break;   // 落到下面的新鲜度判定
        }

        if (isWriting) return false;          // 慢写库中，非连接问题，跳过

        // 只算发生在本次连接【之后】的数据；连接前旧时间戳不算（否则刚重连即被误判陈旧）。
        TimeSpan noDataFor;
        if (connectedSince is { } sinceVal)
        {
            var dataRef = lastChannelResultAt is { } last && last > sinceVal ? last : sinceVal;
            noDataFor = now - dataRef;
        }
        else
        {
            noDataFor = lastChannelResultAt is null ? TimeSpan.MaxValue : now - lastChannelResultAt.Value;
        }
        return noDataFor > TimeSpan.FromSeconds(noDataTimeoutSeconds);
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