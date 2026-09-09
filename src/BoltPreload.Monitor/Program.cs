// 业主监视大屏（只读、无登录）：本服务用 SDK 直连设备（FxDevice 的 DeviceEngine），
// 浏览器通过 WebSocket 收实时帧。局域网里任何电脑/手机开网页就能看。
// 用法：BoltPreload.Monitor.exe [--port 5080]，然后浏览器开 http://本机IP:5080/

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FxDevice;
using FxSDK.Models;

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
var app = builder.Build();

var hub = new MonitorHub();
var history = new HistoryStore(Path.Combine(AppContext.BaseDirectory, "data", "history.db"));
hub.History = history;
// Web 默认规则：字段名大小写不敏感（页面发的是小写 endpoint）
var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// 标定文件：exe 旁边的 .ucf，跟桌面版同一个规则
var ucfPath = Directory.GetFiles(AppContext.BaseDirectory, "*.ucf")
    .OrderByDescending(File.GetLastWriteTime)
    .FirstOrDefault();
if (ucfPath is not null)
{
    try
    {
        hub.Cal = Ucf.Load(ucfPath);
        history.SetCal(hub.Cal.K, hub.Cal.B);
    }
    catch (Exception ex) { Console.WriteLine($"标定文件读不了：{ex.Message}"); }
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

// 业主页面是只读的，改设备地址/断开这两个动作只允许在大屏主机上本地调用，
// 局域网里随便一台电脑不能把大屏指到别的设备上去。
static bool IsLocalCall(HttpContext ctx) =>
    ctx.Connection.RemoteIpAddress is { } ip
    && (IPAddress.IsLoopback(ip) || ip.Equals(ctx.Connection.LocalIpAddress));

app.MapPost("/api/connect", async (HttpContext ctx) =>
{
    if (!IsLocalCall(ctx)) return Results.StatusCode(403);
    var req = await JsonSerializer.DeserializeAsync<ConnectReq>(ctx.Request.Body, jsonOpts);
    var endpoint = req?.Endpoint;
    if (string.IsNullOrWhiteSpace(endpoint)) return Results.BadRequest(new { error = "endpoint 不能为空" });
    try
    {
        await hub.ConnectAsync(endpoint.Trim());
        return Results.Json(hub.State());
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapPost("/api/disconnect", async (HttpContext ctx) =>
{
    if (!IsLocalCall(ctx)) return Results.StatusCode(403);
    await hub.DisconnectAsync();
    return Results.Json(hub.State());
});

// 立刻巡检一轮（换插探头后不想等 10 分钟）：保活循环最多 10 秒内响应
app.MapPost("/api/scan", (HttpContext ctx) =>
{
    if (!IsLocalCall(ctx)) return Results.StatusCode(403);
    hub.ScanRequested = true;
    return Results.Json(new { ok = true, scanning = hub.ScanActive });
});

app.MapGet("/api/state", () => Results.Json(hub.State()));

app.MapGet("/api/history", (string? range) => Results.Json(history.QuerySamples(range ?? "24h")));
app.MapGet("/api/events", (string? range) => Results.Json(history.QueryEvents(range ?? "24h")));
app.MapGet("/api/report", (string? range) => Results.Json(history.QueryReport(range ?? "24h")));
app.MapGet("/api/archive", () => Results.Json(history.Stats()));
app.MapGet("/api/export.csv", (string? range) =>
{
    var r = range ?? "24h";
    var bytes = history.ExportCsv(r);
    return Results.File(bytes, "text/csv; charset=utf-8", $"preload-{r}.csv");
});

app.Map("/ws", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.ServeSocketAsync(ws, ctx.RequestAborted);
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    hub.DisconnectAsync().GetAwaiter().GetResult();
    history.Dispose();
});

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"监视服务已启动：http://localhost:{port}/  （局域网访问用本机 IP）");
Console.WriteLine(hub.Cal is { } c
    ? $"标定：{c.FileName}（{c.Points} 点） {c.K:F4} {c.Unit}/ns"
    : "没找到 .ucf 标定文件，夹紧力用内置 0.8919 kN/ns");

// ── 自动连接：展示页上没有连接按钮，由服务自己连设备并保活 ──────────────
// 设备地址从 farm.json 的 device 字段来，没有就用默认。
var device = "192.168.0.7:23";
var farmPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "farm.json");
try
{
    if (File.Exists(farmPath))
    {
        var root = JsonDocument.Parse(File.ReadAllText(farmPath)).RootElement;
        if (root.TryGetProperty("device", out var d) && d.GetString() is { Length: > 0 } dev)
        {
            device = dev;
        }

        ushort[] ReadChannels(JsonElement el, string name)
            => el.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Select(x => (ushort)x.GetInt32()).Distinct().ToArray()
                : Array.Empty<ushort>();
        if (ReadChannels(root, "channels") is { Length: > 0 } configured) hub.Channels = configured;
        // 规划位和巡检位也一起轮询：不为测力，只看那一路有没有探头信号，抓插错位/拔空。
        // 规划位优先取绑定法兰自己的 flanges[].plannedChannels（各法兰规划可以不同，如叶根法兰 12 位），没写的用全局的
        hub.Planned = ReadChannels(root, "plannedChannels");
        if (root.TryGetProperty("bind", out var bind) && bind.TryGetProperty("flangeIndex", out var fiEl) && fiEl.TryGetInt32(out var fi)
            && root.TryGetProperty("flanges", out var flanges) && flanges.ValueKind == JsonValueKind.Array
            && fi >= 0 && fi < flanges.GetArrayLength() && flanges[fi].TryGetProperty("plannedChannels", out _))
        {
            hub.Planned = ReadChannels(flanges[fi], "plannedChannels");
        }
        hub.Guard = ReadChannels(root, "guardChannels");

        if (root.TryGetProperty("thresholds", out var th) && th.ValueKind == JsonValueKind.Object)
        {
            decimal ReadDec(string name, decimal fallback)
                => th.TryGetProperty(name, out var n) && n.TryGetDecimal(out var v) ? v : fallback;
            history.SetThresholds(ReadDec("ratedKn", 0), ReadDec("warnRatio", 0.85m), ReadDec("alarmRatio", 0.7m));
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"farm.json 读取失败，用默认设备地址：{ex.Message}");
}

Console.WriteLine($"历史库：{history.DbPath}（每 5 秒落盘，保留 90 天）");
Console.WriteLine($"采集设备：{device}（自动连接，断线自动重连），测点 "
                  + string.Join("、", hub.WantedChannels.Select(c => "CH" + c))
                  + (hub.Channels.Count > 0 ? "（farm.json 配置，主动下发轮询）" : "（未配置，按默认 CH1–CH4）"));
var extra = hub.Planned.Concat(hub.Guard).Except(hub.WantedChannels).Distinct().OrderBy(c => c).ToList();
if (extra.Count > 0)
{
    Console.WriteLine($"探头在位识别：额外轮询 {string.Join("、", extra.Select(c => "CH" + c))}，按波形信噪比判断有没有探头");
}

// TCP 没断但测量帧停了（设备被切到待机、卡死、链路半开）也算断：超过这个时间没有帧就重连，
// 不然页面会一直拿最后一帧当"实时"显示。
var stallAfter = TimeSpan.FromSeconds(90);
// 连接时没读到参考声时的测点，隔一阵再试（不重连，只短暂停流补读）。补读要停几秒流，
// 一直读不到的（通道确实没置零）就逐次拉长间隔，最长一小时一次，别老打断业主的实时画面。
var retryMissingMin = TimeSpan.FromMinutes(10);
var retryMissingMax = TimeSpan.FromHours(1);
var retryMissingEvery = retryMissingMin;
// 探头在位巡检：连上先巡一轮，之后每 10 分钟一轮（本机 POST /api/scan 可随时触发），每轮轮宽集 45 秒
var scanLength = TimeSpan.FromSeconds(45);
var scanEvery = TimeSpan.FromMinutes(10);
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            if (!hub.IsConnected)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} 尝试连接设备 {device}……");
                await hub.ConnectAsync(device);
                retryMissingEvery = retryMissingMin;
                var missing = hub.MissingChannels;
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} 已连接，{hub.ChannelCount} 个测点在监测"
                                  + (missing.Count > 0
                                      ? $"；{string.Join("、", missing.Select(c => "CH" + c))} 读不到参考声时，{retryMissingEvery.TotalMinutes:0} 分钟后重试"
                                      : string.Empty));
            }
            else if (DateTime.UtcNow - hub.LastFrameAt > stallAfter)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} 连接还在但 {stallAfter.TotalSeconds:0} 秒没有测量帧，重连设备");
                await hub.DisconnectAsync();
                continue;
            }
            else if (hub.ScanActive && DateTime.UtcNow - hub.ScanStartedAt > scanLength)
            {
                var strays = await hub.EndScanAsync();
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} 巡检结束"
                                  + (strays.Count > 0 ? $"：{string.Join("、", strays.Select(c => "CH" + c))} 有探头信号但未在配置中" : "，未发现插错位的探头"));
            }
            else if (!hub.ScanActive && (hub.ScanRequested || DateTime.UtcNow - hub.LastScanAt > scanEvery))
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} 巡检开始（{(hub.ScanRequested ? "手动触发" : "定时")}）");
                await hub.StartScanAsync();
            }
            else if (hub.MissingChannels.Count > 0 && DateTime.UtcNow - hub.LastChannelRetryAt > retryMissingEvery)
            {
                var added = await hub.RetryMissingChannelsAsync();
                retryMissingEvery = added > 0
                    ? retryMissingMin
                    : TimeSpan.FromTicks(Math.Min(retryMissingEvery.Ticks * 2, retryMissingMax.Ticks));
                var missing = hub.MissingChannels;
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} 补读测点：新增 {added} 个，现在 {hub.ChannelCount} 个在监测"
                                  + (missing.Count > 0
                                      ? $"；{string.Join("、", missing.Select(c => "CH" + c))} 仍读不到，{retryMissingEvery.TotalMinutes:0} 分钟后再试"
                                      : string.Empty));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {(hub.IsConnected ? "保活出错" : "连接失败")}：{ex.Message}，稍后重试");
        }

        await Task.Delay(TimeSpan.FromSeconds(hub.IsConnected ? 10 : 30));
    }
});

