// 只读监视服务。SDK 直连设备，浏览器走 WebSocket。
// BoltPreload.Monitor.exe [--port 5080]

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FxDevice;
using FxSDK.Models;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Net.Http.Headers;

var port = 5080;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var p)) port = p;
}

// 没有可用配置（farm.json 不在/写坏）时多久重试一次读它
const int ConfigRetrySeconds = 10;

// 页面跟着 exe 走，从哪个目录启动都能找到
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// 服务控制器启动时进入服务模式。停机上限 20 秒：断设备最多 6 秒，历史库最多 4 秒。
builder.Host.UseWindowsService(o => o.ServiceName = "BoltPreloadMonitor");
var shutdownTimeout = TimeSpan.FromSeconds(20);
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = shutdownTimeout);
var app = builder.Build();

// Web 默认规则：字段名大小写不敏感（页面发的是小写 endpoint）
var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);

var asService = WindowsServiceHelpers.IsWindowsService();
if (!asService) Console.OutputEncoding = Encoding.UTF8;
// 控制台同时写入 logs\monitor-yyyyMMdd.log，保留 30 天。服务模式没有控制台。
Console.SetOut(new TeeLog(Console.Out, Path.Combine(AppContext.BaseDirectory, "logs"), keepDays: 30));

if (!asService)
{
    // 双击启动时把地址打在窗口上。
    Console.Title = "螺栓预紧力大屏服务（请勿关闭此窗口）";
    var ips = NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(a => a.Address.ToString()).Distinct().ToList();
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine("  螺栓预紧力在线监测 · 大屏服务");
    Console.WriteLine($"  本机打开：http://localhost:{port}/");
    foreach (var ip in ips) Console.WriteLine($"  局域网打开：http://{ip}:{port}/");
    Console.WriteLine("  这个窗口是服务本体，关了网页就没数据。业主机请用「安装大屏服务.cmd」装成后台服务，不再有窗口。");
    Console.WriteLine($"  运行日志同时写在 {Path.Combine(AppContext.BaseDirectory, "logs")}");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
}

// farm.json：全场法兰模板、每台设备（机组、地址、接线、标定、额定）、阈值都在这。
// 读它、解析成 FarmPlan；解析失败返回 null 并给出原因（启动时退回默认，运行中重载时保持原配置不动）
var farmPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "farm.json");
FarmPlan? LoadFarm(Action<string> log, out string? error)
{
    error = null;
    JsonElement? root = null;
    try
    {
        if (File.Exists(farmPath)) root = JsonDocument.Parse(File.ReadAllText(farmPath)).RootElement;
    }
    catch (Exception ex)
    {
        error = $"farm.json 解析失败：{ex.Message}";
        return null;
    }

    var plan = FarmPlan.Load(root, AppContext.BaseDirectory, log);
    if (plan.Devices.Count == 0)
    {
        error = "farm.json 里没有配置任何设备（devices[] 或老写法的 device）";
        return null;
    }

    return plan;
}

// 一个大屏接很多台设备：每台设备一个采集器（各自连接/保活/巡检/维护），共用一个广播和一个历史库。
// 标定与口径按各台设备各法兰取 .ucf 和额定值，没配的回落到程序目录里的 .ucf。
// 没有标定就不算力值：历史里估算预紧力留空、状态记未标定，页面只显示声时差。
// 读不到 farm.json 时不连设备，只提供网页，配置出现后再连。
var farm = LoadFarm(Console.WriteLine, out var farmError);
if (farm is null)
{
    Console.WriteLine($"{farmError}。不按任何默认设备监测：网页照常提供，配置文件出现/修好后自动加载（每 {ConfigRetrySeconds} 秒重试）");
    farm = FarmPlan.Empty;
}

var legacyTurbine = farm.Devices.Count > 0 ? farm.Devices[0].Turbine : 1;
var history = new HistoryStore(Path.Combine(AppContext.BaseDirectory, "data", "history.db"), legacyTurbine);
// 不管有没有标定都登记一次：cal_log 里就能看出这段时间的历史是用哪份系数、哪种口径算的
history.SetPlans(farm.Devices.Select(d => (d.Turbine, d.Plan)));

var broadcaster = new Broadcaster();
// 当前生效的一代配置（风场方案 + 每台设备的采集器 + 它们的保活循环）。改 farm.json 重载时整代替换：
// 老的一代先退役（不再重连）、断开设备，新的一代接着连。端点和状态都只看 site，不持有旧引用
var site = new Site(farm, farm.Devices.Select(d => new MonitorHub(d.Turbine, d.Endpoint, d.Plan, d.Guard, broadcaster, history)).ToList());

// 整站状态：农场级口径 + 每台设备各自的状态。页面开 WebSocket 先收这一份，之后按设备增量更新
object FullState()
{
    var s = site;
    return new
    {
        farmName = s.Farm.FarmName,
        turbineCount = s.Farm.TurbineCount,
        baseline = s.Mode.Mode == BaselineMode.Anchor ? "anchor" : "free",
        baselineText = s.Mode.ModeText,
        warnRatio = s.Mode.WarnRatio,
        alarmRatio = s.Mode.AlarmRatio,
        devices = s.Hubs.Select(h => h.State()).ToList(),
    };
}
broadcaster.FullState = FullState;

MonitorHub? HubOf(int? turbine)
{
    var s = site;
    return turbine is { } t ? s.ByTurbine.GetValueOrDefault(t) : s.Hubs.Count == 1 ? s.Hubs[0] : null;
}

// 90 秒没有测量帧先重发监测命令，再过 90 秒还没帧才重连。缺参考声时：没探头从 10 分钟退避到 1 小时；
// 有探头 2 分钟补读一次；通道有回应、只是参考声时回包没收到（或几小时前还在测）的，1 分钟补读
var stallAfter = TimeSpan.FromSeconds(90);
var retryMissingMin = TimeSpan.FromMinutes(10);
var retryMissingMax = TimeSpan.FromHours(1);
var retryWithProbe = TimeSpan.FromMinutes(2);
var retryQuick = TimeSpan.FromMinutes(1);
// 探头在位巡检：连上先巡一轮，之后每 10 分钟一轮（本机 POST /api/scan 可随时触发），每轮轮宽集 45 秒
var scanLength = TimeSpan.FromSeconds(45);
var scanEvery = TimeSpan.FromMinutes(10);
// 同一时刻只允许一次配置重载（文件监视、手动触发、无配置时的定时重试共用）
var reloadGate = new SemaphoreSlim(1, 1);
// 定时重试上一次记过日志的错误：同一个错误不反复刷
string? lastQuietReloadError = null;

app.UseDefaultFiles();
// no-cache。不发这个头时浏览器会把 app.js 留好几天。
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

// 改地址、断开、维护只接受本机调用。
static bool IsLocalCall(HttpContext ctx) =>
    ctx.Connection.RemoteIpAddress is { } ip
    && (IPAddress.IsLoopback(ip) || ip.Equals(ctx.Connection.LocalIpAddress));

// 手动断开某台设备（重连由保活循环自动做）。只许大屏主机本地调用
app.MapPost("/api/disconnect", async (HttpContext ctx, int? turbine) =>
{
    if (!IsLocalCall(ctx)) return Results.StatusCode(403);
    if (HubOf(turbine) is not { } hub) return Results.BadRequest(new { error = "turbine 不对或没写" });
    await hub.DisconnectAsync();
    return Results.Json(hub.State());
});

// 维护模式：现场要用桌面端置零/调参时，大屏让出那台设备（只有一路 TCP）并在期限内不再重连；minutes<=0 结束维护立即恢复。
// 只许大屏主机本地调用，页面上的按钮在别的电脑上按会得到 403
app.MapPost("/api/maintenance", async (HttpContext ctx) =>
{
    if (!IsLocalCall(ctx)) return Results.StatusCode(403);
    var req = await JsonSerializer.DeserializeAsync<MaintenanceReq>(ctx.Request.Body, jsonOpts);
    if (HubOf(req?.Turbine) is not { } hub) return Results.BadRequest(new { error = "turbine 不对或没写" });
    var minutes = req?.Minutes ?? 0;
    var note = string.IsNullOrWhiteSpace(req?.Note) ? "现场置零/调参" : req!.Note!.Trim();
    if (minutes <= 0)
    {
        hub.ExitMaintenance();
    }
    else if (hub.InMaintenance)
    {
        hub.ExtendMaintenance(TimeSpan.FromMinutes(Math.Min(minutes, 24 * 60)), note);
    }
    else
    {
        await hub.EnterMaintenanceAsync(TimeSpan.FromMinutes(Math.Min(minutes, 24 * 60)), note);
    }

    return Results.Json(hub.State());
});

// 立刻巡检一轮（换插探头后不想等 10 分钟）：保活循环最多 10 秒内响应
app.MapPost("/api/scan", (HttpContext ctx, int? turbine) =>
{
    if (!IsLocalCall(ctx)) return Results.StatusCode(403);
    if (HubOf(turbine) is not { } hub) return Results.BadRequest(new { error = "turbine 不对或没写" });
    hub.ScanRequested = true;
    return Results.Json(new { ok = true, scanning = hub.ScanActive });
});

// 重新加载 farm.json（接线/标定/额定/设备改了不用重启服务）。文件一改服务自己也会重载，这个接口给"改完立刻应用"用。
// 解析不过就拒绝，原配置原样跑。只许大屏主机本地调用
app.MapPost("/api/reload", async (HttpContext ctx) =>
{
    if (!IsLocalCall(ctx)) return Results.StatusCode(403);
    var (ok, message) = await ReloadAsync("手动触发");
    return ok ? Results.Json(new { ok = true, message }) : Results.BadRequest(new { error = message });
});

// 桌面端置零/下发参数后通知大屏重读基线。局域网内都可调用（桌面端常在别的电脑上）：它只会让服务重读一次设备上的参数，
// 改不了任何配置，最坏是被人反复调用多停几秒流
app.MapPost("/api/resync", (int? turbine, string? why) =>
{
    if (HubOf(turbine) is not { } hub) return Results.BadRequest(new { error = "turbine 不对或没写" });
    hub.RequestResync(string.IsNullOrWhiteSpace(why) ? "桌面端通知基线/参数已改" : why!.Trim());
    return Results.Json(new { ok = true });
});

app.MapGet("/api/state", () => Results.Json(FullState()));

// turbine 不给 = 全场（报告页、报警中心）；给了 = 只这台机组（机组页、法兰页趋势）
app.MapGet("/api/history", (string? range, int? turbine) => Results.Json(history.QuerySamples(range ?? "24h", turbine)));
app.MapGet("/api/events", (string? range, int? turbine) => Results.Json(history.QueryEvents(range ?? "24h", turbine)));
app.MapGet("/api/report", (string? range, int? turbine) => Results.Json(history.QueryReport(range ?? "24h", turbine)));

