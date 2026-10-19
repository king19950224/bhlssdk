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

    /// <summary>生效的标定；空 = 未标定。法兰默认规格那种螺栓的。</summary>
    public Ucf.Cal? Cal { get; init; }

    /// <summary>标定从哪来："法兰自己的 xxx.ucf" / "规格 M36 的 xxx.ucf" / "理论 …" / "程序目录 xxx.ucf" / "无"。</summary>
    public string CalSource { get; init; } = "无";

    /// <summary>额定预紧力 kN；0 = 未配置。锚定口径下就是"锚定时假定的预紧力"。法兰没填时取规格上的。</summary>
    public decimal Rated { get; init; }

    /// <summary>法兰默认的试件规格（farm.json boltSpec 引用规格库；老写法 bolt 内嵌对象也认）。</summary>
    public BoltSpec? Spec { get; init; }

    /// <summary>通道单独指定的规格（channelSpecs）：这一路装的不是法兰默认那种螺栓，标定/理论/额定都按它自己的。</summary>
    public IReadOnlyDictionary<ushort, BoltSpec> ChannelSpecs { get; init; } = new Dictionary<ushort, BoltSpec>();

    /// <summary>
    /// 法兰是否接在这台设备上：写了 channels 或 channelBolts 就算。
    /// 只写了 plannedChannels 的是规划位，不参与通道归属和轮询，页面只画待接入的位子。
    /// </summary>
    public bool OnDevice { get; init; }
}

/// <summary>
/// 接线、标定与阈值方案，从 farm.json 解析。桌面端（FxMonitor）和大屏（BoltPreload.Monitor）用同一份：
/// 同一颗螺栓在两边算出来的必须是同一个数、叫同一个名字。
/// 一台 48 路设备可以跨多个法兰（CH1–8 顶段、CH9–16 中段……）：每个法兰在 flanges[] 里写自己的
/// channels / plannedChannels / channelBolts / ucf / ratedKn / boltSpec / channelSpecs，通道由此归属到法兰，力值、额定、页面都跟着走。
/// 兼容老写法：顶层 channels / plannedChannels 归到 bind.flangeIndex 那个法兰。
/// </summary>
public sealed class CalPlan
{
    /// <summary>空方案：没有 farm.json 也没有标定，口径和阈值取缺省。大屏没有可用配置时页面口径按它给。</summary>
    public static CalPlan Empty { get; } = new();

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

    /// <summary>0 不温补 / 1 材料系数 / 2 自定义。farm.json tcMode，默认 1，和厂家项目一致。</summary>
    public ushort TcMode { get; private init; } = 1;

    /// <summary>farm.json tempCoefficient；空则 SDK 默认碳钢系数。</summary>
    public decimal? TempCoefficient { get; private init; }

    public string TcText => TcMode == 0 ? "未温补"
        : TcMode == 2 ? $"自定义温补{(TempCoefficient is { } c ? $" {c:G}" : "")}"
        : "材料温补";

    /// <summary>farm.json tempTau：温度平滑档位（direct/fast/balanced/stable/conservative），默认 fast，和厂家软件一致。</summary>
    public FxSDK.Utility.TempTauProfile TempTau { get; private init; } = FxSDK.Utility.TempTauProfile.Fast;

    public string TempTauText => TempTauName(TempTau);

    /// <summary>温度借用表（farm.json devices[].tempFrom：{ "3": 1 } = CH3 用 CH1 的温度）。</summary>
    public IReadOnlyDictionary<ushort, ushort> TempFrom { get; private init; } = new Dictionary<ushort, ushort>();

    /// <summary>规格库 + 材料库（farm.json 顶层 boltSpecs / materials）。</summary>
    public SpecLibrary Library { get; private init; } = new();

    public static string TempTauName(FxSDK.Utility.TempTauProfile p) => p switch
    {
        FxSDK.Utility.TempTauProfile.Direct => "不平滑",
        FxSDK.Utility.TempTauProfile.Fast => "快（60 s）",
        FxSDK.Utility.TempTauProfile.Stable => "稳（480 s）",
        FxSDK.Utility.TempTauProfile.Conservative => "保守（900 s）",
        _ => "均衡（180 s）",
    };

