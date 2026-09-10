using System.Text.Json;

namespace FxDevice;

/// <summary>基线是怎么置的。这决定了 k·Δt 算出来的那个数叫什么。</summary>
public enum BaselineMode
{
    /// <summary>
    /// 在役锚定：螺栓已经拧紧在法兰上才置零。k·Δt 是"较锚定时刻的变化量"；绝对预紧力只能按
    /// "锚定时 = 额定值"估算，没有额定值就只报变化量。现场常态。
    /// </summary>
    Anchor,

    /// <summary>自由状态置零：拧紧前记基线，k·Δt + b 就是绝对预紧力。安装期/实验室才做得到。</summary>
    Free,
}

/// <summary>一个法兰在这台设备上的接线与标定：哪些通道在测、哪些是规划位、每路装在几号螺栓、用哪份 .ucf、额定多少。</summary>
public sealed class FlangeCal
{
    public int Index { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>在测通道（置过零、算力值）。</summary>
    public IReadOnlyList<ushort> Channels { get; init; } = Array.Empty<ushort>();

    /// <summary>规划探头位（含未接的），只做在位识别。</summary>
    public IReadOnlyList<ushort> Planned { get; init; } = Array.Empty<ushort>();

    /// <summary>接线表：通道 → 螺栓号（farm.json channelBolts）。</summary>
    public IReadOnlyDictionary<ushort, int> Bolts { get; init; } = new Dictionary<ushort, int>();

    /// <summary>生效的标定；空 = 未标定。</summary>
    public Ucf.Cal? Cal { get; init; }

    /// <summary>标定从哪来："法兰自己的 xxx.ucf" / "程序目录 xxx.ucf" / "无"。</summary>
    public string CalSource { get; init; } = "无";

    /// <summary>额定预紧力 kN；0 = 未配置。锚定口径下就是"锚定时假定的预紧力"。</summary>
    public decimal Rated { get; init; }

    /// <summary>
    /// 这个法兰有没有接在这台设备上：写了在测通道（channels）或接线表（channelBolts）就算。
    /// 只写了 plannedChannels 的法兰是"规划"，不在这台设备上——它的通道号不参与归属和轮询，页面只画待接入的位子。
    /// </summary>
    public bool OnDevice { get; init; }

    internal FlangeCal WithCal(Ucf.Cal cal, string source) => new()
    {
        Index = Index, Name = Name, Channels = Channels, Planned = Planned, Bolts = Bolts,
        Cal = cal, CalSource = source, Rated = Rated, OnDevice = OnDevice,
    };
}

/// <summary>
/// 接线、标定与阈值方案，从 farm.json 解析。桌面端（FxMonitor）和大屏（BoltPreload.Monitor）用同一份：
/// 同一颗螺栓在两边算出来的必须是同一个数、叫同一个名字。
/// 一台 48 路设备可以跨多个法兰（CH1–8 顶段、CH9–16 中段……）：每个法兰在 flanges[] 里写自己的
/// channels / plannedChannels / channelBolts / ucf / ratedKn，通道由此归属到法兰，力值、额定、页面都跟着走。
/// 兼容老写法：顶层 channels / plannedChannels 归到 bind.flangeIndex 那个法兰。
/// </summary>
public sealed class CalPlan
{
    public BaselineMode Mode { get; private init; } = BaselineMode.Anchor;

    public decimal WarnRatio { get; private init; } = 0.85m;

    public decimal AlarmRatio { get; private init; } = 0.7m;

    /// <summary>全部法兰（按 farm.json 顺序），包括没接在这台设备上的。</summary>
    public IReadOnlyList<FlangeCal> Flanges { get; private init; } = Array.Empty<FlangeCal>();

    public int BoundIndex { get; private init; }

    /// <summary>有没有读到 farm.json。没有的话只有程序目录的 .ucf 兜底，口径也无从得知。</summary>
    public bool HasFarm { get; private init; }

    /// <summary>程序目录里最新的 .ucf：法兰没配自己的标定时的兜底；不属于任何法兰的通道也用它。</summary>
    public Ucf.Cal? GlobalCal { get; private init; }

    /// <summary>全局 thresholds.ratedKn，法兰没配 ratedKn 时的兜底。</summary>
    public decimal GlobalRated { get; private init; }

    private readonly Dictionary<ushort, int> _channelFlange = new();

    public string ModeText => Mode == BaselineMode.Anchor ? "在役锚定" : "自由状态置零";

