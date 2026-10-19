using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FxDevice;

/// <summary>连接方式，决定有效长度里头和螺母各算多少（VDI 2230 弹性长度份额：头 0.5d、螺母 0.4d、旋合螺纹 0.5d）。</summary>
public enum JointKind
{
    /// <summary>螺栓 + 螺母：夹持 + 0.5d + 0.4d。</summary>
    BoltNut,

    /// <summary>双头螺柱两端螺母：夹持 + 0.4d + 0.4d。</summary>
    StudNuts,

    /// <summary>螺栓拧入螺孔：夹持 + 0.5d + 0.5d。</summary>
    Tapped,
}

/// <summary>
/// 材料库的一条：性能等级带出屈服、声速、弹模、声弹因子、温度系数。厂家 u_spec 那一套。
/// 存 farm.json 顶层 materials[]；没写的等级用内置默认。
/// </summary>
public sealed class MaterialSpec
{
    public string Grade { get; set; } = "10.9";

    public decimal YieldMpa { get; set; } = 940;

    public decimal Velocity { get; set; } = 5900;

    public decimal ElasticGpa { get; set; } = 206;

    /// <summary>声弹因子 L_ae，力公式用 1+L_ae。碳钢常见 2。</summary>
    public decimal Acoustic { get; set; } = 2;

    /// <summary>温度补偿系数；空 = 用 SDK 碳钢默认 <see cref="DefaultTempCoefficient"/>。</summary>
    public decimal? TempCoefficient { get; set; }

    /// <summary>SDK 碳钢默认温度系数 /℃（厂家材料表 10.9 也是这个数）。界面上不留空，空的就显示它。</summary>
    public const decimal DefaultTempCoefficient = 0.00010269m;

    /// <summary>界面/计算实际用的温度系数：填了用填的，没填用 SDK 默认。</summary>
    public decimal EffectiveTempCoefficient => TempCoefficient is { } tc && tc > 0 ? tc : DefaultTempCoefficient;

    public static IReadOnlyList<MaterialSpec> Defaults { get; } = new[]
    {
        new MaterialSpec { Grade = "8.8", YieldMpa = 640 },
        new MaterialSpec { Grade = "9.8", YieldMpa = 720 },
        new MaterialSpec { Grade = "10.9", YieldMpa = 940 },
        new MaterialSpec { Grade = "12.9", YieldMpa = 1100 },
    };

    public MaterialSpec Clone() => new()
    {
        Grade = Grade, YieldMpa = YieldMpa, Velocity = Velocity, ElasticGpa = ElasticGpa,
        Acoustic = Acoustic, TempCoefficient = TempCoefficient,
    };

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["grade"] = Grade.Trim(),
            ["yieldMpa"] = YieldMpa,
            ["velocity"] = Velocity,
            ["elasticGpa"] = ElasticGpa,
            ["acoustic"] = Acoustic,
        };
        if (TempCoefficient is { } tc && tc > 0) o["tempCoefficient"] = tc;
        return o;
    }

    public static MaterialSpec? FromNode(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var grade = Json.Str(o["grade"])?.Trim();
        if (string.IsNullOrEmpty(grade)) return null;
        var m = new MaterialSpec { Grade = grade };
        if (Json.Dec(o["yieldMpa"]) is { } y && y > 0) m.YieldMpa = y;
        if (Json.Dec(o["velocity"]) is { } v && v > 0) m.Velocity = v;
        if (Json.Dec(o["elasticGpa"]) is { } e && e > 0) m.ElasticGpa = e;
        if (Json.Dec(o["acoustic"]) is { } a && a >= 0) m.Acoustic = a;
        if (Json.Dec(o["tempCoefficient"]) is { } tc && tc > 0) m.TempCoefficient = tc;
        return m;
    }
}

/// <summary>
/// 规格库的一条（厂家「试件规格」+ u_bolt_info）：一种螺栓填一次，法兰和通道按名字引用。
/// 直径+螺距 → 净截面积；夹持长度+连接方式 → 有效长度；总长 → 回波窗；有效长度+面积+材料 → 理论斜率。
/// 每项算出来的都可以手填覆盖（AreaMm2 / EffectiveMm）。存 farm.json 顶层 boltSpecs[]。
/// </summary>
public sealed class BoltSpec
{
    /// <summary>
    /// 回波窗起点放在预期回波前多少 ns。厂家软件按 总长×2×10⁶/声速 向下取整到 1 µs 直接当回波窗，不另留余量
    /// （"近似总长"填的是杆长，头高和真实声速本来就把回波推到窗内几微秒处），这里照厂家取 0。
    /// </summary>
    public const decimal LeadNs = 0;