    /// <summary>farm.json 里的写法 ↔ 枚举：direct / fast / balanced / stable / conservative（也认 0～4）。</summary>
    public static FxSDK.Utility.TempTauProfile? ParseTempTau(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "direct" or "0" or "off" or "none" => FxSDK.Utility.TempTauProfile.Direct,
        "fast" or "1" => FxSDK.Utility.TempTauProfile.Fast,
        "balanced" or "2" => FxSDK.Utility.TempTauProfile.Balanced,
        "stable" or "3" => FxSDK.Utility.TempTauProfile.Stable,
        "conservative" or "4" => FxSDK.Utility.TempTauProfile.Conservative,
        _ => null,
    };

    public static string TempTauKey(FxSDK.Utility.TempTauProfile p) => p switch
    {
        FxSDK.Utility.TempTauProfile.Direct => "direct",
        FxSDK.Utility.TempTauProfile.Fast => "fast",
        FxSDK.Utility.TempTauProfile.Stable => "stable",
        FxSDK.Utility.TempTauProfile.Conservative => "conservative",
        _ => "balanced",
    };

    private readonly Dictionary<ushort, int> _channelFlange = new();

    /// <summary>每条规格解析出的标定（它自己的 .ucf 或理论斜率）；空 = 这条规格给不出力。按对象引用查。</summary>
    private readonly Dictionary<BoltSpec, Ucf.Cal?> _specCals = new(ReferenceEqualityComparer.Instance);

    public string ModeText => Mode == BaselineMode.Anchor ? "在役锚定" : "自由状态置零";

    /// <summary>通道属于哪个法兰；-1 = 不在任何接入法兰的配置里（未分配）。</summary>
    public int FlangeOf(ushort ch) => _channelFlange.TryGetValue(ch, out var i) ? i : -1;

    /// <summary>这一路的试件规格：通道单独指定的优先，否则法兰默认；都没有为空。</summary>
    public BoltSpec? SpecFor(ushort ch)
    {
        if (FlangeOf(ch) is not (var i and >= 0)) return null;
        var f = Flanges[i];
        return f.ChannelSpecs.TryGetValue(ch, out var own) ? own : f.Spec;
    }

    /// <summary>这一路是不是"和法兰默认不同的螺栓"（channelSpecs 里单独写了）。</summary>
    public bool HasOwnSpec(ushort ch) => FlangeOf(ch) is var i and >= 0 && Flanges[i].ChannelSpecs.ContainsKey(ch);

    public MaterialSpec MaterialOf(BoltSpec? spec) => Library.MaterialFor(spec?.Grade);

    /// <summary>规格上的标定（它自己的 .ucf 或理论斜率）。</summary>
    public Ucf.Cal? SpecCal(BoltSpec? spec) => spec is { } s && _specCals.TryGetValue(s, out var c) ? c : null;

    /// <summary>
    /// 这一路用哪份标定：通道单独指定了规格 → 只认那条规格自己的（法兰的 .ucf 是给法兰默认那种螺栓做的，
    /// 拿来算另一种螺栓就是错的）；否则法兰的（法兰 .ucf → 规格 .ucf → 规格理论 → 程序目录兜底）。
    /// </summary>
    public Ucf.Cal? CalFor(ushort ch)
    {
        if (FlangeOf(ch) is not (var i and >= 0)) return GlobalCal;
        var f = Flanges[i];
        if (f.ChannelSpecs.TryGetValue(ch, out var own)) return SpecCal(own);
        return f.Cal;
    }

    /// <summary>标定来源的文字，界面提示用。</summary>
    public string CalSourceFor(ushort ch)
    {
        if (FlangeOf(ch) is not (var i and >= 0)) return GlobalCal is { } g ? $"程序目录 {g.FileName}" : "无";
        var f = Flanges[i];
        if (f.ChannelSpecs.TryGetValue(ch, out var own))
        {
            var c = SpecCal(own);
            return c is null ? $"规格「{own.DisplayName}」给不出斜率" : c.Points == 0 ? $"理论 {own.Summary(MaterialOf(own))}" : $"规格「{own.DisplayName}」的 {c.FileName}";
        }

        return f.CalSource;
    }

