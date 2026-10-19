using System.Text;
using FxReader;
using FxSDK.Utility;

Console.OutputEncoding = Encoding.UTF8;

var configPath = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? "appsettings.json";
var readParamsOnStart = args.Contains("--read-params");
var zeroOnStart = args.Contains("--zero");

AppSettings settings;
try
{
    settings = AppSettings.Load(configPath);
}
catch (Exception ex)
{
    Console.WriteLine($"配置读取失败：{ex.Message}");
    return 1;
}

if (settings.Devices.Count == 0)
{
    Console.WriteLine($"{configPath} 里没有配置设备");
    return 1;
}

using var cts = new CancellationTokenSource();
var printer = new ResultPrinter(
    TimeSpan.FromSeconds(Math.Max(0, settings.PrintIntervalSeconds)),
    settings.HexPreviewBytes);

// 不开开发者模式 SDK 就不会解出 HexString
FxUtility.DeveloperMode = settings.HexPreviewBytes > 0
                          || (settings.RawHex && !string.IsNullOrWhiteSpace(settings.RawDir));

RawWriter? raw = null;
if (!string.IsNullOrWhiteSpace(settings.RawDir))
{
    raw = new RawWriter(Path.GetFullPath(settings.RawDir));
}

var zeroStore = string.IsNullOrWhiteSpace(settings.ZeroFile) ? null : ZeroStore.Load(settings.ZeroFile);

var runners = settings.Devices
    .Select(device => new DeviceRunner(device, settings, printer, raw, zeroStore))
    .ToList();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("正在停止……");
    cts.Cancel();
};

Console.WriteLine($"FxSDK 1.6 多通道监测读数程序，共 {runners.Count} 台设备。" +
                  "L 查看已连接设备，P 读取设备参数，Z 置零，Q 或 Ctrl+C 退出。");
foreach (var runner in runners)
{
    Console.WriteLine($"  {runner.Title}  通道 {runner.ChannelCount} 个");
}

Console.WriteLine();
StartKeyboard();

var tasks = new List<Task> { printer.RunAsync(cts.Token), StatusLoopAsync(cts.Token) };
tasks.AddRange(runners.Select(runner => runner.RunAsync(cts.Token)));

if (raw is not null)
{
    Console.WriteLine($"原始数据写入 {raw.Directory}" + (settings.RawHex ? "，含原始报文" : string.Empty));
    tasks.Add(raw.RunAsync(cts.Token));
}

if (readParamsOnStart || zeroOnStart)
{
    tasks.Add(WhenOnlineAsync(cts.Token));
}

await Task.WhenAll(tasks);

foreach (var runner in runners)
{
    runner.Dispose();
}

Console.WriteLine("已停止");
return 0;

async Task StatusLoopAsync(CancellationToken token)
{
    var period = TimeSpan.FromSeconds(Math.Max(5, settings.StatusSeconds));

    while (!token.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(period, token);
        }
        catch (OperationCanceledException)
        {
            break;
        }

        PrintDevices();
    }
}

// 等连上并完成启动流程
async Task WhenOnlineAsync(CancellationToken token)
{
    try
    {
        foreach (var runner in runners)
        {
            while (!runner.Online)
            {
                await Task.Delay(500, token);
            }

            await Task.Delay(1500, token);

            if (readParamsOnStart)
            {
                await runner.ReadParametersAsync(token);
            }

            if (zeroOnStart)
            {
                await runner.ZeroAsync(token);
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
}

async Task ReadParamsAsync()
{
    foreach (var runner in runners)
    {
        await runner.ReadParametersAsync(cts.Token);
    }
}

async Task ZeroAsync()
{
    foreach (var runner in runners)
    {
        await runner.ZeroAsync(cts.Token);
    }
}

void PrintDevices()
{
    var online = runners.Count(runner => runner.Online);
    Console.WriteLine($"---- 已连接设备 {online}/{runners.Count}  {DateTime.Now:HH:mm:ss} ----");

    foreach (var runner in runners)
    {
        Console.WriteLine("  " + runner.StatusLine());
    }

    if (printer.Dropped > 0)
    {
        Console.WriteLine($"  打印队列已丢弃 {printer.Dropped} 条");
    }

    if (raw is not null)
    {
        Console.WriteLine($"  原始数据已写 {raw.Written} 帧" +
                          (raw.Dropped > 0 ? $"，丢弃 {raw.Dropped} 帧" : string.Empty));
    }
}

void StartKeyboard()
{
    if (Console.IsInputRedirected)
    {
        return;
    }

    var thread = new Thread(() =>
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                switch (Console.ReadKey(true).Key)
                {
                    case ConsoleKey.L:
                        PrintDevices();
                        break;
                    case ConsoleKey.P:
                        _ = ReadParamsAsync();
                        break;
                    case ConsoleKey.Z:
                        _ = ZeroAsync();
                        break;
                    case ConsoleKey.Q:
                        cts.Cancel();
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // 程序正在退出
        }
    })
    {
        IsBackground = true
    };

    thread.Start();
}
