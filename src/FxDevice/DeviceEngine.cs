using FxSDK;
using FxSDK.Codec;
using FxSDK.Models;
using FxSDK.Utility;

namespace FxDevice;

public sealed class DeviceOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public ushort Address { get; set; } = 1;
    public int Gap { get; set; } = 400;
    public string Resume { get; set; } = "standby";
    /// <summary>0 不温补 / 1 材料系数 / 2 自定义系数。和厂家 iFastApp、SDK ChannelConfig.TcMode 同一套。</summary>
    public ushort TcMode { get; set; } = 1;
    /// <summary>温补系数；空则用 SDK 默认碳钢 0.00010269。厂家项目里常见 0.000101。</summary>
    public decimal? TempCoefficient { get; set; }

    /// <summary>
    /// 温度平滑档位（SDK 一阶 Tau：Direct 0 s / Fast 60 s / Balanced 180 s / Stable 480 s / Conservative 900 s）。
    /// 厂家软件默认 Fast；SDK 自己的缺省是 Balanced。开着温补时两边温度不一样，声时差就差零点几纳秒。进程级全局。
    /// </summary>
    public TempTauProfile TempTau { get; set; } = TempTauProfile.Fast;

    /// <summary>温度借用：键是通道，值是"用哪一路的温度"。这一路 Pt1000 坏了或探头没温感时用。空 = 各用各的。</summary>
    public IReadOnlyDictionary<ushort, ushort> TempFrom { get; set; } = new Dictionary<ushort, ushort>();

    /// <summary>自动法置零锚点（refEnvNs / refPeriodNs）存盘；空 = 不持久化，SDK 每次会话自己重新捕获。</summary>
    public AnchorStore? Anchors { get; set; }
}

public enum Verdict
{
    Unknown,
    StoredOk,
    Ignored,
    Rewritten,
    NoReadback,
    NotSent,
}

public sealed class ParamRow
{
    public ushort Channel { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Cn { get; set; } = string.Empty;
    public ParamGroup Group { get; set; }
    public ParamKind Kind { get; set; }
    public string DeviceValue { get; set; } = "-";
    public string BeforeValue { get; set; } = "-";
    public string Wrote { get; set; } = "-";
    public string ReadBack { get; set; } = "-";

    public Verdict Verdict { get; set; } = Verdict.Unknown;
    public string Detail { get; set; } = string.Empty;
    public bool IsChannelParam { get; set; }

    /// <summary>
    /// 同一帧里被连带重写的参数（写增益时 SDK 顺带写回波窗、全配置帧写整组寄存器、存储区整块写）。
    /// 预期值等于原值，读回不一致才提示。
    /// </summary>
    public bool Implicit { get; set; }
}

public sealed class DeviceEngine : IDisposable
{
    private readonly FxClient _client = new();
    private readonly Dictionary<ushort, Dictionary<string, string>> _got = new();
    private readonly Dictionary<ushort, EchoWave> _waves = new();

    private static readonly string[] ChannelReads =
    {
        nameof(ChannelConfig.ProbeFreq), nameof(ChannelConfig.EchoWin), nameof(ChannelConfig.EchoWidth),
        nameof(ChannelConfig.PulseConfig), nameof(ChannelConfig.Gain), nameof(ChannelConfig.RefTime),
    };

    private static readonly string[] StorageParams = { "MeasMode", "Threshold", "RefTime", "RefTemp" };
    private static readonly string[] RegisterBatchOrder = { "ProbeFreq", "EchoWin", "EchoWidth", "Gain", "Duty", "Forward", "Reverse" };
    private static readonly string[] PulseParams = { "Duty", "Forward", "Reverse" };

    // 寄存器应答只有几个字节，600 ms 没到基本就是丢了；存储区回包带整条参考波形，要等更久
    private const int RegisterReplyTimeoutMs = 600;
    private const int StorageReplyTimeoutMs = 1500;
    // 写命令等设备应答的上限；超时就把老的定长等待补足，总时长不比以前长
    private const int RegisterAckTimeoutMs = 400;
    private const int StorageAckTimeoutMs = 1000;
    private const int AckSettleMs = 50;

    private long _acks;
    private volatile bool _acksSeen;
    private int _batchReadMisses;
    private volatile bool _batchReadOk = true;
    private long _rxResults;

    /// <summary>
    /// 本次连接以来解出的回包条数（参数应答、写应答、测量帧都算）。宿主拿它判断"连上就被断"是哪一种：
    /// 一条都没有 = 握手通了但设备从没说过话（到设备的网络路径不通、被代理 TUN 接管等），有过回包 = 链路中途断的。
    /// </summary>
    public long ReceivedResults => Interlocked.Read(ref _rxResults);

    public event Action<string>? OnLog;

    /// <summary>
    /// 链路断开（对端关闭、网络中断、心跳超时等），参数是可读的原因。
    /// OnLog 是调试流水，这个事件宿主应当记进日志。
    /// </summary>
    public event Action<string>? OnDisconnected;

    public bool IsConnected { get; private set; }

    public string? LastDisconnect { get; private set; }

    /// <summary>最近一次断开的原因（不带时间前缀），给宿主拼自己的日志用。</summary>
    public string? LastDisconnectReason { get; private set; }

    private DeviceOptions _opt = new();
    private DeviceConfig _device = new();
    private string _sessionId = string.Empty;
    private readonly SemaphoreSlim _io = new(1, 1);
    private int _ioDisposed;

    /// <summary>扫波结束后回波窗有没有写回原值。空 = 这次没放大窗口或写回成功；有字 = 设备上可能还留着大窗口。</summary>
    public string? EchoWinRestoreNote { get; private set; }

    /// <summary>桌面端刚存了锚点后调用：下次灌会话用磁盘上的新文件，不要继续用连接时读进内存的那份。</summary>
    public void ReloadAnchors(string? baseDir = null)
    {
        _opt.Anchors = AnchorStore.Open(baseDir ?? AppContext.BaseDirectory, _opt.Endpoint);
    }

    public DeviceEngine()
    {
        FxSDK.Utility.FxUtility.DeveloperMode = true;

        _client.OnConnected += (_, _) =>
        {
            IsConnected = true;
            Interlocked.Exchange(ref _rxResults, 0);
            Log("已连接");
        };
        _client.OnDisconnected += (_, e) =>
        {
            IsConnected = false;
            var reason = DescribeClose(e);
            LastDisconnect = $"{DateTime.Now:HH:mm:ss} {reason}";
            LastDisconnectReason = reason;
            Log($"连接断开：{reason}");
            OnDisconnected?.Invoke(reason);
        };
        _client.OnResultReceived += (_, e) =>
        {
            foreach (var result in e.Results)
            {
                Interlocked.Increment(ref _rxResults);
                if (result is ChannelResult m)
                {
                    // 全波扫描的结果只给"找回波窗"用，不是测量帧
                    if (m.EchoWaveKind == EchoWaveKind.LongitudinalFull)
                    {
                        _fullWaveSink?.Invoke(m);
                        continue;
                    }

                    if (_scanning) lock (_scan) AbsorbScan(m);
                    if (_streaming) OnMeasurement?.Invoke(m);
                    continue;
                }

                var p = result as DeviceParameter;
                if (p == null) continue;
                if (p.ParamName == MultiChIotProtocolHandler.WriteAckParam)
                {
                    Interlocked.Increment(ref _acks);
                    _acksSeen = true;
                    continue;
                }

                if (p.Channel != null && p.Channel != 0)
                {
                    lock (_got) AbsorbChannel(p.Channel.Value, p);
                }
            }
        };
    }

    private readonly HashSet<ushort> _sessionChannels = new();