    public string Name { get; set; } = string.Empty;

    /// <summary>公称直径 mm：M 后面的名义值，不是卡尺读数。</summary>
    public decimal? NominalMm { get; set; }

    public decimal? PitchMm { get; set; }

    /// <summary>净截面积 mm²，手填覆盖；空则按 ISO 898-1 由直径和螺距算。</summary>
    public decimal? AreaMm2 { get; set; }

    public string Grade { get; set; } = "10.9";

    /// <summary>近似总长 mm（头顶到杆尾），只用来放回波窗。</summary>
    public decimal? LengthMm { get; set; }

    /// <summary>夹持长度 mm：被夹法兰的总厚度。</summary>
    public decimal? ClampMm { get; set; }

    public JointKind Joint { get; set; } = JointKind.BoltNut;

    /// <summary>有效长度 mm，手填覆盖；空则按夹持长度 + 连接方式算。</summary>
    public decimal? EffectiveMm { get; set; }

    /// <summary>这种螺栓的工艺额定预紧力 kN；法兰没单独填时用它。</summary>
    public decimal? RatedKn { get; set; }

    public decimal? LowerKn { get; set; }

    public decimal? UpperKn { get; set; }

    /// <summary>这种螺栓的载荷标定文件（程序目录下）。标定文件对应一种规格，放在规格上比放在法兰上不容易配错。</summary>
    public string? Ucf { get; set; }

    public static decimal JointFactor(JointKind j) => j switch
    {
        JointKind.StudNuts => 0.8m,
        JointKind.Tapped => 1.0m,
        _ => 0.9m,
    };

    public static string JointName(JointKind j) => j switch
    {
        JointKind.StudNuts => "双头螺柱（两端螺母）",
        JointKind.Tapped => "拧入螺孔",
        _ => "螺栓 + 螺母",
    };

    public static string JointKey(JointKind j) => j switch
    {
        JointKind.StudNuts => "stud",
        JointKind.Tapped => "tapped",
        _ => "boltNut",
    };

    public static JointKind ParseJoint(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "stud" or "studnuts" or "双头螺柱" => JointKind.StudNuts,
        "tapped" or "blind" or "拧入螺孔" => JointKind.Tapped,
        _ => JointKind.BoltNut,
    };

    /// <summary>按夹持长度 + 连接方式算的有效长度。</summary>
    public decimal? DerivedEffective =>
        ClampMm is { } c && c > 0 && NominalMm is { } d && d > 0 ? c + JointFactor(Joint) * d : null;

    public decimal? ResolvedEffective => EffectiveMm is { } e && e > 0 ? e : DerivedEffective;

    public decimal? ResolvedArea => AreaMm2 is { } a && a > 0 ? a : StressArea(NominalMm, PitchMm);

    /// <summary>应力截面积 As = π/4·(d − 0.9382P)²（ISO 898-1 / GB/T 3098.1），和厂家对话框算出来的一致。</summary>
    public static decimal? StressArea(decimal? nominalMm, decimal? pitchMm)
    {
        if (nominalMm is not { } d || d <= 0 || pitchMm is not { } p || p <= 0) return null;
        var ds = d - 0.9382m * p;
        if (ds <= 0) return null;
        return (decimal)(Math.PI / 4) * ds * ds;
    }

    /// <summary>按总长预计的回波声时 ns = 总长×2×10⁶/声速。</summary>
    public decimal? ExpectedEchoNs(MaterialSpec m)
        => LengthMm is { } L && L > 0 && m.Velocity > 0 ? L * 2_000_000m / m.Velocity : null;

    /// <summary>回波窗 ns：预期回波前留 LeadNs，向下取整到 1000（设备按 1000 量化，起点只能提前不能推后）。</summary>
    public decimal? EchoWinNs(MaterialSpec m)
        => ExpectedEchoNs(m) is { } t ? Math.Max(0, Math.Floor((t - LeadNs) / 1000m) * 1000m) : null;

