// 业主监视大屏（只读、无登录）：本服务用 SDK 直连设备（FxDevice 的 DeviceEngine），
// 浏览器通过 WebSocket 收实时帧。局域网里任何电脑/手机开网页就能看。
// 用法：BoltPreload.Monitor.exe [--port 5080]，然后浏览器开 http://本机IP:5080/

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

// 页面跟着 exe 走，从哪个目录启动都能找到
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// 业主机上按 Windows 服务跑（安装大屏服务.cmd）：没有窗口、开机即起、意外退出由系统按恢复策略重启。
// 由服务控制器启动时这里自动切到服务模式；双击 exe 就是普通控制台，调试方便
builder.Host.UseWindowsService(o => o.ServiceName = "BoltPreloadMonitor");
var app = builder.Build();

// Web 默认规则：字段名大小写不敏感（页面发的是小写 endpoint）
var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);

var asService = WindowsServiceHelpers.IsWindowsService();
if (!asService) Console.OutputEncoding = Encoding.UTF8;
// 控制台上写的每一行同时落到 exe 旁 logs\monitor-yyyyMMdd.log：服务模式根本没有控制台，
// 控制台模式窗口一关也没了，昨晚为什么断了、什么时候重载过配置全靠这个文件。保留 30 天
Console.SetOut(new TeeLog(Console.Out, Path.Combine(AppContext.BaseDirectory, "logs"), keepDays: 30));

if (!asService)
{
    // 双击起来的控制台：先说清楚这是什么、别关、网页在哪。下面滚的是运行日志，不是报错
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
// 没有标定就不算力值——历史里 kn 留空、状态记"未标定"，页面只显示声时差。宁可没数，不能有假数。
var farm = LoadFarm(Console.WriteLine, out var farmError);
if (farm is null)
{
    Console.WriteLine($"{farmError}，按默认 192.168.0.7:23 / F01 / CH1–CH4 跑");
    farm = FarmPlan.Load(JsonDocument.Parse("""{"device":"192.168.0.7:23","bind":{"turbine":1,"flangeIndex":0}}""").RootElement, AppContext.BaseDirectory, Console.WriteLine);
}

var legacyTurbine = farm.Devices[0].Turbine;
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

// 保活循环的节拍（StartLoops 用；重载配置会再起一套循环，所以放在端点之前定义）
// TCP 没断但测量帧停了（设备被切到待机、卡死、链路半开）也算断：超过这个时间没有帧就重连，
// 不然页面会一直拿最后一帧当"实时"显示。
var stallAfter = TimeSpan.FromSeconds(90);
// 连接时没读到参考声时的测点，隔一阵再试（不重连，只短暂停流补读）。补读要停几秒流，
// 一直读不到的（通道确实没置零）就逐次拉长间隔，最长一小时一次，别老打断业主的实时画面。
// 例外：缺参考声时但探头在位的测点（刚装上、现场正在置零）两分钟补读一次，桌面端置零完大屏很快接上。
var retryMissingMin = TimeSpan.FromMinutes(10);
var retryMissingMax = TimeSpan.FromHours(1);
var retryWithProbe = TimeSpan.FromMinutes(2);
// 探头在位巡检：连上先巡一轮，之后每 10 分钟一轮（本机 POST /api/scan 可随时触发），每轮轮宽集 45 秒
var scanLength = TimeSpan.FromSeconds(45);
var scanEvery = TimeSpan.FromMinutes(10);
// 同一时刻只允许一次配置重载（文件监视和手动触发共用）
var reloadGate = new SemaphoreSlim(1, 1);

app.UseDefaultFiles();
// 静态文件每次都让浏览器回来核对（ETag 命中就 304，代价很小）。默认不发 Cache-Control 时浏览器按启发式
// 缓存 app.js 好几天：升级大屏后业主的浏览器还在跑旧前端、对新后端的数据，算出来的力值就是错的
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

// 业主页面是只读的，改设备地址/断开这两个动作只允许在大屏主机上本地调用，
// 局域网里随便一台电脑不能把大屏指到别的设备上去。
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
    if (minutes <= 0)
    {
        hub.ExitMaintenance();
    }
    else
    {
        await hub.EnterMaintenanceAsync(TimeSpan.FromMinutes(Math.Min(minutes, 24 * 60)), string.IsNullOrWhiteSpace(req?.Note) ? "现场置零/调参" : req!.Note!.Trim());
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

app.MapGet("/api/state", () => Results.Json(FullState()));

// turbine 不给 = 全场（报告页、报警中心）；给了 = 只这台机组（机组页、法兰页趋势）
app.MapGet("/api/history", (string? range, int? turbine) => Results.Json(history.QuerySamples(range ?? "24h", turbine)));
app.MapGet("/api/events", (string? range, int? turbine) => Results.Json(history.QueryEvents(range ?? "24h", turbine)));
app.MapGet("/api/report", (string? range, int? turbine) => Results.Json(history.QueryReport(range ?? "24h", turbine)));
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
    await broadcaster.ServeSocketAsync(ws, ctx.RequestAborted);
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    foreach (var h in site.Hubs) h.DisconnectAsync().GetAwaiter().GetResult();
    history.Dispose();
});

