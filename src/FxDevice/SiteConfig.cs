using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FxDevice;

/// <summary>
/// 现场配置：farm.json 的可编辑视图——全场法兰模板（名字、螺栓数、编号）+ 一台设备的接线/标定/额定。
/// 桌面端的"现场配置"对话框拿它读进来、改完写回：文件里其它不认识的字段（kind、marks、plannedChannels、thresholds、
/// 别的设备……）原样保留，只动这里管的几项。老写法（顶层 device/bind、接线写在模板上）读得进来，写回时转成 devices[] 新写法。
/// </summary>
public sealed class SiteConfig
{
    /// <summary>一个法兰：模板字段全场统一；ucf/ratedKn 是这台设备上这个法兰的。</summary>
    public sealed class Flange
    {
        public string Name { get; set; } = string.Empty;

        public int BoltCount { get; set; } = 1;

        /// <summary>图纸上正上方那颗的编号。</summary>
        public int TopBolt { get; set; } = 1;

        public bool Clockwise { get; set; } = true;

        /// <summary>这台设备上该法兰用的标定文件名（程序目录下）；空 = 没配。</summary>
        public string? Ucf { get; set; }

        /// <summary>额定预紧力 kN；0 = 没配。</summary>
        public decimal Rated { get; set; }

        internal JsonObject? Template { get; set; }

        internal JsonObject? Wiring { get; set; }

        /// <summary>读进来时在模板数组里的下标；新加的为 -1。删法兰后别的设备接线里的 index 要按它重排。</summary>
        internal int OriginalIndex { get; set; } = -1;
    }

    /// <summary>一路通道接在哪：FlangeIndex 为法兰模板下标；None = 不接入；Guard = 巡检位（只判有没有探头）。</summary>
    public sealed class Wire
    {
        public const int None = -1;
        public const int Guard = -2;

        public ushort Ch { get; set; }

        public int FlangeIndex { get; set; } = None;

        /// <summary>装在几号螺栓上；空 = 没登记。</summary>
        public int? Bolt { get; set; }
    }

    private readonly JsonObject _root;
    private readonly bool _legacy;
    private JsonObject? _deviceCurrent;

    /// <summary>文件里代表这台设备的那个对象（写回后指向新写的对象）。</summary>
    private JsonObject? _device => _deviceCurrent;

    public string FarmName { get; set; } = string.Empty;

    public int TurbineCount { get; set; }

    public int Turbine { get; set; } = 1;

    public string Endpoint { get; set; } = string.Empty;

    public List<Flange> Flanges { get; } = new();

    public List<Wire> Wires { get; } = new();

    /// <summary>文件里其它设备的（机组号, 地址），校验"一台机组只能接一台"用。</summary>
    public IReadOnlyList<(int Turbine, string Endpoint)> OtherDevices { get; }

    /// <summary>这台设备原来就在文件里（按 IP 对上的），还是新加的。</summary>
    public bool IsExisting => _device is not null || _legacy;

    private SiteConfig(JsonObject root, JsonObject? device, bool legacy, IReadOnlyList<(int, string)> others)
    {
        _root = root;
        _deviceCurrent = device;
        _legacy = legacy;
        OtherDevices = others;
    }

    /// <summary>别的设备接线里引用了哪些法兰模板：(机组, index 或空, 模板名)。</summary>
    private IEnumerable<(int Turbine, int? Index, string Name)> OtherDeviceFlangeRefs()
    {
        if (_root["devices"] is not JsonArray devs) yield break;
        // 名字按文件里原来的模板数组查（被删掉的模板已不在 Flanges 里了）
        var names = new Dictionary<int, string>();
        if (_root["flanges"] is JsonArray tmpl)
        {
            for (var i = 0; i < tmpl.Count; i++) names[i] = Str((tmpl[i] as JsonObject)?["name"]) ?? $"#{i}";
        }

        foreach (var node in devs)
        {
            if (node is not JsonObject od || ReferenceEquals(od, _device) || od["flanges"] is not JsonArray ofl) continue;
            var t = Int(od["turbine"]) ?? 0;
            foreach (var w in ofl)
            {
                if (w is not JsonObject wo) continue;
                var idx = Int(wo["index"]);
                yield return (t, idx, idx is { } i && names.TryGetValue(i, out var n) ? n : Str(wo["name"]) ?? $"#{idx}");
            }
        }
    }