    /// <summary>
    /// 理论 kN/ns：t₀ = 2·L_e/v，k = E·A_s / ((1+L_ae)·t₀)。
    /// M36、L_e=193、A_s=816.72、E=206 GPa、L_ae=2 → 0.857，厂家理论值 0.864，差的是材料常数。
    /// </summary>
    public decimal? TheoreticalK(MaterialSpec m)
    {
        if (ResolvedEffective is not { } le || le <= 0) return null;
        if (ResolvedArea is not { } area || area <= 0) return null;
        if (m.Velocity <= 0 || m.ElasticGpa <= 0) return null;
        var t0 = 2m * le * 1_000_000m / m.Velocity;           // ns
        var denom = (1m + Math.Max(0, m.Acoustic)) * t0 * 1000m;
        if (denom <= 0) return null;
        var k = m.ElasticGpa * 1000m * area / denom;
        return k > 0 ? k : null;
    }

    public decimal? YieldKn(MaterialSpec m)
        => ResolvedArea is { } area && area > 0 && m.YieldMpa > 0 ? area * m.YieldMpa / 1000m : null;

    public string DisplayName => Name.Trim().Length > 0 ? Name.Trim()
        : NominalMm is { } d ? $"M{d:0.##}{(PitchMm is { } p ? $"×{p:0.##}" : "")}" : "未命名规格";

    public string Summary(MaterialSpec? m = null)
    {
        var parts = new List<string> { DisplayName };
        if (!string.IsNullOrWhiteSpace(Grade)) parts.Add(Grade.Trim());
        if (ResolvedArea is { } a) parts.Add($"As={a:0.#}");
        if (ResolvedEffective is { } le) parts.Add($"Le={le:0.#}");
        if (m is { } && TheoreticalK(m) is { } k) parts.Add($"{k:F3} kN/ns");
        if (Ucf is { Length: > 0 }) parts.Add(Ucf);
        return string.Join(" ", parts);
    }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Name) && NominalMm is null && PitchMm is null && AreaMm2 is null
        && LengthMm is null && ClampMm is null && EffectiveMm is null
        && RatedKn is null && LowerKn is null && UpperKn is null && string.IsNullOrWhiteSpace(Ucf);