app.Run();

internal sealed record ConnectReq(string? Endpoint);

/// <summary>设备连接与 WebSocket 广播。一台设备一份全局状态，多个浏览器同时看。</summary>
internal sealed class MonitorHub : IDisposable
{
    private static readonly ushort[] DefaultChannels = { 1, 2, 3, 4 };

    /// <summary>串行化连接/断开/补读这类会动设备模式的操作。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>保护 _params/_refWaves：State() 在任意 HTTP 线程上枚举，连接流程在另一个线程上改。</summary>
    private readonly object _stateLock = new();

    public void Dispose() => _gate.Dispose();
    private readonly List<(WebSocket Ws, SemaphoreSlim SendLock)> _sockets = new();
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
            Broadcast(new { type = "log", text = _lastLog });
            Broadcast(new { type = "state", state = State() });
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
            Broadcast(new { type = "log", text = _lastLog });
            Broadcast(new { type = "state", state = State() });
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
    private const double SnrProbeDb = 16;   // ≥ 这个且多数帧如此 → 有探头
    private const double SnrNoneDb = 12;    // < 这个且绝大多数帧如此 → 没探头
    private const int SnrWindow = 10;

    private sealed class SignalTrack
    {
        public readonly Queue<double> Recent = new();
        public readonly Queue<(bool Normal, decimal? Echo)> RecentEcho = new();
        public string Presence = "unknown";   // probe / none / unknown
        public double LastSnr;
        public DateTime LastAt;
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