    /// <summary>额定：通道单独规格上的 → 法兰的（法兰 ratedKn → 法兰默认规格 ratedKn → 全局）。</summary>
    public decimal RatedFor(ushort ch)
    {
        if (FlangeOf(ch) is not (var i and >= 0)) return GlobalRated;
        var f = Flanges[i];
        if (f.ChannelSpecs.TryGetValue(ch, out var own)) return own.RatedKn is { } r && r > 0 ? r : 0;
        return f.Rated;
    }

    /// <summary>按规格总长预计的回波声时 ns；没规格/没总长为空。</summary>
    public decimal? ExpectedEchoNs(ushort ch) => SpecFor(ch) is { } s ? s.ExpectedEchoNs(MaterialOf(s)) : null;

    /// <summary>按规格该写的回波窗 ns。</summary>
    public decimal? EchoWinFor(ushort ch) => SpecFor(ch) is { } s ? s.EchoWinNs(MaterialOf(s)) : null;

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

    /// <summary>较置零变化量 kN = k·Δt。未标定为空。</summary>
    public decimal? DeltaKn(ushort ch, decimal? dt) => CalFor(ch) is { } c && dt is { } d ? d * c.K : null;

    /// <summary>夹紧力 kN = k·Δt+b。界面主数字。未标定为空。</summary>
    public decimal? ClampKn(ushort ch, decimal? dt) => CalFor(ch) is { } c && dt is { } d ? d * c.K + c.B : null;

    /// <summary>估算预紧力。自由置零等于夹紧力；在役锚定为额定 + k·Δt。没有额定值为空。Health.Judge 用这个。</summary>
    public decimal? EstKn(ushort ch, decimal? dt)
    {
        if (CalFor(ch) is not { } c || dt is not { } d) return null;
        if (Mode == BaselineMode.Free) return d * c.K + c.B;
        var rated = RatedFor(ch);
        return rated > 0 ? rated + d * c.K : null;
    }

    /// <summary>改温度补偿开关（厂家那三个选项），其它标定/接线不动。</summary>
    public CalPlan WithTc(ushort tcMode, decimal? tempCoefficient) => new()
    {
        Mode = Mode, WarnRatio = WarnRatio, AlarmRatio = AlarmRatio, BoundIndex = BoundIndex, HasFarm = HasFarm,
        Flanges = Flanges, GlobalCal = GlobalCal, GlobalRated = GlobalRated,
        TcMode = tcMode, TempCoefficient = tempCoefficient, TempTau = TempTau, TempFrom = TempFrom, Library = Library,
        WantedChannels = WantedChannels, PlannedChannels = PlannedChannels,
        _channelFlangeInit = _channelFlange, _specCalsInit = _specCals,
    };

    /// <summary>改温度平滑档位，其它不动。</summary>
    public CalPlan WithTempTau(FxSDK.Utility.TempTauProfile tau) => new()
    {
        Mode = Mode, WarnRatio = WarnRatio, AlarmRatio = AlarmRatio, BoundIndex = BoundIndex, HasFarm = HasFarm,
        Flanges = Flanges, GlobalCal = GlobalCal, GlobalRated = GlobalRated,
        TcMode = TcMode, TempCoefficient = TempCoefficient, TempTau = tau, TempFrom = TempFrom, Library = Library,
        WantedChannels = WantedChannels, PlannedChannels = PlannedChannels,
        _channelFlangeInit = _channelFlange, _specCalsInit = _specCals,
    };