    /// <summary>通道属于哪个法兰；-1 = 不在任何接入法兰的配置里（未分配）。</summary>
    public int FlangeOf(ushort ch) => _channelFlange.TryGetValue(ch, out var i) ? i : -1;

    public Ucf.Cal? CalFor(ushort ch) => FlangeOf(ch) is var i and >= 0 ? Flanges[i].Cal : GlobalCal;

    public decimal RatedFor(ushort ch) => FlangeOf(ch) is var i and >= 0 ? Flanges[i].Rated : GlobalRated;

    /// <summary>这一路装在哪："塔筒顶段法兰 #24"；不在接线表里的返回空串。给桌面端列表、置零对话框和操作记录用。</summary>
    public string Describe(ushort ch)
    {
        if (FlangeOf(ch) is not (var i and >= 0)) return string.Empty;
        var f = Flanges[i];
        return f.Bolts.TryGetValue(ch, out var bolt) ? $"{f.Name} #{bolt}" : f.Name;
    }

    /// <summary>所有在测通道（各接入法兰的 channels 之并），升序。</summary>
    public IReadOnlyList<ushort> WantedChannels { get; private init; } = Array.Empty<ushort>();

    /// <summary>所有规划位之并，升序。</summary>
    public IReadOnlyList<ushort> PlannedChannels { get; private init; } = Array.Empty<ushort>();

    /// <summary>较置零基线的变化量 kN = k·Δt。两种口径下都成立；未标定为空。</summary>
    public decimal? DeltaKn(ushort ch, decimal? dt) => CalFor(ch) is { } c && dt is { } d ? d * c.K : null;

    /// <summary>
    /// 预紧力 kN。自由置零：k·Δt + b（绝对值）。在役锚定：额定 + k·Δt（按锚定时 = 额定估算），
    /// 没有额定值给不出绝对值，返回空——宁可不显示，不能把变化量当预紧力显示。
    /// </summary>
    public decimal? ForceKn(ushort ch, decimal? dt)
    {
        if (CalFor(ch) is not { } c || dt is not { } d) return null;
        if (Mode == BaselineMode.Free) return d * c.K + c.B;
        var rated = RatedFor(ch);
        return rated > 0 ? rated + d * c.K : null;
    }

    /// <summary>
    /// 现场手工指定一份标定文件：所有法兰和全局兜底都换成它。这是工程师的显式动作（桌面端"标定文件..."按钮），
    /// 调用方要记操作记录。
    /// </summary>
    public CalPlan WithCal(Ucf.Cal cal)
    {
        var source = $"手工加载 {cal.FileName}";
        return new CalPlan
        {
            Mode = Mode, WarnRatio = WarnRatio, AlarmRatio = AlarmRatio, BoundIndex = BoundIndex, HasFarm = HasFarm,
            Flanges = Flanges.Select(f => f.WithCal(cal, source)).ToList(),
            GlobalCal = cal, GlobalRated = GlobalRated,
            WantedChannels = WantedChannels, PlannedChannels = PlannedChannels,
            _channelFlangeInit = _channelFlange,
        };
    }

    /// <summary>
    /// 找 farm.json：大屏放在 wwwroot 下，桌面端放在 exe 旁（把大屏那份复制过来即可）。返回解析好的根节点，没有就空。
    /// </summary>
    public static JsonElement? FindFarm(string baseDir, Action<string> log)
    {
        foreach (var path in new[] { Path.Combine(baseDir, "farm.json"), Path.Combine(baseDir, "wwwroot", "farm.json") })
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                log($"farm.json 解析失败（{path}）：{ex.Message}");
                return null;
            }
        }

