using System.Threading.Channels;
using BoltPreload.Fx.Models;
using Microsoft.Extensions.Logging;

namespace BoltPreload.Fx.Simulator;

/// <summary>
/// 进程内设备模拟器（Phase 1 用，不依赖真机）。
/// 按配置的采样率、通道数生成假 <see cref="FxChannelResultDto"/>。
/// </summary>
/// <remarks>
/// <para>Phase 1 第一周的关键里程碑就靠这个：模拟器 → IFxConnector → 入库 → API 查询。</para>
/// <para>Phase 2 接真机时，业务层换实现就行，不用动其他代码。</para>
/// <para>注意：本模拟器**独立于 SDK 实现**，按方案 §9.16 SDK 供应商风险，
/// 万一 SDK 失联，我们至少能用模拟器跑回归测试，保证基本可用性。</para>
/// </remarks>
public sealed class SimulatedFxConnector : IFxConnector
{
    private readonly SimulatorOptions _options;
    private readonly ILogger<SimulatedFxConnector> _logger;
    private readonly Channel<FxDecodedEnvelope> _channel;
    private readonly object _connectLock = new();

    // 每次 ConnectAsync 都新建 CTS。早期版本复用同一个 _internalCts → Disconnect 后再 Connect，
    // 新 Task.Run 一启动就 OperationCanceled 立即停掉，模拟器再也不出数据。
    private CancellationTokenSource? _internalCts;
    private Task? _samplingLoop;
    private long _dropped;  // H3：背压丢弃计数

    // 探头热插拔模拟：不在字典 = 在位（默认全部插着）。拔出的通道仍随轮询上报，
    // 但 EchoTime=null / StatusCode=-1（信号过小）——与真机"口上没探头"的表现一致。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, bool> _probePresent = new();

    public long DeviceId => _options.DeviceId;
    public FxConnectorState State { get; private set; } = FxConnectorState.Idle;
    public ChannelReader<FxDecodedEnvelope> Results => _channel.Reader;
    public long DroppedCount => Interlocked.Read(ref _dropped);
    public Func<FxDecodedEnvelope, ValueTask>? OverflowSink { get; set; }

