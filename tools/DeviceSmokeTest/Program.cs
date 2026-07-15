using BoltPreload.Fx;
using BoltPreload.Fx.Models;
using Microsoft.Extensions.Logging;

// 真机连通性冒烟测试：连接 -> 等 Connected(=会话已 provision) -> MultiMeas -> 读实时结果。
// 不依赖 Postgres/JWT/License，直接走 FxSdkConnector，用于验证"设备接入"是否真的通、
// 以及会话锁/ provisioning / EchoTime 计算在真机上的行为。
//
// 用法: dotnet run --project tools/DeviceSmokeTest -- [commParams] [durationSec] [developerMode]
//   例: dotnet run --project tools/DeviceSmokeTest -- 192.168.0.7:23 20 false

var commParams = args.Length > 0 ? args[0] : "192.168.0.7:23";
var durationSec = args.Length > 1 && int.TryParse(args[1], out var d) ? d : 20;
var devMode = args.Length > 2 && bool.TryParse(args[2], out var dm) && dm;
// arg[3]：可选，逗号分隔的启用通道 → 连接后先 ConfigureMultiChannelPolling(下发 MCR)，再 MultiMeas
var enabledChannels = (args.Length > 3 ? args[3] : "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(s => ushort.TryParse(s, out var c) ? c : (ushort)0)
    .Where(c => c > 0)
    .Distinct()
    .OrderBy(c => c)
    .ToArray();
// arg[4]：探测模式 multi(默认,MultiMeas 挂起) | single(SingleMeas 直发,循环) | passive(不下发,纯监听)
var mode = (args.Length > 4 ? args[4] : "multi").ToLowerInvariant();

// 打开厂家 SDK 的 NLog 帧级日志（send/receive/连接事件），devMode=true 时 SDK 会记原始帧十六进制
var nlogCfg = new NLog.Config.LoggingConfiguration();
nlogCfg.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal,
    new NLog.Targets.ConsoleTarget("c") { Layout = "${time} ${level:uppercase=true} [${logger:shortName=true}] ${message} ${exception:format=Message}" });
NLog.LogManager.Configuration = nlogCfg;

using var loggerFactory = LoggerFactory.Create(b => b
    .SetMinimumLevel(LogLevel.Debug)
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; }));
var logger = loggerFactory.CreateLogger<FxSdkConnector>();

FxConnectorFactory.DeveloperMode = devMode; // true 时打印 SDK 协议帧，排查用

// 设备规格与初始通道会话对齐 appsettings.json 的 Fx:Real（无 DB 时的等价配置）
var spec = new FxRealDeviceSpec(
    DeviceId: 1, Model: 3, CommType: 1, CommParams: commParams,
    Address: 1, TransferMode: 1, ChannelCount: 12);

// 会话内为每个目标通道建 ChannelConfig（ProbeFreq>0 是 SDK 依波形算 EchoTime 的前提）
var chList = enabledChannels.Length > 0 ? enabledChannels : new ushort[] { 9 };
var channels = chList.Select(ch => new FxChannelInit
{
    Address = 1, Channel = ch, ProbeFreq = 2m, Gain = 16m, MeasMode = 0, Threshold = -490, RefTime = 32142m, RefTemp = 16.71m, TcMode = 1
}).ToList();

Console.WriteLine($"[smoke] connect {commParams} model=3 addr=1 -> MultiMeas, read {durationSec}s (devMode={devMode})");

await using var connector = FxConnectorFactory.CreateReal(spec, channels, logger);

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec + 20));
var perChannel = new SortedDictionary<ushort, int>();
int total = 0, withEcho = 0, badStatus = 0;

var consumer = Task.Run(async () =>
{
    void Handle(FxChannelResultDto r)
    {
        total++;
        if (r.EchoTimeNs != null) withEcho++;
        if (r.StatusCode != 0) badStatus++;
        perChannel[r.Channel] = perChannel.TryGetValue(r.Channel, out var c) ? c + 1 : 1;
        Console.WriteLine(
            $"[data] ch{r.Channel} status={r.StatusCode} echo={(r.EchoTimeNs?.ToString() ?? "null")}ns " +
            $"temp={(r.SpTempC?.ToString() ?? "null")}C dTof={(r.DeltaTofNs?.ToString() ?? "null")}ns " +
            $"wave={r.EchoWave?.WaveData?.Length ?? 0}pts gain={r.Gain}");
    }
    try
    {
        await foreach (var env in connector.Results.ReadAllAsync(cts.Token))
        {
            switch (env.ResultType)
            {
                case FxResultType.ChannelResult when env.ChannelResult is not null:
                    Handle(env.ChannelResult); break;
                case FxResultType.MultiChannelResult when env.MultiChannelResult is not null:
                    foreach (var kv in env.MultiChannelResult.Channels) Handle(kv.Value); break;
                case FxResultType.DeviceParameter when env.DeviceParameter is not null:
                    Console.WriteLine($"[param] {env.DeviceParameter.ParamName}={env.DeviceParameter.ParamValue} (ch={env.DeviceParameter.Channel?.ToString() ?? "-"})"); break;
                default:
                    Console.WriteLine($"[env] {env.ResultType}"); break;
            }
        }
    }
    catch (OperationCanceledException) { /* expected on stop */ }
}, cts.Token);

