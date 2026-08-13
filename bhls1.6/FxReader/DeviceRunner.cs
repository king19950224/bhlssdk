using System.Collections.Concurrent;
using FxSDK;
using FxSDK.Models;

namespace FxReader;

sealed class DeviceRunner : IDisposable
{
    // 发太密会抢掉设备的测量上报
    private static readonly TimeSpan ReadGap = TimeSpan.FromMilliseconds(400);

    // RefTime 读回来的是一组：测量方法、阈值、参考声时、参考温度、参考波形
    private static readonly string[] ChannelParamNames =
    {
        nameof(ChannelConfig.ProbeFreq),
        nameof(ChannelConfig.EchoWin),
        nameof(ChannelConfig.EchoWidth),
        nameof(ChannelConfig.PulseConfig),
        nameof(ChannelConfig.Gain),
        nameof(ChannelConfig.RefTime)
    };

    // 事件不带会话标识，所以一台设备一个 FxClient
    private readonly FxClient _client = new();
    private readonly DeviceSettings _settings;
    private readonly AppSettings _app;
    private readonly ResultPrinter _printer;
    private readonly RawWriter? _raw;
    private readonly ZeroStore? _zeroStore;
    private readonly DeviceConfig _device;
    private readonly List<ChannelConfig> _channels;

    // 置零取几帧、允许的声时极差、等一帧扫波的上限
    private const int ZeroFrames = 5;
    private const decimal ZeroSpreadNs = 20M;
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(5);

    // 48 通道跑完一轮要十几秒，不看满这个时长不敢断定设备只测一个通道
    private static readonly TimeSpan ObserveWindow = TimeSpan.FromSeconds(30);

    private const decimal AgcTargetPeak = 350M;
    private static readonly double[] AgcGainRange = { 1, 47 };

    // 同一台设备的待机、配置、恢复必须串起来发，不能两拨命令同时下去
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    private readonly object _captureGate = new();
    private ushort _captureAddress;
    private ushort _captureChannel;
    private TaskCompletionSource<ChannelResult>? _capture;

    // 只读连上时不知道设备处在哪个模式，用见过的通道反推
    private readonly ConcurrentDictionary<ushort, byte> _seenChannels = new();
    private WorkMode? _setMode;

    private TaskCompletionSource<SessionCloseReason>? _closed;
    private volatile string _state = "未连接";
    private volatile string? _lastError;
    private long _frames;
    private long _invalid;
    private DateTime _connectedAt;
    private DateTime _firstDataAt;
    private DateTime _lastDataAt;

    public DeviceRunner(
        DeviceSettings settings,
        AppSettings app,
        ResultPrinter printer,
        RawWriter? raw,
        ZeroStore? zeroStore)
    {
        _settings = settings;
        _app = app;
        _printer = printer;
        _raw = raw;
        _zeroStore = zeroStore;
        _device = settings.BuildDevice();
        _channels = settings.BuildChannels();

        var restored = zeroStore?.Apply(settings.Endpoint, _channels) ?? 0;
        if (restored > 0)
        {
            Log($"回灌 {restored} 个通道的参考参数");
        }

        _client.OnConnected += (_, _) =>
        {
            _connectedAt = DateTime.Now;
            _lastError = null;
            Log("TCP 已连接");
        };

        _client.OnDisconnected += (_, e) =>
        {
            _state = "已断开";
            _lastError = Describe(e.Reason) + (e.Exception is null ? string.Empty : "：" + e.Exception.Message);
            _closed?.TrySetResult(e.Reason);
        };

        _client.OnResultReceived += (_, e) => Ingest(e.Results);
    }

    public string Title => _settings.Title;

    public int ChannelCount => _channels.Count;

    public bool Online => _client.IsDeviceConnected(_device);

    public string StatusLine()
    {
        if (!Online)
        {
            return string.IsNullOrEmpty(_lastError) ? $"{Title}  {_state}" : $"{Title}  {_state}（{_lastError}）";
        }

        var idle = _lastDataAt == default ? "还没有数据" : $"{(DateTime.Now - _lastDataAt).TotalSeconds:0.0}s 前";
        return $"{Title}  {_state}  在线 {DateTime.Now - _connectedAt:hh\\:mm\\:ss}  " +
               $"帧 {Interlocked.Read(ref _frames)}  无效 {Interlocked.Read(ref _invalid)}  最后数据 {idle}";
    }