    /// <summary>SDK 的关闭原因翻成现场看得懂的话。对端关闭最常见的两个原因是串口服务器断电/重启和另一个客户端连了进来。</summary>
    private static string DescribeClose(SessionDisconnectedEventArgs e)
    {
        var why = e.Reason switch
        {
            SessionCloseReason.Normal => "本端主动关闭",
            SessionCloseReason.RemoteClosed => "对端关闭了连接（串口服务器/设备断开或重启，或另一个客户端连了进来）",
            SessionCloseReason.ConnectionLost => "网络中断（连接被重置或超时）",
            SessionCloseReason.HeartbeatTimeout => "心跳超时（链路半开，设备长时间没有回应）",
            SessionCloseReason.ProtocolError => "协议错误（收到无法解析的数据）",
            _ => e.Reason.ToString(),
        };
        return e.Exception is { } ex ? $"{why}：{ex.Message}" : why;
    }

    public async Task OpenAsync(DeviceOptions opt, CancellationToken ct)
    {
        _opt = opt;
        _device = BuildDevice(opt);
        // SDK 的温度平滑是进程级全局：按 farm.json 的档位设，和厂家软件（默认 Fast 60 s）对齐
        FxUtility.TempTauProfile = opt.TempTau;
        if (opt.Anchors is { Count: > 0 } anchors) Log($"读到 {anchors.Count} 路自动法锚点（{anchors.Path}）");
        await _client.ConnectAsync(_device, ct).ConfigureAwait(false);
        _sessionId = _client.GetSessionId(_device);

        await _client.ConfigureDeviceAsync(_sessionId, _device, null, false).ConfigureAwait(false);

        await _client.SetDeviceModeAsync(_sessionId, WorkMode.Standby, 0, opt.Address).ConfigureAwait(false);
        await Task.Delay(800, ct).ConfigureAwait(false);
    }

    /// <summary>会话中改温补：显示立刻按新公式算；下次组通道配置也带上。</summary>
    public void SetTempComp(ushort tcMode, decimal? tempCoefficient)
    {
        _opt.TcMode = tcMode;
        _opt.TempCoefficient = tempCoefficient;
    }

    /// <summary>会话中改温度平滑档位：SDK 每帧都按当前档位算，立刻生效。</summary>
    public void SetTempTau(TempTauProfile profile)
    {
        _opt.TempTau = profile;
        FxUtility.TempTauProfile = profile;
    }

    /// <summary>会话中改温度借用表：已建会话的通道立刻按新表重配（不发命令，只改 SDK 会话里的通道配置）。</summary>
    public async Task SetTempFromAsync(IReadOnlyDictionary<ushort, ushort> tempFrom)
    {
        _opt.TempFrom = tempFrom;
        foreach (var ch in _sessionChannels.ToArray())
        {
            if (Snapshot(ch).Count > 0) await ApplySessionConfigAsync(ch).ConfigureAwait(false);
        }
    }

    private Action<ChannelResult>? _fullWaveSink;