    /// <summary>先读 wwwroot\farm.json（和大屏同一份），没有再读 exe 旁的。两份都在且内容不同时说明一声，界面用 wwwroot 那份。</summary>
    public static JsonElement? FindFarm(string baseDir, Action<string> log)
    {
        var www = Path.Combine(baseDir, "wwwroot", "farm.json");
        var side = Path.Combine(baseDir, "farm.json");
        if (File.Exists(www) && File.Exists(side))
        {
            try
            {
                if (!string.Equals(File.ReadAllText(www), File.ReadAllText(side), StringComparison.Ordinal))
                    log("exe 旁同时有 wwwroot\\farm.json 和 farm.json，内容不一致，界面用 wwwroot 这份");
            }
            catch (Exception ex)
            {
                log("比对两份 farm.json 失败：" + ex.Message);
            }
        }

        foreach (var path in new[] { www, side })
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
    /// 解析一台设备的方案。farm 是整份 farm.json：baseline / thresholds / flanges[] 结构模板 / boltSpecs / materials；
    /// device 是 devices[] 里这台设备那一项（它的 flanges[] 按 index 或 name 对上模板，写接线/标定/额定/规格）；
    /// device 为空时按老写法从顶层 flanges[] 直接读接线。
    /// </summary>
    public static CalPlan Load(JsonElement? farm, JsonElement? device, string baseDir, Action<string> log)
    {
        var mode = BaselineMode.Anchor;
        decimal globalRated = 0, warn = 0.85m, alarm = 0.7m;
        ushort tcMode = 1;
        decimal? tempCoefficient = null;
        var tempTau = FxSDK.Utility.TempTauProfile.Fast;
        var tempFrom = new Dictionary<ushort, ushort>();
        var boundIndex = 0;
        var flanges = new List<FlangeCal>();
        var channelFlange = new Dictionary<ushort, int>();
        var ucfCache = new Dictionary<string, Ucf.Cal?>(StringComparer.OrdinalIgnoreCase);
        var library = SpecLibrary.Load(farm);
        var specCals = new Dictionary<BoltSpec, Ucf.Cal?>(ReferenceEqualityComparer.Instance);

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

        // 一条规格给出的标定：它自己的 .ucf 优先，没有就按材料常数算理论斜率（Points = 0 标记"理论"）
        Ucf.Cal? CalOfSpec(BoltSpec spec)
        {
            if (specCals.TryGetValue(spec, out var known)) return known;
            Ucf.Cal? cal = null;
            var named = spec.Ucf is { Length: > 0 };
            if (named) cal = LoadUcf(spec.Ucf!, $"规格「{spec.DisplayName}」");
            if (cal is null && !named && spec.TheoreticalK(library.MaterialFor(spec.Grade)) is { } k)
                cal = new Ucf.Cal(k, 0, "kN", 0, $"理论·{spec.DisplayName}·{spec.Grade}");
            specCals[spec] = cal;
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

            if (root.TryGetProperty("tcMode", out var tm) && tm.TryGetInt32(out var tmi) && tmi is >= 0 and <= 2)
            {
                tcMode = (ushort)tmi;
            }

            if (root.TryGetProperty("tempCoefficient", out var tcc) && tcc.TryGetDecimal(out var tcv) && tcv > 0)
            {
                tempCoefficient = tcv;
            }

            if (root.TryGetProperty("tempTau", out var tt))
            {
                var parsed = tt.ValueKind == JsonValueKind.String ? ParseTempTau(tt.GetString())
                    : tt.ValueKind == JsonValueKind.Number && tt.TryGetInt32(out var ti) ? ParseTempTau(ti.ToString()) : null;
                if (parsed is { } p) tempTau = p;
                else log($"tempTau 写的 {tt.GetRawText()} 不认识（direct / fast / balanced / stable / conservative），按 fast");
            }

            log($"声时差温补：{(tcMode == 0 ? "关（回波声时 − 参考声时）" : tcMode == 2 ? $"自定义系数 {tempCoefficient:G}" : "材料系数")}，与厂家 TcMode={tcMode} 同一套；温度平滑 {TempTauName(tempTau)}");
            if (library.Specs.Count > 0) log($"规格库 {library.Specs.Count} 条：{string.Join("、", library.Specs.Select(s => s.DisplayName))}");

            // 温度借用：设备那一项的 tempFrom（老写法在顶层）
            var tfSrc = device is { } dv && dv.TryGetProperty("tempFrom", out var tf1) ? tf1
                : device is null && root.TryGetProperty("tempFrom", out var tf2) ? tf2 : (JsonElement?)null;
            if (tfSrc is { ValueKind: JsonValueKind.Object } tfo)
            {
                foreach (var p in tfo.EnumerateObject())
                {
                    if (ushort.TryParse(p.Name, out var ch) && p.Value.TryGetInt32(out var from) && from > 0 && from <= ushort.MaxValue && from != ch)
                    {
                        tempFrom[ch] = (ushort)from;
                    }
                    else
                    {
                        log($"tempFrom 里 \"{p.Name}\": {p.Value.GetRawText()} 不合法（通道号 → 借哪一路的温度，不能借自己），跳过");
                    }
                }

                if (tempFrom.Count > 0) log($"温度借用：{string.Join("、", tempFrom.Select(kv => $"CH{kv.Key}←CH{kv.Value}"))}");
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

                    // 法兰默认规格：boltSpec 引用规格库；老写法 bolt 内嵌一个对象也认
                    BoltSpec? spec = null;
                    if (src is { } s6 && s6.TryGetProperty("boltSpec", out var bsn) && bsn.ValueKind == JsonValueKind.String)
                    {
                        spec = library.Find(bsn.GetString());
                        if (spec is null) log($"法兰「{name}」引用的规格「{bsn.GetString()}」不在规格库里，按没配规格");
                    }
                    else if (src is { } s7 && s7.TryGetProperty("bolt", out var be) && be.ValueKind == JsonValueKind.Object)
                    {
                        spec = BoltSpec.FromJson(be);
                    }

                    // 通道单独指定的规格
                    var channelSpecs = new Dictionary<ushort, BoltSpec>();
                    if (src is { } s8 && s8.TryGetProperty("channelSpecs", out var cs) && cs.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in cs.EnumerateObject())
                        {
                            if (!ushort.TryParse(p.Name, out var ch)) continue;
                            var sn = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                            if (library.Find(sn) is { } own) channelSpecs[ch] = own;
                            else log($"法兰「{name}」CH{ch} 引用的规格「{sn}」不在规格库里，按法兰默认");
                        }
                    }

                    var ucfName = src is { } s4 && s4.TryGetProperty("ucf", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    var ownCal = string.IsNullOrWhiteSpace(ucfName) ? null : LoadUcf(ucfName!, $"法兰「{name}」");
                    var source = ownCal is not null ? $"法兰自己的 {ucfName}" : globalSource;
                    if (ownCal is null && spec is { } sp && CalOfSpec(sp) is { } specCal)
                    {
                        ownCal = specCal;
                        source = specCal.Points == 0 ? $"理论 {sp.Summary(library.MaterialFor(sp.Grade))}" : $"规格「{sp.DisplayName}」的 {specCal.FileName}";
                    }

                    foreach (var own in channelSpecs.Values) CalOfSpec(own);

                    // 点名了 .ucf 却读不到：按未标定。换成目录里随便哪份最新的，斜率可能差几倍
                    var named = !string.IsNullOrWhiteSpace(ucfName);
                    if (named && ownCal is null)
                    {
                        source = $"{ucfName} 找不到或读不了";
                        log($"法兰「{name}」指定的标定文件 {ucfName} 找不到或读不了，按未标定（不用程序目录里别的 .ucf 顶替）");
                    }

                    var cal = ownCal ?? (named ? null : globalCal);
                    var rated = src is { } s5 && s5.TryGetProperty("ratedKn", out var r) && r.TryGetDecimal(out var rv) ? rv
                        : spec?.RatedKn is { } sr && sr > 0 ? sr
                        : globalRated;
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
                        Spec = spec,
                        ChannelSpecs = channelSpecs,
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
                            + $"，额定 {(rated > 0 ? rated + " kN" : "未配置")}"
                            + (spec is { } ? $"，规格 {spec.DisplayName}" : string.Empty)
                            + (channelSpecs.Count > 0 ? $"，通道单独规格 {string.Join("、", channelSpecs.Select(kv => $"CH{kv.Key}={kv.Value.DisplayName}"))}" : string.Empty));
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
            TcMode = tcMode,
            TempCoefficient = tempCoefficient,
            TempTau = tempTau,
            TempFrom = tempFrom,
            Library = library,
            WantedChannels = onDevice.SelectMany(f => f.Channels).Distinct().OrderBy(c => c).ToArray(),
            PlannedChannels = onDevice.SelectMany(f => f.Planned).Distinct().OrderBy(c => c).ToArray(),
            _channelFlangeInit = channelFlange,
            _specCalsInit = specCals,
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

    private Dictionary<BoltSpec, Ucf.Cal?> _specCalsInit
    {
        init
        {
            foreach (var kv in value) _specCals[kv.Key] = kv.Value;
        }
    }
}
