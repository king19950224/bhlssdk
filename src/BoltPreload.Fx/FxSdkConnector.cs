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
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private volatile string? _sessionId;
    // 会话代次：每次(重)连的 provisioning 进锁后记录一个代次，断开事件(OnSdkDisconnected/DisconnectAsync)自增它。
    // provisioning 在置 Connected 前校验代次未变，杜绝"断开与 provisioning 交叉"把过期会话错误标成 Connected 的竞态。
    private long _sessionGeneration;
    private bool _disposed;

    // H3：背压丢弃计数 + 告警节流时间戳
    private long _dropped;
    private long _lastOverflowWarnTicks;

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
    private int _state = (int)FxConnectorState.Idle;
    // State 跨线程访问（SDK 事件线程写断开、看门狗/命令线程读写连接）：用 volatile backing field 保证可见性。
    public FxConnectorState State
    {
        get => (FxConnectorState)Volatile.Read(ref _state);
        private set => Volatile.Write(ref _state, (int)value);
    }
    public ChannelReader<FxDecodedEnvelope> Results => _channel.Reader;
    public long DroppedCount => Interlocked.Read(ref _dropped);
    public Func<FxDecodedEnvelope, ValueTask>? OverflowSink { get; set; }

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
        // FullMode=Wait：让 TryWrite 满时返回 false，从而能精确统计背压丢弃并在丢弃前落 WAL 兜底（H3）。
        // 实际不会阻塞——写入端只用非阻塞 TryWrite（见 BackpressureWriter），满时手动丢最旧腾位。
        _channel = System.Threading.Channels.Channel.CreateBounded<FxDecodedEnvelope>(
            new BoundedChannelOptions(capacity: 8192)
            {
                FullMode = BoundedChannelFullMode.Wait,
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
        await _sessionLock.WaitAsync(cancellationToken);
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
            _sessionLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (_sessionId is null)
            {
                State = FxConnectorState.Disconnected;
                return;
            }

            await _client.CloseAsync(_sessionId);
            // 自增代次：与断开事件一致，使任何进行中的 provisioning 结束时不再把过期会话标 Connected。
            Interlocked.Increment(ref _sessionGeneration);
            _sessionId = null;
            State = FxConnectorState.Disconnected;
            // 不能 TryComplete：IngestionService 正在 ReadAllAsync，重连后还要继续消费同一个通道。
        }
        finally
        {
            _sessionLock.Release();
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
            // 取消则放弃本次重连。DisconnectAsync 已把 State 置 Disconnected；此处兜底确保不残留 Reconnecting
            // （若上面的断开因异常未走到置位分支），避免运维/诊断长期看到 Reconnecting 假象。
            if (State == FxConnectorState.Reconnecting)
                State = FxConnectorState.Disconnected;
            return;
        }

        await ConnectAsync(cancellationToken);
    }

    // 串行化所有"会话级"操作（连接/断开 + 下发命令）：避免命令与 ReconnectAsync/DisconnectAsync 并发使用 _sessionId，
    // 否则命令可能在会话被置空/换新的瞬间读到过期 session（看门狗重连 + 波形采样 + 通道扫描会在同一会话上交错）。
    // Connect/Disconnect 已用同一把 _sessionLock，命令方法只调用 _client.*（不回调本类公开方法），故不会重入死锁。
    private async Task RunUnderSessionLockAsync(Func<string, Task> action, CancellationToken ct)
    {
        await _sessionLock.WaitAsync(ct);
        try
        {
            var session = _sessionId ?? throw new InvalidOperationException("Not connected");
            await action(session);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public Task SetWorkModeAsync(FxWorkMode mode, ushort channel = 0, ushort address = 1, CancellationToken cancellationToken = default)
        => RunUnderSessionLockAsync(session =>
        {
            var sdkMode = (WorkMode)(int)mode;
            return _client.SetDeviceModeAsync(session, sdkMode, channel, address);
        }, cancellationToken);

    public Task ReadDeviceParamAsync(string propertyName, ushort address = 1, CancellationToken cancellationToken = default)
        => RunUnderSessionLockAsync(session => _client.ReadDeviceParamAsync(session, propertyName, address), cancellationToken);

    public Task ReadChannelParamAsync(string propertyName, ushort channel = 1, ushort address = 1, CancellationToken cancellationToken = default)
        => RunUnderSessionLockAsync(session => _client.ReadChannelParamAsync(session, propertyName, channel, address), cancellationToken);

    public Task ConfigureMultiChannelPollingAsync(
        IReadOnlyCollection<ushort> enabledChannels,
        ushort? singleCount = null,
        ushort? dailyCount = null,
        uint? startSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (enabledChannels.Count == 0)
            throw new ArgumentException("enabledChannels 不能为空", nameof(enabledChannels));

        var channels = enabledChannels
            .Where(ch => ch > 0)
            .Distinct()
            .OrderBy(ch => ch)
            .ToList();
        if (channels.Count == 0)
            throw new ArgumentException("enabledChannels 必须包含正数通道号", nameof(enabledChannels));

        return RunUnderSessionLockAsync(session =>
        {
            // _config.MCRConfig 是共享可变状态：放在会话锁内改，避免与并发下发命令交错读到半更新的配置
            _config.MCRConfig ??= new MCRConfig();
            _config.MCRConfig.EnabledChannels = channels;
            if (singleCount is not null) _config.MCRConfig.SingleCount = singleCount.Value;
            if (dailyCount is not null) _config.MCRConfig.DailyCount = dailyCount.Value;
            if (startSeconds is not null) _config.MCRConfig.StartSeconds = startSeconds.Value;

            _logger.LogWarning("下发多通道轮询配置 device={DeviceId} enabledChannels={Channels} singleCount={SingleCount} dailyCount={DailyCount}",
                _deviceId, string.Join(",", channels), _config.MCRConfig.SingleCount, _config.MCRConfig.DailyCount);

            return _client.ConfigureDeviceAsync(session, _config, nameof(_config.MCRConfig), sendCommand: true);
        }, cancellationToken);
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

    // 当前设备端 MCR 轮询启用的通道集（读 DeviceConfig.MCRConfig 的实时会话状态）。
    // ProbeScan 扫描前快照它作为"业务轮询集"，扫描后恢复，避免 mcr_single 逐通道改写后只剩最后一个通道。
    public IReadOnlyList<ushort> GetSessionMcrChannels()
        => _config.MCRConfig?.EnabledChannels is { Count: > 0 } ch ? ch.ToArray() : Array.Empty<ushort>();

    public Task ConfigureChannelAsync(
        ushort channel,
        ChannelConfigUpdate updates,
        ushort address = 1,
        CancellationToken cancellationToken = default)
        => RunUnderSessionLockAsync(async session =>
        {
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
                await _client.ConfigureChannelAsync(session, cfg, nameof(cfg.ProbeFreq), sendCommand: true);
            }
            if (updates.EchoWinNs is not null)
            {
                cfg.EchoWin = updates.EchoWinNs.Value;
                await _client.ConfigureChannelAsync(session, cfg, nameof(cfg.EchoWin), sendCommand: true);
            }
            if (updates.EchoWidthUs is not null)
            {
                cfg.EchoWidth = updates.EchoWidthUs.Value;
                await _client.ConfigureChannelAsync(session, cfg, nameof(cfg.EchoWidth), sendCommand: true);
            }
            if (updates.Gain is not null)
            {
                cfg.Gain = updates.Gain.Value;
                await _client.ConfigureChannelAsync(session, cfg, nameof(cfg.Gain), sendCommand: true);
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
        }, cancellationToken);

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
        // provision（写 SessionContext）本身也是"会话级"操作，必须与命令/重连共用 _sessionLock 串行化：
        // 否则 _sessionId 赋值 + Configure*(sessionId) 会与 RunUnderSessionLockAsync 的命令在同一 session 上交错。
        // 本方法由 OnSdkConnectedSync 以 fire-and-forget 在线程池执行，且只调用 _client.*（不回调本类公开方法），故不会重入死锁。
        await _sessionLock.WaitAsync();
        try
        {
            if (_disposed) return;
            // 记录本次 provisioning 的会话代次；断开事件会自增它，末尾据此判断期间是否已断开。
            var myGeneration = Interlocked.Increment(ref _sessionGeneration);
            // 会话 id 读入局部变量全程使用，避免中途被断开事件清空导致 NRE / 用到过期字段。
            var session = _config.GetSessionId();
            _sessionId = session;
            _logger.LogInformation("FxSdk 已连接 device={DeviceId}, session={Session}", _deviceId, session);

            // 必须把 DeviceConfig 存到 SessionContext，否则后续 ReadDeviceParam / ReadChannelParam
            // 走 SDK EncodeReadDeviceParameter 时 GetDeviceConfig 返回 null 导致空指令发不出去
            // 用 sendCommand=false 表示只更新会话状态，不下发指令到设备
            await _client.ConfigureDeviceAsync(session, _config, propertyName: null, sendCommand: false);
            _logger.LogInformation("已把 DeviceConfig 存到 SessionContext device={DeviceId}", _deviceId);

            // SDK 的 MultiChIotProtocolHandler.ProcessResult 只有在 SessionContext 里存在
            // ChannelConfig 且 ProbeFreq > 0 时，才会根据波形计算 EchoTime。
            // 这里的 sendCommand=false 只写 SDK 本地会话，不改设备参数。
            foreach (var chCfg in _initialChannelConfigs)
            {
                await _client.ConfigureChannelAsync(session, chCfg, propertyName: null, sendCommand: false);
                _latestChannelConfig[chCfg.Channel] = chCfg;
                _logger.LogInformation(
                    "已把 ChannelConfig 存到 SessionContext device={DeviceId} addr={Addr} ch={Ch} probe={Probe}MHz mode={Mode} threshold={Thr} refTime={RefTime} refTemp={RefTemp}",
                    _deviceId, chCfg.Address, chCfg.Channel, chCfg.ProbeFreq, chCfg.MeasMode,
                    chCfg.Threshold, chCfg.RefTime, chCfg.RefTemp);
            }

            // 代次守卫：provisioning 期间若已发生断开（DisconnectAsync/OnSdkDisconnected 自增了代次），
            // 不得把这个已过期的会话标成 Connected，否则 IngestionService 会对着已断链路发 MultiMeas 采空数据。
            if (Volatile.Read(ref _sessionGeneration) != myGeneration)
            {
                _logger.LogWarning("provisioning 期间设备已断开，放弃置 Connected device={DeviceId}", _deviceId);
                return;
            }

            // 会话已完整 provision（DeviceConfig + 各 ChannelConfig 均已注入 SessionContext）后，才对外置 Connected。
            // 否则 IngestionService 一见 Connected 就 SetWorkMode(MultiMeas)，可能赶在 ChannelConfig 注入完成前，
            // 导致开机最初若干条测量 EchoTime=null（SDK 需 ChannelConfig.ProbeFreq>0 才计算 EchoTime）。
            State = FxConnectorState.Connected;
        }
        catch (Exception ex)
        {
            // provisioning 关键步骤（ConfigureDevice/Channel）失败：绝不能置 Connected 让上层采空数据。
            // 置 Faulted 并清会话，交由看门狗按"未连接"重连、重跑 provisioning（旧实现吞异常仍置 Connected → EchoTime 长期 null）。
            Interlocked.Increment(ref _sessionGeneration);
            _sessionId = null;
            State = FxConnectorState.Faulted;
            _logger.LogError(ex, "provisioning 失败，置 Faulted 待看门狗重连 device={DeviceId}", _deviceId);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private void OnSdkDisconnected(object? sender, SessionDisconnectedEventArgs args)
    {
        // 不持锁、立即生效：自增代次让任何进行中的 provisioning 结束时不再置 Connected；
        // 抢锁反而会被正在跑的 provisioning 阻塞，使断开状态延迟反映、看门狗迟迟不重连。
        // _sessionId / State 均为 volatile，跨线程写对读方立即可见。
        Interlocked.Increment(ref _sessionGeneration);
        _sessionId = null;
        State = FxConnectorState.Disconnected;
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
            if (envelope is null) continue;

            // H3：背压可观测 + 丢弃前落 WAL 兜底（旧实现 DropOldest 静默丢数，"通道已满"日志是死代码）。
            var dropped = BackpressureWriter.WriteWithBackpressure(_channel, envelope, ref _dropped, OverflowSink);
            if (dropped > 0) WarnOverflowThrottled(dropped);
        }
    }

    /// <summary>背压丢弃告警（节流：每设备每秒最多一条，避免高频刷屏）。</summary>
    private void WarnOverflowThrottled(int droppedThisBatch)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastOverflowWarnTicks);
        if (now - last < TimeSpan.TicksPerSecond) return;
        if (Interlocked.CompareExchange(ref _lastOverflowWarnTicks, now, last) != last) return;
        _logger.LogWarning(
            "FxSdk 结果通道背压丢弃（消费跟不上，DB 慢?）device={DeviceId} 本批丢弃={Dropped} 累计丢弃={Total} WAL兜底={Wal}",
            _deviceId, droppedThisBatch, DroppedCount, OverflowSink is not null);
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
        // 整个重标定序列（ReadChannelParam LEchoWave → 整组写 RefTime）在会话锁内串行执行，
        // 避免与波形采样 ReadChannelParam / 看门狗重连并发使用同一 session。
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
        var session = _sessionId ?? throw new InvalidOperationException("Not connected");

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
                await _client.ReadChannelParamAsync(session, "LEchoWave", channel, address);

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

        await _client.ConfigureChannelAsync(session, cfg, nameof(cfg.RefTime), sendCommand: true);

        return new RecalibrateResult(
            Channel: channel,
            Address: address,
            NewRefTimeNs: refTime,
            NewRefTempC: refTemp,
            EchoWaveBytes: latestWave.WaveData?.Count ?? 0,
            Timestamp: DateTimeOffset.UtcNow);
        }
        finally
        {
            _sessionLock.Release();
        }
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
        // ReadChannelParam("LEchoWave") 响应里 ParamValue 是 SDK 的 EchoWave —— 必须解耦成 FxEchoWaveDto，
        // 否则 PostgresMeasurementSink 的 `case FxEchoWaveDto` 永不命中，主动采集的波形会被当成大 JSON
        // 塞进 device_parameter_history，而不是落 WaveStore + wave_features（波形诊断数据源）。
        var value = dp.ParamValue is EchoWave ew ? ToDto(ew) : dp.ParamValue;
        return new FxDeviceParameterDto(
            Address: dp.Address,
            Channel: dp.Channel,
            ParamName: dp.ParamName,
            ParamValue: value);
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
        _sessionLock.Dispose();
    }
}