// 业主说明：报警中心和统计报告共用。句子由已算好的状态和力值拼出来，不另算、不外发
app.MapGet("/api/narrative", (string? range) =>
{
    var canonical = range == "7d" || range == "30d" ? range : "24h";
    return Results.Json(new
    {
        range = canonical,
        text = OwnerBrief.Speak(site, history, canonical),
        asOf = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    });
});
app.MapGet("/api/archive", () => Results.Json(history.Stats()));
// 边查边写到响应流：30 天的数据攒成一个字节数组再发会把内存吃光
app.MapGet("/api/export.csv", async (HttpContext ctx, string? range, int? turbine) =>
{
    var r = HistoryStore.CanonicalRange(range);
    ctx.Response.ContentType = "text/csv; charset=utf-8";
    var disposition = new ContentDispositionHeaderValue("attachment");
    disposition.SetHttpFileName(turbine is { } tb ? $"preload-F{tb:00}-{r}.csv" : $"preload-{r}.csv");
    ctx.Response.Headers.ContentDisposition = disposition.ToString();
    await history.ExportCsvAsync(r, turbine, ctx.Response.Body, ctx.RequestAborted);
});

app.Map("/ws", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    // 停服务时主动断开 WebSocket。Kestrel 优雅停机会等在途请求结束，不断开要等满 30 秒，
    // 正好撞上服务控制器的 30 秒上限被强杀
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, app.Lifetime.ApplicationStopping);
    await broadcaster.ServeSocketAsync(ws, linked.Token);
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    // 停服务的顺序：先让保活循环退出、采集器退役（否则断开后循环会马上重连），再把设备切回监测模式并断开，最后关历史库。
    // SDK 收尾在设备不配合时可能很慢，最多等 6 秒，历史库最多 4 秒，不超过 20 秒的停机上限
    var s = site;
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} 正在停止服务：断开 {s.Hubs.Count} 台设备……");
    s.Cts.Cancel();
    foreach (var h in s.Hubs) h.Retire();
    try
    {
        if (!Task.WhenAll(s.Hubs.Select(h => h.DisconnectAsync())).Wait(TimeSpan.FromSeconds(6)))
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} 停服务：设备断开未在 6 秒内完成，继续退出");
        }
    }
    catch (AggregateException ex)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} 停服务：断开设备出错：{ex.InnerException?.Message ?? ex.Message}");
    }

    history.Dispose();
});
app.Lifetime.ApplicationStopped.Register(() => Console.WriteLine($"{DateTime.Now:HH:mm:ss} 服务已停止"));

Console.WriteLine($"监视服务已启动：http://localhost:{port}/  （局域网访问用本机 IP）");
Console.WriteLine($"基线口径：{site.Mode.ModeText}" + (site.Mode.Mode == BaselineMode.Anchor
    ? "——k·Δt 是较锚定时刻的变化量，绝对预紧力按“锚定时 = 额定值”估算"
    : "——k·Δt + b 就是绝对预紧力"));
Console.WriteLine($"历史库：{history.DbPath}（采样每 5 秒一条保留 90 天，事件保留 2 年）");
DescribeSite(site);

void DescribeSite(Site s)
{
    if (s.Hubs.Count == 0)
    {
        Console.WriteLine($"接入设备 0 台：没有可用配置，不连任何设备，等 {farmPath} 出现/修好（每 {ConfigRetrySeconds} 秒重试，保存文件也会立刻触发）");
        return;
    }

    Console.WriteLine($"接入设备 {s.Hubs.Count} 台：");
    foreach (var h in s.Hubs)
    {
        var p = h.Plan;
        // 各法兰的接线/标定/额定在 CalPlan.Load 里已逐条打印；这里只补全局兜底和没配到的情况
        var note = !p.Flanges.Any(f => f.OnDevice)
            ? "没有任何法兰配置在测通道，按默认 CH1–CH4 监测" + (p.GlobalCal is { } g ? $"，标定用程序目录的 {g.FileName}" : "，且没有 .ucf，不计算预紧力")
            : p.GlobalCal is null && p.Flanges.Any(f => f.OnDevice && f.Cal is null)
                ? "有法兰没配 ucf、程序目录里也没有 .ucf：这些法兰不计算预紧力"
                : p.Mode == BaselineMode.Anchor && p.Flanges.Any(f => f.OnDevice && f.Rated <= 0)
                    ? "有法兰未配置额定预紧力：锚定口径下只显示变化量，不判松动"
                    : string.Empty;
        var extra = h.Planned.Concat(h.Guard).Except(h.WantedChannels).Distinct().OrderBy(c => c).ToList();
        Console.WriteLine($"  {h.Label}：测点 {string.Join("、", h.WantedChannels.Select(c => "CH" + c))}"
                          + $"，{p.TcText}"
                          + (extra.Count > 0 ? $"，在位识别另轮询 {string.Join("、", extra.Select(c => "CH" + c))}" : string.Empty)
                          + (note.Length > 0 ? $"。{note}" : string.Empty));
    }
}

// ── 重载配置：farm.json 改了（接线/标定/额定/设备）不用重启服务 ──────────────────────────
// 新配置先解析，解析不过或没有设备就拒绝，原配置原样跑；解析过了才让老的一代退役：不再重连 → 断开设备 →
// 新的一代建采集器、起保活循环 → 通知页面刷新（页面要重新拿 farm.json 里的法兰模板和新的 state）。
// 同一时刻只允许一次重载；文件监视、手动触发和无配置时的定时重试共用这一个入口。
// quiet = 定时重试：同一个错误只在第一次和错误变了的时候写日志，别每 10 秒刷一行
async Task<(bool Ok, string Message)> ReloadAsync(string trigger, bool quiet = false)
{
    await reloadGate.WaitAsync();
    try
    {
        var notes = new List<string>();
        var fresh = LoadFarm(notes.Add, out var error);
        if (fresh is null)
        {
            var msg = site.Hubs.Count == 0 ? $"{error}，仍不连任何设备" : $"{error}，配置未改动，继续按原配置监测";
            if (!quiet || msg != lastQuietReloadError)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} 重新加载配置（{trigger}）失败：{msg}");
                lastQuietReloadError = quiet ? msg : null;
            }

            return (false, msg);
        }

        lastQuietReloadError = null;

        Console.WriteLine($"{DateTime.Now:HH:mm:ss} 重新加载配置（{trigger}）……");
        foreach (var n in notes) Console.WriteLine(n);

        // 先建好下一代、登记历史库，成功了再拆旧的。中途失败原配置继续跑
        try
        {
            history.SetPlans(fresh.Devices.Select(d => (d.Turbine, d.Plan)));
        }
        catch (Exception ex)
        {
            var msg = $"历史库登记标定失败：{ex.Message}，配置未改动，继续按原配置监测";
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} 重新加载配置（{trigger}）失败：{msg}");
            return (false, msg);
        }

        var old = site;
        Site next;
        try
        {
            next = new Site(fresh, fresh.Devices.Select(d => new MonitorHub(d.Turbine, d.Endpoint, d.Plan, d.Guard, broadcaster, history)).ToList());
            foreach (var h in next.Hubs)
            {
                if (old.ByTurbine.TryGetValue(h.Turbine, out var prev)) h.InheritLiveMemory(prev);
            }
        }
        catch (Exception ex)
        {
            var msg = $"新采集器没建起来：{ex.Message}，配置未改动，继续按原配置监测";
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} 重新加载配置（{trigger}）失败：{msg}");
            return (false, msg);
        }

        foreach (var h in old.Hubs) h.Retire();
        old.Cts.Cancel();
        foreach (var h in old.Hubs)
        {
            try { await h.DisconnectAsync(); }
            catch (Exception ex) { Console.WriteLine($"{DateTime.Now:HH:mm:ss} {h.Label} 断开出错：{ex.Message}"); }
        }

        site = next;
        DescribeSite(next);
        StartLoops(next);
        broadcaster.Broadcast(new { type = "reload", reason = trigger });
        var summary = $"已应用：{fresh.Devices.Count} 台设备（{string.Join("、", next.Hubs.Select(h => h.Label))}）";
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} 重新加载配置完成，{summary}");
        return (true, summary);
    }
    finally
    {
        reloadGate.Release();
    }
}

// 保存 farm.json 即重载（含临时文件改名）。FileSystemWatcher 用 using 钉住，否则会被回收，保存后不再重载。
Directory.CreateDirectory(Path.GetDirectoryName(farmPath)!);
using var farmWatcher = new FileSystemWatcher(Path.GetDirectoryName(farmPath)!, "farm.json")
{
    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
};
using var farmChangeDebounce = new System.Timers.Timer(1500) { AutoReset = false };
farmChangeDebounce.Elapsed += (_, _) => _ = Task.Run(async () =>
{
    try { await ReloadAsync("farm.json 改动"); }
    catch (Exception ex) { Console.WriteLine($"{DateTime.Now:HH:mm:ss} 重新加载配置异常：{ex.Message}"); }
});
void OnFarmChanged(object? sender, FileSystemEventArgs e)
{
    farmChangeDebounce.Stop();
    farmChangeDebounce.Start();
}
farmWatcher.Changed += OnFarmChanged;
farmWatcher.Created += OnFarmChanged;
farmWatcher.Renamed += (s, e) => OnFarmChanged(s, e);
farmWatcher.EnableRaisingEvents = true;

// 起来时（或重载后）一台设备都没有：定时重试读配置，直到读到为止。文件监视是主路；这条是文件一直在、只是当时读不了
// （被别的进程锁着、半截）时的兜底
_ = Task.Run(async () =>
{
    using var tick = new PeriodicTimer(TimeSpan.FromSeconds(ConfigRetrySeconds));
    try
    {
        while (await tick.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
        {
            if (site.Hubs.Count == 0) await ReloadAsync("等待配置", quiet: true);
        }
    }
    catch (OperationCanceledException)
    {
        // 停服务
    }
});

// 端口绑定成功后再连设备：第二个实例抢不到端口时，不要先给设备发待机
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} 端口已就绪，开始连接设备");
    StartLoops(site);
});

