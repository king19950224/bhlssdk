using System.Globalization;
using FxSDK;
using FxSDK.Models;
using FxSDK.Utility;

// FxSDK 多通道采集示例（Model = 3 多通道监测版）
//
// 使用方法：
//   1. 修改下方 DeviceEndpoint 为设备 IP:Port（或串口参数）
//   2. dotnet run --project src/MultiChannelDemo -c Release
//   3. Ctrl+C 退出，会自动 StopAllAsync 关闭会话

const string DeviceEndpoint = "192.168.0.7:23";
const byte CommunicationType = 1;       // 0 串口  1 TCP Client  2 TCP Server
const ushort DeviceAddress = 1;
const decimal MeasFrequencyHz = 10M;
const ushort SampleDepth = 4;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
FxUtility.DeveloperMode = true;

Console.WriteLine("=== FxSDK 多通道采集示例 ===");
Console.WriteLine($"目标设备  : {DeviceEndpoint}");
Console.WriteLine($"通讯方式  : {(CommunicationType == 1 ? "TCP Client" : CommunicationType == 0 ? "Serial" : "TCP Server")}");
Console.WriteLine($"测量频率  : {MeasFrequencyHz} Hz, 采样深度: {SampleDepth}");
Console.WriteLine();

var client = new FxClient();
EchoWave? lastWave = null;

var syncTime = CalculateExtensions.GetSyncTimeBase2000(DateTime.Now);
var deviceConfig = new DeviceConfig
{
    Model = 3,
    CommunicationType = CommunicationType,
    CommunicationParameters = DeviceEndpoint,
    Address = DeviceAddress,
    TransferMode = 1,                   // 1 直连  0 LoraMesh
    MeasConfig = new MeasConfig
    {
        Frequency = MeasFrequencyHz,
        SampleDepth = SampleDepth,
        DaysSince2000 = (ushort)syncTime.Item1,
        SecondsToday = syncTime.Item2,
    },
};

client.OnConnected += async (sender, _) =>
{
    var session = sender as FxSDK.Core.IoSession;
    var sessionId = session?.SessionId ?? deviceConfig.GetSessionId();
    Console.WriteLine($"[Connected] 会话 {sessionId} 已连接，开始下发配置...");

    try
    {
        await client.ConfigureDeviceAsync(sessionId, deviceConfig, nameof(DeviceConfig.MeasConfig), true);

        await client.SetDeviceModeAsync(sessionId, WorkMode.Standby, 0, DeviceAddress);

        // 通道 1：82mm 螺栓，参考声时 33858.6 ns / 15.6 ℃
        await ConfigureChannel(client, sessionId,
            address: DeviceAddress, channel: 1, boltLengthMm: 82,
            gain: 10, threshold: 165,
            refTimeNs: 33858.6M, refTempC: 15.6M);

        // 演示「细粒度更新」：只把 Gain 一个属性单独发下去
        var ch1Update = new ChannelConfig { Address = DeviceAddress, Channel = 1, Gain = 1 };
        await client.ConfigureChannelAsync(sessionId, ch1Update, nameof(ChannelConfig.Gain), true);

        // 通道 2：80mm 螺栓，无温度补偿
        await ConfigureChannel(client, sessionId,
            address: DeviceAddress, channel: 2, boltLengthMm: 80,
            gain: 8, threshold: -223,
            refTimeNs: 32637M, refTempC: 15.6M,
            tcMode: 0);

        // 通道 3：82.5mm 螺栓，动态特征法
        await ConfigureChannel(client, sessionId,
            address: DeviceAddress, channel: 3, boltLengthMm: 82.5,
            gain: 2, threshold: 0,
            refTimeNs: 36812.8M, refTempC: 14.8M,
            tcMode: 0, measMode: 1);

        // 多通道轮询：通道 [1,2,3]，每通道采 1 次后切下一通道
        deviceConfig.MCRConfig = new MCRConfig
        {
            EnabledChannels = new List<ushort> { 1, 2, 3 },
            SingleCount = 1,
        };
        await client.ConfigureDeviceAsync(sessionId, deviceConfig, nameof(DeviceConfig.MCRConfig), true);

        // 读取设备参数（结果通过 OnResultReceived 异步返回）
        await client.ReadDeviceParamAsync(sessionId, nameof(DeviceConfig.Address), DeviceAddress);
        await client.ReadDeviceParamAsync(sessionId, DeviceConfig.BatVoltage, DeviceAddress);

        deviceConfig.NetCheckConfig = new NetCheckConfig { Enabled = false, CheckPeriod = 3600 };
        await client.ConfigureDeviceAsync(sessionId, deviceConfig, nameof(DeviceConfig.NetCheckConfig), true);
        await client.ReadDeviceParamAsync(sessionId, nameof(DeviceConfig.NetCheckConfig), DeviceAddress);

        // 读取通道参数
        foreach (var prop in new[]
                 {
                     nameof(ChannelConfig.ProbeFreq),
                     nameof(ChannelConfig.EchoWin),
                     nameof(ChannelConfig.EchoWidth),
                     nameof(ChannelConfig.Gain),
                     nameof(ChannelConfig.MeasMode),
                     nameof(ChannelConfig.Threshold),
                     nameof(ChannelConfig.PulseConfig),
                     nameof(ChannelConfig.RefTime),
                 })
        {
            await client.ReadChannelParamAsync(sessionId, prop, channel: 1, address: DeviceAddress);
        }

        // 启动多通道连续测量
        await client.SetDeviceModeAsync(sessionId, WorkMode.MultiMeas, 0, DeviceAddress);
        Console.WriteLine("[Connected] 多通道测量已启动，等待数据...");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Connected] 配置过程异常: {ex.Message}");
        Console.Error.WriteLine(ex.StackTrace);
    }
};