        return null;
    }

    internal static ushort[] ReadChannels(JsonElement el, string name)
        => el.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(x => x.TryGetInt32(out var v) && v is > 0 and <= ushort.MaxValue)
                .Select(x => (ushort)x.GetInt32()).Distinct().OrderBy(c => c).ToArray()
            : Array.Empty<ushort>();

    /// <summary>老写法（一台设备、接线写在顶层 flanges[] 里）。</summary>
    public static CalPlan Load(JsonElement? farm, string baseDir, Action<string> log) => Load(farm, null, baseDir, log);

    /// <summary>
    /// 解析一台设备的方案。farm 是整份 farm.json：baseline / thresholds / flanges[] 结构模板；
    /// device 是 devices[] 里这台设备那一项（它的 flanges[] 按 index 或 name 对上模板，写接线/标定/额定）；
    /// device 为空时按老写法从顶层 flanges[] 直接读接线。
    /// </summary>
    public static CalPlan Load(JsonElement? farm, JsonElement? device, string baseDir, Action<string> log)
    {
        var mode = BaselineMode.Anchor;
        decimal globalRated = 0, warn = 0.85m, alarm = 0.7m;
        var boundIndex = 0;
        var flanges = new List<FlangeCal>();
        var channelFlange = new Dictionary<ushort, int>();
        var ucfCache = new Dictionary<string, Ucf.Cal?>(StringComparer.OrdinalIgnoreCase);

        Ucf.Cal? LoadUcf(string name, string forWhat)
        {
            if (ucfCache.TryGetValue(name, out var cached)) return cached;
            Ucf.Cal? cal = null;
            try
            {
                cal = Ucf.Load(Path.Combine(baseDir, name));
            }
            catch (Exception ex)
            {
                log($"标定文件 {name}（{forWhat}）读不了：{ex.Message}");
            }

            ucfCache[name] = cal;
            return cal;
        }

        // 程序目录里最新的 .ucf 做兜底
        var newest = Directory.Exists(baseDir)
            ? Directory.GetFiles(baseDir, "*.ucf").OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
            : null;
        var globalCal = newest is null ? null : LoadUcf(Path.GetFileName(newest), "程序目录");
        var globalSource = globalCal is null ? "无" : $"程序目录 {Path.GetFileName(newest!)}";

        if (farm is { } root)
        {
            if (root.TryGetProperty("baseline", out var bl) && bl.ValueKind == JsonValueKind.String
                && string.Equals(bl.GetString(), "free", StringComparison.OrdinalIgnoreCase))
            {
                mode = BaselineMode.Free;
            }

            if (root.TryGetProperty("thresholds", out var th) && th.ValueKind == JsonValueKind.Object)
            {
                decimal ReadDec(string name, decimal fallback)
                    => th.TryGetProperty(name, out var n) && n.TryGetDecimal(out var v) ? v : fallback;
                globalRated = ReadDec("ratedKn", 0);
                warn = ReadDec("warnRatio", 0.85m);
                alarm = ReadDec("alarmRatio", 0.7m);
            }

            var legacy = device is null;
            if (legacy && root.TryGetProperty("bind", out var bind) && bind.TryGetProperty("flangeIndex", out var fiEl) && fiEl.TryGetInt32(out var fi))
            {
                boundIndex = fi;
            }

            // 老写法：顶层 channels / plannedChannels 归到绑定法兰
            var legacyChannels = legacy ? ReadChannels(root, "channels") : Array.Empty<ushort>();
            var legacyPlanned = legacy ? ReadChannels(root, "plannedChannels") : Array.Empty<ushort>();

            // 新写法：这台设备的 flanges[]，按 index（或 name）对到模板上
            var wiring = new Dictionary<int, JsonElement>();
            if (device is { } dev && dev.TryGetProperty("flanges", out var dfl) && dfl.ValueKind == JsonValueKind.Array
                && root.TryGetProperty("flanges", out var tmpl) && tmpl.ValueKind == JsonValueKind.Array)
            {
                var names = tmpl.EnumerateArray().Select((f, i) => (Name: f.TryGetProperty("name", out var nm) ? nm.GetString() : null, Index: i)).ToList();
                foreach (var w in dfl.EnumerateArray())
                {
                    int? idx = w.TryGetProperty("index", out var ie) && ie.TryGetInt32(out var iv) ? iv : null;
                    if (idx is null && w.TryGetProperty("name", out var wn) && wn.ValueKind == JsonValueKind.String)
                    {
                        idx = names.FirstOrDefault(x => x.Name == wn.GetString()).Name is null ? null : names.First(x => x.Name == wn.GetString()).Index;
                    }

                    if (idx is null || idx < 0 || idx >= names.Count)
                    {
                        log($"设备接线里有一项对不上 flanges 模板（要写 index 或和模板一致的 name），跳过：{w.GetRawText()}");
                        continue;
                    }

                    wiring[idx.Value] = w;
                }
            }

            if (root.TryGetProperty("flanges", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                var boundSet = !legacy;   // 新写法：镜头聚焦第一个接了线的法兰
                foreach (var f in arr.EnumerateArray())
                {
                    var name = f.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString()! : $"法兰 #{index}";
                    // 接线/标定/额定：新写法从这台设备的那一项读，老写法就在模板自己身上
                    var src = legacy ? f : wiring.TryGetValue(index, out var w) ? w : (JsonElement?)null;
                    var channels = src is { } s1 ? ReadChannels(s1, "channels") : Array.Empty<ushort>();
                    // 规划位：模板给全场统一的，设备那一项也可以覆盖
                    var planned = src is { } s2 && s2.TryGetProperty("plannedChannels", out _) ? ReadChannels(s2, "plannedChannels") : ReadChannels(f, "plannedChannels");
                    if (legacy && index == boundIndex)
                    {
                        if (channels.Length == 0) channels = legacyChannels;
                        if (planned.Length == 0) planned = legacyPlanned;
                    }

                    // channelBolts 是接线表：键是通道、值是螺栓号，键也算接了线的位子
                    var bolts = new Dictionary<ushort, int>();
                    if (src is { } s3 && s3.TryGetProperty("channelBolts", out var cb) && cb.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in cb.EnumerateObject())
                        {
                            if (ushort.TryParse(p.Name, out var ch) && p.Value.TryGetInt32(out var bolt)) bolts[ch] = bolt;
                        }
                    }

                    var ucfName = src is { } s4 && s4.TryGetProperty("ucf", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    var ownCal = string.IsNullOrWhiteSpace(ucfName) ? null : LoadUcf(ucfName!, $"法兰「{name}」");
                    var cal = ownCal ?? globalCal;
                    var source = ownCal is not null ? $"法兰自己的 {ucfName}" : globalSource;
                    var rated = src is { } s5 && s5.TryGetProperty("ratedKn", out var r) && r.TryGetDecimal(out var rv) ? rv : globalRated;
                    if (!boundSet && (channels.Length > 0 || bolts.Count > 0))
                    {
                        boundIndex = index;
                        boundSet = true;
                    }

                    var fc = new FlangeCal
                    {
                        Index = index,
                        Name = name,
                        Channels = channels,
                        // 探头位 = 模板规划位 ∪ 实际接线的 channels ∪ 接线表里的键。接了线但不在模板规划里的通道（顶段模板规划 CH1–8，
                        // 现场却接在 CH9）也必须占一个位子，否则它离线时页面上连"离线"都不显示，人会以为没这颗
                        Planned = planned.Concat(channels).Concat(bolts.Keys).Distinct().OrderBy(c => c).ToArray(),
                        Bolts = bolts,
                        Cal = cal,
                        CalSource = source,
                        Rated = rated,
                        OnDevice = channels.Length > 0 || bolts.Count > 0,
                    };
                    flanges.Add(fc);

                    if (fc.OnDevice)
                    {
                        // 只有接在设备上的法兰才占通道号；纯规划的法兰（如叶根 1–12 位）不能把顶段的 CH1–8 抢走
                        foreach (var ch in fc.Channels.Concat(fc.Planned).Distinct())
                        {
                            if (channelFlange.TryGetValue(ch, out var other))
                            {
                                log($"CH{ch} 同时写在法兰「{flanges[other].Name}」和「{name}」里，按前者算；一路探头只能在一个法兰上");
                                continue;
                            }

                            channelFlange[ch] = index;
                        }

                        log($"法兰「{name}」：在测 {(fc.Channels.Count > 0 ? string.Join("、", fc.Channels.Select(c => "CH" + c)) : "（尚无置零通道）")}"
                            + (fc.Planned.Count > 0 ? $"，规划/接线位 {fc.Planned.Count} 个" : string.Empty)
                            + $"，标定 {(cal is null ? "无" : $"{cal.FileName}（{cal.Points} 点）{cal.K:F4} kN/ns，来源 {source}")}"
                            + $"，额定 {(rated > 0 ? rated + " kN" : "未配置")}");
                    }

                    index++;
                }
            }
        }

        var onDevice = flanges.Where(f => f.OnDevice).ToList();
        return new CalPlan
        {
            Mode = mode,
            WarnRatio = warn,
            AlarmRatio = alarm,
            Flanges = flanges,
            BoundIndex = boundIndex,
            HasFarm = farm is not null,
            GlobalCal = globalCal,
            GlobalRated = globalRated,
            WantedChannels = onDevice.SelectMany(f => f.Channels).Distinct().OrderBy(c => c).ToArray(),
            PlannedChannels = onDevice.SelectMany(f => f.Planned).Distinct().OrderBy(c => c).ToArray(),
            _channelFlangeInit = channelFlange,
        };
    }

    // 只为了在对象初始化器里灌字典；构造完就不再改
    private Dictionary<ushort, int> _channelFlangeInit
    {
        init
        {
            foreach (var kv in value) _channelFlange[kv.Key] = kv.Value;
        }
    }
}