// 这一代配置的保活循环：配置重载时老一代的循环随 Cts 取消退出，退役的采集器也不会再被连上
void StartLoops(Site s)
{
    var ct = s.Cts.Token;
    foreach (var hub in s.Hubs)
    {
        s.Loops.Add(Task.Run(async () =>
    {
        var retryMissingEvery = retryMissingMin;
        var tag = hub.Label;
        // 下次补读缺失测点的间隔：快速名单 1 分钟 > 有探头信号 2 分钟 > 普通退避
        TimeSpan NextRetry() => hub.AnyMissingQuick ? retryQuick : hub.AnyMissingWithProbe ? retryWithProbe : retryMissingEvery;
        static string Dur(TimeSpan ts) => ts.TotalMinutes >= 1 ? $"{ts.TotalMinutes:0} 分钟" : $"{ts.TotalSeconds:0} 秒";
        while (!ct.IsCancellationRequested && !hub.Retired)
        {
            try
            {
                // 维护中：不连、不重连，把设备留给现场的桌面端；到期自动往下走恢复连接
                if (hub.InMaintenance)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }

                if (hub.MaintenanceUntil is { } expired)
                {
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {tag} 维护期限到（{expired.ToLocalTime():HH:mm}），恢复连接设备");
                    hub.ExitMaintenance();
                }

                if (!hub.IsConnected)
                {
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {tag} 尝试连接……");
                    await hub.ConnectAsync();
                    retryMissingEvery = retryMissingMin;
                    var missing = hub.MissingChannels;
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {tag} 已连接，{hub.ChannelCount} 个测点在监测"
                                      + (missing.Count > 0
                                          ? $"；{string.Join("、", missing.Select(c => "CH" + c))} 读不到参考声时，{Dur(NextRetry())}后重试"
                                            + (hub.AnyMissingQuick ? "（通道有回应，多半是参考声时那条回包被吞了，快速补读）" : string.Empty)
                                          : string.Empty));
                }
                else if (DateTime.UtcNow - hub.LastFrameAt > stallAfter)
                {
                    // 帧停了但 TCP 还在，多半是另一个程序把设备切了模式。先重发监测命令，再等 90 秒才重连，
                    // 重连要在待机下重读十几路参数，慢且可能漏读
                    if (!hub.RearmTried)
                    {
                        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {tag} 连接还在但 {stallAfter.TotalSeconds:0} 秒没有测量帧，先重新下发监测命令（可能是别的程序把设备切了模式）");
                        await hub.RearmStreamAsync();
                    }
                    else
                    {
                        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {tag} 重新下发监测命令后仍 {stallAfter.TotalSeconds:0} 秒没有测量帧，重连设备");
                        await hub.DisconnectAsync();
                        continue;
                    }
                }
                else if (hub.ResyncRequested)
                {
                    // 声时差跳过门限就重读基线
                    await hub.ResyncBaselinesAsync();
                }
                else if (hub.ScanActive && DateTime.UtcNow - hub.ScanStartedAt > scanLength)
                {
                    var strays = await hub.EndScanAsync();
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {tag} 巡检结束"
                                      + (strays.Count > 0 ? $"：{string.Join("、", strays.Select(c => "CH" + c))} 有探头信号但未在配置中" : "，未发现插错位的探头"));
                }
                else if (!hub.ScanActive && (hub.ScanRequested || DateTime.UtcNow - hub.LastScanAt > scanEvery))
                {
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {tag} 巡检开始（{(hub.ScanRequested ? "手动触发" : "定时")}）");
                    await hub.StartScanAsync();
                }
                else if (hub.MissingChannels.Count > 0
                         && DateTime.UtcNow - hub.LastChannelRetryAt > NextRetry())
                {
                    // 快速补读不动普通退避：它是针对"回包被吞"的，吞了几次不代表现场没置零
                    var quick = hub.AnyMissingQuick;
                    var added = await hub.RetryMissingChannelsAsync();
                    if (!quick)
                    {
                        retryMissingEvery = added > 0
                            ? retryMissingMin
                            : TimeSpan.FromTicks(Math.Min(retryMissingEvery.Ticks * 2, retryMissingMax.Ticks));
                    }

                    var missing = hub.MissingChannels;
                    var why = hub.AnyMissingQuick ? "（通道有回应，参考声时那条回包还没收到，快速补读）"
                        : hub.AnyMissingWithProbe ? "（有探头信号，等现场置零）"
                        : string.Empty;
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {tag} 补读测点：新增 {added} 个，现在 {hub.ChannelCount} 个在监测"
                                      + (missing.Count > 0
                                          ? $"；{string.Join("、", missing.Select(c => "CH" + c))} 仍读不到，{Dur(NextRetry())}后再试{why}"
                                          : string.Empty));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;   // 配置重载，这一代退出
            }
            catch (Exception ex)
            {
                if (hub.Retired) break;
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} {tag} {(hub.IsConnected ? "保活出错" : "连接失败")}：{ex.Message}，稍后重试");
                hub.NoteFailure(ex.Message);
            }

            // 维护刚结束 1 秒后重连；已连接时每 2 秒看一次跳变；没连上 30 秒后再试
            try { await Task.Delay(TimeSpan.FromSeconds(hub.IsConnected ? 2 : hub.MaintenanceUntil is null ? 30 : 1), ct); }
            catch (OperationCanceledException) { break; }
        }
    }, CancellationToken.None));
    }
}

try
{
    app.Run();
}
catch (Exception ex) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
{
    // 只处理停机阶段的异常：停机收尾超时后宿主会在这里抛 OperationCanceledException。
    // 启动阶段的异常（端口被占、目录没权限）照常抛出，让服务控制器按恢复策略重启
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} 停机收尾超过 {shutdownTimeout.TotalSeconds:0} 秒或出错（{ex.GetType().Name}：{ex.Message}），进程退出");
}

/// <summary>
/// 控制台输出同时写进按天分的日志文件，写失败不影响程序。多线程写用一把锁串行。
/// 文件以共享读写方式打开，服务重启时新旧实例会重叠几秒，独占打开会让后起的实例写不进去。
/// Write()（不带换行）也落盘，ASP.NET 的控制台日志走 Write，服务模式下没有控制台。
/// </summary>
internal sealed class TeeLog : TextWriter
{
    private readonly TextWriter _console;
    private readonly string _dir;
    private readonly int _keepDays;
    private readonly object _gate = new();
    private readonly StringBuilder _partial = new();
    private StreamWriter? _file;
    private DateTime _fileDay;
    private int _openFailures;

    public TeeLog(TextWriter console, string dir, int keepDays)
    {
        _console = console;
        _dir = dir;
        _keepDays = keepDays;
    }

    public override Encoding Encoding => _console.Encoding;

    public override void Write(char value)
    {
        _console.Write(value);
        lock (_gate) Absorb(value);
    }

    public override void Write(string? value)
    {
        _console.Write(value);
        if (string.IsNullOrEmpty(value)) return;
        lock (_gate)
        {
            foreach (var ch in value) Absorb(ch);
        }
    }

    public override void WriteLine(string? value)
    {
        _console.WriteLine(value);
        lock (_gate)
        {
            if (_partial.Length > 0)
            {
                value = _partial.Append(value).ToString();
                _partial.Clear();
            }

            WriteFileLine(value ?? string.Empty);
        }
    }

    public override void WriteLine() => WriteLine(string.Empty);

    public override void Flush()
    {
        _console.Flush();
        lock (_gate) _file?.Flush();
    }

    /// <summary>不带换行的片段攒成整行再落盘（调用方要在锁里）。</summary>
    private void Absorb(char ch)
    {
        if (ch == '\r') return;
        if (ch == '\n')
        {
            WriteFileLine(_partial.ToString());
            _partial.Clear();
            return;
        }

        _partial.Append(ch);
    }

    private void WriteFileLine(string line)
    {
        try
        {
            var today = DateTime.Now.Date;
            if (_file is null || _fileDay != today)
            {
                _file?.Dispose();
                _file = null;
                Directory.CreateDirectory(_dir);
                var stream = new FileStream(Path.Combine(_dir, $"monitor-{today:yyyyMMdd}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _fileDay = today;
                if (_openFailures > 0)
                {
                    _file.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} （此前有 {_openFailures} 行日志因打不开文件没有写入）");
                    _openFailures = 0;
                }

                Prune();
            }

            // 控制台上大多行自带 HH:mm:ss，文件里只补日期；没带时间的行补全日期时间
            var stamped = line.Length >= 9 && line[2] == ':' && line[5] == ':' && line[8] == ' ';
            _file.WriteLine($"{DateTime.Now:yyyy-MM-dd}{(stamped ? " " : $" {DateTime.Now:HH:mm:ss} ")}{line}");
        }
        catch
        {
            // 日志写不进去就只剩控制台，不能因为这个把服务弄死；记个数，下次打开成功时补一句
            if (_file is null) _openFailures++;
        }
    }

    private void Prune()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "monitor-*.log"))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-_keepDays)) File.Delete(f);
            }
        }
        catch
        {
        }
    }
}

/// <summary>一代配置：风场方案、每台设备的采集器、它们的保活循环。farm.json 重载时整代替换。</summary>
internal sealed class Site
{
    public Site(FarmPlan farm, List<MonitorHub> hubs)
    {
        Farm = farm;
        Hubs = hubs;
        ByTurbine = hubs.ToDictionary(h => h.Turbine);
        // 口径/阈值全场统一，取第一台的；一台设备都没有（没有可用配置）就按缺省口径给页面
        Mode = farm.Devices.Count > 0 ? farm.Devices[0].Plan : CalPlan.Empty;
    }

    public FarmPlan Farm { get; }

    public List<MonitorHub> Hubs { get; }

    public Dictionary<int, MonitorHub> ByTurbine { get; }

    public CalPlan Mode { get; }

    public CancellationTokenSource Cts { get; } = new();

    public List<Task> Loops { get; } = new();
}

internal sealed record MaintenanceReq(int? Turbine, int? Minutes, string? Note);

/// <summary>
/// WebSocket 广播，所有设备共用。每个浏览器连接一条有界队列，由一个发送循环按顺序写出。
/// 队列满了丢最旧的一条，实时帧过时就没用了。
/// </summary>
internal sealed class Broadcaster
{
    private sealed class Client
    {
        public Client(WebSocket ws)
        {
            Ws = ws;
            Outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
        }

        public WebSocket Ws { get; }

        public Channel<byte[]> Outbox { get; }
    }

    private readonly List<Client> _clients = new();

    /// <summary>整站状态（农场级 + 每台设备），新连上的浏览器先收这一份。</summary>
    public Func<object> FullState { get; set; } = () => new { };