    /// <summary>只比 IP，端口可以不写。</summary>
    public static string Host(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return string.Empty;
        var s = endpoint.Trim();
        var i = s.LastIndexOf(':');
        return i > 0 ? s[..i] : s;
    }

    public static bool SameHost(string? a, string? b) => string.Equals(Host(a), Host(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 从 farm.json 文本读出当前设备（按地址）的配置；文本为空/没有这个文件就是一份空配置。
    /// 解析失败抛 JsonException——对话框要把原因给人看，不能悄悄当成空文件把老配置覆盖掉。
    /// </summary>
    public static SiteConfig Load(string? json, string endpoint)
    {
        var root = string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json) as JsonObject ?? new JsonObject();

        // 模板
        var flanges = new List<Flange>();
        if (root["flanges"] is JsonArray tmpl)
        {
            var i = 0;
            foreach (var node in tmpl)
            {
                var f = node as JsonObject ?? new JsonObject();
                var numbering = f["numbering"] as JsonObject;
                flanges.Add(new Flange
                {
                    Name = Str(f["name"]) ?? $"法兰 #{i}",
                    BoltCount = Math.Max(1, Int(f["boltCount"]) ?? 1),
                    TopBolt = Math.Max(1, Int(numbering?["topBolt"]) ?? 1),
                    Clockwise = numbering?["clockwise"]?.GetValue<bool>() ?? true,
                    Template = f,
                    OriginalIndex = i,
                });
                i++;
            }
        }

        JsonObject? device = null;
        var legacy = false;
        var others = new List<(int, string)>();
        var turbine = 0;

        if (root["devices"] is JsonArray devs && devs.Count > 0)
        {
            foreach (var node in devs)
            {
                if (node is not JsonObject d) continue;
                var ep = Str(d["device"]) ?? string.Empty;
                var t = Int(d["turbine"]) ?? 0;
                if (device is null && SameHost(ep, endpoint))
                {
                    device = d;
                    turbine = t;
                }
                else
                {
                    others.Add((t, ep));
                }
            }
        }
        else if (root["device"] is not null || root["bind"] is not null)
        {
            // 老写法：只有一台设备，接线写在模板自己身上
            legacy = true;
            turbine = Int((root["bind"] as JsonObject)?["turbine"]) ?? 1;
        }

        var cfg = new SiteConfig(root, device, legacy, others)
        {
            FarmName = Str(root["farmName"]) ?? string.Empty,
            TurbineCount = Int(root["turbineCount"]) ?? 0,
            Endpoint = device is not null ? Str(device["device"]) ?? endpoint : endpoint,
            Turbine = turbine > 0 ? turbine : (others.Count > 0 ? others.Max(o => o.Item1) + 1 : 1),
        };
        cfg.Flanges.AddRange(flanges);

        // 接线：新写法从设备项的 flanges[] 读（按 index 或 name 对模板），老写法直接从模板读
        var wiring = new Dictionary<int, JsonObject>();
        if (device?["flanges"] is JsonArray dfl)
        {
            foreach (var node in dfl)
            {
                if (node is not JsonObject w) continue;
                var idx = Int(w["index"]);
                if (idx is null && Str(w["name"]) is { } nm) idx = flanges.FindIndex(f => f.Name == nm) is var k and >= 0 ? k : null;
                if (idx is { } ix && ix >= 0 && ix < flanges.Count) wiring[ix] = w;
            }
        }
        else if (legacy)
        {
            for (var i = 0; i < flanges.Count; i++) wiring[i] = flanges[i].Template!;
        }

        foreach (var (idx, w) in wiring)
        {
            var f = flanges[idx];
            f.Wiring = w;
            f.Ucf = Str(w["ucf"]);
            f.Rated = Dec(w["ratedKn"]) ?? 0;
            var bolts = new Dictionary<ushort, int>();
            if (w["channelBolts"] is JsonObject cb)
            {
                foreach (var p in cb)
                {
                    if (ushort.TryParse(p.Key, out var ch) && Int(p.Value) is { } b) bolts[ch] = b;
                }
            }

            foreach (var ch in Channels(w["channels"]).Concat(bolts.Keys).Distinct().OrderBy(c => c))
            {
                if (cfg.Wires.Any(x => x.Ch == ch)) continue;   // 一路只能在一个法兰上，先出现的算
                cfg.Wires.Add(new Wire { Ch = ch, FlangeIndex = idx, Bolt = bolts.TryGetValue(ch, out var b) ? b : null });
            }
        }

        foreach (var ch in Channels((device ?? (legacy ? root : null))?["guardChannels"]))
        {
            if (cfg.Wires.Any(x => x.Ch == ch)) continue;
            cfg.Wires.Add(new Wire { Ch = ch, FlangeIndex = Wire.Guard });
        }

        cfg.Wires.Sort((a, b) => a.Ch.CompareTo(b.Ch));
        return cfg;
    }

    /// <summary>保存前的检查：返回错误列表，空 = 可以保存。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Turbine < 1) errors.Add("机组号要从 1 开始");
        if (Host(Endpoint).Length == 0) errors.Add("设备地址不能为空（ip:端口）");
        foreach (var (t, ep) in OtherDevices)
        {
            if (t == Turbine) errors.Add($"F{Turbine:00} 已经接了设备 {ep}，一台机组只能接一台");
            if (SameHost(ep, Endpoint)) errors.Add($"地址 {Host(Endpoint)} 已经是 F{t:00} 的设备，两台设备不能同一个地址");
        }

        // 删掉的法兰模板若还被别的设备的接线引用，不能删——那台设备的探头会失去归属
        var kept = new HashSet<int>(Flanges.Where(f => f.OriginalIndex >= 0).Select(f => f.OriginalIndex));
        foreach (var (t, idx, name) in OtherDeviceFlangeRefs())
        {
            if (idx is { } i && !kept.Contains(i)) errors.Add($"F{t:00} 的接线还引用着被删掉的法兰「{name}」，先改那台设备");
        }

        if (Flanges.Count == 0) errors.Add("至少要有一个法兰");
        for (var i = 0; i < Flanges.Count; i++)
        {
            var f = Flanges[i];
            var label = f.Name.Length > 0 ? $"法兰「{f.Name}」" : $"第 {i + 1} 个法兰";
            if (f.Name.Trim().Length == 0) errors.Add($"第 {i + 1} 个法兰没有名字");
            if (f.BoltCount < 1) errors.Add($"{label}的螺栓数要 ≥ 1");
            if (f.TopBolt < 1 || f.TopBolt > f.BoltCount) errors.Add($"{label}正上方编号要在 1–{f.BoltCount} 之间");
            if (f.Rated < 0) errors.Add($"{label}额定预紧力不能为负");
            var onIt = Wires.Where(w => w.FlangeIndex == i).ToList();
            var dup = onIt.Where(w => w.Bolt is { }).GroupBy(w => w.Bolt).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            foreach (var b in dup) errors.Add($"{label}的 #{b} 螺栓上接了多路探头（{string.Join("、", onIt.Where(w => w.Bolt == b).Select(w => "CH" + w.Ch))}）");
            foreach (var w in onIt.Where(w => w.Bolt is { } b && (b < 1 || b > f.BoltCount)))
            {
                errors.Add($"CH{w.Ch} 的螺栓号 {w.Bolt} 超出{label}的 1–{f.BoltCount}");
            }
        }

        if (Flanges.Count > 0 && Flanges.Select(f => f.Name.Trim()).Distinct().Count() != Flanges.Count) errors.Add("法兰名字有重复");
        foreach (var w in Wires.Where(w => w.FlangeIndex >= Flanges.Count)) errors.Add($"CH{w.Ch} 指向不存在的法兰");
        return errors;
    }