client.OnDisconnected += (_, args) =>
{
    Console.WriteLine($"[Disconnected] 原因={args.Reason}  异常={args.Exception?.Message}");
};

client.OnResultReceived += (_, args) =>
{
    foreach (var item in args.Results)
    {
        switch (item)
        {
            case ChannelResult cr:
                PrintChannelResult(cr);
                if (cr.EchoWave?.WaveData is { Count: > 0 })
                    lastWave = cr.EchoWave;
                break;
            case DeviceParameter dp:
                PrintDeviceParameter(dp);
                break;
            case MultiChannelResult mcr:
                Console.WriteLine($"[MultiCh] 一次轮询聚合 {mcr.Results.Count} 个通道结果");
                foreach (var kv in mcr.Results)
                    PrintChannelResult(kv.Value);
                break;
        }
    }
};

var shutdown = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n收到 Ctrl+C，正在关闭 SDK...");
    shutdown.TrySetResult();
};

try
{
    await client.ConnectAsync(deviceConfig);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[Connect] 连接失败: {ex.Message}");
    return 1;
}

await shutdown.Task;

try { await client.StopAllAsync(); }
catch (Exception ex) { Console.Error.WriteLine($"[StopAll] {ex.Message}"); }

Console.WriteLine("SDK 已关闭，退出。");
return 0;


static async Task ConfigureChannel(
    FxClient client,
    string sessionId,
    ushort address,
    ushort channel,
    double boltLengthMm,
    decimal gain,
    short threshold,
    decimal refTimeNs,
    decimal refTempC,
    ushort tcMode = 1,
    ushort measMode = 2)
{
    var echoWin = CalculateExtensions.CalcEchoWin(boltLengthMm);
    var cfg = new ChannelConfig
    {
        Address = address,
        Channel = channel,
        ProbeFreq = 2M,
        EchoWin = (decimal)echoWin,
        Gain = gain,
        Threshold = threshold,
        MeasMode = measMode,
        TcMode = tcMode,
        RefTime = refTimeNs,
        RefTemp = refTempC,
        PulseConfig = new PulseConfig { Duty = 50, Forward = 3, Reverse = 0 },
    };
    await client.ConfigureChannelAsync(sessionId, cfg, propertyName: null, sendCommand: true);
    Console.WriteLine(
        $"  ↪ 通道 {channel} 已配置: 螺栓 {boltLengthMm}mm, EchoWin={echoWin:F0}ns, " +
        $"Gain={gain}, Thr={threshold}, RefTime={refTimeNs}ns @ {refTempC}℃, " +
        $"TcMode={tcMode}, MeasMode={measMode}");
}

static void PrintChannelResult(ChannelResult r)
{
    var echo = r.EchoTime?.ToString("F1", CultureInfo.InvariantCulture) ?? "N/A";
    var delta = r.DeltaTof?.ToString("F1", CultureInfo.InvariantCulture) ?? "N/A";
    var temp = r.SpTemp?.ToString("F1", CultureInfo.InvariantCulture) ?? "N/A";
    var wavePts = r.EchoWave?.WaveData?.Count ?? 0;

    Console.WriteLine(
        $"[Data] {r.ReceiveTime:HH:mm:ss.fff} addr={r.Address} ch={r.Channel} " +
        $"status={r.StatusCode} echo={echo}ns ΔTOF={delta}ns temp={temp}℃ wavePts={wavePts}");
}

static void PrintDeviceParameter(DeviceParameter p)
{
    switch (p.ParamName)
    {
        case nameof(DeviceConfig.NetCheckConfig) when p.ParamValue is NetCheckConfig nc:
            Console.WriteLine($"[Param] {p.ReceiveTime:HH:mm:ss.fff} addr={p.Address} NetCheck enabled={nc.Enabled} period={nc.CheckPeriod}s");
            break;
        case nameof(ChannelConfig.PulseConfig) when p.ParamValue is PulseConfig pc:
            Console.WriteLine($"[Param] {p.ReceiveTime:HH:mm:ss.fff} addr={p.Address} ch={p.Channel} Pulse duty={pc.Duty} fwd={pc.Forward} rev={pc.Reverse}");
            break;
        case nameof(ChannelConfig.LEchoWave) when p.ParamValue is EchoWave wave:
            Console.WriteLine($"[Param] {p.ReceiveTime:HH:mm:ss.fff} addr={p.Address} 参考回波 start={wave.EchoStart}ns int={wave.ClockInt}ns pts={wave.WaveData?.Count ?? 0}");
            break;
        default:
            Console.WriteLine($"[Param] {p.ReceiveTime:HH:mm:ss.fff} addr={p.Address} ch={p.Channel?.ToString() ?? "-"} {p.ParamName}={p.ParamValue}");
            break;
    }
}