    /// <summary>波形信噪比（dB）：窗口开头一段当噪声底，其余部分的峰值当信号。</summary>
    private static double? Snr(EchoWave? w)
    {
        var d = w?.WaveData;
        if (d is null || d.Count < 64) return null;
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

    /// <summary>收一帧就更新该路的在位判定；判定翻转时返回 (旧, 新)，否则 null。</summary>
    private (string From, string To)? TrackSignal(ChannelResult m)
    {
        var snr = Snr(m.EchoWave);
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
            return (prev, next);
        }
    }

    public Ucf.Cal? Cal { get; set; }

    public HistoryStore? History { get; set; }

    /// <summary>TCP 还活着就算连着。帧停了由外面的看门狗按 LastFrameAt 判。</summary>
    public bool IsConnected => _engine is { IsConnected: true };

    /// <summary>最近一帧测量到达的时间（UTC）。连上/补读后先按当时时刻算，免得刚切完模式就被看门狗误判。</summary>
    public DateTime LastFrameAt => new(Interlocked.Read(ref _lastFrameTicks), DateTimeKind.Utc);

    /// <summary>上次补读缺失测点的时间（UTC），连接成功也算一次。</summary>
    public DateTime LastChannelRetryAt { get; private set; } = DateTime.MinValue;

    public int ChannelCount { get { lock (_stateLock) return _params.Count; } }

