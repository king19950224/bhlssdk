using System.Threading.Channels;
using BoltPreload.Fx.Models;
using FxSDK;
using FxSDK.Models;
using Microsoft.Extensions.Logging;
using WorkMode = FxSDK.Models.WorkMode;

namespace BoltPreload.Fx;

/// <summary>
/// 真实 FxSDK（C# 1.4）的适配器。
/// 把 SDK 的 <see cref="FxClient.OnResultReceived"/> 事件转成内部 <see cref="System.Threading.Channels.Channel{T}"/>。
/// Phase 1 第二周开始接入实验室真机 192.168.0.7:23。
/// </summary>
/// <remarks>
/// <para>本类只做"事件 → 通道"的桥接，不做业务计算。业务计算在 Application 层。</para>
/// <para>断线重连由 <c>BoltPreload.Application.Ingestion.IngestionWatchdogService</c> 看门狗驱动，
/// 调用 <see cref="ReconnectAsync"/> 重连并重发工作模式（按方案 §5.2 厂家重连示例落地）。</para>
/// </remarks>
public sealed class FxSdkConnector : IFxConnector
{
    private readonly FxClient _client;
    private readonly DeviceConfig _config;
    private readonly IReadOnlyList<ChannelConfig> _initialChannelConfigs;
    private readonly long _deviceId;
    private readonly ILogger<FxSdkConnector> _logger;
    private readonly Channel<FxDecodedEnvelope> _channel;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private string? _sessionId;
    private bool _disposed;

    // 重连时 Close 与 Connect 之间的间隔：给旧 TCP 会话/socket 完全拆除的时间
    //（厂家重连参考实现 CloseAsync 后用 100ms，这里取稍保守的 200ms）
    private const int ReconnectGapMs = 200;

    // 重标定用：缓存每通道最近一次的 ChannelResult（EchoTime/SpTemp） 和 LEchoWave
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, ChannelResult> _latestChannelResult = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, EchoWave> _latestEchoWave = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, ChannelConfig> _latestChannelConfig = new();

    // Recalibrate 等待 LEchoWave 上行用的事件驱动唤醒（替代 100ms busy-poll）
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, TaskCompletionSource<EchoWave>> _echoWaveWaiters = new();

    public long DeviceId => _deviceId;
    public FxConnectorState State { get; private set; } = FxConnectorState.Idle;
    public ChannelReader<FxDecodedEnvelope> Results => _channel.Reader;

    public FxSdkConnector(
        long deviceId,
        DeviceConfig config,
        ILogger<FxSdkConnector> logger,
        IEnumerable<ChannelConfig>? initialChannelConfigs = null)
    {
        _deviceId = deviceId;
        _config = config;
        _initialChannelConfigs = (initialChannelConfigs ?? Array.Empty<ChannelConfig>()).ToArray();
        _logger = logger;
        _client = new FxClient();
        _channel = System.Threading.Channels.Channel.CreateBounded<FxDecodedEnvelope>(
            new BoundedChannelOptions(capacity: 8192)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true
            });

        // 订阅 SDK 事件
        _client.OnConnected += OnSdkConnectedSync;
        _client.OnDisconnected += OnSdkDisconnected;
        _client.OnResultReceived += OnSdkResultReceived;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (State == FxConnectorState.Connected)
                return;

            State = FxConnectorState.Connecting;
            _logger.LogInformation("FxSdk 连接 device={DeviceId}, params={Params}",
                _deviceId, _config.CommunicationParameters);