Console.WriteLine($"监视服务已启动：http://localhost:{port}/  （局域网访问用本机 IP）");
Console.WriteLine($"基线口径：{site.Mode.ModeText}" + (site.Mode.Mode == BaselineMode.Anchor
    ? "——k·Δt 是较锚定时刻的变化量，绝对预紧力按“锚定时 = 额定值”估算"
    : "——k·Δt + b 就是绝对预紧力"));
Console.WriteLine($"历史库：{history.DbPath}（采样每 5 秒一条保留 90 天，事件保留 2 年）");
DescribeSite(site);

void DescribeSite(Site s)
{
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
                          + (extra.Count > 0 ? $"，在位识别另轮询 {string.Join("、", extra.Select(c => "CH" + c))}" : string.Empty)
                          + (note.Length > 0 ? $"。{note}" : string.Empty));
    }
}

// ── 重载配置：farm.json 改了（接线/标定/额定/设备）不用重启服务 ──────────────────────────
// 新配置先解析，解析不过或没有设备就拒绝，原配置原样跑；解析过了才让老的一代退役：不再重连 → 断开设备 →
// 新的一代建采集器、起保活循环 → 通知页面刷新（页面要重新拿 farm.json 里的法兰模板和新的 state）。
// 同一时刻只允许一次重载；文件监视和手动触发共用这一个入口
async Task<(bool Ok, string Message)> ReloadAsync(string trigger)
{
    await reloadGate.WaitAsync();
    try
    {
        var notes = new List<string>();
        var fresh = LoadFarm(notes.Add, out var error);
        if (fresh is null)
        {
            var msg = $"{error}，配置未改动，继续按原配置监测";
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} 重新加载配置（{trigger}）失败：{msg}");
            return (false, msg);
        }

        Console.WriteLine($"{DateTime.Now:HH:mm:ss} 重新加载配置（{trigger}）……");
        foreach (var n in notes) Console.WriteLine(n);

        // 老的一代：先退役（保活循环不再重连它），再断开设备。断开要等它手里正在做的连接/补读做完
        var old = site;
        foreach (var h in old.Hubs) h.Retire();
        old.Cts.Cancel();
        foreach (var h in old.Hubs)
        {
            try { await h.DisconnectAsync(); }
            catch (Exception ex) { Console.WriteLine($"{DateTime.Now:HH:mm:ss} {h.Label} 断开出错：{ex.Message}"); }
        }

        // 新的一代。同一台机组还在维护期的，维护状态带过去，别一重载就把设备从桌面端手里抢回来
        history.SetPlans(fresh.Devices.Select(d => (d.Turbine, d.Plan)));
        var next = new Site(fresh, fresh.Devices.Select(d => new MonitorHub(d.Turbine, d.Endpoint, d.Plan, d.Guard, broadcaster, history)).ToList());
        foreach (var h in next.Hubs)
        {
            if (old.ByTurbine.TryGetValue(h.Turbine, out var prev)) h.InheritMaintenance(prev);
        }

        site = next;
        DescribeSite(next);
        StartLoops(next);
        // 页面自己刷新一遍：法兰模板（颗数/编号/名字）是页面直接读 farm.json 的，state 也全换了
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

// farm.json 一保存就重载（编辑器保存常是"写临时文件再改名"，Created/Renamed 也要接；连着几次写只重载一次）
var farmWatcher = new FileSystemWatcher(Path.GetDirectoryName(farmPath)!, "farm.json")
{
    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
};
var farmChangeDebounce = new System.Timers.Timer(1500) { AutoReset = false };
farmChangeDebounce.Elapsed += (_, _) => _ = Task.Run(() => ReloadAsync("farm.json 改动"));
void OnFarmChanged(object? sender, FileSystemEventArgs e)
{
    farmChangeDebounce.Stop();
    farmChangeDebounce.Start();
}
farmWatcher.Changed += OnFarmChanged;
farmWatcher.Created += OnFarmChanged;
farmWatcher.Renamed += (s, e) => OnFarmChanged(s, e);
farmWatcher.EnableRaisingEvents = true;

// ── 自动连接：展示页上没有连接按钮，由服务自己连设备并保活；每台设备一条循环（参数见上面 stallAfter 等） ──
StartLoops(site);

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
                                          ? $"；{string.Join("、", missing.Select(c => "CH" + c))} 读不到参考声时，{retryMissingEvery.TotalMinutes:0} 分钟后重试"
                                          : string.Empty));
                }
                else if (DateTime.UtcNow - hub.LastFrameAt > stallAfter)
                {
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {tag} 连接还在但 {stallAfter.TotalSeconds:0} 秒没有测量帧，重连设备");
                    await hub.DisconnectAsync();
                    continue;
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
                         && DateTime.UtcNow - hub.LastChannelRetryAt > (hub.AnyMissingWithProbe ? retryWithProbe : retryMissingEvery))
                {
                    var added = await hub.RetryMissingChannelsAsync();
                    retryMissingEvery = added > 0
                        ? retryMissingMin
                        : TimeSpan.FromTicks(Math.Min(retryMissingEvery.Ticks * 2, retryMissingMax.Ticks));
                    var missing = hub.MissingChannels;
                    var next = hub.AnyMissingWithProbe ? retryWithProbe : retryMissingEvery;
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {tag} 补读测点：新增 {added} 个，现在 {hub.ChannelCount} 个在监测"
                                      + (missing.Count > 0
                                          ? $"；{string.Join("、", missing.Select(c => "CH" + c))} 仍读不到，{next.TotalMinutes:0} 分钟后再试{(hub.AnyMissingWithProbe ? "（有探头信号，等现场置零）" : string.Empty)}"
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

            // 维护刚结束要马上连回去，别让业主多等 30 秒
            try { await Task.Delay(TimeSpan.FromSeconds(hub.IsConnected ? 10 : hub.MaintenanceUntil is null ? 30 : 1), ct); }
            catch (OperationCanceledException) { break; }
        }
    }, CancellationToken.None));
    }
}