try
{
    await connector.ConnectAsync(cts.Token);

    // 等待 State=Connected：这一步专门验证 OnSdkConnectedAsync 的会话 provisioning 完成（锁内）
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (connector.State != FxConnectorState.Connected && sw.Elapsed < TimeSpan.FromSeconds(12))
        await Task.Delay(150, cts.Token);
    Console.WriteLine($"[smoke] state={connector.State} (provision {(connector.State == FxConnectorState.Connected ? "OK" : "NOT confirmed")}) after {sw.ElapsedMilliseconds}ms");

    foreach (var s in connector.GetSessionChannelConfigs())
        Console.WriteLine($"[session] ch{s.Channel} probe={s.ProbeFreqMhz}MHz mode={s.MeasMode} thr={s.Threshold} refTime={s.RefTimeNs} refTemp={s.RefTempC}");

    var singleCh = enabledChannels.Length > 0 ? enabledChannels[0] : (ushort)9;

    if (mode == "passive")
    {
        Console.WriteLine($"[smoke] PASSIVE：不下发任何指令，仅监听设备是否主动上报/签到，{durationSec}s ...");
        await Task.Delay(TimeSpan.FromSeconds(durationSec), cts.Token);
    }
    else if (mode == "single")
    {
        // model=3 下 SingleMeas 是【直发】(0x02)，不进挂起队列 —— 用它探测设备能否当场响应一次测量
        Console.WriteLine($"[smoke] SINGLE：每 800ms 直发 SingleMeas(ch{singleCh})，{durationSec}s ...");
        var loopSw = System.Diagnostics.Stopwatch.StartNew();
        var shots = 0;
        while (loopSw.Elapsed < TimeSpan.FromSeconds(durationSec) && !cts.IsCancellationRequested
               && connector.State == FxConnectorState.Connected)
        {
            try { await connector.SetWorkModeAsync(FxWorkMode.SingleMeas, channel: singleCh, address: 1, cts.Token); shots++; }
            catch (Exception ex) { Console.WriteLine($"[smoke] SingleMeas 下发异常: {ex.Message}"); break; }
            await Task.Delay(800, cts.Token);
        }
        Console.WriteLine($"[smoke] SINGLE 结束，共直发 {shots} 次 SingleMeas，finalState={connector.State}");
    }
    else // multi
    {
        if (enabledChannels.Length > 0)
        {
            await connector.ConfigureMultiChannelPollingAsync(enabledChannels, cancellationToken: cts.Token);
            Console.WriteLine($"[smoke] MCR polling armed channels=[{string.Join(",", enabledChannels)}] (wrote to device)");
        }

        await connector.SetWorkModeAsync(FxWorkMode.MultiMeas, channel: 0, address: 1, cts.Token);
        Console.WriteLine($"[smoke] WorkMode->MultiMeas ok, collecting {durationSec}s ...");

        await Task.Delay(TimeSpan.FromSeconds(durationSec), cts.Token);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[smoke] ERROR: {ex.GetType().Name}: {ex.Message}");
}
finally
{
    cts.Cancel();
    try { await consumer; } catch { }
    try { await connector.DisconnectAsync(); } catch { }
}

Console.WriteLine(
    $"[smoke] DONE total={total} withEcho={withEcho} badStatus={badStatus} " +
    $"channels=[{string.Join(", ", perChannel.Select(k => $"ch{k.Key}:{k.Value}"))}] " +
    $"finalState={connector.State} droppedByBackpressure={connector.DroppedCount}");
Console.WriteLine(total == 0
    ? "[smoke] 结论: 连接侧已建立，但 MultiMeas 期间【未收到任何测量】。可能：设备未配置轮询通道(MCR)/未处于监测态/通道未接探头。"
    : withEcho == 0
        ? "[smoke] 结论: 收到测量但 EchoTime 全为 null。通常是会话内该通道 ChannelConfig(ProbeFreq) 缺失或设备未锁到回波。"
        : "[smoke] 结论: 链路 OK — 已连接、会话已 provision、收到带 EchoTime 的实时测量。");