    /// <summary>
    /// 全波扫描（找回波窗）：让设备把这一路从 0 到 rangeNs 的整段波形分段发上来，SDK 拼成一条。
    /// 现在的回波窗比 rangeNs 小时先临时把回波窗写到 rangeNs，扫完写回原值并读回核对。
    /// 调用方要先停流、结束后自己恢复监测。设备不支持全波模式就超时返回空。
    /// </summary>
    public async Task<EchoWave?> FullWaveAsync(ushort ch, uint rangeNs, TimeSpan timeout, CancellationToken ct)
    {
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        EchoWinRestoreNote = null;
        _streaming = false;
        await _client.SetDeviceModeAsync(_sessionId, WorkMode.Standby, 0, _opt.Address).ConfigureAwait(false);
        await Task.Delay(500, ct).ConfigureAwait(false);
        await EnsureChannelSessionAsync(ch).ConfigureAwait(false);
        await PumpReadsAsync(ch, RegisterReads, ct).ConfigureAwait(false);

        var before = Snapshot(ch);
        var cfg = BuildConfig(ch, before);
        if (cfg.ProbeFreq <= 0) cfg.ProbeFreq = 2m;
        var origWin = Num(before, "EchoWin");
        if (origWin is null) throw new InvalidOperationException($"CH{ch} 回波窗未读到，不能扫波（扫完无法把回波窗写回去）");

        var widened = false;
        if (rangeNs > origWin.Value)
        {
            cfg.EchoWin = rangeNs;
            await SendAsync(ch, cfg, ParamGroup.Register, EchoWinWriteNames, ct, quiet: true).ConfigureAwait(false);
            widened = true;
            Log($"CH{ch} 扫波：回波窗临时 {Fmt(origWin.Value)} -> {rangeNs} ns");
        }
        else
        {
            await _client.ConfigureChannelAsync(_sessionId, cfg, null, false).ConfigureAwait(false);
        }

        var tcs = new TaskCompletionSource<EchoWave>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fullWaveSink = m =>
        {
            if (m.Channel == ch && m.EchoWave is { WaveData.Count: > 1 } w) tcs.TrySetResult(w);
        };

        EchoWave? wave = null;
        try
        {
            await _client.SetDeviceModeAsync(_sessionId, WorkMode.FullWaveRead, ch, _opt.Address).ConfigureAwait(false);
            using var timer = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
            try
            {
                wave = await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log($"CH{ch} 扫波 {timeout.TotalSeconds:0} 秒内没有收到完整波形（设备可能不支持全波模式）");
            }
        }
        finally
        {
            _fullWaveSink = null;
            await _client.SetDeviceModeAsync(_sessionId, WorkMode.Standby, 0, _opt.Address).ConfigureAwait(false);
            await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            if (widened)
            {
                cfg.EchoWin = origWin.Value;
                await SendAsync(ch, cfg, ParamGroup.Register, EchoWinWriteNames, CancellationToken.None, quiet: true).ConfigureAwait(false);
                lock (_got)
                {
                    if (_got.TryGetValue(ch, out var bag0)) bag0.Remove("EchoWin");
                }

                await PumpReadsAsync(ch, EchoWinReadSet, CancellationToken.None).ConfigureAwait(false);
                var back = Raw(Snapshot(ch), "EchoWin");
                var restored = SameNum(back, Fmt(origWin.Value));
                EchoWinRestoreNote = restored ? null : $"回波窗读回 {back}，应为 {Fmt(origWin.Value)} ns，请到「参数下发」核对（设备可能还留着扫波时的大窗口）";
                Log(restored
                    ? $"CH{ch} 扫波结束，回波窗已写回 {back} ns"
                    : $"CH{ch} 扫波结束，{EchoWinRestoreNote}");
            }
        }

        if (wave is not null)
        {
            Log($"CH{ch} 全波 {wave.WaveData!.Count} 点，{wave.ClockInt} ns/点，覆盖 0～{(long)wave.WaveData.Count * wave.ClockInt} ns");
        }

        return wave;
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>
    /// 置零成功后记自动法锚点：包络时间 = 波形起点 + Fx × 点间隔，周期取 SDK 给的中位周期。
    /// 只有自动法（MeasMode=0）用得上；没有 Assist（阈值法/位置法或这一帧没解出）就不记。
    /// </summary>
    public bool RecordAnchor(ushort ch, decimal refTime, EchoWave wave, EchoAssist? assist)
    {
        if (_opt.Anchors is not { } store || assist?.Fx is not { } fx || assist.PeriodNs is not { } period || period <= 0) return false;
        var env = wave.EchoStart + fx * wave.ClockInt;
        try
        {
            store.Save(ch, new AnchorStore.Anchor(refTime, env, period, DateTime.Now));
        }
        catch (Exception ex)
        {
            Log($"CH{ch} 设备已置零，自动法锚点没存上：{ex.Message}（{store.Path}）。大屏可能按下一帧重新捕获");
            return false;
        }

        Log($"CH{ch} 自动法锚点已存：包络 {env:F1} ns，周期 {period:F2} ns（{store.Path}）");
        return true;
    }

    public Task CloseAsync() => ShutdownAsync();

    public async Task RebootDeviceAsync()
    {
        _streaming = false;
        await _client.SetDeviceModeAsync(_sessionId, WorkMode.Reboot, 0, _opt.Address).ConfigureAwait(false);
        Log("已发送设备重启命令");
        await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _client.CloseAsync(_sessionId).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        IsConnected = false;
    }

    public async Task<Dictionary<string, string>> ReadChannelValuesAsync(ushort ch, CancellationToken ct)
    {
        await EnsureChannelSessionAsync(ch).ConfigureAwait(false);
        await ReadChannelAsync(ch, ct).ConfigureAwait(false);
        var bag = Snapshot(ch);
        var result = new Dictionary<string, string>();
        foreach (var key in bag.Keys)
        {
            result[key] = Raw(bag, key);
        }

        if (bag.ContainsKey("WaveStart")) result["LEchoWave"] = WaveCell(bag);
        return result;
    }

    public async Task<Dictionary<string, string>> ReadStorageAsync(ushort ch, CancellationToken ct)
    {
        await EnsureChannelSessionAsync(ch).ConfigureAwait(false);
        // 只丢掉存储区旧值，逼这一次真的去读参考声时。探头频率、回波窗、增益留下：
        // 整袋删掉的话，没变化的通道不会把寄存器读回来，恢复监测就会按缺省 2 MHz 重建会话。
        Dictionary<string, string>? kept;
        lock (_got)
        {
            kept = _got.TryGetValue(ch, out var old) ? new Dictionary<string, string>(old) : null;
            _got.Remove(ch);
        }

        await PumpReadsAsync(ch, StorageReadSet, ct).ConfigureAwait(false);

        var produced = Snapshot(ch);
        var gotStorage = produced.ContainsKey("RefTime");
        lock (_got)
        {
            if (kept is not null)
            {
                if (!_got.TryGetValue(ch, out var bag))
                {
                    _got[ch] = kept;
                }
                else
                {
                    foreach (var kv in kept)
                    {
                        if (!bag.ContainsKey(kv.Key)) bag[kv.Key] = kv.Value;
                    }
                }
            }
        }

        // 存储区没读到：调用方按失败处理。缓存里仍是上一次的寄存器和旧基线，不把缺省频率写进会话。
        if (!gotStorage) return new Dictionary<string, string>();

        var bagNow = Snapshot(ch);
        var result = new Dictionary<string, string>();
        foreach (var key in bagNow.Keys)
        {
            result[key] = Raw(bagNow, key);
        }

        if (bagNow.ContainsKey("WaveStart")) result["LEchoWave"] = WaveCell(bagNow);
        return result;
    }

    public static bool IsZeroed(Dictionary<string, string> storage) =>
        storage.TryGetValue("RefTime", out var v) && v != "-" && v != "未写入";

    private static readonly string[] RegisterReads =
    {
        nameof(ChannelConfig.ProbeFreq), nameof(ChannelConfig.EchoWin),
        nameof(ChannelConfig.EchoWidth), nameof(ChannelConfig.PulseConfig), nameof(ChannelConfig.Gain),
    };

    private static readonly string[] StorageReadSet = { nameof(ChannelConfig.RefTime) };
    private static readonly string[] EchoWinReadSet = { nameof(ChannelConfig.EchoWin) };
    private static readonly string[] EchoWinWriteNames = { "EchoWin" };

    private static readonly string[] ZeroWriteNames = { "RefTime", "RefTemp" };

    private static string ReadNameOf(string param)
    {
        switch (param)
        {
            case "Duty":
            case "Forward":
            case "Reverse":
                return nameof(ChannelConfig.PulseConfig);
            case "MeasMode":
            case "Threshold":
            case "RefTemp":
            case "RefTime":
                return nameof(ChannelConfig.RefTime);
            default:
                return param;
        }
    }

    /// <summary>
    /// 请求-应答式补读：一条读命令等到回应再发下一条（链路近似半双工，紧挨着发会和大回包撞车）。
    /// 缺 2 项以上寄存器时先一帧合并读；一轮里连着两条都没回音就认为通道这一轮不吭声，不再整包重发。
    /// </summary>
    private async Task PumpReadsAsync(ushort ch, string[] reads, CancellationToken ct, int rounds = 3,
        bool verbose = false)
    {
        for (var round = 0; round < rounds; round++)
        {
            // SDK 在会话断开后发送不报错，这里不查的话，连接流程会在死链路上逐条等超时，约 2 分钟才失败
            if (!IsConnected) return;
            var missing = reads.Where(n => !HasReply(Snapshot(ch), n)).ToArray();
            if (missing.Length == 0) return;
            if (verbose && round > 0)
            {
                Log($"CH{ch} {missing.Length} 项无响应，重试 {round}/{rounds - 1}...");
            }

            var attempts = 0;
            var replied = false;
            var regs = missing.Where(RegisterReads.Contains).ToArray();
            var batchTried = false;
            var batchGot = 0;
            if (_batchReadOk && regs.Length >= 2)
            {
                batchTried = true;
                batchGot = await ReadRegisterBatchAsync(ch, regs, ct).ConfigureAwait(false);
                attempts++;
                replied = batchGot > 0;
            }

            var singleRegReplied = false;
            foreach (var name in missing)
            {
                if (HasReply(Snapshot(ch), name)) continue;
                if (!replied && attempts >= 2) break;
                attempts++;
                if (!await ReadOneAsync(ch, name, ct).ConfigureAwait(false)) continue;
                replied = true;
                if (RegisterReads.Contains(name)) singleRegReplied = true;
            }

            // 通道活着、单条读有回应，合并读却没有：多半是设备不认这条命令。连续两次就不再试了
            if (batchTried && batchGot == 0 && singleRegReplied && ++_batchReadMisses >= 2 && _batchReadOk)
            {
                _batchReadOk = false;
                Log("设备不响应寄存器合并读，改为逐个读取");
            }
        }
    }

    private static int ReplyTimeoutOf(string readName) =>
        readName == nameof(ChannelConfig.RefTime) ? StorageReplyTimeoutMs : RegisterReplyTimeoutMs;

    private async Task<bool> WaitRepliesAsync(ushort ch, string[] names, int timeoutMs, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var bag = Snapshot(ch);
            if (names.All(n => HasReply(bag, n))) return true;
            if (!IsConnected || Environment.TickCount64 >= deadline) return false;
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> ReadOneAsync(ushort ch, string name, CancellationToken ct)
    {
        await _client.ReadChannelParamAsync(_sessionId, name, ch, _opt.Address).ConfigureAwait(false);
        return await WaitRepliesAsync(ch, new[] { name }, ReplyTimeoutOf(name), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 一帧读回 0x00~0x04 全部寄存器，返回这一帧新带回了几项。
    /// </summary>
    private async Task<int> ReadRegisterBatchAsync(ushort ch, string[] wanted, CancellationToken ct)
    {
        var had = wanted.Count(n => HasReply(Snapshot(ch), n));
        await _client.ReadChannelParamAsync(_sessionId, MultiChIotProtocolHandler.ChannelRegistersParam, ch, _opt.Address)
            .ConfigureAwait(false);
        await WaitRepliesAsync(ch, wanted, RegisterReplyTimeoutMs, ct).ConfigureAwait(false);
        return wanted.Count(n => HasReply(Snapshot(ch), n)) - had;
    }

    public async Task<Dictionary<string, string>> ReadRegisterValuesAsync(ushort ch, CancellationToken ct)
    {
        await EnsureChannelSessionAsync(ch).ConfigureAwait(false);

        lock (_got)
        {
            if (_got.TryGetValue(ch, out var stale))
            {
                foreach (var key in RegisterBatchOrder)
                {
                    stale.Remove(key);
                }
            }
        }

        await PumpReadsAsync(ch, RegisterReads, ct).ConfigureAwait(false);

        var bag = Snapshot(ch);
        var result = new Dictionary<string, string>();
        if (!RegisterReads.Any(n => HasReply(bag, n))) return result;
        foreach (var key in RegisterBatchOrder)
        {
            result[key] = Raw(bag, key);
        }

        return result;
    }

    public async Task<List<ParamRow>> WriteRegistersAsync(ushort ch,
        IReadOnlyList<(string Name, decimal Value)> items, CancellationToken ct)
    {
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        var rows = new List<ParamRow>();
        var todo = new List<(ParamSpec Spec, ParamRow Row, decimal Value)>();
        foreach (var item in items)
        {
            var spec = ParamCatalog.Find(item.Name);
            if (spec == null)
            {
                throw new ArgumentException($"未知参数：{item.Name}", nameof(items));
            }

            var online = spec.Group == ParamGroup.Register || spec.Group == ParamGroup.Pulse || spec.Group == ParamGroup.Storage;
            if (!online || spec.Kind != ParamKind.ReadWrite)
            {
                throw new ArgumentException($"参数 {item.Name} 不支持在线下发", nameof(items));
            }

            var row = NewRow(spec, ch);
            rows.Add(row);
            todo.Add((spec, row, item.Value));
        }

        if (todo.Count == 0) return rows;

        await EnsureChannelSessionAsync(ch).ConfigureAwait(false);

        var hwTodo = todo.Where(t => t.Spec.Group != ParamGroup.Storage).ToList();
        var storageTodo = todo.Where(t => t.Spec.Group == ParamGroup.Storage).ToList();

        // 硬件寄存器按 5 个一帧写。没读到的项会按默认值写进去（回波窗默认 0），改之前先读齐。
        var needReads = todo.Select(t => ReadNameOf(t.Spec.Name)).ToList();
        if (hwTodo.Count > 0) needReads.AddRange(RegisterReads);
        var missingReads = needReads.Distinct().Where(n => !HasReply(Snapshot(ch), n)).ToArray();
        if (missingReads.Length > 0)
        {
            await PumpReadsAsync(ch, missingReads, ct).ConfigureAwait(false);
        }

        var before = Snapshot(ch);
        if (before.Count == 0)
        {
            foreach (var t in todo)
            {
                t.Row.Verdict = Verdict.NoReadback;
                t.Row.Detail = "通道无响应";
            }

            return rows;
        }

        foreach (var t in todo)
        {
            t.Row.DeviceValue = Raw(before, t.Row.Name);
            t.Row.BeforeValue = t.Row.DeviceValue;
        }

        var cfg = BuildConfig(ch, before);
        foreach (var t in todo)
        {
            ApplyParam(cfg, t.Row.Name, t.Value);
        }

        var reads = new List<string>();
        var implicitNames = new List<string>();

        if (hwTodo.Count > 0)
        {
            if (RegisterBatchOrder.All(n => Num(before, n) != null))
            {
                await SendAllRegistersAsync(ch, cfg, ct).ConfigureAwait(false);
                implicitNames.AddRange(RegisterBatchOrder);
            }
            else
            {
                var gain = hwTodo.FirstOrDefault(t => t.Row.Name == "Gain");
                var editsEchoWin = hwTodo.Any(t => t.Row.Name == "EchoWin");
                if (gain.Row != null && !editsEchoWin && Num(before, "EchoWin") == null)
                {
                    gain.Row.Verdict = Verdict.NotSent;
                    gain.Row.Detail = "回波窗当前值未读到，写增益会连带把回波窗写成 0，已跳过";
                    hwTodo.Remove(gain);
                }
                else if (gain.Row != null && !editsEchoWin)
                {
                    implicitNames.Add("EchoWin");
                }

                foreach (var group in hwTodo.Select(t => t.Spec.Group).Distinct())
                {
                    var names = hwTodo.Where(t => t.Spec.Group == group).Select(t => t.Row.Name).ToArray();
                    await SendAsync(ch, cfg, group, names, ct).ConfigureAwait(false);
                    if (group == ParamGroup.Pulse) implicitNames.AddRange(PulseParams);
                }
            }

            reads.AddRange(hwTodo.Select(t => ReadNameOf(t.Row.Name)));
        }

        if (storageTodo.Count > 0)
        {
            // 存储区是整块写（测量模式+阈值+参考声时+参考温度+参考波形）。未置零的通道读回来的
            // 是擦除态哑值和一段假波形，原样写回去这个通道就"看起来置过零了"。置零只走 ZeroChannelAsync。
            var waveStart = Raw(before, "WaveStart");
            var reason = !IsZeroed(before) || waveStart == "未写入" || waveStart == "-"
                ? "通道未置零，存储区参数不能在线改，请先在「通道置零」里置零"
                : await SendAsync(ch, cfg, ParamGroup.Storage, storageTodo.Select(t => t.Row.Name).ToArray(), ct)
                    .ConfigureAwait(false);
            if (reason != null)
            {
                foreach (var t in storageTodo)
                {
                    t.Row.Verdict = Verdict.NotSent;
                    t.Row.Detail = reason;
                }
            }
            else
            {
                reads.Add(nameof(ChannelConfig.RefTime));
                implicitNames.AddRange(StorageParams);
            }
        }

        var sent = todo.Where(t => t.Row.Verdict == Verdict.Unknown).ToList();
        var implicitRows = new List<(ParamRow Row, decimal Value)>();
        foreach (var name in implicitNames.Distinct())
        {
            if (todo.Any(t => t.Row.Name == name)) continue;
            if (Num(before, name) is not decimal keep) continue;
            var row = NewRow(ParamCatalog.Find(name)!, ch);
            row.Implicit = true;
            row.DeviceValue = Raw(before, name);
            row.BeforeValue = row.DeviceValue;
            rows.Add(row);
            implicitRows.Add((row, keep));
            reads.Add(ReadNameOf(name));
        }

        if (sent.Count == 0 && implicitRows.Count == 0) return rows;

        var targets = sent.Select(t => (t.Row, t.Value)).Concat(implicitRows).ToList();
        await ReadBackAndJudgeAsync(ch, targets, reads.Distinct().ToArray(), ct).ConfigureAwait(false);
        return rows;
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>
    /// 写后读回并判定。读回值仍等于原值时隔半秒再读一次，设备可能先应答后落盘。
    /// </summary>
    private async Task ReadBackAndJudgeAsync(ushort ch, IReadOnlyList<(ParamRow Row, decimal Value)> targets,
        string[] readSet, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            lock (_got)
            {
                if (_got.TryGetValue(ch, out var bag0))
                {
                    foreach (var t in targets) bag0.Remove(t.Row.Name);
                    if (readSet.Contains(nameof(ChannelConfig.PulseConfig)))
                    {
                        foreach (var key in PulseParams) bag0.Remove(key);
                    }

                    if (readSet.Contains(nameof(ChannelConfig.RefTime)))
                    {
                        foreach (var key in StorageParams) bag0.Remove(key);
                    }
                }
            }

            await PumpReadsAsync(ch, readSet, ct).ConfigureAwait(false);

            var after = Snapshot(ch);
            foreach (var (row, value) in targets)
            {
                var expect = Expect(row.Name, value);
                var actual = Raw(after, row.Name);
                row.Wrote = expect;
                row.ReadBack = actual;
                (row.Verdict, row.Detail) = Judge(row.Name, value, expect, actual, row.BeforeValue);
                if (row.Implicit && row.Verdict == Verdict.Rewritten)
                {
                    row.Detail = $"连带写入后读回 {actual}，原值 {row.BeforeValue}";
                }

                row.DeviceValue = actual;
            }

            if (attempt == 0 && targets.Any(t => !t.Row.Implicit && t.Row.Verdict == Verdict.Ignored))
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            break;
        }
    }

    public async Task<List<ParamRow>> ZeroChannelAsync(ushort ch, decimal refTimeNs, decimal refTempC,
        EchoWave liveWave, CancellationToken ct)
    {
        if (liveWave == null || liveWave.WaveData == null || liveWave.WaveData.Count == 0)
        {
            throw new ArgumentException("缺少当前回波波形", nameof(liveWave));
        }

        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        await EnsureChannelSessionAsync(ch).ConfigureAwait(false);

        if (!HasReply(Snapshot(ch), nameof(ChannelConfig.RefTime)))
        {
            await PumpReadsAsync(ch, StorageReadSet, ct).ConfigureAwait(false);
        }

        var before = Snapshot(ch);
        var wanted = new Dictionary<string, decimal>()
        {
            ["RefTime"] = refTimeNs,
            ["RefTemp"] = refTempC,
        };
        var rows = new List<ParamRow>();
        foreach (var name in ZeroWriteNames)
        {
            var row = NewRow(ParamCatalog.Find(name)!, ch);
            row.DeviceValue = Raw(before, name);
            row.BeforeValue = row.DeviceValue;
            rows.Add(row);
        }

        var cfg = BuildConfig(ch, before);
        ApplyParam(cfg, "RefTime", wanted["RefTime"]);
        ApplyParam(cfg, "RefTemp", wanted["RefTemp"]);
        cfg.LEchoWave = liveWave;

        Log($"CH{ch} 置零：参考声时 {Raw(before, "RefTime")} -> {Fmt(wanted["RefTime"])}，"
            + $"参考温度 {Raw(before, "RefTemp")} -> {Fmt(wanted["RefTemp"])}");

        var reason = await SendAsync(ch, cfg, ParamGroup.Storage, ZeroWriteNames, ct).ConfigureAwait(false);
        if (reason != null)
        {
            foreach (var row in rows)
            {
                row.Verdict = Verdict.NotSent;
                row.Detail = reason;
            }

            return rows;
        }

        await ReadBackAndJudgeAsync(ch, rows.Select(r => (r, wanted[r.Name])).ToList(), StorageReadSet, ct)
            .ConfigureAwait(false);
        return rows;
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task ResumeMonitorAsync(IReadOnlyList<ushort> channels, CancellationToken ct)
    {
        if (channels.Count == 0)
        {
            throw new ArgumentException("通道列表为空", nameof(channels));
        }

        foreach (var ch in channels)
        {
            if (Snapshot(ch).Count > 0)
            {
                await ApplySessionConfigAsync(ch).ConfigureAwait(false);
            }
        }

        await SetPollingAsync(channels, ct).ConfigureAwait(false);
        _streaming = true;
        await _client.SetDeviceModeAsync(_sessionId, WorkMode.MultiMeas, 0, _opt.Address).ConfigureAwait(false);
        Log($"已恢复监测，{channels.Count} 个通道");
    }

    private volatile bool _streaming;
    public event Action<ChannelResult>? OnMeasurement;

    public sealed class ProbeScan
    {
        public ushort Channel { get; set; }
        public bool Heard { get; set; }
        public int Frames { get; set; }
        public int Peak { get; set; }
        public double SnrMax { get; set; }
        public double SnrAvg { get; set; }
        public bool HasProbe { get; set; }
        public bool Suspect { get; set; }
    }

    private sealed class ScanStat
    {
        public int Frames;
        public int Echo;
        public int Peak;
        public readonly List<double> Snrs = new();
    }

    public const double ProbeSnrDb = 16;
    public const double NoiseSnrDb = 12;
    private const int WideScanMinSeconds = 6;
    private const int WideScanMaxSeconds = 45;
    private const int VerifyFramesPerChannel = 6;

    private readonly Dictionary<ushort, ScanStat> _scan = new();
    private volatile bool _scanning;

    public static double? Snr(EchoWave? wave)
    {
        var d = wave?.WaveData;
        if (d == null || d.Count < 64) return null;
        if (d.Count(x => Math.Abs((int)x) >= 2040) > d.Count / 2) return 0;
        var head = Math.Max(16, d.Count / 8);
        double mean = 0;
        for (var i = 0; i < head; i++) mean += d[i];
        mean /= head;
        double sq = 0;
        for (var i = 0; i < head; i++) sq += (d[i] - mean) * (d[i] - mean);
        var noise = Math.Max(1.0, Math.Sqrt(sq / head));
        double peak = 0;
        for (var i = head; i < d.Count; i++) peak = Math.Max(peak, Math.Abs(d[i] - mean));
        return 20 * Math.Log10(Math.Max(peak, 1.0) / noise);
    }

    private void AbsorbScan(ChannelResult m)
    {
        var snr = Snr(m.EchoWave);
        if (snr == null) return;
        if (!_scan.TryGetValue(m.Channel, out var s))
        {
            s = new ScanStat();
            _scan[m.Channel] = s;
        }

        s.Frames++;
        if (m.EchoTime.HasValue) s.Echo++;
        s.Peak = Math.Max(s.Peak, m.EchoWave!.WaveData!.Max(x => Math.Abs((int)x)));
        s.Snrs.Add(snr.Value);
    }

    private async Task<Dictionary<ushort, ScanStat>> CollectScanAsync(IReadOnlyList<ushort> channels,
        int minSeconds, double maxSeconds, Func<Dictionary<ushort, ScanStat>, bool> isDone,
        Action<int, double>? progress, CancellationToken ct)
    {
        lock (_scan) _scan.Clear();
        await SetPollingAsync(channels, ct).ConfigureAwait(false);

        _scanning = true;
        var started = DateTime.UtcNow;
        try
        {
            await _client.SetDeviceModeAsync(_sessionId, WorkMode.MultiMeas, 0, _opt.Address).ConfigureAwait(false);
            while (true)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                bool done;
                int heard;
                lock (_scan)
                {
                    heard = _scan.Count;
                    done = isDone(_scan);
                }

                progress?.Invoke(heard, elapsed);
                if (done && elapsed >= minSeconds) break;
                if (elapsed >= maxSeconds) break;
            }
        }
        finally
        {
            _scanning = false;
            await _client.SetDeviceModeAsync(_sessionId, WorkMode.Standby, 0, _opt.Address).ConfigureAwait(false);
            await Task.Delay(1500, CancellationToken.None).ConfigureAwait(false);
        }

        lock (_scan)
        {
            return channels.Where(_scan.ContainsKey).ToDictionary(c => c, c => _scan[c]);
        }
    }

    public async Task<List<ProbeScan>> ScanProbesAsync(int channelCount, Action<string>? progress, CancellationToken ct)
    {
        var all = Enumerable.Range(1, channelCount).Select(i => (ushort)i).ToArray();

        _streaming = false;
        await _client.SetDeviceModeAsync(_sessionId, WorkMode.Standby, 0, _opt.Address).ConfigureAwait(false);
        await Task.Delay(500, ct).ConfigureAwait(false);
        foreach (var ch in all)
        {
            await EnsureChannelSessionAsync(ch).ConfigureAwait(false);
        }

        var wide = await CollectScanAsync(all, WideScanMinSeconds, WideScanMaxSeconds,
            stats => stats.Count >= channelCount,
            (heard, elapsed) => progress?.Invoke($"正在扫描 {channelCount} 个通道，已响应 {heard} 个，{elapsed:0} 秒..."),
            ct).ConfigureAwait(false);

        var initialUnheard = all.Where(c => !wide.ContainsKey(c)).ToList();
        var candidates = wide.Where(kv => kv.Value.Snrs.Max() >= NoiseSnrDb || kv.Value.Echo > 0)
            .Select(kv => kv.Key).ToList();

        // 复核阶段的窄轮询集：既复核有信号迹象的候选通道，也把宽扫一帧都没听到的通道拉进来补扫。
        // 窄轮询集里每个通道的帧率比 48 通道全扫高约 10 倍，几秒就能确认它到底有没有回波，
        // 避免宽扫期偶发丢帧、或 45 秒内没轮到，就把一个可能插了探头的通道误判成"未响应"漏掉。
        var verify = candidates.Concat(initialUnheard).Distinct().OrderBy(c => c).ToList();

        var narrow = new Dictionary<ushort, ScanStat>();
        if (verify.Count > 0)
        {
            var maxSeconds = Math.Clamp(verify.Count * 2.0, 12, 40);
            narrow = await CollectScanAsync(verify, 4, maxSeconds,
                stats => verify.All(c => stats.TryGetValue(c, out var s) && s.Frames >= VerifyFramesPerChannel),
                (heard, elapsed) => progress?.Invoke($"正在复核 {verify.Count} 个通道，已响应 {heard} 个，{elapsed:0} 秒..."),
                ct).ConfigureAwait(false);
        }

        var result = new List<ProbeScan>();
        foreach (var ch in all)
        {
            if (!narrow.TryGetValue(ch, out var s) && !wide.TryGetValue(ch, out s))
            {
                result.Add(new ProbeScan { Channel = ch });
                continue;
            }

            var strong = s.Snrs.Count(v => v >= ProbeSnrDb);
            var majority = Math.Ceiling(s.Frames * 0.6);
            var avg = s.Snrs.Average();
            // 平均信噪比过噪声门才算有探头，空通道的噪声也可能被解出声时
            var hasProbe = strong >= majority || (s.Echo >= majority && avg >= NoiseSnrDb);
            result.Add(new ProbeScan
            {
                Channel = ch,
                Heard = true,
                Frames = s.Frames,
                Peak = s.Peak,
                SnrMax = s.Snrs.Max(),
                SnrAvg = avg,
                HasProbe = hasProbe,
                Suspect = !hasProbe && avg >= NoiseSnrDb,
            });
        }

        var probes = result.Where(r => r.HasProbe).Select(r => "CH" + r.Channel).ToList();
        var suspects = result.Where(r => r.Suspect).Select(r => $"CH{r.Channel}({r.SnrAvg:0}dB)").ToList();
        var unheard = result.Where(r => !r.Heard).Select(r => r.Channel).ToList();
        Log($"扫描完成：有探头 {(probes.Count > 0 ? string.Join("、", probes) : "无")}"
            + (suspects.Count > 0 ? $"，信号弱 {string.Join("、", suspects)}" : string.Empty)
            + (unheard.Count > 0 ? $"，两轮均无响应 {string.Join("、", unheard.Select(c => "CH" + c))}" : string.Empty));
        return result;
    }

    public async Task<Dictionary<ushort, Dictionary<string, string>>> StartStreamAsync(
        IReadOnlyList<ushort> channels, CancellationToken ct)
    {
        if (channels.Count == 0)
        {
            throw new ArgumentException("通道列表为空", nameof(channels));
        }

        var config = new Dictionary<ushort, Dictionary<string, string>>();
        foreach (var ch in channels.OrderBy(c => c))
        {
            await EnsureChannelSessionAsync(ch).ConfigureAwait(false);
            await ReadChannelAsync(ch, ct).ConfigureAwait(false);
            var bag = Snapshot(ch);
            if (bag.Count == 0)
            {
                Log($"CH{ch} 参数读取失败");
            }

            config[ch] = bag;
            await _client.ConfigureChannelAsync(_sessionId, BuildConfig(ch, bag), null, false)
                .ConfigureAwait(false);
        }

        await SetPollingAsync(config.Keys.OrderBy(c => c).ToArray(), ct).ConfigureAwait(false);
        _streaming = true;
        await _client.SetDeviceModeAsync(_sessionId, WorkMode.MultiMeas, 0, _opt.Address).ConfigureAwait(false);
        return config;
    }

    public async Task StopStreamAsync()
    {
        _streaming = false;
        await _client.SetDeviceModeAsync(_sessionId, WorkMode.Standby, 0, _opt.Address).ConfigureAwait(false);
        await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
    }

    public EchoWave? RefWave(ushort ch)
    {
        lock (_waves) return _waves.TryGetValue(ch, out var w) ? w : null;
    }

    public static string DescribeStatus(MeasStatusCode status) => status switch
    {
        MeasStatusCode.Normal => "正常",
        MeasStatusCode.SignalTooSmall => "信号太小",
        MeasStatusCode.ThresholdNotReached => "未达阈值",
        MeasStatusCode.SignalOverflow => "信号溢出",
        MeasStatusCode.SimilarityLow => "波形相似度低",
        MeasStatusCode.UnknownSignalError => "信号异常",
        MeasStatusCode.SnrLow => "信噪比低",
        MeasStatusCode.BelowLowerLimit => "低于下限",
        MeasStatusCode.AboveUpperLimit => "高于上限",
        _ => $"未知({(short)status})",
    };

    public async Task SetPollingAsync(IReadOnlyList<ushort> channels, CancellationToken ct)
    {
        if (channels.Count == 0) throw new ArgumentException("通道列表为空", nameof(channels));
        _device.MCRConfig = new MCRConfig
        {
            EnabledChannels = channels.ToList(),
            StartSeconds = 0,
            DailyCount = 0,
            SingleCount = 1,
        };
        Log($"下发轮询通道 {string.Join("、", channels.Select(c => "CH" + c))}");
        await _client.ConfigureDeviceAsync(_sessionId, _device, nameof(DeviceConfig.MCRConfig), true)
            .ConfigureAwait(false);
        await Task.Delay(Math.Max(800, _opt.Gap * 2), ct).ConfigureAwait(false);
    }

    private async Task EnsureChannelSessionAsync(ushort ch)
    {
        if (_sessionChannels.Contains(ch)) return;
        await _client.ConfigureChannelAsync(_sessionId,
            new ChannelConfig { Address = _opt.Address, Channel = ch }, null, false).ConfigureAwait(false);
        // 配置真发出去了才登记；先登记再 await 的话，抛了异常这一路就永远跳过配置
        _sessionChannels.Add(ch);
    }

    public static string? Validate(string name, decimal value) => name switch
    {
        // 设备按整数存，小数会被截掉。
        "EchoWidth" or "Duty" or "Forward" or "Reverse" or "MeasMode" or "Threshold"
            when value != decimal.Truncate(value) => "必须是整数",
        "ProbeFreq" when value <= 0 || value > 65 => "探头频率范围 0~65 MHz",
        "EchoWin" when value < 0 || value > 65535000 => "回波窗范围 0~65535000 ns",
        "EchoWidth" when value < 0 || value > 65535 => "回波宽度范围 0~65535 us",
        "Duty" when value < 0 || value > 100 => "占空比范围 0~100",
        "Forward" when value < 0 || value > 15 => "正向脉冲数范围 0~15",
        "Reverse" when value < 0 || value > 15 => "反向脉冲数范围 0~15",
        "Gain" when value < 1 || value > 47 => "增益范围 1~47",
        "MeasMode" when value < 0 || value > 2 => "测量模式只能是 0、1 或 2",
        "Threshold" when value < -32768 || value > 32767 => "检波阈值范围 -32768~32767",
        "RefTime" when value < 0 => "参考声时不能为负",
        "RefTime" when value >= 4294967295m => "参考声时超出范围",
        "RefTemp" when value < 0 => "参考温度不能为负",
        "RefTemp" when value > 327 => "参考温度范围 0~327 ℃",
        "NetCheck.Enabled" when value != 0 && value != 1 => "网络自检开关只能是 0 或 1",
        "NetCheck.CheckPeriod" when value < 0 || value > 86399 => "自检周期范围 0~86399 秒",
        _ => ParamCatalog.Find(name)?.Kind == ParamKind.ReadWrite ? null : "该参数不支持下发",
    };

    /// <summary>
    /// 把 cfg 里的一组参数写到设备。返回 null 表示命令已发出，否则是没发出去的原因。
    /// </summary>
    private async Task<string?> SendAsync(ushort ch, ChannelConfig cfg, ParamGroup group, string[] names,
        CancellationToken ct, bool quiet = false)
    {
        if (group == ParamGroup.Storage
            && (cfg.RefTime == null || cfg.LEchoWave == null || cfg.LEchoWave.WaveData == null || cfg.LEchoWave.WaveData.Count == 0))
        {
            if (!quiet) Log($"CH{ch} 未置零，无法写入存储区");
            return "通道未置零，存储区未写入";
        }

        cfg.Channel = ch;

        if (group == ParamGroup.Register)
        {
            foreach (var name in names)
            {
                if (!quiet)
                {
                    Log($"CH{ch} 下发 {ParamCatalog.CnOf(name)}={Fmt(GetParam(cfg, name))}");
                }

                // SDK 写增益时先补写一帧回波窗，是两帧、两个应答
                await WriteFramesAsync(ch, cfg, name, name == "Gain" ? 2 : 1,
                    RegisterAckTimeoutMs, Math.Max(600, _opt.Gap * 2), ct).ConfigureAwait(false);
            }

            return null;
        }

        var property = group == ParamGroup.Pulse
            ? nameof(ChannelConfig.PulseConfig)
            : nameof(ChannelConfig.RefTime);

        if (!quiet)
        {
            var shown = string.Join("、", names.Select(n => $"{ParamCatalog.CnOf(n)}={Fmt(GetParam(cfg, n))}"));
            Log($"CH{ch} 下发 {shown}");
        }

        if (group == ParamGroup.Storage)
        {
            await WriteFramesAsync(ch, cfg, property, 1, StorageAckTimeoutMs, Math.Max(1000, _opt.Gap * 3), ct)
                .ConfigureAwait(false);
        }
        else
        {
            await WriteFramesAsync(ch, cfg, property, 1, RegisterAckTimeoutMs, Math.Max(600, _opt.Gap * 2), ct)
                .ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// 全配置帧：探头频率、回波窗、回波宽度、脉冲、增益 5 个寄存器一帧写完（厂家软件的常规路径）。
    /// 调用方须保证 cfg 里 5 项都是设备当前值或用户要改的值。
    /// </summary>
    private async Task SendAllRegistersAsync(ushort ch, ChannelConfig cfg, CancellationToken ct)
    {
        cfg.Channel = ch;
        var shown = string.Join("、", RegisterBatchOrder.Select(n => $"{ParamCatalog.CnOf(n)}={Fmt(GetParam(cfg, n))}"));
        Log($"CH{ch} 一帧下发全部寄存器：{shown}");
        await WriteFramesAsync(ch, cfg, null, 1, RegisterAckTimeoutMs, Math.Max(600, _opt.Gap * 2), ct)
            .ConfigureAwait(false);
    }

    /// <summary>等写应答。超时则补足原来的定长等待。成不成功看随后的读回。</summary>
    private async Task WriteFramesAsync(ushort ch, ChannelConfig cfg, string? property, int frames,
        int ackTimeoutMs, int fallbackMs, CancellationToken ct)
    {
        var acks0 = Interlocked.Read(ref _acks);
        var started = Environment.TickCount64;
        await _client.ConfigureChannelAsync(_sessionId, cfg, property, true).ConfigureAwait(false);
        if (await WaitAcksAsync(acks0, frames, ackTimeoutMs, ct).ConfigureAwait(false))
        {
            await Task.Delay(AckSettleMs, ct).ConfigureAwait(false);
            return;
        }

        if (!IsConnected) return;   
        if (_acksSeen)
        {
            Log($"CH{ch} 写 {property ?? "全部寄存器"} 未收到设备应答，按定长等待继续");
        }

        var remaining = fallbackMs - (int)(Environment.TickCount64 - started);
        if (remaining > 0)
        {
            await Task.Delay(remaining, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> WaitAcksAsync(long acks0, int frames, int timeoutMs, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Interlocked.Read(ref _acks) - acks0 < frames)
        {
            if (!IsConnected || Environment.TickCount64 >= deadline) return false;
            await Task.Delay(10, ct).ConfigureAwait(false);
        }

        return true;
    }

    private async Task ReadChannelAsync(ushort ch, CancellationToken ct)
    {
        lock (_got) _got.Remove(ch);
        await PumpReadsAsync(ch, ChannelReads, ct, rounds: 5, verbose: true).ConfigureAwait(false);
    }

    private static bool HasReply(Dictionary<string, string> bag, string name) => name switch
    {
        nameof(ChannelConfig.PulseConfig) => bag.ContainsKey("Duty"),
        _ => bag.ContainsKey(name),
    };

    private async Task ShutdownAsync()
    {
        try
        {
            var resume = ParseResume(_opt.Resume);
            await _client.SetDeviceModeAsync(_sessionId, resume.Mode, resume.Channel, _opt.Address).ConfigureAwait(false);
            Log(resume.Mode == WorkMode.MultiMeas ? "设备已恢复为监测模式" : "设备已切换为待机模式");
            await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            await _client.CloseAsync(_sessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"恢复设备模式失败：{ex.Message}");
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ioDisposed, 1) != 0) return;
        _io.Dispose();
    }

    public static string Expect(string name, decimal v) => name switch
    {
        "EchoWin" => Fmt(Math.Round(v / 1000, MidpointRounding.ToEven) * 1000),
        "Gain" => Fmt(Math.Round(v, MidpointRounding.ToEven)),
        "RefTime" => Fmt(Math.Round(v, MidpointRounding.ToEven)),
        "RefTemp" => Fmt(Math.Round(v * 100, MidpointRounding.ToEven) / 100),
        "ProbeFreq" => Fmt(Math.Round(v * 1000, MidpointRounding.ToEven) / 1000),
        _ => Fmt(v),
    };

    public static (Verdict, string) Judge(string name, decimal probe, string expect, string actual, string before)
    {
        if (actual == "-") return (Verdict.NoReadback, "读回无响应");
        if (SameNum(actual, expect))
        {
            var quantized = (name == "EchoWin" || name == "RefTemp" || name == "Gain") && expect != Fmt(probe);
            return (Verdict.StoredOk, quantized ? $"写入值 {Fmt(probe)} 被量化为 {expect}" : string.Empty);
        }

        return SameNum(actual, before)
            ? (Verdict.Ignored, "写入无效，设备值未变化")
            : (Verdict.Rewritten, $"读回值 {actual} 与写入值 {expect} 不一致");
    }

    private ParamRow NewRow(ParamSpec spec, ushort ch) => new()
    {
        Channel = ch,
        Name = spec.Name,
        Cn = spec.Cn,
        Group = spec.Group,
        Kind = spec.Kind,
        IsChannelParam = spec.IsChannel,
    };

    private void AbsorbChannel(ushort ch, DeviceParameter p)
    {
        if (!_got.TryGetValue(ch, out var bag))
        {
            bag = new Dictionary<string, string>();
            _got[ch] = bag;
        }

        switch (p.ParamValue)
        {
            case PulseConfig pc:
                bag["Duty"] = pc.Duty.ToString();
                bag["Forward"] = pc.Forward.ToString();
                bag["Reverse"] = pc.Reverse.ToString();
                break;
            case EchoWave w:
                bag["WavePts"] = (w.WaveData?.Count ?? 0).ToString();
                bag["WaveStart"] = w.EchoStart.ToString();
                lock (_waves) _waves[ch] = w;
                break;
            default:
                bag[p.ParamName] = Convert.ToString(p.ParamValue) ?? string.Empty;
                break;
        }
    }

    public Dictionary<string, string> Snapshot(ushort ch)
    {
        lock (_got)
        {
            return _got.TryGetValue(ch, out var bag)
                ? new Dictionary<string, string>(bag)
                : new Dictionary<string, string>();
        }
    }

    public async Task ApplySessionConfigAsync(ushort ch)
    {
        var bag = Snapshot(ch);
        var cfg = BuildConfig(ch, bag);
        if (cfg.ProbeFreq <= 0) cfg.ProbeFreq = 2m;
        if (cfg.EchoWin <= 0 && Num(bag, "WaveStart") is decimal ws) cfg.EchoWin = ws;
        await _client.ConfigureChannelAsync(_sessionId, cfg, null, false).ConfigureAwait(false);
        // 自动法且已置零却没有存盘锚点（别的软件置的零，或锚点文件丢了）：SDK 会用下一帧重新捕获，
        // 螺栓较置零时松紧超过半个周期就可能锁到相邻上升沿。写进日志，出了整周期的偏差能对回来。
        var noAnchor = Num(bag, "MeasMode") == 0 && cfg.RefTime.HasValue && cfg.RefEnvTime is null;
        Log($"CH{ch} 会话参数：探头频率 {cfg.ProbeFreq} MHz，参考声时 {cfg.RefTime?.ToString() ?? "-"} ns，温补 TcMode={cfg.TcMode}"
            + (cfg.UseTempChannel > 0 ? $"，温度取自 CH{cfg.UseTempChannel}" : string.Empty)
            + (cfg.RefEnvTime is { } env ? $"，自动法锚点 {env:F0} ns / 周期 {cfg.RefPeriodNs:F1} ns"
                : noAnchor ? "，自动法锚点：无存盘记录，SDK 按下一帧重新捕获" : string.Empty));
    }

    private ChannelConfig BuildConfig(ushort ch, Dictionary<string, string> bag)
    {
        var cfg = new ChannelConfig { Address = _opt.Address, Channel = ch };
        if (Num(bag, "ProbeFreq") is decimal pf) cfg.ProbeFreq = pf;
        if (Num(bag, "EchoWin") is decimal ew) cfg.EchoWin = ew;
        if (Num(bag, "EchoWidth") is decimal wd) cfg.EchoWidth = (ushort)wd;
        if (Num(bag, "Gain") is decimal g) cfg.Gain = g;
        if (Num(bag, "MeasMode") is decimal mm) cfg.MeasMode = (ushort)mm;
        if (Num(bag, "Threshold") is decimal th) cfg.Threshold = (short)th;
        if (Num(bag, "RefTime") is decimal rt) cfg.RefTime = rt;
        if (Num(bag, "RefTemp") is decimal rtp) cfg.RefTemp = rtp;
        // 和厂家 iFastApp 同一套：0 不温补 / 1 材料系数 / 2 自定义。farm.json tcMode，默认 1（厂家项目开着温补）。
        cfg.TcMode = _opt.TcMode;
        if (_opt.TempCoefficient is { } tcoef) cfg.TempCoefficient = tcoef;
        // 温度借用（厂家 UseTempChannel）：这一路的温度用另一路的。被借的那一路也得在轮询里，不然借不到
        if (_opt.TempFrom.TryGetValue(ch, out var src) && src > 0 && src != ch) cfg.UseTempChannel = src;
        // 自动法锚点：只在设备上的参考声时和存锚点时一致才用，否则让 SDK 自己重新捕获
        if (_opt.Anchors?.Get(ch, cfg.RefTime) is { } anchor)
        {
            cfg.RefEnvTime = anchor.EnvNs;
            cfg.RefPeriodNs = anchor.PeriodNs;
        }

        if (Num(bag, "Duty") is decimal du) cfg.PulseConfig.Duty = (byte)du;
        if (Num(bag, "Forward") is decimal fw) cfg.PulseConfig.Forward = (byte)fw;
        if (Num(bag, "Reverse") is decimal rv) cfg.PulseConfig.Reverse = (byte)rv;
        lock (_waves)
        {
            if (_waves.TryGetValue(ch, out var w)) cfg.LEchoWave = w;
        }

        return cfg;
    }

    private static decimal GetParam(ChannelConfig cfg, string name) => name switch
    {
        "ProbeFreq" => cfg.ProbeFreq,
        "EchoWin" => cfg.EchoWin,
        "EchoWidth" => cfg.EchoWidth,
        "Gain" => cfg.Gain,
        "Duty" => cfg.PulseConfig.Duty,
        "Forward" => cfg.PulseConfig.Forward,
        "Reverse" => cfg.PulseConfig.Reverse,
        "MeasMode" => cfg.MeasMode,
        "Threshold" => cfg.Threshold,
        "RefTime" => cfg.RefTime ?? 0,
        "RefTemp" => cfg.RefTemp ?? 0,
        _ => 0,
    };

    private static void ApplyParam(ChannelConfig cfg, string name, decimal value)
    {
        switch (name)
        {
            case "ProbeFreq": cfg.ProbeFreq = value; break;
            case "EchoWin": cfg.EchoWin = value; break;
            case "EchoWidth": cfg.EchoWidth = (ushort)value; break;
            case "Gain": cfg.Gain = value; break;
            case "Duty": cfg.PulseConfig.Duty = (byte)value; break;
            case "Forward": cfg.PulseConfig.Forward = (byte)value; break;
            case "Reverse": cfg.PulseConfig.Reverse = (byte)value; break;
            case "MeasMode": cfg.MeasMode = (ushort)value; break;
            case "Threshold": cfg.Threshold = (short)value; break;
            case "RefTime": cfg.RefTime = value; break;
            case "RefTemp": cfg.RefTemp = value; break;
            default: break;
        }
    }

    public static string Raw(Dictionary<string, string> bag, string name)
    {
        if (!bag.TryGetValue(name, out var v) || v.Length == 0) return "-";
        return ParamCatalog.Sentinels.TryGetValue(name, out var s) && v == s ? "未写入" : v;
    }

    private static string WaveCell(Dictionary<string, string> bag)
    {
        var start = Raw(bag, "WaveStart");
        return start == "未写入" ? "未写入" : $"{Raw(bag, "WavePts")}点@{start}ns";
    }

    public static decimal? Num(Dictionary<string, string> bag, string name)
    {
        var v = Raw(bag, name);
        return v == "-" || v == "未写入" ? null
            : decimal.TryParse(v, out var d) ? d : null;
    }

    /// <summary>声时差。tcMode=0：回波声时 − 参考声时；>0：先把回波声时温补到参考温度再减，和 SDK CalcDeltaTof / 厂家同一条。</summary>
    public static decimal? DeltaTof(decimal? echoTime, decimal? refTime) =>
        echoTime is { } e && refTime is { } r ? e - r : null;

    /// <summary>
    /// 温补用的温度：原始温度（和置零写下的参考温度是同一种数）；超出 SDK 有效范围（Pt1000 开路/短路）时用 SDK 越界保护后的业务温度。
    /// </summary>
    public static decimal? CompTemp(ChannelResult? m)
    {
        if (m is null) return null;
        return m.RawTemp is { } raw && raw >= FxUtility.MinTemp && raw <= FxUtility.MaxTemp ? raw : m.SpTemp;
    }

    public static decimal? DeltaTof(
        decimal? echoTime, decimal? refTime, decimal? temp, decimal? refTemp,
        ushort tcMode, decimal? tempCoefficient = null)
    {
        if (echoTime is not { } e || refTime is not { } r) return null;
        if (tcMode == 0) return e - r;
        var tRef = refTemp ?? CalculateExtensions.StandardTemperature;
        var tNow = temp ?? tRef;
        var tc = tempCoefficient ?? CalculateExtensions.DefaultTempCoeff;
        return CalculateExtensions.CalcDeltaTof(r, tRef, e, tNow, tcMode, tc);
    }

    public static decimal? DeltaTof(Dictionary<string, string> bag, decimal? echoTime) =>
        DeltaTof(echoTime, Num(bag, "RefTime"));

    public static decimal? DeltaTof(
        Dictionary<string, string> bag, decimal? echoTime, decimal? temp,
        ushort tcMode, decimal? tempCoefficient = null) =>
        DeltaTof(echoTime, Num(bag, "RefTime"), temp, Num(bag, "RefTemp"), tcMode, tempCoefficient);

    public static string Fmt(decimal v) => v.ToString("0.####");

    public static bool SameNum(string a, string b)
    {
        if (decimal.TryParse(a, out var x)
            && decimal.TryParse(b, out var y))
        {
            return x == y;
        }

        return a == b;
    }

    public static (WorkMode Mode, ushort Channel) ParseResume(string spec)
    {
        var s = (spec ?? string.Empty).Trim().ToLowerInvariant();
        return s == "multi" || s == "monitor" ? (WorkMode.MultiMeas, (ushort)0) : (WorkMode.Standby, (ushort)0);
    }

    private static DeviceConfig BuildDevice(DeviceOptions opt) => new()
    {
        Model = 3,
        CommunicationType = 1,
        CommunicationParameters = opt.Endpoint,
        ConnTimeOut = 20,
        TransferMode = 1,
        Address = opt.Address,
        UsChannelCount = 48,
        MaxTerminal = 1,
        TerminalAddresses = new[] { opt.Address },
        MeasConfig = new MeasConfig { Frequency = 10, SampleDepth = 4 },
        MCRConfig = new MCRConfig { DailyCount = 0, SingleCount = 30 },
    };

    private void Log(string msg) => OnLog?.Invoke($"{DateTime.Now:HH:mm:ss} {msg}");
}