    /// <summary>要监测的测点：farm.json 配了就用配置，没配按默认 CH1–CH4。</summary>
    public IReadOnlyList<ushort> WantedChannels => Channels.Count > 0 ? Channels : DefaultChannels;

    /// <summary>想监测但这次没读到参考声时、暂未纳入的测点。</summary>
    public IReadOnlyList<ushort> MissingChannels
    {
        get
        {
            lock (_stateLock) return WantedChannels.Where(c => !_params.ContainsKey(c)).ToList();
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

    /// <summary>配置的测点通道。给了的话连接时主动下发轮询集把设备拉起来。</summary>
    public IReadOnlyList<ushort> Channels { get; set; } = Array.Empty<ushort>();

    /// <summary>规划的探头位（farm.json plannedChannels）：也轮询，已置零的自动纳入监测，没置零的只做在位识别。</summary>
    public IReadOnlyList<ushort> Planned { get; set; } = Array.Empty<ushort>();

    /// <summary>巡检位（farm.json guardChannels）：只读寄存器、只轮询、只判有没有探头，抓插到规划外通道的探头。</summary>
    public IReadOnlyList<ushort> Guard { get; set; } = Array.Empty<ushort>();

    public async Task ConnectAsync(string endpoint)
    {
        await _gate.WaitAsync();
        try
        {
            if (_engine is not null) await CloseEngineAsync();

            var engine = new DeviceEngine();
            engine.OnLog += s =>
            {
                // 引擎日志自带 HH:mm:ss 前缀
                _lastLog = s;
                Broadcast(new { type = "log", text = s });
            };
            engine.OnMeasurement += m =>
            {
                Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                var flip = TrackSignal(m);
                // 只有配置在监测的通道才算测点帧，才落历史；规划位/巡检位的帧只带 monitored=false 给页面做在位识别
                bool zeroed;
                double? snr;
                lock (_stateLock)
                {
                    zeroed = _params.ContainsKey(m.Channel);
                    snr = _signals.TryGetValue(m.Channel, out var tr) ? tr.LastSnr : (double?)null;
                }

                Broadcast(FrameJson(m, snr, zeroed));
                if (zeroed) History?.Ingest(m);
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
                        Broadcast(new { type = "log", text = _lastLog });
                    }

                    Broadcast(new { type = "state", state = State() });
                }
            };

            var opt = new DeviceOptions
            {
                Endpoint = endpoint,
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
                        var r = await ReadChannelIntoAsync(engine, ch, wanted ? fresh : null, freshWaves);
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
            _engine = engine;
            _endpoint = endpoint;
            Broadcast(new { type = "state", state = State() });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_engine is null) return;
            await CloseEngineAsync();
            Broadcast(new { type = "state", state = State() });
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
            await engine.StopStreamAsync();
            try
            {
                foreach (var ch in missing)
                {
                    await ReadChannelIntoAsync(engine, ch, fresh, freshWaves);
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

            if (fresh.Count > 0) Broadcast(new { type = "state", state = State() });
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
        SortedDictionary<ushort, object> waves)
    {
        var bag = await engine.ReadChannelValuesAsync(ch, CancellationToken.None);
        if (bag.Count == 0) return ReadOutcome.NoReply;
        if (!DeviceEngine.IsZeroed(bag)) return ReadOutcome.NotZeroed;
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
                @params = kv.Value.Select(p => new { n = p.Key, v = p.Value }).ToList(),
                refWave = _refWaves.GetValueOrDefault(kv.Key),
            }).ToList();
            polled = _polled.ToList();
            // 每一路的在位判定：页面据此区分"探头拔空"和"未配置通道冒出探头"
            signals = _signals.OrderBy(kv => kv.Key).Select(kv => (object)new
            {
                ch = kv.Key,
                snr = Math.Round(kv.Value.LastSnr, 1),
                presence = kv.Value.Presence,
                zeroed = _params.ContainsKey(kv.Key) || _zeroedExtra.Contains(kv.Key),
                configured = WantedChannels.Contains(kv.Key),
                planned = Planned.Contains(kv.Key),
                at = kv.Value.LastAt,
            }).ToList();
        }

        return new
        {
            connected = _engine is { IsConnected: true },
            endpoint = _endpoint,
            log = _lastLog,
            cal = Cal is { } c
                ? new { k = c.K, b = c.B, unit = c.Unit, name = c.FileName, points = c.Points }
                : new { k = 0.8919m, b = 0m, unit = "kN", name = "内置", points = 0 },
            channels,
            polled,
            signals,
            scanning = ScanActive,
        };
    }