    public void Broadcast(object msg)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg);
        lock (_clients)
        {
            foreach (var c in _clients) c.Outbox.Writer.TryWrite(bytes);
        }
    }

    public async Task ServeSocketAsync(WebSocket ws, CancellationToken ct)
    {
        var client = new Client(ws);
        lock (_clients) _clients.Add(client);
        // 发送循环和接收循环并行：接收只为了收到 Close，发送把队列里的消息按序写出
        var sender = SendLoopAsync(client, ct);
        try
        {
            client.Outbox.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(new { type = "state", state = FullState() }));
            var buf = new byte[1024];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            lock (_clients) _clients.Remove(client);
            client.Outbox.Writer.TryComplete();
            try { await sender; } catch { /* 连接已断，发送循环怎么退出都无所谓 */ }
            // 浏览器主动断开时回一个 Close 把握手做完；发送循环已停，这里不会和 SendAsync 撞车
            if (ws.State == WebSocketState.CloseReceived)
            {
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None); }
                catch { /* 对方已经不在了 */ }
            }
        }
    }

    private static async Task SendLoopAsync(Client client, CancellationToken ct)
    {
        try
        {
            await foreach (var bytes in client.Outbox.Reader.ReadAllAsync(ct))
            {
                if (client.Ws.State != WebSocketState.Open) break;
                await client.Ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }
}

/// <summary>一台采集设备的连接、在位识别、状态判定与状态快照。一个大屏有很多个，各自独立连自己那台设备，共用广播和历史库。</summary>
internal sealed class MonitorHub : IDisposable
{
    private static readonly ushort[] DefaultChannels = { 1, 2, 3, 4 };

    /// <summary>串行化连接/断开/补读这类会动设备模式的操作。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>保护 _params/_refWaves：State() 在任意 HTTP 线程上枚举，连接流程在另一个线程上改。</summary>
    private readonly object _stateLock = new();

    private readonly Broadcaster _bc;
    private readonly HistoryStore _history;

    public MonitorHub(int turbine, string endpoint, CalPlan plan, IReadOnlyList<ushort> guard, Broadcaster bc, HistoryStore history)
    {
        Turbine = turbine;
        ConfiguredEndpoint = endpoint;
        Plan = plan;
        Guard = guard;
        _bc = bc;
        _history = history;
    }

    /// <summary>这台设备装在哪台机组上。帧、状态、历史都带它。</summary>
    public int Turbine { get; }

    /// <summary>farm.json 里配的地址（连接前也知道）。</summary>
    public string ConfiguredEndpoint { get; }

    public string Label => $"F{Turbine:00}（{ConfiguredEndpoint}）";

    public void Dispose() => _gate.Dispose();

    private void Broadcast(object msg) => _bc.Broadcast(msg);

    /// <summary>这台设备的状态变了：只发它自己那份，页面按机组增量更新。</summary>
    private void BroadcastState() => Broadcast(new { type = "device", state = State() });

    /// <summary>保活循环里连接失败时记一句，页面上"未连接"旁边能看到为什么。</summary>
    public void NoteFailure(string reason)
    {
        _lastLog = $"{DateTime.Now:HH:mm:ss} 连接失败：{reason}";
        BroadcastState();
    }

    private DeviceEngine? _engine;
    private string _endpoint = "";
    private string _lastLog = "";
    private long _lastFrameTicks = DateTime.UtcNow.Ticks;

    /// <summary>连接时读回的每通道参数（显示名 → 值），键是通道号。</summary>
    private readonly SortedDictionary<ushort, List<KeyValuePair<string, string>>> _params = new();

    /// <summary>给业主说明用的现成快照，这里不发网络请求。</summary>
    private readonly Dictionary<ushort, OwnerShot> _owner = new();

    private readonly SortedDictionary<ushort, object> _refWaves = new();

    /// <summary>当前下发给设备的轮询集：已置零的测点 + 只为识别探头而轮询的规划位/巡检位。</summary>
    private List<ushort> _polled = new();

    /// <summary>规划位里设备上已有基线（历史置过零）但没配进 channels 的：页面据此提示"已置零，可直接加入配置"。</summary>
    private readonly HashSet<ushort> _zeroedExtra = new();

    /// <summary>连接时能回包的全部通道（配置 ∪ 规划 ∪ 巡检）：巡检时轮这一整套。</summary>
    private List<ushort> _reachable = new();

    /// <summary>正在巡检（轮宽集）。常态只轮配置通道 + 已发现有探头的野通道，巡检时把规划位/巡检位都带上看一眼。</summary>
    public bool ScanActive { get; private set; }

    public DateTime ScanStartedAt { get; private set; } = DateTime.MinValue;

    public DateTime LastScanAt { get; private set; } = DateTime.MinValue;

    /// <summary>外部（本机 API）要求尽快巡检一次。</summary>
    public bool ScanRequested { get; set; }

    /// <summary>常态轮询集：配置通道 + 巡检里发现有探头信号的野通道（要持续盯着它）。</summary>
    private List<ushort> NarrowPollSet()
    {
        lock (_stateLock)
        {
            var strays = _signals.Where(kv => kv.Value.Presence == "probe" && !WantedChannels.Contains(kv.Key)).Select(kv => kv.Key);
            return WantedChannels.Where(c => _reachable.Contains(c)).Concat(strays).Distinct().OrderBy(c => c).ToList();
        }
    }

    /// <summary>切换轮询集：短暂停流 → 重下轮询集 → 恢复监测（约 1–2 秒没帧）。</summary>
    private async Task SwitchPollSetAsync(DeviceEngine engine, List<ushort> set)
    {
        if (set.Count == 0) return;
        await engine.StopStreamAsync();
        await engine.ResumeMonitorAsync(set, CancellationToken.None);
        Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
        lock (_stateLock) _polled = set;
    }

    /// <summary>
    /// 开始一轮巡检：带上规划位和巡检位轮询约 45 秒，找插错位置的探头。
    /// 同时从设备重读在测各路的基线。声时差按本会话缓存的参考声时计算，现场用桌面端重新置零后不重读的话，
    /// 页面会一直按旧基线算出几百 kN 的假松动。巡检本来就要停一次流切轮询集，顺便读，每 10 分钟一次。
    /// </summary>
    public async Task StartScanAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_engine is not { IsConnected: true } engine || ScanActive) return;
            List<ushort> wide;
            lock (_stateLock) wide = _reachable.ToList();
            await engine.StopStreamAsync();
            await RefreshBaselinesAsync(engine);
            await SwitchPollSetAsync(engine, wide);
            ScanActive = true;
            ScanStartedAt = DateTime.UtcNow;
            LastScanAt = DateTime.UtcNow;
            ScanRequested = false;
            _lastLog = $"{DateTime.Now:HH:mm:ss} 巡检开始：轮询 {string.Join("、", wide.Select(c => "CH" + c))} 看各路有没有探头";
            Broadcast(new { type = "log", t = Turbine, text = _lastLog });
            BroadcastState();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 重读在测各路设备上的存储参数（参考声时/温度、测量模式、阈值；流已停）。和本会话缓存的不一样的：整路重读参数和参考波形、
    /// 更新会话（下一次 Resume 会按新基线/新算法算声时差）、交历史库记"基线重置"。没变的什么都不动。读失败的跳过，下一轮再读。
    /// 返回变了的路数。
    /// </summary>
    private async Task<int> RefreshBaselinesAsync(DeviceEngine engine)
    {
        List<ushort> monitored;
        Dictionary<ushort, List<KeyValuePair<string, string>>> old;
        lock (_stateLock)
        {
            monitored = _params.Keys.OrderBy(c => c).ToList();
            old = _params.ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        if (monitored.Count == 0) return 0;
        engine.ReloadAnchors();
        var changed = new SortedDictionary<ushort, List<KeyValuePair<string, string>>>();
        var waves = new SortedDictionary<ushort, object>();
        var baselines = new Dictionary<ushort, (decimal? RefTime, decimal? RefTemp)>();
        // 这次从设备读到的参考声时。没变的通道也要写进缓存，声时差不能继续减连接时的旧值。
        var readBack = new Dictionary<ushort, (decimal? RefTime, decimal? RefTemp)>();
        foreach (var ch in monitored)
        {
            try
            {
                var storage = await engine.ReadStorageAsync(ch, CancellationToken.None);
                if (storage.Count == 0 || !DeviceEngine.IsZeroed(storage)) continue;
                readBack[ch] = (ParseDec(storage.GetValueOrDefault("RefTime") ?? ""), ParseDec(storage.GetValueOrDefault("RefTemp") ?? ""));
                // 参考声时、测量模式、阈值任一变了都算：测量模式/阈值是客户端算回波的输入，桌面端改了本会话不跟着改，两边算出的声时就不一样
                var same = StorageKeysToCompare.All(k =>
                {
                    var cn = ParamOrder.First(p => p.Key == k).Cn;
                    var was = old.GetValueOrDefault(ch)?.FirstOrDefault(p => p.Key == cn).Value ?? "";
                    var now = Render(k, storage);
                    return k == "RefTime" ? ParseDec(was) == ParseDec(now) : was == now;
                });
                if (same) continue;
                await ReadChannelIntoAsync(engine, ch, changed, waves, baselines);
            }
            catch (Exception ex)
            {
                _lastLog = $"{DateTime.Now:HH:mm:ss} CH{ch} 重读基线失败：{ex.Message}";
            }
        }

        if (readBack.Count > 0)
        {
            lock (_stateLock)
            {
                foreach (var kv in readBack) _lastReadBaselines[kv.Key] = kv.Value;
            }
        }

        if (changed.Count == 0) return 0;
        lock (_stateLock)
        {
            foreach (var kv in changed) _params[kv.Key] = kv.Value;
            foreach (var kv in waves) _refWaves[kv.Key] = kv.Value;
            _lastDt.Clear();   // 基线换了，声时差从头比，别把换基线本身当成一次跳变
        }

        var msg = $"{DateTime.Now:HH:mm:ss} 设备上的基线/存储参数变了（现场重新置零或改了测量模式）：{string.Join("、", changed.Keys.Select(c => $"CH{c} 参考声时 {baselines[c].RefTime} ns"))}，本会话已按新值算";
        Console.WriteLine(msg);
        _lastLog = msg;
        Broadcast(new { type = "log", t = Turbine, text = _lastLog });
        _history.RecordBaselines(Turbine, baselines);
        return changed.Count;
    }

    /// <summary>结束巡检：收窄到常态轮询集。返回这轮发现的、未在配置中却有探头信号的通道。</summary>
    public async Task<List<ushort>> EndScanAsync()
    {
        await _gate.WaitAsync();
        try
        {
            ScanActive = false;
            if (_engine is not { IsConnected: true } engine) return new List<ushort>();
            var narrow = NarrowPollSet();
            await SwitchPollSetAsync(engine, narrow);
            var strays = narrow.Where(c => !WantedChannels.Contains(c)).ToList();
            _lastLog = $"{DateTime.Now:HH:mm:ss} 巡检结束：常态轮询 {string.Join("、", narrow.Select(c => "CH" + c))}"
                       + (strays.Count > 0 ? $"，其中 {string.Join("、", strays.Select(c => "CH" + c))} 是未配置却有探头信号的通道" : "，规划位/巡检位都没有探头");
            Broadcast(new { type = "log", t = Turbine, text = _lastLog });
            BroadcastState();
            return strays;
        }
        finally
        {
            _gate.Release();
        }
    }

    // 在位看信噪比，不看有没有解出回波。真探头 30–37 dB，空通道 0–6 dB，尖峰偶到 14 dB。
    // ≥16 dB 且多数帧如此 = 有探头；<12 dB 且绝大多数帧如此 = 没探头；中间不改判。门限与 DeviceEngine 同一份。
    private const double SnrProbeDb = DeviceEngine.ProbeSnrDb;   // ≥ 这个且多数帧如此 → 有探头
    private const double SnrNoneDb = DeviceEngine.NoiseSnrDb;    // < 这个且绝大多数帧如此 → 没探头
    private const int SnrWindow = 10;

    private sealed class SignalTrack
    {
        public readonly Queue<double> Recent = new();
        public readonly Queue<(bool Normal, decimal? Echo)> RecentEcho = new();
        public string Presence = "unknown";   // probe / none / unknown
        public bool SeenProbe;
        public double LastSnr;
        public DateTime LastAt;

        /// <summary>在位判定上次翻转的时刻（UTC）。页面靠它做"接线核对"：刚脱耦/插上的那一路高亮几秒，现场对号。</summary>
        public DateTime? ChangedAt;
    }

    /// <summary>
    /// 配置通道（已置零、真在测）的回波声时中位数：同一法兰同一批螺栓，回波声时应在同一量级。
    /// 拿它当"稳定回波"通路的参照：193 µs 那种串扰假回波过不了这一关。
    /// </summary>
    private decimal? ReferenceEchoNs()
    {
        var echoes = new List<decimal>();
        foreach (var ch in WantedChannels)
        {
            if (!_signals.TryGetValue(ch, out var t) || !_params.ContainsKey(ch)) continue;
            foreach (var (normal, echo) in t.RecentEcho)
            {
                if (normal && echo is { } e) echoes.Add(e);
            }
        }

        if (echoes.Count < 3) return null;
        echoes.Sort();
        return echoes[echoes.Count / 2];
    }

    private readonly Dictionary<ushort, SignalTrack> _signals = new();

    /// <summary>这一路曾经见过探头。重连不清：否则"无信号"会变成"待接入"从报警里消失。</summary>
    private readonly HashSet<ushort> _seenProbe = new();

    /// <summary>收一帧就更新该路的在位判定；判定翻转时返回 (旧, 新)，否则 null。</summary>
    private (string From, string To)? TrackSignal(ChannelResult m)
    {
        // 和桌面版同一个 Snr：那份带削顶保护（超过一半采样打满量程按 0 dB），这里之前少了这一句
        var snr = DeviceEngine.Snr(m.EchoWave);
        if (snr is null) return null;
        lock (_stateLock)
        {
            if (!_signals.TryGetValue(m.Channel, out var t))
            {
                _signals[m.Channel] = t = new SignalTrack();
                t.SeenProbe = _seenProbe.Contains(m.Channel);
            }
            t.LastSnr = snr.Value;
            t.LastAt = DateTime.UtcNow;
            t.Recent.Enqueue(snr.Value);
            if (t.Recent.Count > SnrWindow) t.Recent.Dequeue();
            t.RecentEcho.Enqueue((m.StatusCode == MeasStatusCode.Normal, m.EchoTime));
            if (t.RecentEcho.Count > SnrWindow) t.RecentEcho.Dequeue();
            if (t.Recent.Count < 5) return null;
            var strong = t.Recent.Count(s => s >= SnrProbeDb);
            var weak = t.Recent.Count(s => s < SnrNoneDb);

            // 第二条判据：耦合差的探头信噪比只有 10 dB 左右，但设备每帧都判正常、声时稳定，
            // 且和同法兰其他螺栓的声时同量级，按有探头处理
            var stableEcho = false;
            if (t.RecentEcho.Count >= 5 && ReferenceEchoNs() is { } refEcho)
            {
                var normals = t.RecentEcho.Where(e => e.Normal && e.Echo is { }).Select(e => e.Echo!.Value).ToList();
                if (normals.Count >= t.RecentEcho.Count * 0.8)
                {
                    var mean = normals.Average();
                    var spread = normals.Max() - normals.Min();
                    stableEcho = spread < 300 && Math.Abs(mean - refEcho) < refEcho * 0.3m
                                 && t.Recent.Average() >= 8;
                }
            }

            var next = strong >= t.Recent.Count * 0.6 || stableEcho ? "probe"
                : weak >= t.Recent.Count * 0.8 ? "none"
                : t.Presence;
            if (next == "probe")
            {
                t.SeenProbe = true;
                _seenProbe.Add(m.Channel);
            }
            if (next == t.Presence) return null;
            var prev = t.Presence;
            t.Presence = next;
            // 第一次从 unknown 定下来不算"变化"，那只是刚连上；之后的翻转才是接线动作/探头故障
            if (prev != "unknown") t.ChangedAt = DateTime.UtcNow;
            return (prev, next);
        }
    }

    /// <summary>标定与口径方案（启动时定，之后只读）。</summary>
    public CalPlan Plan { get; }

    /// <summary>TCP 还活着就算连着。帧停了由外面的看门狗按 LastFrameAt 判。</summary>
    public bool IsConnected => _engine is { IsConnected: true };

    /// <summary>
    /// 配置重载后这台采集器退役：不再连接（保活循环若正好在这一刻要连，进来也直接返回），
    /// 由重载流程断开它。设备只能让新的一代连，不然新旧两代抢同一台设备。
    /// </summary>
    public bool Retired { get; private set; }

    public void Retire() => Retired = true;

    /// <summary>最近一帧到达时间（UTC）。连上后先记当前时刻，避免刚切完模式就被判成帧停了。</summary>
    public DateTime LastFrameAt => new(Interlocked.Read(ref _lastFrameTicks), DateTimeKind.Utc);

    /// <summary>上次补读缺失测点的时间（UTC），连接成功也算一次。</summary>
    public DateTime LastChannelRetryAt { get; private set; } = DateTime.MinValue;

    // 声时差由本会话用缓存的参考声时算出。相邻两帧跳过 JumpNs 就停落库并重读基线。
    private const decimal JumpNs = 50m;
    private static readonly string[] StorageKeysToCompare = { "RefTime", "MeasMode", "Threshold" };
    private readonly Dictionary<ushort, (decimal Dt, DateTime At)> _lastDt = new();
    private readonly HashSet<ushort> _resyncPending = new();

    /// <summary>有测点的声时差跳变，或桌面端通知"我刚置零/改参数了"，等保活循环来做一次基线重读。</summary>
    public bool ResyncRequested { get; private set; }

    /// <summary>
    /// 桌面端置零或下发参数后的通知（POST /api/resync）。重新置零只改设备上的参考声时，帧里看不出来，
    /// 本会话会一直按旧基线算声时差；收到通知几秒内重读，不用等 10 分钟一次的巡检。
    /// </summary>
    public void RequestResync(string why)
    {
        ResyncRequested = true;
        _lastLog = $"{DateTime.Now:HH:mm:ss} {why}，重读基线中";
        Console.WriteLine(_lastLog);
    }

    /// <summary>与厂家同一套：开温补时用最新基线 + 当前温度走 CalcDeltaTof；基线还没读到才用 SDK 帧上的 DeltaTof。</summary>
    private decimal? ShownDelta(ChannelResult m)
    {
        lock (_stateLock)
        {
            if (_lastReadBaselines.TryGetValue(m.Channel, out var b))
                return DeviceEngine.DeltaTof(
                           m.EchoTime, b.RefTime, DeviceEngine.CompTemp(m), b.RefTemp,
                           Plan.TcMode, Plan.TempCoefficient)
                       ?? m.DeltaTof;
        }

        return m.DeltaTof;
    }

    private void NoteJump(ushort channel, MeasStatusCode status, decimal? deltaTof)
    {
        if (status != MeasStatusCode.Normal || deltaTof is not { } dt) return;
        var now = DateTime.UtcNow;
        lock (_stateLock)
        {
            if (_lastDt.TryGetValue(channel, out var last) && now - last.At < TimeSpan.FromSeconds(30)
                && Math.Abs(dt - last.Dt) > JumpNs && !_resyncPending.Contains(channel))
            {
                _resyncPending.Add(channel);
                ResyncRequested = true;
                _lastLog = $"{DateTime.Now:HH:mm:ss} CH{channel} 声时差瞬间跳了 {dt - last.Dt:+0;-0} ns，像是现场重新置零/改了参数，重读基线中";
                Console.WriteLine(_lastLog);
            }

            _lastDt[channel] = (dt, now);
        }
    }

    private bool IsResyncPending(ushort ch)
    {
        lock (_stateLock) return _resyncPending.Contains(ch);
    }

    /// <summary>跳变后的重读：停流 → 重读在测各路的存储参数/基线（变了的整路重读）→ 恢复轮询。</summary>
    public async Task ResyncBaselinesAsync()
    {
        await _gate.WaitAsync();
        try
        {
            ResyncRequested = false;
            if (_engine is not { IsConnected: true } engine) return;
            List<ushort> set;
            lock (_stateLock) set = _polled.ToList();
            await engine.StopStreamAsync();
            var changed = await RefreshBaselinesAsync(engine);
            await SwitchPollSetAsync(engine, set);
            lock (_stateLock)
            {
                _resyncPending.Clear();
                _lastDt.Clear();   // 按新基线从头比
            }

            if (changed == 0)
            {
                _lastLog = $"{DateTime.Now:HH:mm:ss} 重读基线：设备上的参考声时没变，声时差跳变是真实变化或参数改动（未重新置零）";
                Console.WriteLine(_lastLog);
            }

            BroadcastState();
        }
        finally
        {
            _gate.Release();
        }
    }

    public int ChannelCount { get { lock (_stateLock) return _params.Count; } }

    /// <summary>要监测的测点：各法兰 channels 之并；一个都没配按默认 CH1–CH4。</summary>
    public IReadOnlyList<ushort> WantedChannels => Plan.WantedChannels.Count > 0 ? Plan.WantedChannels : DefaultChannels;

    /// <summary>想监测但这次没读到参考声时、暂未纳入的测点。</summary>
    public IReadOnlyList<ushort> MissingChannels
    {
        get
        {
            lock (_stateLock) return WantedChannels.Where(c => !_params.ContainsKey(c)).ToList();
        }
    }

    /// <summary>有探头但没基线：出现后 30 分钟内按 2 分钟补读，过后按普通退避。</summary>
    public bool AnyMissingWithProbe
    {
        get
        {
            // 勤补读只在探头出现后的 30 分钟内。补读要停几秒流。
            var fresh = DateTime.UtcNow - TimeSpan.FromMinutes(30);
            lock (_stateLock)
            {
                return WantedChannels.Any(c => !_params.ContainsKey(c)
                                               && _signals.TryGetValue(c, out var t) && t.Presence == "probe"
                                               && (t.ChangedAt ?? _connectedAt) > fresh);
            }
        }
    }

    /// <summary>这条连接建立的时刻（UTC）：连上时探头就在位的，按连上算"刚出现"。</summary>
    private DateTime _connectedAt = DateTime.MinValue;

    /// <summary>这次连接尝试开始的时刻（UTC）：断开日志里写"这条连接维持了多久"，几秒就断和跑了几小时才断不是一回事。</summary>
    private DateTime _attemptStartedAt = DateTime.MinValue;

    /// <summary>
    /// 各测点最近一次读到参考声时的时刻（UTC），断线不清。重连时某路读不到参考声时、但几小时前还在测，
    /// 多半是回包丢了（链路上有别的程序在抢、VPN 抖动），按快速补读处理，不等 10 分钟。
    /// </summary>
    private readonly Dictionary<ushort, DateTime> _lastZeroedAt = new();

    /// <summary>快速补读名单：通道有回应但参考声时那条大回包没收到的，以及几小时内还在测、这次却整路没回音的。</summary>
    private readonly HashSet<ushort> _quickRetry = new();

    /// <summary>每条连接最多快速补读这么多次（每次停几秒流），之后回到普通退避。</summary>
    private const int QuickRetryBudget = 5;

    private int _quickRetriesLeft;

    /// <summary>有缺失测点在快速补读名单里且预算没用完：保活循环按 1 分钟而不是 10 分钟补读。</summary>
    public bool AnyMissingQuick
    {
        get
        {
            lock (_stateLock) return _quickRetriesLeft > 0 && _quickRetry.Any(c => !_params.ContainsKey(c));
        }
    }

    /// <summary>几小时内还在监测里的测点：重连时读不到参考声时，优先当成回包丢了。</summary>
    private bool RecentlyZeroed(ushort ch)
    {
        lock (_stateLock)
        {
            return _lastZeroedAt.TryGetValue(ch, out var at) && DateTime.UtcNow - at < TimeSpan.FromHours(6);
        }
    }

    /// <summary>帧停了之后已经重新下发过一次监测命令（收到帧就清掉）。再停才重连。</summary>
    public bool RearmTried { get; private set; }

    /// <summary>
    /// 连接还在但长时间没有测量帧：先不断线，重新下发轮询集并切回监测模式。
    /// 常见原因是厂家软件或桌面端连着同一台设备，把它切成了待机；这时重连只会多读一遍参数，还可能漏读。
    /// 重发后仍没帧，由看门狗重连。
    /// </summary>
    public async Task RearmStreamAsync()
    {
        await _gate.WaitAsync();
        try
        {
            RearmTried = true;
            if (_engine is not { IsConnected: true } engine) return;
            List<ushort> set;
            lock (_stateLock) set = _polled.ToList();
            if (set.Count == 0) return;
            await SwitchPollSetAsync(engine, set);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static readonly (string Key, string Cn)[] ParamOrder =
    {
        ("ProbeFreq", "探头频率 MHz"),
        ("EchoWin", "回波窗 ns"),
        ("EchoWidth", "回波宽度 us"),
        ("Duty", "激励占空比 %"),
        ("Forward", "正向脉冲数"),
        ("Reverse", "反向脉冲数"),
        ("Gain", "增益档"),
        ("MeasMode", "测量模式"),
        ("Threshold", "检波阈值"),
        ("RefTime", "参考声时 ns"),
        ("RefTemp", "参考温度 ℃"),
        ("LEchoWave", "参考波形"),
    };

    /// <summary>规划的探头位（各法兰 plannedChannels 之并）：也轮询，已置零的自动纳入监测，没置零的只做在位识别。</summary>
    public IReadOnlyList<ushort> Planned => Plan.PlannedChannels;

    /// <summary>巡检位（farm.json guardChannels）：只读寄存器、只轮询、只判有没有探头，抓插到规划外通道的探头。</summary>
    public IReadOnlyList<ushort> Guard { get; }

    public async Task ConnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Retired) return;
            if (_engine is not null) await CloseEngineAsync();

            var engine = new DeviceEngine();
            engine.OnLog += s =>
            {
                // 引擎日志自带 HH:mm:ss 前缀。这是逐条的调试流水，只给页面的运维视图看，不进日志文件
                _lastLog = s;
                Broadcast(new { type = "log", t = Turbine, text = s });
            };
            // 带上这条连接维持了多久、收到过几条回包：链接成功、一条回话都没有就在几秒内被断，是到设备的网络路径不通
            engine.OnDisconnected += reason =>
            {
                var held = DateTime.UtcNow - _attemptStartedAt;
                var rx = engine.ReceivedResults;
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} {Label} 设备连接断开：{reason}（这条连接维持 {Dur(held)}，收到 {rx} 条回包）"
                                  + (rx <= 1 && held < TimeSpan.FromSeconds(15) ? LinkHint : string.Empty));
            };
            engine.OnMeasurement += m =>
            {
                // 不是本会话轮询的通道：串口服务器把另一个客户端的回包也转给了我们，直接丢弃
                bool mine;
                lock (_stateLock) mine = _polled.Contains(m.Channel);
                if (!mine) return;

                Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                RearmTried = false;
                var dt = ShownDelta(m);
                NoteJump(m.Channel, m.StatusCode, dt);
                var flip = TrackSignal(m);
                // 只有配置在监测的通道才算测点帧，才落历史；规划位/巡检位的帧只带 monitored=false 给页面做在位识别
                bool zeroed;
                double? snr;
                var presence = "unknown";
                var seenProbe = false;
                var settled = false;
                lock (_stateLock)
                {
                    zeroed = _params.ContainsKey(m.Channel);
                    if (_signals.TryGetValue(m.Channel, out var tr))
                    {
                        snr = tr.LastSnr;
                        presence = tr.Presence;
                        seenProbe = tr.SeenProbe;
                        // 在位判定要攒几帧才定下来；定不下来（信噪比常年悬在灰区）的攒满一窗也算"看过了"
                        settled = tr.Presence != "unknown" || tr.Recent.Count >= SnrWindow;
                    }
                    else
                    {
                        snr = null;
                    }
                }

                // 力值和状态只在这里算一次：帧、历史、事件、页面全用同一个数、同一个结论
                var mismatch = Health.IsBaselineMismatch(dt);
                var estKn = mismatch ? null : Plan.EstKn(m.Channel, dt);
                var deltaKn = mismatch ? null : Plan.DeltaKn(m.Channel, dt);
                var clampKn = mismatch ? null : Plan.ClampKn(m.Channel, dt);
                var health = zeroed ? Health.Judge(m.StatusCode == MeasStatusCode.Normal, mismatch, presence, Plan, m.Channel, estKn, seenProbe) : null;
                if (zeroed && health is { Length: > 0 } judged && judged != "pending")
                {
                    RememberOwner(m.Channel, judged, estKn, deltaKn);
                }

                Broadcast(FrameJson(m, snr, zeroed, mismatch, estKn, deltaKn, clampKn, health, dt));
                // 刚连上、在位判定还没定下来的那几帧不进历史：空通道会先记"检测异常"再改"探头无信号"，每次重连都多两条假事件。
                // 声时差刚跳变、基线正在重读的那几秒也不进：按旧基线算出来的几百 kN 假松动不能写进历史和事件
                if (zeroed && settled && health != "pending" && !IsResyncPending(m.Channel)) _history.Ingest(Turbine, m, health!, estKn, deltaKn, dt);
                if (flip is { } f)
                {
                    var configured = WantedChannels.Contains(m.Channel);
                    bool hasBase;
                    lock (_stateLock) hasBase = zeroed || _zeroedExtra.Contains(m.Channel);
                    var msg = f.To == "probe"
                        ? (zeroed ? $"CH{m.Channel} 探头信号恢复" : configured
                            ? $"CH{m.Channel} 有探头信号，但该通道读不到参考声时"
                            : $"CH{m.Channel} 出现探头信号，此通道未在配置中{(hasBase ? "（设备上已有基线）" : "（且未置零）")} —— 探头可能插错位置")
                        : f.To == "none" && f.From == "probe"
                            ? (configured ? $"CH{m.Channel} 探头信号消失 —— 可能被拔出或接到了别的通道" : $"CH{m.Channel} 无探头信号")
                            : null;
                    if (msg is not null)
                    {
                        Console.WriteLine($"{DateTime.Now:HH:mm:ss} 在位识别：{msg}");
                        _lastLog = $"{DateTime.Now:HH:mm:ss} {msg}";
                        Broadcast(new { type = "log", t = Turbine, text = _lastLog });
                    }

                    BroadcastState();
                }
            };

            var opt = new DeviceOptions
            {
                Endpoint = ConfiguredEndpoint,
                Address = 1,
                Resume = "multi",
                TcMode = Plan.TcMode,
                TempCoefficient = Plan.TempCoefficient,
                TempTau = Plan.TempTau,
                TempFrom = Plan.TempFrom,
                // 桌面端置零时存的自动法锚点（exe 旁 data\anchors.json，两个程序发布在同一个文件夹时就是同一份）
                Anchors = AnchorStore.Open(AppContext.BaseDirectory, ConfiguredEndpoint),
            };
            _attemptStartedAt = DateTime.UtcNow;
            var attemptStarted = _attemptStartedAt;
            RearmTried = false;
            await engine.OpenAsync(opt, CancellationToken.None);

            // 读参数要十几秒，期间链路可能断开。SDK 在会话断开后读写不报错，这里不查的话，
            // 下面会在死链路上逐条等超时，2 分钟后才以"没有回包"失败，原因也不对
            void ThrowIfDropped()
            {
                if (engine.IsConnected) return;
                var held = DateTime.UtcNow - attemptStarted;
                var rx = engine.ReceivedResults;
                throw new InvalidOperationException(
                    $"连上 {Dur(held)}后被对方断开（{engine.LastDisconnectReason ?? "原因未知"}），期间收到 {rx} 条回包"
                    + (rx <= 1 && held < TimeSpan.FromSeconds(15) ? LinkHint : "。链路中途断了，稍后重连"));
            }

            try
            {
                ThrowIfDropped();
            }
            catch
            {
                try { await engine.CloseAsync(); } catch { /* 本来就没连上 */ }
                throw;
            }

            // 连的过程中被退役（配置重载、停服务）：把刚占上的设备还回去，不再往下读参数
            if (Retired)
            {
                try { await engine.CloseAsync(); } catch { /* 退役了，尽力而为 */ }
                return;
            }

            try
            {
                // 参数在待机下读。监测中读命令只零散应答，参考波形 600 点，快读会漏，没有参考声时就算不出 Δt。
                var fresh = new SortedDictionary<ushort, List<KeyValuePair<string, string>>>();
                var freshWaves = new SortedDictionary<ushort, object>();
                var baselines = new Dictionary<ushort, (decimal? RefTime, decimal? RefTemp)>();
                var signalOnly = new List<ushort>();   // 只轮询做在位识别、不测力：规划位/巡检位，以及配置了但没置零的
                var zeroedExtra = new HashSet<ushort>(); // 规划位里设备上已有基线的（历史置过零），给页面提示用
                var quick = new HashSet<ushort>();       // 配置的测点里这次没读全、但多半是回包被吞了的：连上后 1 分钟就补读
                // 配置的测点和规划位一起读。只有 channels 里的才算监测，规划位即使置过零也只做在位识别，
                // 设备里常有遗留基线，不能因此把还没装探头的位子当测点报探头丢失
                var pending = WantedChannels.Concat(Planned).Distinct().OrderBy(c => c).ToList();
                const int passes = 2;
                for (var pass = 1; pass <= passes && pending.Count > 0; pass++)
                {
                    // 第二遍先歇两秒：大回包可能还在路上，紧接着重发会跟它冲突（半双工链路）
                    if (pass > 1) await Task.Delay(2000);
                    var still = new List<ushort>();
                    foreach (var ch in pending)
                    {
                        ThrowIfDropped();
                        var wanted = WantedChannels.Contains(ch);
                        var r = await ReadChannelIntoAsync(engine, ch, wanted ? fresh : null, freshWaves, baselines);
                        // 整路没回应的下一遍再读；寄存器回了、只差参考声时那条带 600 点波形的大回包的，也再读一遍，
                        // 不按未置零处理
                        if (r == ReadOutcome.NoReply || (r == ReadOutcome.NoStorageReply && pass < passes)) { still.Add(ch); continue; }
                        if (!wanted || r != ReadOutcome.Zeroed) signalOnly.Add(ch);
                        if (wanted && r == ReadOutcome.NoStorageReply) quick.Add(ch);
                        if (!wanted && r == ReadOutcome.Zeroed) zeroedExtra.Add(ch);
                    }

                    pending = still;
                }

                foreach (var ch in pending)
                {
                    _lastLog = $"{DateTime.Now:HH:mm:ss} CH{ch} 一条回包都没有（通道不存在），暂不纳入";
                    // 几小时前还在测的测点整路没回音：多半是链路把回包吞了（另一个程序在抢、VPN 抖），也快速补读
                    if (WantedChannels.Contains(ch) && RecentlyZeroed(ch)) quick.Add(ch);
                }

                // 巡检位只读寄存器（快），能回包就轮询，用来抓插到规划外通道的探头
                foreach (var ch in Guard.Except(fresh.Keys).Except(signalOnly).Except(pending))
                {
                    ThrowIfDropped();
                    try
                    {
                        var regs = await engine.ReadRegisterValuesAsync(ch, CancellationToken.None);
                        if (regs.Count > 0) signalOnly.Add(ch);
                    }
                    catch (Exception ex)
                    {
                        _lastLog = $"{DateTime.Now:HH:mm:ss} CH{ch} 巡检位读寄存器失败：{ex.Message}";
                    }
                }

                ThrowIfDropped();

                // 记下能回包的通道。连上后先按巡检宽集轮一遍（规划位、巡检位都看有没有探头），
                // 45 秒后收窄到配置通道，空通道测得慢，一直全轮会拖慢测点刷新
                var reachable = fresh.Keys.Concat(signalOnly).Distinct().OrderBy(c => c).ToList();
                if (reachable.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"配置的测点（{string.Join("、", WantedChannels.Select(c => "CH" + c))}）一条回包都没有，无法监测"
                        + "。TCP 通着但设备不回话：串口服务器后面的设备没应答（可能被另一个程序占着切了模式，或设备死机）");
                }

                if (fresh.Count == 0)
                {
                    _lastLog = $"{DateTime.Now:HH:mm:ss} 配置的测点都读不到参考声时，先只做探头在位识别";
                }

                // 灌会话（含探头频率兜底）→ 下发轮询集 → 切监测
                await engine.ResumeMonitorAsync(reachable, CancellationToken.None);
                ThrowIfDropped();

                lock (_stateLock)
                {
                    _params.Clear();
                    _refWaves.Clear();
                    _signals.Clear();
                    _zeroedExtra.Clear();
                    foreach (var kv in fresh) _params[kv.Key] = kv.Value;
                    foreach (var kv in freshWaves) if (fresh.ContainsKey(kv.Key)) _refWaves[kv.Key] = kv.Value;
                    foreach (var ch in zeroedExtra) _zeroedExtra.Add(ch);
                    _reachable = reachable;
                    _polled = reachable;
                    _lastReadBaselines = baselines;
                    ScanActive = true;
                    ScanStartedAt = DateTime.UtcNow;
                    LastScanAt = DateTime.UtcNow;
                    var now = DateTime.UtcNow;
                    foreach (var ch in fresh.Keys) _lastZeroedAt[ch] = now;
                    _quickRetry.Clear();
                    foreach (var ch in quick) _quickRetry.Add(ch);
                    _quickRetriesLeft = QuickRetryBudget;
                }
            }
            catch
            {
                try { await engine.CloseAsync(); } catch { /* 尽力恢复设备模式并释放 TCP */ }
                throw;
            }

            // 读参数那十几秒里被退役：同上，还回去。过了这一行 _engine 才归这个采集器，DisconnectAsync 才能管到它
            if (Retired)
            {
                try { await engine.CloseAsync(); } catch { /* 退役了，尽力而为 */ }
                return;
            }

            Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
            LastChannelRetryAt = DateTime.UtcNow;
            _connectedAt = DateTime.UtcNow;
            _engine = engine;
            _endpoint = ConfiguredEndpoint;
            // 读到的基线交历史库比对：和上次记的不一样就是现场重新置零过，记流水 + "基线重置"事件
            Dictionary<ushort, (decimal? RefTime, decimal? RefTemp)> read;
            lock (_stateLock) read = _lastReadBaselines;
            if (read.Count > 0) _history.RecordBaselines(Turbine, read);
            BroadcastState();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>连接流程里读到的各路基线，连接成功后交给历史库。放字段是为了不改 ConnectAsync 里那段 try 的结构。</summary>
    private Dictionary<ushort, (decimal? RefTime, decimal? RefTemp)> _lastReadBaselines = new();

    // 维护模式：大屏断开并在期限内不重连，把 TCP 让给桌面端。到期自动恢复。

    /// <summary>维护截止时刻（UTC）；空 = 不在维护。</summary>
    public DateTime? MaintenanceUntil { get; private set; }

    public string MaintenanceNote { get; private set; } = string.Empty;

    public bool InMaintenance => MaintenanceUntil is { } u && u > DateTime.UtcNow;

    public async Task EnterMaintenanceAsync(TimeSpan duration, string note)
    {
        MaintenanceUntil = DateTime.UtcNow + duration;
        MaintenanceNote = note;
        _lastLog = $"{DateTime.Now:HH:mm:ss} 进入维护模式（{note}），{MaintenanceUntil.Value.ToLocalTime():HH:mm} 自动恢复监测；已让出设备连接";
        Console.WriteLine(_lastLog);
        await DisconnectAsync();   // 里面会广播 state
    }

    /// <summary>已经在维护里：只把截止时间往后推，不再断开一次。</summary>
    public void ExtendMaintenance(TimeSpan duration, string note)
    {
        MaintenanceUntil = DateTime.UtcNow + duration;
        if (!string.IsNullOrWhiteSpace(note)) MaintenanceNote = note;
        BroadcastState();
    }

    public void ExitMaintenance()
    {
        if (MaintenanceUntil is null) return;
        MaintenanceUntil = null;
        MaintenanceNote = string.Empty;
        _lastLog = $"{DateTime.Now:HH:mm:ss} 维护结束，恢复连接设备";
        Console.WriteLine(_lastLog);
        BroadcastState();
    }

    /// <summary>
    /// 配置重载时把还活着的会话记忆从老一代带过来：维护租约、探头在位、业主快照。
    /// 现场正在用桌面端置零并保存 farm.json 时，新一代不能立刻抢设备；探头曾经见过也不能变成「待接入」。
    /// </summary>
    public void InheritLiveMemory(MonitorHub from)
    {
        if (from.InMaintenance)
        {
            MaintenanceUntil = from.MaintenanceUntil;
            MaintenanceNote = from.MaintenanceNote;
            _lastLog = $"{DateTime.Now:HH:mm:ss} 维护中（{MaintenanceNote}），配置重载后继续让出设备，{MaintenanceUntil!.Value.ToLocalTime():HH:mm} 恢复";
        }

        HashSet<ushort> seen;
        Dictionary<ushort, OwnerShot> owner;
        lock (from._stateLock)
        {
            seen = new HashSet<ushort>(from._seenProbe);
            owner = new Dictionary<ushort, OwnerShot>(from._owner);
        }

        lock (_stateLock)
        {
            foreach (var ch in seen) _seenProbe.Add(ch);
            foreach (var kv in owner) _owner[kv.Key] = kv.Value;
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_engine is null) return;
            await CloseEngineAsync();
            BroadcastState();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 把连接时没读到参考声时的测点再试一遍：短暂停流 → 待机下补读 → 带上全部测点恢复监测。
    /// 不断 TCP，页面上其他测点只是停几秒帧，不会闪"设备未连接"。返回新纳入的测点数。
    /// </summary>
    public async Task<int> RetryMissingChannelsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            LastChannelRetryAt = DateTime.UtcNow;
            if (_engine is not { IsConnected: true } engine) return 0;
            var missing = MissingChannels;
            if (missing.Count == 0) return 0;
            var wasQuick = AnyMissingQuick;

            var fresh = new SortedDictionary<ushort, List<KeyValuePair<string, string>>>();
            var freshWaves = new SortedDictionary<ushort, object>();
            var baselines = new Dictionary<ushort, (decimal? RefTime, decimal? RefTemp)>();
            var storageMissed = new List<ushort>();
            await engine.StopStreamAsync();
            try
            {
                foreach (var ch in missing)
                {
                    if (!engine.IsConnected) break;   // 链路断了，保活循环会重连；别在死链路上一条条等超时
                    var r = await ReadChannelIntoAsync(engine, ch, fresh, freshWaves, baselines);
                    if (r == ReadOutcome.NoStorageReply) storageMissed.Add(ch);
                }
            }
            finally
            {
                // 读成什么样都要把流恢复回来；恢复失败就让看门狗按"没有帧"重连
                ushort[] all;
                lock (_stateLock)
                {
                    var now = DateTime.UtcNow;
                    foreach (var kv in fresh)
                    {
                        _params[kv.Key] = kv.Value;
                        _lastZeroedAt[kv.Key] = now;
                        _quickRetry.Remove(kv.Key);
                    }

                    foreach (var kv in freshWaves) _refWaves[kv.Key] = kv.Value;
                    foreach (var kv in baselines) _lastReadBaselines[kv.Key] = kv.Value;
                    // 这次又只差参考声时那条回包的，留在快速名单里再试；预算按次扣，免得链路一直吵就一直每分钟停流
                    foreach (var ch in storageMissed) _quickRetry.Add(ch);
                    if (wasQuick) _quickRetriesLeft--;
                    _reachable = _reachable.Concat(fresh.Keys).Distinct().OrderBy(c => c).ToList();
                    _polled = _polled.Concat(fresh.Keys).Distinct().OrderBy(c => c).ToList();
                    all = _polled.ToArray();
                }

                if (engine.IsConnected)
                {
                    await engine.ResumeMonitorAsync(all, CancellationToken.None);
                    Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                }
            }

            if (baselines.Count > 0) _history.RecordBaselines(Turbine, baselines);
            if (fresh.Count > 0) BroadcastState();
            return fresh.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// NoReply：一条回包都没有。NoStorageReply：寄存器回了，参考声时那条存储区回包（带 600 点参考波形，最容易丢）没到，
    /// 不确定是否置零。NotZeroed：设备明确回答未写入。Zeroed：有参考声时。
    /// </summary>
    private enum ReadOutcome { NoReply, NoStorageReply, NotZeroed, Zeroed }

    /// <summary>
    /// 待机下全量读一个通道：一条回包都没有算不存在；有回包没置零只算"在"；已置零（有参考声时）
    /// 且给了 into 时收进监测结果（into 为 null 表示这一路只做在位识别，不纳入监测）。
    /// </summary>
    private static async Task<ReadOutcome> ReadChannelIntoAsync(DeviceEngine engine, ushort ch,
        SortedDictionary<ushort, List<KeyValuePair<string, string>>>? into,
        SortedDictionary<ushort, object> waves,
        Dictionary<ushort, (decimal? RefTime, decimal? RefTemp)> baselines)
    {
        var bag = await engine.ReadChannelValuesAsync(ch, CancellationToken.None);
        if (bag.Count == 0) return ReadOutcome.NoReply;
        if (!DeviceEngine.IsZeroed(bag))
        {
            return DeviceEngine.Raw(bag, "RefTime") == "-" ? ReadOutcome.NoStorageReply : ReadOutcome.NotZeroed;
        }
        // 设备上这一路的基线（参考声时/参考温度）：交给历史库比对，变了就是现场重新置零过
        baselines[ch] = (ParseDec(DeviceEngine.Raw(bag, "RefTime")), ParseDec(DeviceEngine.Raw(bag, "RefTemp")));
        if (into is null) return ReadOutcome.Zeroed;

        into[ch] = ParamOrder.Select(p => new KeyValuePair<string, string>(p.Cn, Render(p.Key, bag))).ToList();
        if (engine.RefWave(ch) is { WaveData.Count: > 0 } rw)
        {
            waves[ch] = new { start = rw.EchoStart, clock = rw.ClockInt, data = rw.WaveData };
        }

        return ReadOutcome.Zeroed;
    }

    private async Task CloseEngineAsync()
    {
        try { await _engine!.CloseAsync(); }
        catch { /* 断线状态下恢复不了，设备重启后自己回监测 */ }
        _engine = null;
        _endpoint = "";
        lock (_stateLock)
        {
            _params.Clear();
            _refWaves.Clear();
            _signals.Clear();
            _zeroedExtra.Clear();
            _polled = new List<ushort>();
            _lastDt.Clear();
            _resyncPending.Clear();
            _quickRetry.Clear();   // _lastZeroedAt 故意不清：下次连接据它判断"读不到参考声时"是不是回包被吞了
            _lastReadBaselines.Clear();
        }

        ResyncRequested = false;
        RearmTried = false;
    }

    /// <summary>
    /// TCP 握手成功但没收到任何回包就断开时，附在日志后的提示。
    /// 常见原因：设备在 VPN 对面而 VPN 没连上，本机代理软件的 TUN 模式代为完成握手，几秒后拨号超时再断开。
    /// </summary>
    private const string LinkHint =
        "——TCP 握手成功但设备没有任何回话就被断：多半是到设备的网络路径不通（VPN 没连上，或被本机代理软件的 TUN 模式接管后拨号失败），不是设备故障";

    /// <summary>时长写成人话：不足 1 分钟写秒，否则写分钟。</summary>
    private static string Dur(TimeSpan ts) =>
        ts.TotalMinutes >= 1 ? $"{ts.TotalMinutes:0} 分钟" : $"{ts.TotalSeconds:0} 秒";

    internal void RememberOwner(ushort channel, string health, decimal? estKn, decimal? deltaKn)
    {
        var shot = new OwnerShot(channel, health, estKn, deltaKn, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        lock (_stateLock) _owner[channel] = shot;
    }

    /// <summary>有基线的通道。超过 60 秒没有新帧算断流，和页面的 STALE_MS 一致。</summary>
    internal List<(OwnerShot Shot, bool Fresh)> CopyOwner(long nowMs)
    {
        lock (_stateLock)
        {
            var keys = new HashSet<ushort>(_params.Keys);
            foreach (var ch in _owner.Keys) keys.Add(ch);
            var list = new List<(OwnerShot, bool)>(keys.Count);
            foreach (var ch in keys.OrderBy(c => c))
            {
                if (_owner.TryGetValue(ch, out var shot) && nowMs - shot.AtMs <= OwnerBrief.StaleMs)
                {
                    list.Add((shot, true));
                }
                else if (_owner.TryGetValue(ch, out var last))
                {
                    list.Add((last with { Health = "offline", EstKn = null, DeltaKn = null }, false));
                }
                else
                {
                    list.Add((new OwnerShot(ch, "offline", null, null, 0), false));
                }
            }

            return list;
        }
    }

    public object State()
    {
        // 先在锁里把通道列表物化：匿名对象里若留着惰性 LINQ，序列化时枚举的就是正在被改的字典
        List<object> channels, signals;
        List<ushort> polled;
        lock (_stateLock)
        {
            channels = _params.Select(kv => (object)new
            {
                ch = kv.Key,
                flange = Plan.FlangeOf(kv.Key),
                @params = kv.Value.Select(p => new { n = p.Key, v = p.Value }).ToList(),
                refWave = _refWaves.GetValueOrDefault(kv.Key),
            }).ToList();
            polled = _polled.ToList();
            // 每一路的在位判定：页面据此区分"探头拔空"和"未配置通道冒出探头"
            signals = _signals.OrderBy(kv => kv.Key).Select(kv => (object)new
            {
                ch = kv.Key,
                flange = Plan.FlangeOf(kv.Key),
                snr = Math.Round(kv.Value.LastSnr, 1),
                presence = kv.Value.Presence,
                zeroed = _params.ContainsKey(kv.Key) || _zeroedExtra.Contains(kv.Key),
                configured = WantedChannels.Contains(kv.Key),
                planned = Planned.Contains(kv.Key),
                at = kv.Value.LastAt,
                changedAt = kv.Value.ChangedAt,
            }).ToList();
        }

        static object CalJson(Ucf.Cal? c, string source) => c is { }
            ? new { k = c.K, b = c.B, unit = c.Unit, name = c.FileName, points = c.Points, umPerNs = Ucf.UmPerNs, source }
            : new { k = 0m, b = 0m, unit = "kN", name = "未标定", points = 0, umPerNs = Ucf.UmPerNs, source };

        return new
        {
            turbine = Turbine,
            connected = _engine is { IsConnected: true },
            endpoint = ConfiguredEndpoint,
            log = _lastLog,
            // 维护模式：大屏主动让出设备给现场桌面端，到期自动恢复。页面据此把"未连接"说成"维护中"
            maintenance = InMaintenance ? new { until = MaintenanceUntil, note = MaintenanceNote } : null,
            // 全局兜底标定（程序目录）；k = 0 表示未标定
            cal = CalJson(Plan.GlobalCal, Plan.GlobalCal is null ? "无" : "程序目录"),
            // 基线口径与阈值由服务端统一给出，页面不再自己从 farm.json 猜：两边算出来的必须是同一个数
            baseline = Plan.Mode == BaselineMode.Anchor ? "anchor" : "free",
            baselineText = Plan.ModeText,
            rated = Plan.GlobalRated,
            warnRatio = Plan.WarnRatio,
            alarmRatio = Plan.AlarmRatio,
            // 每个法兰的接线与标定：通道归属、在测/规划位、用的哪份 .ucf、额定多少。页面按这个分组，不再假设只有一个绑定法兰
            flanges = Plan.Flanges.Select(f => new
            {
                index = f.Index,
                name = f.Name,
                onDevice = f.OnDevice,
                channels = f.Channels,
                planned = f.Planned,
                // 接线表：通道 → 螺栓号，页面按它把探头画到图纸上的那颗螺栓
                bolts = f.Bolts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                rated = f.Rated,
                cal = CalJson(f.Cal, f.CalSource),
                specs = f.Channels.Concat(f.Planned).Distinct().ToDictionary(
                    ch => ch.ToString(),
                    ch => new { rated = Plan.RatedFor(ch), cal = CalJson(Plan.CalFor(ch), Plan.CalSourceFor(ch)) }),
            }).ToList(),
            channels,
            polled,
            signals,
            scanning = ScanActive,
        };
    }

    private object FrameJson(ChannelResult m, double? snr, bool monitored, bool mismatch, decimal? estKn, decimal? deltaKn, decimal? clampKn, string? health, decimal? deltaTof)
    {
        return new
        {
            type = "frame",
            t = Turbine,
            ch = m.Channel,
            monitored,
            snr = snr is { } s ? Math.Round(s, 1) : (double?)null,
            echoTime = m.EchoTime,
            deltaTof,
            mismatch,
            estKn,
            deltaKn,
            clampKn,
            // 服务端判定（Health.Judge）；规划位/巡检位的帧没有
            health,
            temp = m.SpTemp ?? m.RawTemp,
            status = DeviceEngine.DescribeStatus(m.StatusCode),
            normal = m.StatusCode == MeasStatusCode.Normal,
            wave = m.EchoWave?.WaveData is { Count: > 0 }
                ? new { start = m.EchoWave.EchoStart, clock = m.EchoWave.ClockInt, data = m.EchoWave.WaveData }
                : null,
        };
    }

    private static decimal? ParseDec(string s)
        => decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string Render(string key, Dictionary<string, string> bag) => key switch
    {
        "MeasMode" => DeviceEngine.Raw(bag, key) switch
        {
            "0" => "0（自动峰值）",
            "1" => "1（位置法）",
            "2" => "2（阈值法）",
            var other => other,
        },
        "LEchoWave" => bag.TryGetValue("WavePts", out var pts) && pts != "0"
            ? $"{pts} 点 @ {bag.GetValueOrDefault("WaveStart", "?")}ns"
            : "无",
        _ => DeviceEngine.Raw(bag, key),
    };
}