    public BoltSpec Clone() => new()
    {
        Name = Name, NominalMm = NominalMm, PitchMm = PitchMm, AreaMm2 = AreaMm2, Grade = Grade,
        LengthMm = LengthMm, ClampMm = ClampMm, Joint = Joint, EffectiveMm = EffectiveMm,
        RatedKn = RatedKn, LowerKn = LowerKn, UpperKn = UpperKn, Ucf = Ucf,
    };

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        if (Name.Trim().Length > 0) o["name"] = Name.Trim();
        if (NominalMm is { } d) o["nominalMm"] = d;
        if (PitchMm is { } p) o["pitchMm"] = p;
        if (AreaMm2 is { } a) o["areaMm2"] = a;
        if (!string.IsNullOrWhiteSpace(Grade)) o["grade"] = Grade.Trim();
        if (LengthMm is { } L) o["lengthMm"] = L;
        if (ClampMm is { } c) o["clampMm"] = c;
        if (Joint != JointKind.BoltNut) o["joint"] = JointKey(Joint);
        if (EffectiveMm is { } e) o["effectiveMm"] = e;
        if (RatedKn is { } r) o["ratedKn"] = r;
        if (LowerKn is { } lo) o["lowerKn"] = lo;
        if (UpperKn is { } up) o["upperKn"] = up;
        if (Ucf is { Length: > 0 }) o["ucf"] = Ucf.Trim();
        return o;
    }

    public static BoltSpec? FromNode(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var s = new BoltSpec
        {
            Name = Json.Str(o["name"]) ?? string.Empty,
            Grade = Json.Str(o["grade"]) ?? "10.9",
            NominalMm = Json.Dec(o["nominalMm"]),
            PitchMm = Json.Dec(o["pitchMm"]),
            AreaMm2 = Json.Dec(o["areaMm2"]),
            LengthMm = Json.Dec(o["lengthMm"]),
            ClampMm = Json.Dec(o["clampMm"]),
            Joint = ParseJoint(Json.Str(o["joint"])),
            EffectiveMm = Json.Dec(o["effectiveMm"]),
            RatedKn = Json.Dec(o["ratedKn"]),
            LowerKn = Json.Dec(o["lowerKn"]),
            UpperKn = Json.Dec(o["upperKn"]),
            Ucf = Json.Str(o["ucf"]),
        };
        return s.IsEmpty ? null : s;
    }

    public static BoltSpec? FromJson(JsonElement el)
        => el.ValueKind == JsonValueKind.Object ? FromNode(JsonNode.Parse(el.GetRawText())) : null;

    public static bool TryParseDec(string? text, out decimal value)
    {
        text = (text ?? string.Empty).Trim().Replace(",", ".");
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>
/// 规格库 + 材料库，farm.json 顶层 boltSpecs[] / materials[]。全场统一，桌面端和大屏读同一份。
/// 法兰用 boltSpec 引用一条规格（法兰默认），通道用 channelSpecs 单独覆盖（实验室一路一种螺栓）。
/// </summary>
public sealed class SpecLibrary
{
    public List<BoltSpec> Specs { get; } = new();

    public List<MaterialSpec> Materials { get; } = new();

    public BoltSpec? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        return Specs.FirstOrDefault(s => string.Equals(s.Name.Trim(), n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>等级对应的材料：库里有就用库里的，没有用内置默认，再没有按 10.9。</summary>
    public MaterialSpec MaterialFor(string? grade)
    {
        var g = (grade ?? string.Empty).Trim();
        return Materials.FirstOrDefault(m => string.Equals(m.Grade.Trim(), g, StringComparison.OrdinalIgnoreCase))
               ?? MaterialSpec.Defaults.FirstOrDefault(m => m.Grade == g)
               ?? MaterialSpec.Defaults[2];
    }

    /// <summary>界面上可选的等级：库里的 ∪ 内置默认。</summary>
    public IEnumerable<string> Grades()
        => Materials.Select(m => m.Grade.Trim()).Concat(MaterialSpec.Defaults.Select(m => m.Grade))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public string UniqueName(string baseName)
    {
        var b = string.IsNullOrWhiteSpace(baseName) ? "规格" : baseName.Trim();
        if (Find(b) is null) return b;
        for (var i = 2; i < 1000; i++)
        {
            if (Find($"{b} {i}") is null) return $"{b} {i}";
        }

        return $"{b} {Guid.NewGuid():N}"[..Math.Min(24, b.Length + 33)];
    }

    public static SpecLibrary Load(JsonElement? root)
        => Load(root is { ValueKind: JsonValueKind.Object } r ? JsonNode.Parse(r.GetRawText()) as JsonObject : null);

    public static SpecLibrary Load(JsonObject? root)
    {
        var lib = new SpecLibrary();
        if (root is null) return lib;
        if (root["materials"] is JsonArray mats)
        {
            foreach (var n in mats)
            {
                if (MaterialSpec.FromNode(n) is { } m && lib.Materials.All(x => !string.Equals(x.Grade, m.Grade, StringComparison.OrdinalIgnoreCase)))
                    lib.Materials.Add(m);
            }
        }

        if (root["boltSpecs"] is JsonArray specs)
        {
            foreach (var n in specs)
            {
                if (BoltSpec.FromNode(n) is not { } s) continue;
                if (s.Name.Trim().Length == 0) s.Name = lib.UniqueName(s.DisplayName);
                if (lib.Find(s.Name) is null) lib.Specs.Add(s);
            }
        }

        return lib;
    }

    /// <summary>写回顶层数组；空库就把键删掉，别留空数组。</summary>
    public void WriteTo(JsonObject root)
    {
        if (Specs.Count > 0) root["boltSpecs"] = new JsonArray(Specs.Select(s => (JsonNode)s.ToJson()).ToArray());
        else root.Remove("boltSpecs");
        if (Materials.Count > 0) root["materials"] = new JsonArray(Materials.Select(m => (JsonNode)m.ToJson()).ToArray());
        else root.Remove("materials");
    }
}

internal static class Json
{
    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static decimal? Dec(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<decimal>(out var d)) return d;
        if (v.TryGetValue<double>(out var f)) return (decimal)f;
        if (v.TryGetValue<int>(out var i)) return i;
        return null;
    }
}