    /// <summary>把模型写回 JSON 文本（缩进、中文不转义）。别的字段照原样保留。</summary>
    public string ToJson()
    {
        // 顶层
        if (FarmName.Trim().Length > 0) _root["farmName"] = FarmName.Trim();
        var maxTurbine = Math.Max(Turbine, OtherDevices.Count > 0 ? OtherDevices.Max(o => o.Turbine) : 0);
        _root["turbineCount"] = Math.Max(TurbineCount, maxTurbine);

        // 模板：在原对象上改，kind / marks / plannedChannels 等原样带着；老写法留在模板上的接线字段清掉
        var tmpl = new JsonArray();
        foreach (var f in Flanges)
        {
            var o = f.Template is { } t ? (JsonObject)t.DeepClone() : new JsonObject();
            foreach (var legacyKey in new[] { "channels", "channelBolts", "ucf", "ratedKn" }) o.Remove(legacyKey);
            o["name"] = f.Name.Trim();
            o["boltCount"] = f.BoltCount;
            var numbering = o["numbering"] as JsonObject ?? new JsonObject();
            numbering["topBolt"] = f.TopBolt;
            numbering["clockwise"] = f.Clockwise;
            o["numbering"] = numbering;
            tmpl.Add(o);
        }

        _root["flanges"] = tmpl;

        // 这台设备
        var dev = _device is { } d ? (JsonObject)d.DeepClone() : new JsonObject();
        dev["turbine"] = Turbine;
        dev["device"] = Endpoint.Trim();
        var dfl = new JsonArray();
        for (var i = 0; i < Flanges.Count; i++)
        {
            var f = Flanges[i];
            var onIt = Wires.Where(w => w.FlangeIndex == i).OrderBy(w => w.Ch).ToList();
            var hasUcf = !string.IsNullOrWhiteSpace(f.Ucf);
            if (onIt.Count == 0 && !hasUcf && f.Rated <= 0) continue;

            var o = f.Wiring is { } w0 && !_legacy ? (JsonObject)w0.DeepClone() : new JsonObject();
            foreach (var k in new[] { "name", "channels", "channelBolts", "ucf", "ratedKn" }) o.Remove(k);
            var ordered = new JsonObject { ["index"] = i };
            ordered["channels"] = new JsonArray(onIt.Select(w => (JsonNode)w.Ch).ToArray());
            var bolts = onIt.Where(w => w.Bolt is { }).ToList();
            if (bolts.Count > 0)
            {
                var cb = new JsonObject();
                foreach (var w in bolts) cb[w.Ch.ToString()] = w.Bolt!.Value;
                ordered["channelBolts"] = cb;
            }

            if (hasUcf) ordered["ucf"] = f.Ucf!.Trim();
            if (f.Rated > 0) ordered["ratedKn"] = f.Rated;
            foreach (var kv in o) ordered[kv.Key] = kv.Value?.DeepClone();
            dfl.Add(ordered);
        }

        dev["flanges"] = dfl;
        var guard = Wires.Where(w => w.FlangeIndex == Wire.Guard).Select(w => (JsonNode)w.Ch).ToArray();
        if (guard.Length > 0) dev["guardChannels"] = new JsonArray(guard);
        else dev.Remove("guardChannels");

        var devices = _root["devices"] as JsonArray ?? new JsonArray();
        // 模板增删后下标变了：别的设备接线里按 index 引用的要跟着改（按 name 引用的不动）
        var remap = new Dictionary<int, int>();
        for (var i = 0; i < Flanges.Count; i++)
        {
            if (Flanges[i].OriginalIndex >= 0) remap[Flanges[i].OriginalIndex] = i;
        }

        foreach (var node in devices)
        {
            if (node is not JsonObject od || ReferenceEquals(od, _device) || od["flanges"] is not JsonArray ofl) continue;
            for (var k = ofl.Count - 1; k >= 0; k--)
            {
                if (ofl[k] is not JsonObject w || Int(w["index"]) is not { } oi) continue;
                if (remap.TryGetValue(oi, out var ni)) w["index"] = ni;
                else ofl.RemoveAt(k);   // 引用的模板已删（Validate 会拦，这里兜底）
            }
        }

        // 这台设备：原来就在文件里的按对象替换（地址改了也认得是它），新加的追加
        var replaced = false;
        for (var i = 0; i < devices.Count; i++)
        {
            if (devices[i] is JsonObject od && (ReferenceEquals(od, _device) || (_device is null && SameHost(Str(od["device"]), Endpoint))))
            {
                devices[i] = dev;
                replaced = true;
                break;
            }
        }

        if (!replaced) devices.Add(dev);
        _root["devices"] = devices;
        // 写回后这台设备就是 dev 这个对象了；再调用一次 ToJson 仍能按对象找到它
        _deviceCurrent = dev;
        for (var i = 0; i < Flanges.Count; i++) Flanges[i].OriginalIndex = i;

        // 老写法的顶层字段转成 devices[] 后就多余了
        foreach (var k in new[] { "device", "bind", "channels", "plannedChannels", "guardChannels" }) _root.Remove(k);

        return _root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<decimal>(out var d)) return (int)d;
        return null;
    }

    private static decimal? Dec(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<decimal>(out var d)) return d;
        if (v.TryGetValue<double>(out var f)) return (decimal)f;
        return null;
    }

    private static IEnumerable<ushort> Channels(JsonNode? n)
    {
        if (n is not JsonArray arr) yield break;
        foreach (var x in arr)
        {
            if (Int(x) is { } v && v > 0 && v <= ushort.MaxValue) yield return (ushort)v;
        }
    }
}