            await _client.ConnectAsync(_config, cancellationToken);
        }
        catch (Exception ex)
        {
            State = FxConnectorState.Faulted;
            _logger.LogError(ex, "FxSdk 连接失败 device={DeviceId}", _deviceId);
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_sessionId is null)
            {
                State = FxConnectorState.Disconnected;
                return;
            }

            await _client.CloseAsync(_sessionId);
            _sessionId = null;
            State = FxConnectorState.Disconnected;
            // 不能 TryComplete：IngestionService 正在 ReadAllAsync，重连后还要继续消费同一个通道。
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("FxSdk 准备重连 device={DeviceId}, state={State}", _deviceId, State);
        State = FxConnectorState.Reconnecting;

        try
        {
            await DisconnectAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FxSdk 重连前断开失败 device={DeviceId}，继续尝试连接", _deviceId);
        }

        // 关闭与重连之间留间隔，让旧 TCP 会话/socket 完全拆除再建新连接（对齐厂家重连参考：Close→Delay→Connect）
        try
        {
            await Task.Delay(ReconnectGapMs, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return; // 取消则放弃本次重连
        }

        await ConnectAsync(cancellationToken);
    }

    public Task SetWorkModeAsync(FxWorkMode mode, ushort channel = 0, ushort address = 1, CancellationToken cancellationToken = default)
    {
        if (_sessionId is null)
            throw new InvalidOperationException("Not connected");

        var sdkMode = (WorkMode)(int)mode;
        return _client.SetDeviceModeAsync(_sessionId, sdkMode, channel, address);
    }

    public Task ReadDeviceParamAsync(string propertyName, ushort address = 1, CancellationToken cancellationToken = default)
    {
        if (_sessionId is null)
            throw new InvalidOperationException("Not connected");
        return _client.ReadDeviceParamAsync(_sessionId, propertyName, address);
    }

    public Task ReadChannelParamAsync(string propertyName, ushort channel = 1, ushort address = 1, CancellationToken cancellationToken = default)
    {
        if (_sessionId is null)
            throw new InvalidOperationException("Not connected");
        return _client.ReadChannelParamAsync(_sessionId, propertyName, channel, address);
    }

    public Task ConfigureMultiChannelPollingAsync(
        IReadOnlyCollection<ushort> enabledChannels,
        ushort? singleCount = null,
        ushort? dailyCount = null,
        uint? startSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (_sessionId is null)
            throw new InvalidOperationException("Not connected");
        if (enabledChannels.Count == 0)
            throw new ArgumentException("enabledChannels 不能为空", nameof(enabledChannels));

        var channels = enabledChannels
            .Where(ch => ch > 0)
            .Distinct()
            .OrderBy(ch => ch)
            .ToList();
        if (channels.Count == 0)
            throw new ArgumentException("enabledChannels 必须包含正数通道号", nameof(enabledChannels));

        _config.MCRConfig ??= new MCRConfig();
        _config.MCRConfig.EnabledChannels = channels;
        if (singleCount is not null) _config.MCRConfig.SingleCount = singleCount.Value;
        if (dailyCount is not null) _config.MCRConfig.DailyCount = dailyCount.Value;
        if (startSeconds is not null) _config.MCRConfig.StartSeconds = startSeconds.Value;

        _logger.LogWarning("下发多通道轮询配置 device={DeviceId} enabledChannels={Channels} singleCount={SingleCount} dailyCount={DailyCount}",
            _deviceId, string.Join(",", channels), _config.MCRConfig.SingleCount, _config.MCRConfig.DailyCount);

        return _client.ConfigureDeviceAsync(_sessionId, _config, nameof(_config.MCRConfig), sendCommand: true);
    }

    public IReadOnlyList<FxSessionChannelConfigSnapshot> GetSessionChannelConfigs()
    {
        return _latestChannelConfig.Values
            .OrderBy(c => c.Address)
            .ThenBy(c => c.Channel)
            .Select(c => new FxSessionChannelConfigSnapshot(
                c.Address,
                c.Channel,
                c.ProbeFreq,
                c.Gain,
                c.MeasMode,
                c.Threshold,
                c.RefTime,
                c.RefTemp,
                c.TcMode,
                c.TempCoefficient))
            .ToArray();
    }

    public async Task ConfigureChannelAsync(
        ushort channel,
        ChannelConfigUpdate updates,
        ushort address = 1,
        CancellationToken cancellationToken = default)
    {
        if (_sessionId is null)
            throw new InvalidOperationException("Not connected");

        // ChannelConfig 是 stateful，必须先填好整个对象（address/channel + 要改的字段），
        // 再用 propertyName 标记 SDK 只编码哪一项指令发到设备
        var cfg = new FxSDK.Models.ChannelConfig
        {
            Address = address,
            Channel = channel
        };

        // SDK 协议层（MultiChIotProtocolHandler.EncodeChannel）只支持 5 个 propertyName：
        //     ProbeFreq / EchoWin / EchoWidth / PulseConfig / Gain
        //   以及 RefTime —— 但它实际是「整组标定基线」：一条命令把
        //   MeasMode + Threshold + RefTime + RefTemp + LEchoWave 一起写。
        //   所以单写 RefTemp / Threshold / MeasMode / TcMode / TempCoefficient 都会被 SDK 拒绝。
        //   这里只接受 SDK 真实支持的属性；RefTime 走专门的"重标定"流程（需先读到 LEchoWave）。
        if (updates.ProbeFreqMhz is not null)
        {
            cfg.ProbeFreq = updates.ProbeFreqMhz.Value;
            await _client.ConfigureChannelAsync(_sessionId, cfg, nameof(cfg.ProbeFreq), sendCommand: true);
        }
        if (updates.EchoWinNs is not null)
        {
            cfg.EchoWin = updates.EchoWinNs.Value;
            await _client.ConfigureChannelAsync(_sessionId, cfg, nameof(cfg.EchoWin), sendCommand: true);
        }
        if (updates.EchoWidthUs is not null)
        {
            cfg.EchoWidth = updates.EchoWidthUs.Value;
            await _client.ConfigureChannelAsync(_sessionId, cfg, nameof(cfg.EchoWidth), sendCommand: true);
        }
        if (updates.Gain is not null)
        {
            cfg.Gain = updates.Gain.Value;
            await _client.ConfigureChannelAsync(_sessionId, cfg, nameof(cfg.Gain), sendCommand: true);
        }

        // 这些属性 SDK 协议不允许单独写，直接报错引导调用者用 /recalibrate
        if (updates.RefTimeNs is not null || updates.RefTempC is not null
            || updates.Threshold is not null || updates.MeasMode is not null
            || updates.TcMode is not null || updates.TempCoefficient is not null)
        {
            throw new NotSupportedException(
                "RefTime/RefTemp/Threshold/MeasMode/TcMode/TempCoefficient 必须通过重标定流程（一次性写整组基线）。" +
                "调用 POST /api/devices/{id}/channels/{ch}/recalibrate 即可；或者：" +
                "1) 先 ReadChannelParam(LEchoWave) 拿当前波形；" +
                "2) 然后 SDK 自带 RefTime 命令一次性写 MeasMode+Threshold+RefTime+RefTemp+LEchoWave 整组。");
        }
    }

    // ========== SDK 事件 → 内部 Channel ==========

    // SDK 事件签名是同步 EventHandler。我们包一层 fire-and-forget async：
    //   1) 不在 SDK 事件分发线程里阻塞 .GetAwaiter().GetResult()，避免拖垮整条 SDK 事件链路；
    //   2) 内部异常一律自吞，否则 async void 会顶到 AppDomain 把进程崩了。
    private void OnSdkConnectedSync(object? sender, EventArgs args)
    {
        _ = OnSdkConnectedAsync();
    }

    private async Task OnSdkConnectedAsync()
    {
        _sessionId = _config.GetSessionId();
        _logger.LogInformation("FxSdk 已连接 device={DeviceId}, session={Session}", _deviceId, _sessionId);

        try
        {
            // 必须把 DeviceConfig 存到 SessionContext，否则后续 ReadDeviceParam / ReadChannelParam
            // 走 SDK EncodeReadDeviceParameter 时 GetDeviceConfig 返回 null 导致空指令发不出去
            // 用 sendCommand=false 表示只更新会话状态，不下发指令到设备
            await _client.ConfigureDeviceAsync(_sessionId, _config, propertyName: null, sendCommand: false);
            _logger.LogInformation("已把 DeviceConfig 存到 SessionContext device={DeviceId}", _deviceId);

            // SDK 的 MultiChIotProtocolHandler.ProcessResult 只有在 SessionContext 里存在
            // ChannelConfig 且 ProbeFreq > 0 时，才会根据波形计算 EchoTime。
            // 这里的 sendCommand=false 只写 SDK 本地会话，不改设备参数。
            foreach (var chCfg in _initialChannelConfigs)
            {
                await _client.ConfigureChannelAsync(_sessionId, chCfg, propertyName: null, sendCommand: false);
                _latestChannelConfig[chCfg.Channel] = chCfg;
                _logger.LogInformation(
                    "已把 ChannelConfig 存到 SessionContext device={DeviceId} addr={Addr} ch={Ch} probe={Probe}MHz mode={Mode} threshold={Thr} refTime={RefTime} refTemp={RefTemp}",
                    _deviceId, chCfg.Address, chCfg.Channel, chCfg.ProbeFreq, chCfg.MeasMode,
                    chCfg.Threshold, chCfg.RefTime, chCfg.RefTemp);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "存储 DeviceConfig 到 session 失败 device={DeviceId}", _deviceId);
        }

        // 会话已完整 provision（DeviceConfig + 各 ChannelConfig 均已注入 SessionContext）后，才对外置 Connected。
        // 否则 IngestionService 一见 Connected 就 SetWorkMode(MultiMeas)，可能赶在 ChannelConfig 注入完成前，
        // 导致开机最初若干条测量 EchoTime=null（SDK 需 ChannelConfig.ProbeFreq>0 才计算 EchoTime）。
        State = FxConnectorState.Connected;
    }

    private void OnSdkDisconnected(object? sender, SessionDisconnectedEventArgs args)
    {
        State = FxConnectorState.Disconnected;
        _sessionId = null;
        _logger.LogWarning("FxSdk 断开 device={DeviceId}", _deviceId);
    }

    private void OnSdkResultReceived(object? sender, DecodedResultsReceivedEventArgs args)
    {
        var now = DateTimeOffset.UtcNow;
        // SDK 的事件参数属性叫 Results（不是 Data），见 external/FxSDK/Models/IoSessionEventArgs.cs:33
        foreach (var result in args.Results)
        {
            UpdateRecalibrateCache(result);

            var envelope = ConvertResult(result, now);
            if (envelope is not null && !_channel.Writer.TryWrite(envelope))
            {
                _logger.LogWarning("FxSdk 结果通道已满，丢弃 device={DeviceId}", _deviceId);
            }
        }
    }

    /// <summary>
    /// 把 ChannelResult / DeviceParameter(LEchoWave) 缓存起来，用于 Recalibrate 时取最新基线。
    /// 注意：不要存太多，只保最近一次（按通道）。
    /// 同时唤醒任何 RecalibrateChannelAsync 中正在等的 TCS。
    /// </summary>
    private void UpdateRecalibrateCache(DecodedResult result)
    {
        switch (result)
        {
            case ChannelResult cr:
                // 只缓存有 EchoTime 的（设备真算出 TOF 的那一次），方便 Recalibrate 抓基线
                if (cr.EchoTime is not null && cr.SpTemp is not null && cr.StatusCode >= 0)
                {
                    _latestChannelResult[cr.Channel] = cr;
                }
                if (cr.EchoWave is not null)
                {
                    _latestEchoWave[cr.Channel] = cr.EchoWave;
                    NotifyEchoWaveArrived(cr.Channel, cr.EchoWave);
                }
                break;

            case MultiChannelResult mcr:
                foreach (var (chNo, sub) in mcr.Results)
                {
                    if (sub.EchoTime is not null && sub.SpTemp is not null && sub.StatusCode >= 0)
                    {
                        _latestChannelResult[chNo] = sub;
                    }
                    if (sub.EchoWave is not null)
                    {
                        _latestEchoWave[chNo] = sub.EchoWave;
                        NotifyEchoWaveArrived(chNo, sub.EchoWave);
                    }
                }
                break;

            case DeviceParameter dp when dp.Channel.HasValue && dp.ParamValue is EchoWave ew:
                // ReadChannelParam(LEchoWave) 上报：把通道当前波形落到缓存
                _latestEchoWave[dp.Channel.Value] = ew;
                NotifyEchoWaveArrived(dp.Channel.Value, ew);
                break;

            case DeviceParameter dp when dp.Channel.HasValue && dp.ParamValue is ChannelConfig chCfg:
                _latestChannelConfig[dp.Channel.Value] = chCfg;
                break;
        }
    }

    private void NotifyEchoWaveArrived(ushort channel, EchoWave ew)
    {
        if (_echoWaveWaiters.TryRemove(channel, out var tcs))
        {
            tcs.TrySetResult(ew);
        }
    }

    public async Task<RecalibrateResult> RecalibrateChannelAsync(
        ushort channel,
        ushort address = 1,
        decimal? explicitRefTimeNs = null,
        decimal? explicitRefTempC = null,
        CancellationToken cancellationToken = default)
    {
        if (_sessionId is null)
            throw new InvalidOperationException("Not connected");

        // 1. 决定 RefTime / RefTemp 的来源：
        //    - 显式参数优先（运维强制指定，比如设备当前测不出 TOF 时）
        //    - 否则取最近一次有效 ChannelResult（推荐路径）
        decimal refTime, refTemp;
        if (explicitRefTimeNs.HasValue && explicitRefTempC.HasValue)
        {
            refTime = explicitRefTimeNs.Value;
            refTemp = explicitRefTempC.Value;
            _logger.LogWarning("重标定使用显式参数 ch={Ch} RefTime={Rt} RefTemp={Rtemp}", channel, refTime, refTemp);
        }
        else if (_latestChannelResult.TryGetValue(channel, out var latestCr))
        {
            refTime = latestCr.EchoTime!.Value;
            refTemp = latestCr.SpTemp!.Value;
            // 显式参数若只传了其中之一，覆盖对应字段
            if (explicitRefTimeNs.HasValue) refTime = explicitRefTimeNs.Value;
            if (explicitRefTempC.HasValue) refTemp = explicitRefTempC.Value;
        }
        else
        {
            throw new InvalidOperationException(
                $"通道 {channel} 没有最近的有效测量（EchoTime+SpTemp），且未提供显式 RefTime/RefTemp。" +
                "请确认设备处于 MultiMeas 模式且能稳定测出 EchoTime；" +
                "或在请求体里提供 explicitRefTimeNs/explicitRefTempC 强制写入。");
        }

        // 2. 拿 LEchoWave：如果缓存没有，触发 ReadChannelParam 并 TCS 等待（事件驱动，不再 100ms busy-poll）
        if (!_latestEchoWave.TryGetValue(channel, out var latestWave))
        {
            _logger.LogInformation("通道 {Ch} 缺 LEchoWave 缓存，触发上行读取", channel);

            // 先注册等待器，再发请求 — 避免"请求-响应"之间的 race
            var tcs = new TaskCompletionSource<EchoWave>(TaskCreationOptions.RunContinuationsAsynchronously);
            _echoWaveWaiters[channel] = tcs;

            try
            {
                await _client.ReadChannelParamAsync(_sessionId, "LEchoWave", channel, address);

                // 二次检查：发请求过程中如果数据已到，直接用
                if (_latestEchoWave.TryGetValue(channel, out latestWave))
                {
                    _echoWaveWaiters.TryRemove(channel, out _);
                }
                else
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        latestWave = await tcs.Task.WaitAsync(timeoutCts.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"通道 {channel}: 等待 LEchoWave 上行超时（5s）。检查设备是否响应 ReadChannelParam。");
                    }
                }
            }
            finally
            {
                _echoWaveWaiters.TryRemove(channel, out _);
            }
        }

        // 3. 取该通道当前已知的 MeasMode / Threshold。
        //    propertyName="RefTime" 是【整组写入】——SDK 会把 MeasMode+Threshold+RefTime+RefTemp+LEchoWave
        //       一条命令打包下发（见 external/FxSDK/Codec/MultiChIotProtocolHandler.cs:269-294）。
        //    所以 MeasMode/Threshold 必须用设备真实值，绝不能用跨设备硬编码的猜测值，
        //    否则重标定会连带改掉该通道的 TOF 提取算法（MeasMode：0=自动数峰法/1=动态特征/2=阈值法）和阈值。
        //    缺这两项时直接拒绝，引导先读回配置，而不是冒险写入。
        if (!_latestChannelConfig.TryGetValue(channel, out var existingCfg))
        {
            throw new InvalidOperationException(
                $"通道 {channel} 尚无已知 ChannelConfig（MeasMode/Threshold），无法安全重标定。" +
                "RefTime 命令会整组写入 MeasMode+Threshold+RefTime+RefTemp+LEchoWave，缺 MeasMode/Threshold 会用猜测值覆盖设备实际配置。" +
                "请先 ReadChannelParam 读回该通道配置（或在启动初始通道配置中提供该通道），再重标定。");
        }

        // 4. 构造 ChannelConfig（一次性整组写）：RefTime/RefTemp/LEchoWave 为新基线，
        //    MeasMode/Threshold 沿用设备现值（来自启动初始配置或最近一次 ReadChannelParam）。
        var cfg = new ChannelConfig
        {
            Address = address,
            Channel = channel,
            RefTime = refTime,
            RefTemp = refTemp,
            LEchoWave = latestWave,
            MeasMode = existingCfg.MeasMode,
            Threshold = existingCfg.Threshold,
        };

        _logger.LogInformation(
            "重标定通道 {Ch}: RefTime={Rt}ns RefTemp={Rtemp}°C LEchoWave.pts={Pts}",
            channel, cfg.RefTime, cfg.RefTemp, latestWave.WaveData?.Count ?? 0);

        await _client.ConfigureChannelAsync(_sessionId, cfg, nameof(cfg.RefTime), sendCommand: true);

        return new RecalibrateResult(
            Channel: channel,
            Address: address,
            NewRefTimeNs: refTime,
            NewRefTempC: refTemp,
            EchoWaveBytes: latestWave.WaveData?.Count ?? 0,
            Timestamp: DateTimeOffset.UtcNow);
    }

    private FxDecodedEnvelope? ConvertResult(DecodedResult result, DateTimeOffset now)
    {
        // SDK 的 ReceiveTime 是 DateTime。Kind 可能是 Local/Utc/Unspecified，需要安全转成 UTC DateTimeOffset
        // 原因：
        //  - Npgsql 8 写 PostgreSQL timestamptz 要求 Offset=0（UTC），传 +08:00 会报 ArgumentException
        //  - new DateTimeOffset(rt, TimeSpan.Zero) 在 rt.Kind=Local 时也会抛 ArgumentException
        var rt = result.ReceiveTime;
        DateTimeOffset receiveTime;
        if (rt == default)
        {
            receiveTime = now;  // now 已经是 UtcNow
        }
        else if (rt.Kind == DateTimeKind.Utc)
        {
            receiveTime = new DateTimeOffset(rt, TimeSpan.Zero);
        }
        else if (rt.Kind == DateTimeKind.Local)
        {
            // 本地时区 → UTC（统一存库口径）
            receiveTime = new DateTimeOffset(rt).ToUniversalTime();
        }
        else
        {
            // Unspecified：SDK 内部没标 Kind，按"设备本地时间"处理，转 UTC
            receiveTime = new DateTimeOffset(DateTime.SpecifyKind(rt, DateTimeKind.Local)).ToUniversalTime();
        }

        return result switch
        {
            ChannelResult cr => new FxDecodedEnvelope(
                DeviceId: _deviceId,
                ReceiveTime: receiveTime,
                IngestedAt: now,
                ResultType: FxResultType.ChannelResult,
                ChannelResult: ToDto(cr)),

            MultiChannelResult mcr => new FxDecodedEnvelope(
                DeviceId: _deviceId,
                ReceiveTime: receiveTime,
                IngestedAt: now,
                ResultType: FxResultType.MultiChannelResult,
                MultiChannelResult: ToDto(mcr)),

            DeviceParameter dp => new FxDecodedEnvelope(
                DeviceId: _deviceId,
                ReceiveTime: receiveTime,
                IngestedAt: now,
                ResultType: FxResultType.DeviceParameter,
                DeviceParameter: ToDto(dp)),

            _ => null
        };
    }

    private static FxDeviceParameterDto ToDto(DeviceParameter dp)
    {
        return new FxDeviceParameterDto(
            Address: dp.Address,
            Channel: dp.Channel,
            ParamName: dp.ParamName,
            ParamValue: dp.ParamValue);
    }

    private static FxChannelResultDto ToDto(ChannelResult cr)
    {
        return new FxChannelResultDto(
            Address: cr.Address,
            Channel: cr.Channel,
            StatusCode: cr.StatusCode,
            EchoTimeNs: cr.EchoTime,
            EchoTime2Ns: cr.EchoTime2,
            SpTempC: cr.SpTemp,
            DeltaTofNs: cr.DeltaTof,
            Gain: 0f,  // SDK 1.4 的 ChannelResult 没有 Gain 字段（在 ChannelConfig），Phase 2 从配置带过来
            EchoWave: cr.EchoWave is null ? null : ToDto(cr.EchoWave),
            LowAi: cr.LowAi,
            HighAi: cr.HighAi,
            Angle: cr.Angle,
            MidResults: cr.MidResults);
    }

    private static FxEchoWaveDto ToDto(EchoWave ew)
    {
        return new FxEchoWaveDto(
            Fc: ew.Fc,
            EchoStart: ew.EchoStart,
            ClockInt: ew.ClockInt,
            WaveData: ew.WaveData?.ToArray() ?? Array.Empty<short>(),
            Threshold: ew.Thr);
    }

    private static FxMultiChannelResultDto ToDto(MultiChannelResult mcr)
    {
        var dict = mcr.Results.ToDictionary(kv => kv.Key, kv => ToDto(kv.Value));
        return new FxMultiChannelResultDto(dict);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try { await DisconnectAsync(); } catch { /* swallow on dispose */ }
        // FxClient 没实现 IDisposable，用 StopAllAsync 做清理（停所有监听器 + 关所有会话）
        try { await _client.StopAllAsync(); } catch { /* swallow on dispose */ }
        _channel.Writer.TryComplete();
        _connectLock.Dispose();
    }
}