app.Run();

/// <summary>
/// 控制台输出同时写进按天分的日志文件。写文件失败不能影响程序（磁盘满、目录只读就只剩控制台）。
/// 多线程同时 WriteLine 用一把锁串起来，行不会交错。
/// </summary>
internal sealed class TeeLog : TextWriter
{
    private readonly TextWriter _console;
    private readonly string _dir;
    private readonly int _keepDays;
    private readonly object _gate = new();
    private StreamWriter? _file;
    private DateTime _fileDay;

    public TeeLog(TextWriter console, string dir, int keepDays)
    {
        _console = console;
        _dir = dir;
        _keepDays = keepDays;
    }

    public override Encoding Encoding => _console.Encoding;

    public override void Write(char value) => _console.Write(value);

    public override void Write(string? value) => _console.Write(value);

    public override void WriteLine(string? value)
    {
        _console.WriteLine(value);
        lock (_gate)
        {
            try
            {
                var today = DateTime.Now.Date;
                if (_file is null || _fileDay != today)
                {
                    _file?.Dispose();
                    Directory.CreateDirectory(_dir);
                    _file = new StreamWriter(Path.Combine(_dir, $"monitor-{today:yyyyMMdd}.log"), append: true, new UTF8Encoding(false)) { AutoFlush = true };
                    _fileDay = today;
                    Prune();
                }

                // 控制台上大多行自带 HH:mm:ss，文件里只补日期；没带时间的行补全日期时间
                var stamped = value is { Length: >= 9 } && value[2] == ':' && value[5] == ':' && value[8] == ' ';
                _file.WriteLine($"{DateTime.Now:yyyy-MM-dd}{(stamped ? " " : $" {DateTime.Now:HH:mm:ss} ")}{value}");
            }
            catch
            {
                // 日志写不进去就只剩控制台，不能因为这个把服务弄死
            }
        }
    }