    public SimulatedFxConnector(SimulatorOptions options, ILogger<SimulatedFxConnector> logger)
    {
        _options = options;
        _logger = logger;
        // 容量与真机 FxSdkConnector 对齐（8192）。FullMode=Wait 让 TryWrite 满时可检测（H3 背压可观测）。
        _channel = System.Threading.Channels.Channel.CreateBounded<FxDecodedEnvelope>(
            new BoundedChannelOptions(capacity: 8192)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_connectLock)
        {
            if (State == FxConnectorState.Connected)
                return Task.CompletedTask;

            State = FxConnectorState.Connecting;
            _logger.LogInformation("模拟器开始连接 device={DeviceId}, channels={Channels}, rate={Hz}Hz",
                _options.DeviceId, _options.ChannelCount, _options.SamplingRateHz);

            _internalCts = new CancellationTokenSource();
            State = FxConnectorState.Connected;
            // Task.Run 调度令牌用 None：scheduling 取消和"循环内部 ct 取消"概念不同；
            // 真正的取消信号通过 _internalCts.Token 透传给 RunSamplingLoopAsync。
            _samplingLoop = Task.Run(() => RunSamplingLoopAsync(_internalCts.Token), CancellationToken.None);
            return Task.CompletedTask;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_connectLock)
        {
            if (State == FxConnectorState.Disconnected)
                return;
            _logger.LogInformation("模拟器断开 device={DeviceId}", _options.DeviceId);
            State = FxConnectorState.Disconnected;
            cts = _internalCts;
            loop = _samplingLoop;
            _internalCts = null;
            _samplingLoop = null;
        }
        if (cts is not null)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }
        if (loop is not null)
        {
            try { await loop; }
            catch (OperationCanceledException) { /* 预期 */ }
        }
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("模拟器 Reconnect device={DeviceId}", _options.DeviceId);
        await DisconnectAsync(cancellationToken);
        await ConnectAsync(cancellationToken);
    }

    /// <summary>
    /// 模拟现场插/拔某通道的探头（测试/演示用）。拔出后该通道上报无回波，插回恢复正常测量。
    /// </summary>
    public void SetProbePresent(ushort channel, bool present)
    {
        _probePresent[channel] = present;
        _logger.LogInformation("模拟器 探头{Action} device={DeviceId} ch={Channel}",
            present ? "插入" : "拔出", _options.DeviceId, channel);
    }

    public Task SetWorkModeAsync(FxWorkMode mode, ushort channel = 0, ushort address = 1, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("模拟器 SetWorkMode={Mode} channel={Channel}", mode, channel);
        return Task.CompletedTask;
    }

    public Task ReadDeviceParamAsync(string propertyName, ushort address = 1, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("模拟器 ReadDeviceParam {Property}", propertyName);
        return Task.CompletedTask;
    }

    public Task ReadChannelParamAsync(string propertyName, ushort channel = 1, ushort address = 1, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("模拟器 ReadChannelParam {Property} ch={Channel}", propertyName, channel);
        return Task.CompletedTask;
    }

    public Task ConfigureMultiChannelPollingAsync(
        IReadOnlyCollection<ushort> enabledChannels,
        ushort? singleCount = null,
        ushort? dailyCount = null,
        uint? startSeconds = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("模拟器 ConfigureMultiChannelPolling channels={Channels}",
            string.Join(",", enabledChannels.OrderBy(x => x)));
        return Task.CompletedTask;
    }

    public IReadOnlyList<FxSessionChannelConfigSnapshot> GetSessionChannelConfigs()
        => Enumerable.Range(1, _options.ChannelCount)
            .Select(i => new FxSessionChannelConfigSnapshot(
                _options.Address,
                (ushort)i,
                2m,
                10m,
                2,
                0,
                null,
                null,
                1,
                null))
            .ToArray();

    public Task ConfigureChannelAsync(ushort channel, ChannelConfigUpdate updates, ushort address = 1, CancellationToken cancellationToken = default)
    {
        // 模拟器不存配置，只打日志（Phase 3 可以让模拟器按配置改基线值）
        _logger.LogDebug("模拟器 ConfigureChannel ch={Channel} updates={Updates}", channel, updates);
        return Task.CompletedTask;
    }

    public Task<RecalibrateResult> RecalibrateChannelAsync(
        ushort channel, ushort address = 1,
        decimal? explicitRefTimeNs = null, decimal? explicitRefTempC = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("模拟器 Recalibrate ch={Channel}（无实际效果）", channel);
        return Task.FromResult(new RecalibrateResult(
            Channel: channel,
            Address: address,
            NewRefTimeNs: explicitRefTimeNs ?? 32200m,
            NewRefTempC: explicitRefTempC ?? 23.0m,
            EchoWaveBytes: 500,
            Timestamp: DateTimeOffset.UtcNow));
    }

    private async Task RunSamplingLoopAsync(CancellationToken ct)
    {
        var random = new Random((int)_options.DeviceId);
        var periodMs = (int)Math.Max(1, 1000.0 / _options.SamplingRateHz);
        var periodDelay = TimeSpan.FromMilliseconds(periodMs);
        var loopStart = DateTimeOffset.UtcNow;

        // 未绑定螺栓的通道：给一个独立"基准声时"，按 raw 处理（不参与预紧力）
        var baseTofPerChannel = new Dictionary<ushort, double>();
        for (ushort ch = 1; ch <= _options.ChannelCount; ch++)
            baseTofPerChannel[ch] = 27000 + ch * 100;

        var period = 0L;
        while (!ct.IsCancellationRequested)
        {
            // 每 10 个周期，所有通道各上传一帧 EchoWave（模拟 FullWaveRead 间隔上传）。
            // 注意：不能用跨通道的全局 tick%10，否则多通道时部分通道永远轮不到上传波形。
            var emitWave = period % 10 == 0;
            for (ushort ch = 1; ch <= _options.ChannelCount; ch++)
            {
                if (ct.IsCancellationRequested) break;

                var now = DateTimeOffset.UtcNow;

                // 探头被"拔出"的通道：照常上报（设备仍轮询该口）但无回波，模拟空口/探头脱落
                if (_probePresent.TryGetValue(ch, out var present) && !present)
                {
                    var noEcho = new FxChannelResultDto(
                        Address: _options.Address, Channel: ch, StatusCode: -1,
                        EchoTimeNs: null, EchoTime2Ns: null,
                        SpTempC: (decimal)Math.Round(22.0 + (random.NextDouble() - 0.5), 2),
                        DeltaTofNs: null, Gain: 10f, EchoWave: null,
                        LowAi: null, HighAi: null, Angle: null, MidResults: null);
                    BackpressureWriter.WriteWithBackpressure(_channel, new FxDecodedEnvelope(
                        DeviceId: _options.DeviceId, ReceiveTime: now, IngestedAt: now,
                        ResultType: FxResultType.ChannelResult, ChannelResult: noEcho), ref _dropped, OverflowSink);
                    continue;
                }

                var baseTof = baseTofPerChannel[ch];

                // 模拟温度小幅波动 22 ± 0.5°C
                var temp = 22.0 + (random.NextDouble() - 0.5);

                // 模拟声时噪声：用 Box-Muller 出近似高斯，标准差 3 ns（贴近真机典型 3-5ns）。
                // 历史版本只有 ±1ns 均匀噪声 → 算法在过净数据上容易"过拟合"，到真机直接翻车。
                var u1 = 1.0 - random.NextDouble();
                var u2 = 1.0 - random.NextDouble();
                var gauss = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                var noise = gauss * 3.0;

                // 绑定螺栓的通道：按物理把"真实预紧力"编码进 EchoTime（echo = (RefTof + K_L·F)·tempFactor + 噪声）。
                // 采集链路用匹配标定把 F 算回来 → UI 是真实计算值。未绑定通道维持原始 baseTof，走 raw。
                var phys = SimulatedBoltPhysics.For(ch);
                decimal echoTime;
                if (phys is not null)
                {
                    var preload = SimulatedBoltPhysics.CurrentPreloadN(phys, (now - loopStart).TotalHours);
                    var tempFactor = 1m + phys.TempCoeff * ((decimal)Math.Round(temp, 2) - phys.RefTempC);
                    echoTime = Math.Round((phys.RefTofNs + phys.Kl * preload) * tempFactor + (decimal)noise, 2);
                }
                else
                {
                    echoTime = (decimal)Math.Round(baseTof + noise, 2);
                }

                FxEchoWaveDto? echoWave = emitWave ? GenerateFakeWave(random, (double)echoTime) : null;

                var result = new FxChannelResultDto(
                    Address: _options.Address,
                    Channel: ch,
                    StatusCode: 0,
                    EchoTimeNs: echoTime,
                    EchoTime2Ns: null,
                    SpTempC: (decimal)Math.Round(temp, 2),
                    DeltaTofNs: null,
                    Gain: 10f,
                    EchoWave: echoWave,
                    LowAi: null,
                    HighAi: null,
                    Angle: null,
                    MidResults: null);

                var envelope = new FxDecodedEnvelope(
                    DeviceId: _options.DeviceId,
                    ReceiveTime: now,
                    IngestedAt: now,
                    ResultType: FxResultType.ChannelResult,
                    ChannelResult: result);

                // H3：背压可观测 + 丢弃前兜底（与真机 connector 一致），满时丢最旧并计数。
                BackpressureWriter.WriteWithBackpressure(_channel, envelope, ref _dropped, OverflowSink);
            }

            period++;
            try { await Task.Delay(periodDelay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

    /// <summary>
    /// 生成假波形（256 点）：基底噪声 + 两个高斯包络峰，模拟真实回波。
    /// 主回波峰(index 128)对齐到本次 <paramref name="echoCenterNs"/>，使 A-Scan 上
    /// 检出点/RefTime/ΔTOF 与真实声时物理一致（EchoStart = echo − 128×ClockInt）。
    /// </summary>
    private static FxEchoWaveDto GenerateFakeWave(Random rng, double echoCenterNs)
    {
        const int pts = 256;
        const int clockInt = 25;
        var data = new short[pts];
        // 两个回波峰位置（基于 TOF 的"虚拟"采样位置）；peak1 = 主回波 → 对齐 echoCenterNs
        var peak1 = pts * 0.5;
        var peak2 = pts * 0.7;
        for (int i = 0; i < pts; i++)
        {
            var noise = (rng.NextDouble() - 0.5) * 30;
            var peak1Val = 500 * Math.Exp(-Math.Pow(i - peak1, 2) / 50.0) * Math.Sin((i - peak1) * 0.5);
            var peak2Val = 300 * Math.Exp(-Math.Pow(i - peak2, 2) / 60.0) * Math.Sin((i - peak2) * 0.5);
            data[i] = (short)Math.Clamp(noise + peak1Val + peak2Val, -32000, 32000);
        }
        return new FxEchoWaveDto(
            Fc: 0,
            EchoStart: (uint)Math.Max(0, echoCenterNs - peak1 * clockInt),
            ClockInt: clockInt,
            WaveData: data,
            Threshold: -250);
    }
}

public sealed record SimulatorOptions(
    long DeviceId,
    ushort Address = 1,
    ushort ChannelCount = 3,
    double SamplingRateHz = 50);