    public async Task RunAsync(CancellationToken token)
    {
        var retry = TimeSpan.FromSeconds(Math.Max(1, _app.ReconnectSeconds));

        while (!token.IsCancellationRequested)
        {
            var closed = new TaskCompletionSource<SessionCloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
            _closed = closed;

            try
            {
                await _commandLock.WaitAsync(token);
                try
                {
                    await StartAsync(token);
                }
                finally
                {
                    _commandLock.Release();
                }

                var reason = await closed.Task.WaitAsync(token);
                Log($"连接断开：{Describe(reason)}");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _state = "连接失败";
                _lastError = ex.Message;
                Log($"启动失败：{ex.Message}");
                await SafeCloseAsync();
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            Log($"{retry.TotalSeconds:0} 秒后重连");

            try
            {
                await Task.Delay(retry, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await SafeCloseAsync();
        _state = "已停止";
    }

    private async Task StartAsync(CancellationToken token)
    {
        _state = "连接中";
        Log($"连接 {_settings.Endpoint}");

        await _client.ConnectAsync(_device, token);
        var sessionId = _client.GetSessionId(_device);
        _state = "配置中";

        // 协议编码要从会话取 DeviceConfig，缺了它工作模式和通道命令都会编成空指令
        await _client.ConfigureDeviceAsync(sessionId, _device, null, false);

        // 解码要用通道上下文，只读模式也要存
        foreach (var channel in _channels)
        {
            await _client.ConfigureChannelAsync(sessionId, channel, null, false);
        }

        if (!_settings.ApplyConfigOnConnect)
        {
            _state = "只读";
            Log("只读模式：不下发参数，等设备自己上报");
            return;
        }

        // 通道号 0 时协议层自己遍历全部终端地址
        await _client.SetDeviceModeAsync(sessionId, WorkMode.Standby, 0, _device.Address);
        await Task.Delay(200, token);

        foreach (var channel in _channels)
        {
            await _client.ConfigureChannelAsync(sessionId, channel, null, true);
        }

        await _client.ConfigureDeviceAsync(sessionId, _device, nameof(DeviceConfig.MeasConfig), true);
        await _client.ConfigureDeviceAsync(sessionId, _device, nameof(DeviceConfig.MCRConfig), true);

        await _client.SetDeviceModeAsync(sessionId, WorkMode.MultiMeas, 0, _device.Address);
        _setMode = WorkMode.MultiMeas;

        _state = "监测中";
        Log($"已进入多通道监测，通道 {_channels.Count} 个");
    }

    // 结果异步回到 OnResultReceived
    public async Task ReadParametersAsync(CancellationToken token)
    {
        if (!Online)
        {
            Log("没连上，读不了参数");
            return;
        }

        // 拿不到就是设备正被别的流程操作，这次直接放弃，不排队
        if (!await _commandLock.WaitAsync(TimeSpan.Zero, token))
        {
            Log("设备正忙，这次读参数跳过");
            return;
        }

        var sessionId = _client.GetSessionId(_device);
        var resume = ResolveResumeMode();

        try
        {
            // 采集中的通道不响应读命令，先停掉数据流
            Log($"设备待机，读 {_channels.Count} 个通道的参数");
            await _client.SetDeviceModeAsync(sessionId, WorkMode.Standby, 0, _device.Address);
            await Task.Delay(500, token);

            foreach (var channel in _channels)
            {
                foreach (var name in ChannelParamNames)
                {
                    await _client.ReadChannelParamAsync(sessionId, name, channel.Channel, channel.Address);
                    await Task.Delay(ReadGap, token);
                }
            }

            Log("参数读完");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"读取参数出错：{ex.Message}");
        }
        finally
        {
            // 出什么岔子都不能把设备丢在待机
            try
            {
                await _client.SetDeviceModeAsync(sessionId, resume.Mode, resume.Channel, _device.Address);
                Log(resume.Mode == WorkMode.SingleMeas
                    ? $"已恢复单通道测量，通道 {resume.Channel}"
                    : "已恢复多通道监测");
            }
            catch (Exception ex)
            {
                Log($"恢复模式失败：{ex.Message}");
            }

            _commandLock.Release();
        }
    }

    // 逐通道扫波取参考声时，写进设备并存盘，之后 SDK 才会算声时差
    public async Task ZeroAsync(CancellationToken token)
    {
        if (!Online)
        {
            Log("没连上，不能置零");
            return;
        }

        if (!await _commandLock.WaitAsync(TimeSpan.Zero, token))
        {
            Log("设备正忙，这次置零跳过");
            return;
        }

        var sessionId = _client.GetSessionId(_device);
        var resume = ResolveResumeMode();
        var zeroed = new List<ChannelConfig>();

        try
        {
            // 不先把整轮轮询停掉，单通道扫波命令会被上报流淹掉
            await _client.SetDeviceModeAsync(sessionId, WorkMode.Standby, 0, _device.Address);
            await Task.Delay(500, token);

            foreach (var channel in _channels)
            {
                var frame = await ScanAsync(sessionId, channel, token);
                if (frame?.EchoTime is null)
                {
                    continue;
                }

                channel.RefTime = frame.EchoTime;
                channel.RefTemp = frame.SpTemp ?? channel.RefTemp;
                channel.LEchoWave = frame.EchoWave;

                // 自动法还要记住包络锚点和载波周期，否则重启后会漂
                if (channel.MeasMode == 0 && frame.Assist?.Fx is not null && frame.EchoWave is not null)
                {
                    channel.RefEnvTime = frame.EchoWave.EchoStart + frame.Assist.Fx.Value * frame.EchoWave.ClockInt;
                    channel.RefPeriodNs = frame.Assist.PeriodNs;
                }

                await _client.ConfigureChannelAsync(sessionId, channel, nameof(ChannelConfig.RefTime), true);
                zeroed.Add(channel);

                Log($"通道{channel.Channel} 已置零，参考声时 {channel.RefTime:F1}ns，参考温度 {channel.RefTemp:F2}°C");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"置零出错：{ex.Message}");
        }
        finally
        {
            try
            {
                // 刚从扫波切回待机，不等一下设备来不及处理恢复命令，会卡在最后那个通道上。
                // 故意不吃取消令牌：就算用户按了退出，设备的模式也得先恢复回去，不然它停在扫波态
                await Task.Delay(500, CancellationToken.None);
                await _client.SetDeviceModeAsync(sessionId, resume.Mode, resume.Channel, _device.Address);
                Log(resume.Mode == WorkMode.SingleMeas
                    ? $"已恢复单通道测量，通道 {resume.Channel}"
                    : "已恢复多通道监测");
            }
            catch (Exception ex)
            {
                Log($"恢复模式失败：{ex.Message}");
            }

            _commandLock.Release();
        }

        if (zeroed.Count > 0 && _zeroStore is not null)
        {
            _zeroStore.Save(_settings.Endpoint, zeroed);
            Log($"{zeroed.Count} 个通道的参考参数已存盘");
        }
    }

    private async Task<ChannelResult?> ScanAsync(string sessionId, ChannelConfig channel, CancellationToken token)
    {
        await _client.ConfigureChannelAsync(sessionId, channel, null, true);
        await _client.SetDeviceModeAsync(sessionId, WorkMode.LWaveRead, channel.Channel, channel.Address);

        try
        {
            var frames = await CollectAsync(channel, token);

            // 信号不够先让 AGC 把增益推上去，再扫一次
            if (frames.Count < ZeroFrames)
            {
                var oldGain = channel.Gain;
                var agc = await _client.RunAgcControlAsync(
                    sessionId, channel, AgcTargetPeak, AgcGainRange, 1M, token);

                if (agc is null)
                {
                    return null;
                }

                Log($"通道{channel.Channel} 自动增益 {oldGain}→{agc.Gain}，{agc.Status}，迭代 {agc.FinalIterations} 次");

                if (agc.Gain == oldGain)
                {
                    Log($"通道{channel.Channel} 增益没调动，不置零");
                    return null;
                }

                frames = await CollectAsync(channel, token);
            }

            if (frames.Count < ZeroFrames)
            {
                Log($"通道{channel.Channel} 有效波形只有 {frames.Count} 帧，不置零");
                return null;
            }

            var times = frames.Select(x => x.EchoTime!.Value).ToList();
            var spread = times.Max() - times.Min();
            if (spread > ZeroSpreadNs)
            {
                Log($"通道{channel.Channel} 声时还在跳，{ZeroFrames} 帧极差 {spread:F1}ns，不置零");
                return null;
            }

            return frames[^1];
        }
        finally
        {
            await _client.SetDeviceModeAsync(sessionId, WorkMode.Standby, channel.Channel, channel.Address);
            // 同上，回待机后的缓冲不受取消影响——命令连着发设备会漏收
            await Task.Delay(300, CancellationToken.None);
        }
    }

    private async Task<List<ChannelResult>> CollectAsync(ChannelConfig channel, CancellationToken token)
    {
        var frames = new List<ChannelResult>();
        var misses = 0;

        // 多给几次机会，头几帧经常还没稳
        for (var i = 0; i < ZeroFrames * 4 && frames.Count < ZeroFrames; i++)
        {
            ChannelResult frame;
            try
            {
                frame = await NextFrameAsync(channel.Address, channel.Channel, token);
            }
            catch (TimeoutException)
            {
                if (++misses >= 2)
                {
                    break;
                }

                continue;
            }

            if (frame.StatusCode.IsSignalValid() && frame.EchoTime.HasValue)
            {
                frames.Add(frame);
            }
        }

        return frames;
    }

    private Task<ChannelResult> NextFrameAsync(ushort address, ushort channel, CancellationToken token)
    {
        var waiter = new TaskCompletionSource<ChannelResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_captureGate)
        {
            _captureAddress = address;
            _captureChannel = channel;
            _capture = waiter;
        }

        return waiter.Task.WaitAsync(ScanTimeout, token);
    }

    private (WorkMode Mode, ushort Channel) ResolveResumeMode()
    {
        if (_setMode.HasValue)
        {
            return (_setMode.Value, 0);
        }

        // 看得不够久就猜单通道，会把设备锁死在一个通道上，宁可恢复轮询
        var observed = _firstDataAt == default ? TimeSpan.Zero : DateTime.Now - _firstDataAt;
        var channels = _seenChannels.Keys.ToArray();

        return channels.Length == 1 && observed >= ObserveWindow
            ? (WorkMode.SingleMeas, channels[0])
            : (WorkMode.MultiMeas, (ushort)0);
    }

    private async Task SafeCloseAsync()
    {
        try
        {
            if (_client.IsDeviceConnected(_device))
            {
                await _client.CloseDeviceFastAsync(_device);
            }
        }
        catch (Exception ex)
        {
            Log($"关闭连接出错：{ex.Message}");
        }
    }

    private void Ingest(DecodedResult[] results)
    {
        foreach (var decoded in results)
        {
            switch (decoded)
            {
                case MultiChannelResult multi:
                    foreach (var channel in multi.Results.Values)
                    {
                        Take(channel);
                    }

                    break;

                case ChannelResult single:
                    Take(single);
                    break;

                case DeviceParameter parameter:
                    var channelText = parameter.Channel > 0 ? $"/通道{parameter.Channel}" : string.Empty;
                    Log($"参数 地址{parameter.Address}{channelText} " +
                        $"{parameter.ParamName}={FormatParamValue(parameter.ParamValue)}");
                    break;
            }
        }
    }

    private void Take(ChannelResult result)
    {
        Interlocked.Increment(ref _frames);
        if (result.StatusCode.IsSignalInvalid())
        {
            Interlocked.Increment(ref _invalid);
        }

        _lastDataAt = DateTime.Now;
        if (_firstDataAt == default)
        {
            _firstDataAt = _lastDataAt;
        }

        _seenChannels.TryAdd(result.Channel, 0);

        if (_capture is not null)
        {
            lock (_captureGate)
            {
                if (_capture is not null
                    && result.Address == _captureAddress
                    && result.Channel == _captureChannel)
                {
                    _capture.TrySetResult(result);
                    _capture = null;
                }
            }
        }

        var time = result.ReceiveTime == default ? DateTime.Now : result.ReceiveTime;
        var wave = result.EchoWave;

        _printer.Push(new Sample(
            Title,
            time,
            result.Address,
            result.Channel,
            result.EchoTime,
            result.DeltaTof,
            result.SpTemp,
            result.StatusCode,
            wave?.WaveData?.Count ?? 0,
            result.HexString));

        _raw?.Push(new RawFrame(
            Title,
            time,
            result.Address,
            result.Channel,
            result.StatusCode,
            result.EchoTime,
            result.DeltaTof,
            result.RawTemp,
            result.SpTemp,
            wave?.EchoStart ?? 0,
            wave?.ClockInt ?? 0,
            wave?.WaveData,
            result.HexString));
    }

    private static string FormatParamValue(object value)
    {
        return value switch
        {
            EchoWave wave =>
                $"{wave.WaveData?.Count ?? 0} 点，起点 {wave.EchoStart}ns，间隔 {wave.ClockInt}ns",
            PulseConfig pulse =>
                $"占空比{pulse.Duty} 正向{pulse.Forward} 反向{pulse.Reverse}",
            _ => value.ToString() ?? string.Empty
        };
    }

    private void Log(string text)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} [{Title}] {text}");
    }

    private static string Describe(SessionCloseReason reason)
    {
        return reason switch
        {
            SessionCloseReason.Normal => "主动关闭",
            SessionCloseReason.RemoteClosed => "对端关闭",
            SessionCloseReason.ConnectionLost => "网络中断",
            SessionCloseReason.HeartbeatTimeout => "心跳超时",
            SessionCloseReason.ProtocolError => "协议错误",
            _ => reason.ToString()
        };
    }

    // runner 跟进程一起存在，这里只是把命令锁按规矩收掉
    public void Dispose() => _commandLock.Dispose();
}