    private static object FrameJson(ChannelResult m, double? snr, bool monitored) => new
    {
        type = "frame",
        ch = m.Channel,
        monitored,
        snr = snr is { } s ? Math.Round(s, 1) : (double?)null,
        echoTime = m.EchoTime,
        deltaTof = m.DeltaTof,
        // 声时偏离置零基线超出物理可能：页面不得把它算成力值显示
        mismatch = HistoryStore.IsBaselineMismatch(m.DeltaTof),
        temp = m.SpTemp ?? m.RawTemp,
        status = DeviceEngine.DescribeStatus(m.StatusCode),
        normal = m.StatusCode == MeasStatusCode.Normal,
        wave = m.EchoWave?.WaveData is { Count: > 0 }
            ? new { start = m.EchoWave.EchoStart, clock = m.EchoWave.ClockInt, data = m.EchoWave.WaveData }
            : null,
    };

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

    // ── WebSocket ────────────────────────────────────────────────────────
    public async Task ServeSocketAsync(WebSocket ws, CancellationToken ct)
    {
        var entry = (ws, new SemaphoreSlim(1, 1));
        lock (_sockets) _sockets.Add(entry);
        try
        {
            await SendAsync(entry, new { type = "state", state = State() }, ct);
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
            lock (_sockets) _sockets.Remove(entry);
        }
    }

    private void Broadcast(object payload)
    {
        (WebSocket, SemaphoreSlim)[] targets;
        lock (_sockets) targets = _sockets.ToArray();
        if (targets.Length == 0) return;
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        foreach (var t in targets)
        {
            _ = Task.Run(async () =>
            {
                try { await SendRawAsync(t, json, CancellationToken.None); }
                catch { lock (_sockets) _sockets.Remove(t); }
            });
        }
    }

    private static async Task SendAsync((WebSocket Ws, SemaphoreSlim Lock) t, object payload, CancellationToken ct)
        => await SendRawAsync(t, JsonSerializer.SerializeToUtf8Bytes(payload), ct);

    private static async Task SendRawAsync((WebSocket Ws, SemaphoreSlim Lock) t, byte[] json, CancellationToken ct)
    {
        await t.Lock.WaitAsync(ct);
        try
        {
            if (t.Ws.State == WebSocketState.Open)
            {
                await t.Ws.SendAsync(json, WebSocketMessageType.Text, true, ct);
            }
        }
        finally
        {
            t.Lock.Release();
        }
    }
}