    public override void WriteLine() => WriteLine(string.Empty);

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
        Mode = farm.Devices[0].Plan;   // 口径/阈值全场统一，取第一台的
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
/// WebSocket 广播：所有设备共用。每个浏览器连接的出站消息按产生顺序进有界队列，由一个发送循环依次写出：
/// 之前每条消息 Task.Run 一次，任务抢锁的先后不定，帧会乱序到达；慢客户端还会把任务无限堆起来。
/// 队列满了丢最旧的一条——实时帧过时了就没意义，下一帧马上会来。
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

    /// <summary>开始一轮巡检：把规划位/巡检位都带上轮 45 秒左右，抓插到别处的探头。</summary>
    public async Task StartScanAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_engine is not { IsConnected: true } engine || ScanActive) return;
            List<ushort> wide;
            lock (_stateLock) wide = _reachable.ToList();
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

    // ── 探头在位识别 ─────────────────────────────────────────────────────
    // 每路按最近几帧的波形信噪比判"有探头 / 没探头"。历史教训（08-24）：串扰杂波 6 dB 也能解出声时，
    // 所以不能看"有没有解出回波"，要看信噪比是否够高且连续多帧稳定。
    // 真机标定（09-03）：真探头 30–37 dB，空通道 0–6 dB，噪声尖峰偶到 14 dB，个别通道常年 10 dB 上下悬着。
    // 16/12 两道门中间是灰区，不改判，宁可不报也别误报。
    // 门限和算法都用 DeviceEngine 那一份：桌面版和大屏对同一路波形必须给同一个在位结论
    private const double SnrProbeDb = DeviceEngine.ProbeSnrDb;   // ≥ 这个且多数帧如此 → 有探头
    private const double SnrNoneDb = DeviceEngine.NoiseSnrDb;    // < 这个且绝大多数帧如此 → 没探头
    private const int SnrWindow = 10;

    private sealed class SignalTrack
    {
        public readonly Queue<double> Recent = new();
        public readonly Queue<(bool Normal, decimal? Echo)> RecentEcho = new();
        public string Presence = "unknown";   // probe / none / unknown
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

    /// <summary>收一帧就更新该路的在位判定；判定翻转时返回 (旧, 新)，否则 null。</summary>
    private (string From, string To)? TrackSignal(ChannelResult m)
    {
        // 和桌面版同一个 Snr：那份带削顶保护（超过一半采样打满量程按 0 dB），这里之前少了这一句
        var snr = DeviceEngine.Snr(m.EchoWave);
        if (snr is null) return null;
        lock (_stateLock)
        {
            if (!_signals.TryGetValue(m.Channel, out var t)) _signals[m.Channel] = t = new SignalTrack();
            t.LastSnr = snr.Value;
            t.LastAt = DateTime.UtcNow;
            t.Recent.Enqueue(snr.Value);
            if (t.Recent.Count > SnrWindow) t.Recent.Dequeue();
            t.RecentEcho.Enqueue((m.StatusCode == MeasStatusCode.Normal, m.EchoTime));
            if (t.RecentEcho.Count > SnrWindow) t.RecentEcho.Dequeue();
            if (t.Recent.Count < 5) return null;
            var strong = t.Recent.Count(s => s >= SnrProbeDb);
            var weak = t.Recent.Count(s => s < SnrNoneDb);

            // 第二条通路（真机 09-03 CH8 实证）：耦合差的探头信噪比只有 10 dB，但设备帧帧判"正常"、
            // 声时稳定、且和同法兰其他螺栓的声时同量级 —— 这不是噪声，是探头。
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

    /// <summary>最近一帧测量到达的时间（UTC）。连上/补读后先按当时时刻算，免得刚切完模式就被看门狗误判。</summary>
    public DateTime LastFrameAt => new(Interlocked.Read(ref _lastFrameTicks), DateTimeKind.Utc);

    /// <summary>上次补读缺失测点的时间（UTC），连接成功也算一次。</summary>
    public DateTime LastChannelRetryAt { get; private set; } = DateTime.MinValue;

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

    /// <summary>
    /// 缺参考声时、但探头信号在位的测点：多半是刚装上、现场正拿桌面端置零。这种要勤补读——
    /// 09-10 现场 16:43 在桌面端把 CH6 置零，大屏按 10→20 分钟的退避到 16:48 才补读到，人在旁边看着"未置零"发呆。
    /// 没探头信号的缺失测点照旧拉长间隔，别为空口子老打断实时画面。
    /// </summary>
    public bool AnyMissingWithProbe
    {
        get
        {
            // 勤补读只在探头刚出现的半小时内：这是"有人正在装、正在置零"的窗口。探头挂了几个小时还没基线的，
            // 多半是没人管它，按普通退避——每次补读都要停几秒流，不能为它永远两分钟打断一次业主画面
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
                // 引擎日志自带 HH:mm:ss 前缀
                _lastLog = s;
                Broadcast(new { type = "log", t = Turbine, text = s });
            };
            engine.OnMeasurement += m =>
            {
                Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                var flip = TrackSignal(m);
                // 只有配置在监测的通道才算测点帧，才落历史；规划位/巡检位的帧只带 monitored=false 给页面做在位识别
                bool zeroed;
                double? snr;
                var presence = "unknown";
                var settled = false;
                lock (_stateLock)
                {
                    zeroed = _params.ContainsKey(m.Channel);
                    if (_signals.TryGetValue(m.Channel, out var tr))
                    {
                        snr = tr.LastSnr;
                        presence = tr.Presence;
                        // 在位判定要攒几帧才定下来；定不下来（信噪比常年悬在灰区）的攒满一窗也算"看过了"
                        settled = tr.Presence != "unknown" || tr.Recent.Count >= SnrWindow;
                    }
                    else
                    {
                        snr = null;
                    }
                }

                // 力值和状态只在这里算一次：帧、历史、事件、页面全用同一个数、同一个结论
                var mismatch = HistoryStore.IsBaselineMismatch(m.DeltaTof);
                var kn = mismatch ? null : Plan.ForceKn(m.Channel, m.DeltaTof);
                var dkn = mismatch ? null : Plan.DeltaKn(m.Channel, m.DeltaTof);
                var health = zeroed ? Health.Judge(m.StatusCode == MeasStatusCode.Normal, mismatch, presence, Plan, m.Channel, kn) : null;

                Broadcast(FrameJson(m, snr, zeroed, mismatch, kn, dkn, health));
                // 刚连上、在位判定还没定下来的那几帧不进历史：空通道会先记"检测异常"再改"探头无信号"，每次重连都多两条假事件
                if (zeroed && settled) _history.Ingest(Turbine, m, health!, kn, dkn);
                if (flip is { } f)
                {
                    var configured = WantedChannels.Contains(m.Channel);
                    bool hasBase;
                    lock (_stateLock) hasBase = zeroed || _zeroedExtra.Contains(m.Channel);
                    var msg = f.To == "probe"
                        ? (zeroed ? $"CH{m.Channel} 探头信号恢复" : configured
                            ? $"CH{m.Channel} 有探头信号，但该通道读不到参考声时"
                            : $"CH{m.Channel} 出现探头信号，此通道未在配置中{(hasBase ? "（设备上已有基线）" : "（且未置零）")} —— 探头可能插错位置")
                        : f.To == "none"
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
            };
            await engine.OpenAsync(opt, CancellationToken.None);
            if (!engine.IsConnected)
            {
                try { await engine.CloseAsync(); } catch { /* 本来就没连上 */ }
                throw new InvalidOperationException("TCP 通了但马上被对方断开，多半 IP 不对或设备被别的程序连着");
            }

            // 从这里起占着设备唯一的一路 TCP：后面任何一步失败都必须把它关掉，
            // 否则下一轮重连会一直撞上"设备被别的程序连着"。
            try
            {
                // 设备此刻在待机（OpenAsync 切的）。参数必须在待机下读：监测中设备对读命令
                // 只零散应答（现场实测），而且存储区回包带整条参考波形（本机型 600 点），
                // 来得慢，短超时的快读会漏 —— 会话里没有参考声时，SDK 就算不出 Δt，
                // 页面上每颗螺栓都会变成"检测异常"。所以走带三轮补读的全量读。
                var fresh = new SortedDictionary<ushort, List<KeyValuePair<string, string>>>();
                var freshWaves = new SortedDictionary<ushort, object>();
                var baselines = new Dictionary<ushort, (decimal? RefTime, decimal? RefTemp)>();
                var signalOnly = new List<ushort>();   // 只轮询做在位识别、不测力：规划位/巡检位，以及配置了但没置零的
                var zeroedExtra = new HashSet<ushort>(); // 规划位里设备上已有基线的（历史置过零），给页面提示用
                // 配置的测点 ∪ 规划位一起全量读。只有 channels 里配置的才算监测；规划位即使置过零也只做在位识别——
                // 设备里常有历史遗留的基线，不能因为它就把"还没装探头"的位子当成测点报"探头丢失"
                var pending = WantedChannels.Concat(Planned).Distinct().OrderBy(c => c).ToList();
                for (var pass = 1; pass <= 2 && pending.Count > 0; pass++)
                {
                    // 第二遍先歇两秒：大回包可能还在路上，紧接着重发会跟它撞车（半双工链路）
                    if (pass > 1) await Task.Delay(2000);
                    var still = new List<ushort>();
                    foreach (var ch in pending)
                    {
                        var wanted = WantedChannels.Contains(ch);
                        var r = await ReadChannelIntoAsync(engine, ch, wanted ? fresh : null, freshWaves, baselines);
                        if (r == ReadOutcome.NoReply) { still.Add(ch); continue; }
                        if (!wanted || r == ReadOutcome.NotZeroed) signalOnly.Add(ch);
                        if (!wanted && r == ReadOutcome.Zeroed) zeroedExtra.Add(ch);
                    }

                    pending = still;
                }

                foreach (var ch in pending)
                {
                    _lastLog = $"{DateTime.Now:HH:mm:ss} CH{ch} 一条回包都没有（通道不存在），暂不纳入";
                }

                // 巡检位只读寄存器（快），能回包就轮询，用来抓插到规划外通道的探头
                foreach (var ch in Guard.Except(fresh.Keys).Except(signalOnly).Except(pending))
                {
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

                // 能回包的通道都记下来；连上后先按"巡检"宽集轮一轮（规划位/巡检位都看一眼有没有探头），
                // 45 秒后收窄到配置通道 —— 空通道测起来很慢，一直全轮会把测点刷新拖到几秒一帧
                var reachable = fresh.Keys.Concat(signalOnly).Distinct().OrderBy(c => c).ToList();
                if (reachable.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"配置的测点（{string.Join("、", WantedChannels.Select(c => "CH" + c))}）一条回包都没有，无法监测");
                }

                if (fresh.Count == 0)
                {
                    _lastLog = $"{DateTime.Now:HH:mm:ss} 配置的测点都读不到参考声时，先只做探头在位识别";
                }

                // 灌会话（含探头频率兜底）→ 下发轮询集 → 切监测
                await engine.ResumeMonitorAsync(reachable, CancellationToken.None);

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
                }
            }
            catch
            {
                try { await engine.CloseAsync(); } catch { /* 尽力恢复设备模式并释放 TCP */ }
                throw;
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

    // ── 维护模式 ─────────────────────────────────────────────────────────
    // 设备只允许一路 TCP。现场要用桌面端置零/调参时，大屏得主动让出设备并且别再抢回来；
    // 之前只能把大屏进程杀掉——业主眼前的画面直接黑掉，谁也不知道是坏了还是在维护。
    // 维护模式有期限：到点自动恢复监测，忘了点"结束"也不会让大屏一直断着。

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
    /// 配置重载时把维护状态从老一代带过来：现场正在用桌面端置零、顺手在现场配置里保存了一下，
    /// 新一代要是立刻连上设备，就和桌面端抢起来了。
    /// </summary>
    public void InheritMaintenance(MonitorHub from)
    {
        if (!from.InMaintenance) return;
        MaintenanceUntil = from.MaintenanceUntil;
        MaintenanceNote = from.MaintenanceNote;
        _lastLog = $"{DateTime.Now:HH:mm:ss} 维护中（{MaintenanceNote}），配置重载后继续让出设备，{MaintenanceUntil!.Value.ToLocalTime():HH:mm} 恢复";
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

            var fresh = new SortedDictionary<ushort, List<KeyValuePair<string, string>>>();
            var freshWaves = new SortedDictionary<ushort, object>();
            var baselines = new Dictionary<ushort, (decimal? RefTime, decimal? RefTemp)>();
            await engine.StopStreamAsync();
            try
            {
                foreach (var ch in missing)
                {
                    await ReadChannelIntoAsync(engine, ch, fresh, freshWaves, baselines);
                }
            }
            finally
            {
                // 读成什么样都要把流恢复回来；恢复失败就让看门狗按"没有帧"重连
                ushort[] all;
                lock (_stateLock)
                {
                    foreach (var kv in fresh) _params[kv.Key] = kv.Value;
                    foreach (var kv in freshWaves) _refWaves[kv.Key] = kv.Value;
                    _reachable = _reachable.Concat(fresh.Keys).Distinct().OrderBy(c => c).ToList();
                    _polled = _polled.Concat(fresh.Keys).Distinct().OrderBy(c => c).ToList();
                    all = _polled.ToArray();
                }

                await engine.ResumeMonitorAsync(all, CancellationToken.None);
                Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
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

    private enum ReadOutcome { NoReply, NotZeroed, Zeroed }

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
        if (!DeviceEngine.IsZeroed(bag)) return ReadOutcome.NotZeroed;
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
            }).ToList(),
            channels,
            polled,
            signals,
            scanning = ScanActive,
        };
    }

    private object FrameJson(ChannelResult m, double? snr, bool monitored, bool mismatch, decimal? kn, decimal? dkn, string? health)
    {
        // 力值和状态只在服务端算这一次，页面照抄：基线失配或未标定 kn 就是空，页面不得自己拿声时差乘系数、也不自己判状态
        return new
        {
            type = "frame",
            t = Turbine,
            ch = m.Channel,
            monitored,
            snr = snr is { } s ? Math.Round(s, 1) : (double?)null,
            echoTime = m.EchoTime,
            deltaTof = m.DeltaTof,
            // 声时偏离置零基线超出物理可能：不算力值
            mismatch,
            kn,
            dkn,
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
