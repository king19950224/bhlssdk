using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FxDevice;

internal readonly record struct OwnerShot(
    ushort Channel,
    string Health,
    decimal? EstKn,
    decimal? DeltaKn,
    long AtMs);

/// <summary>
/// 业主说明的素材。数字和状态都是帧和历史里已经算好的，这里汇总、换词，再按固定句式写成一段话。
/// </summary>
internal static class OwnerBrief
{
    internal const long StaleMs = 60_000;
    internal const int AttentionLimit = 8;

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal readonly record struct Row(
        int Turbine,
        ushort Channel,
        string Health,
        decimal? EstKn,
        decimal? DeltaKn,
        decimal Rated,
        string Place);

    internal static string Build(Site site, HistoryStore history, string range)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rows = new List<Row>();
        var anyCal = false;
        var anyRated = false;
        foreach (var hub in site.Hubs)
        {
            anyCal |= HubCalibrated(hub);
            anyRated |= HubRated(hub);
            foreach (var (shot, fresh) in hub.CopyOwner(nowMs))
            {
                var health = fresh ? shot.Health : "offline";
                if (health == "pending") continue;
                rows.Add(new Row(
                    hub.Turbine,
                    shot.Channel,
                    health,
                    fresh ? shot.EstKn : null,
                    fresh ? shot.DeltaKn : null,
                    hub.Plan.RatedFor(shot.Channel),
                    PlaceOf(hub, shot.Channel)));
            }
        }

        var (channels, events) = history.QueryNarrative(range);
        var period = new Dictionary<(int, int), (double? EstMin, double? EstMax, double? EstAvg, double? DeltaMin, double? DeltaMax, double? DeltaAvg)>();
        foreach (var ch in channels)
        {
            period[(ch.Turbine, ch.Channel)] = (ch.EstMin, ch.EstMax, ch.EstAvg, ch.DeltaMin, ch.DeltaMax, ch.DeltaAvg);
        }

        return Compose(site.Farm.FarmName, site.Mode.Mode == BaselineMode.Anchor, range, anyCal, anyRated, rows, period, events);
    }

    internal static string PlaceOf(MonitorHub hub, ushort channel)
    {
        var index = hub.Plan.FlangeOf(channel);
        if (index < 0) return Place(hub.Turbine, null, null, assigned: false);
        var flange = hub.Plan.Flanges[index];
        int? bolt = flange.Bolts.TryGetValue(channel, out var n) ? n : null;
        return Place(hub.Turbine, flange.Name, bolt, assigned: true);
    }

    /// <summary>法兰名里有禁用词就不写法兰名；不写通道号。</summary>
    internal static string Place(int turbine, string? flangeName, int? bolt, bool assigned)
    {
        var head = string.Create(CultureInfo.InvariantCulture, $"F{turbine:00}");
        var name = string.IsNullOrWhiteSpace(flangeName) ? null : flangeName.Trim();
        if (name is not null && Narrative.HasBanned(name)) name = null;
        if (!assigned && name is null) return head + " · 未分配";
        if (bolt is int b)
            return name is null ? $"{head} · 螺栓 {b}" : $"{head} · {name} · 螺栓 {b}";
        return name is null ? head : $"{head} · {name}";
    }

    internal static string Compose(
        string? farmName,
        bool anchor,
        string range,
        bool anyCalibrated,
        bool anyRated,
        IReadOnlyList<Row> rows,
        IReadOnlyDictionary<(int Turbine, int Channel), (double? EstMin, double? EstMax, double? EstAvg, double? DeltaMin, double? DeltaMax, double? DeltaAvg)> period,
        HistoryStore.NarrativeEvents events)
    {
        var bolts = 0;
        var ok = 0;
        var low = 0;
        var loose = 0;
        var review = 0;
        var unjudged = 0;
        var offline = 0;
        var named = new List<Row>();
        foreach (var row in rows)
        {
            if (row.Health == "pending") continue;
            bolts++;
            switch (Bucket(row.Health))
            {
                case "ok": ok++; break;
                case "low": low++; break;
                case "loose": loose++; break;
                case "unjudged": unjudged++; break;
                case "offline": offline++; break;
                default: review++; break;
            }

            if (Rank(row.Health) >= 0) named.Add(row);
        }

        named.Sort(static (a, b) =>
        {
            var byRank = Rank(a.Health).CompareTo(Rank(b.Health));
            if (byRank != 0) return byRank;
            var byTurbine = a.Turbine.CompareTo(b.Turbine);
            return byTurbine != 0 ? byTurbine : a.Channel.CompareTo(b.Channel);
        });

        var judged = ok + low + loose;
        int? healthPct = judged > 0 && anyCalibrated && anyRated
            ? (int)Math.Round(100m * ok / judged, MidpointRounding.AwayFromZero)
            : null;

        var take = Math.Min(AttentionLimit, named.Count);
        var attention = new List<ItemJson>(take);
        for (var i = 0; i < take; i++)
        {
            attention.Add(ToItem(named[i], anchor, period));
        }

        var brief = new BriefJson
        {
            Farm = string.IsNullOrWhiteSpace(farmName) ? "风场" : farmName.Trim(),
            RangeLabel = RangeLabel(range),
            ForceWord = anchor ? "估算预紧力" : "预紧力",
            Now = new NowJson
            {
                Bolts = bolts,
                Ok = ok,
                Low = low,
                Loose = loose,
                Review = review,
                Unjudged = unjudged,
                Offline = offline,
                HealthPct = healthPct,
            },
            Attention = attention,
            AttentionMore = Math.Max(0, named.Count - AttentionLimit),
            Period = new EventsJson
            {
                BecameLoose = events.BecameLoose,
                Recovered = events.Recovered,
                ReadingReset = events.ReadingReset,
                NeedsReview = events.NeedsReview,
            },
        };

        return JsonSerializer.Serialize(brief, JsonOpts);
    }

    internal static string Speak(Site site, HistoryStore history, string range) =>
        Narrative.Speak(Build(site, history, range));

    internal static void SelfCheck()
    {
        var period = new Dictionary<(int, int), (double?, double?, double?, double?, double?, double?)>
        {
            [(5, 9)] = (1088.02, 1210.2, 1140.55, -20, -1, -8),
        };
        var json = Compose(
            "百恒风电场",
            anchor: true,
            "24h",
            anyCalibrated: true,
            anyRated: true,
            new[] { new Row(5, 9, "loose", 1102.44m, -12.2m, 1780m, Place(5, "中段法兰", 24, true)) },
            period,
            new HistoryStore.NarrativeEvents(1, 0, 0, 1));

        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            if (root.GetProperty("forceWord").GetString() != "估算预紧力") throw new InvalidOperationException("forceWord");
            if (root.GetProperty("now").GetProperty("healthPct").GetInt32() != 0) throw new InvalidOperationException("healthPct");
            var item = root.GetProperty("attention")[0];
            if (item.GetProperty("place").GetString() != "F05 · 中段法兰 · 螺栓 24") throw new InvalidOperationException("place");
            if (item.TryGetProperty("changeKn", out _)) throw new InvalidOperationException("不该有 changeKn");
            if (item.GetProperty("status").GetString() != "松动") throw new InvalidOperationException("status");
            if (item.GetProperty("ratioPct").GetInt32() != 62) throw new InvalidOperationException("ratio");
        }

        var spoken = Narrative.Speak(json);
        const string expect =
            "近24小时监测 1 颗，正常 0 颗，偏低 0 颗，松动 1 颗，需要现场复核 0 颗，健康率 0%。"
            + "F05 · 中段法兰 · 螺栓 24 松动，估算预紧力 1102.44 kN，额定 1780 kN，占 62%，最低 1088.02 kN，最高 1210.2 kN。"
            + "近24小时新出现松动 1 次，恢复正常 0 次，读数基准更新 0 次，需要复核 1 次。";
        if (spoken != expect) throw new InvalidOperationException("说明：" + spoken);
        if (Narrative.HasBanned(spoken)) throw new InvalidOperationException("说明里有禁用词");
        if (!Narrative.HasBanned("通道 1") || !Narrative.HasBanned("CH9 松动"))
            throw new InvalidOperationException("禁用词没拦住");

        var change = Compose(
            "风场",
            anchor: true,
            "7d",
            anyCalibrated: false,
            anyRated: false,
            new[] { new Row(2, 1, "unrated", null, -3.25m, 0m, Place(2, "通道法兰", null, true)) },
            new Dictionary<(int, int), (double?, double?, double?, double?, double?, double?)>(),
            default);
        using (var doc = JsonDocument.Parse(change))
        {
            var item = doc.RootElement.GetProperty("attention")[0];
            if (item.GetProperty("place").GetString() != "F02") throw new InvalidOperationException("法兰名里的禁用词应去掉");
            if (item.TryGetProperty("forceKn", out _)) throw new InvalidOperationException("没有额定就不写力值");
            if (item.GetProperty("changeKn").GetDecimal() != -3.25m) throw new InvalidOperationException("changeKn");
            if (doc.RootElement.GetProperty("now").TryGetProperty("healthPct", out _))
                throw new InvalidOperationException("没标定就不写健康率");
        }

        var said = Narrative.Speak(change);
        const string expectChange =
            "7天监测 1 颗，正常 0 颗，偏低 0 颗，松动 0 颗，需要现场复核 0 颗，还不能判断松紧 1 颗。"
            + "F02 还不能判断松紧，较安装时变化 -3.25 kN。"
            + "7天新出现松动 0 次，恢复正常 0 次，读数基准更新 0 次，需要复核 0 次。";
        if (said != expectChange) throw new InvalidOperationException("说明：" + said);
        if (said.Contains("预紧力", StringComparison.Ordinal) || said.Contains("健康率", StringComparison.Ordinal))
            throw new InvalidOperationException("变化量被说成了预紧力或健康率");

        var many = new Row[9];
        for (var i = 0; i < many.Length; i++)
            many[i] = new Row(1, (ushort)(i + 1), "loose", 100m, 0m, 200m, Place(1, "底段", i + 1, true));
        var packed = Compose(
            "风场",
            anchor: false,
            "30d",
            anyCalibrated: true,
            anyRated: true,
            many,
            new Dictionary<(int, int), (double?, double?, double?, double?, double?, double?)>(),
            default);
        using (var doc = JsonDocument.Parse(packed))
        {
            if (doc.RootElement.GetProperty("attention").GetArrayLength() != 8)
                throw new InvalidOperationException("attention");
            if (doc.RootElement.GetProperty("attentionMore").GetInt32() != 1)
                throw new InvalidOperationException("attentionMore");
            if (doc.RootElement.GetProperty("forceWord").GetString() != "预紧力")
                throw new InvalidOperationException("free forceWord");
            if (doc.RootElement.GetProperty("now").GetProperty("loose").GetInt32() != 9)
                throw new InvalidOperationException("loose");
        }

        var listed = Narrative.Speak(packed);
        if (!listed.Contains("另有 1 颗没有逐一点名。", StringComparison.Ordinal))
            throw new InvalidOperationException("未点名颗数");
        if (!listed.Contains("预紧力 100 kN，额定 200 kN，占 50%", StringComparison.Ordinal))
            throw new InvalidOperationException("自由状态力值");
        if (listed.Contains("估算预紧力", StringComparison.Ordinal))
            throw new InvalidOperationException("自由状态不该写估算预紧力");

        var reviewJson = Compose(
            "风场",
            anchor: false,
            "24h",
            anyCalibrated: true,
            anyRated: true,
            new[] { new Row(3, 2, "fault", 900m, 0m, 1000m, Place(3, "底段", 2, true)) },
            new Dictionary<(int, int), (double?, double?, double?, double?, double?, double?)>(),
            default);
        var reviewText = Narrative.Speak(reviewJson);
        if (!reviewText.EndsWith("需要现场复核。", StringComparison.Ordinal))
            throw new InvalidOperationException("收束：" + reviewText);
        if (reviewText.Contains("原因", StringComparison.Ordinal) || reviewText.Contains("处理", StringComparison.Ordinal))
            throw new InvalidOperationException("收束写了原因或办法");

        var dir = Path.Combine(Path.GetTempPath(), "bp-narr-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new HistoryStore(Path.Combine(dir, "history.db"), 1);
            var (chs, ev) = db.QueryNarrative("7d");
            if (chs.Count != 0 || ev.BecameLoose != 0 || ev.NeedsReview != 0)
                throw new InvalidOperationException("空历史");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 库文件可能还被占用 */ }
        }
    }

    private static ItemJson ToItem(
        Row row,
        bool anchor,
        IReadOnlyDictionary<(int Turbine, int Channel), (double? EstMin, double? EstMax, double? EstAvg, double? DeltaMin, double? DeltaMax, double? DeltaAvg)> period)
    {
        var useChange = anchor && row.Rated <= 0;
        decimal? force = null;
        decimal? change = null;
        decimal? rated = null;
        int? ratio = null;
        if (useChange)
        {
            change = Round2(row.DeltaKn);
        }
        else if (row.EstKn is { } est)
        {
            force = Round2(est);
            if (row.Rated > 0)
            {
                rated = Round2(row.Rated);
                ratio = (int)Math.Round(100m * est / row.Rated, MidpointRounding.AwayFromZero);
            }
        }

        PeriodJson? span = null;
        if (period.TryGetValue((row.Turbine, row.Channel), out var p))
        {
            var low = Round2(useChange ? p.DeltaMin : p.EstMin);
            var high = Round2(useChange ? p.DeltaMax : p.EstMax);
            var avg = Round2(useChange ? p.DeltaAvg : p.EstAvg);
            if (low is not null || high is not null || avg is not null)
                span = new PeriodJson { LowKn = low, HighKn = high, AvgKn = avg };
        }

        return new ItemJson
        {
            Place = row.Place,
            Status = StatusWord(row.Health),
            ForceKn = force,
            ChangeKn = change,
            RatedKn = rated,
            RatioPct = ratio,
            Period = span,
        };
    }

    private static bool HubCalibrated(MonitorHub hub)
    {
        var on = hub.Plan.Flanges.Where(static f => f.OnDevice).ToList();
        if (on.Count == 0) return hub.Plan.GlobalCal is not null;
        return on.Any(static f => f.Cal is not null);
    }

    private static bool HubRated(MonitorHub hub)
    {
        var on = hub.Plan.Flanges.Where(static f => f.OnDevice).ToList();
        if (on.Count == 0) return hub.Plan.GlobalRated > 0;
        return on.Any(static f => f.Rated > 0);
    }

    private static string RangeLabel(string range) => range switch
    {
        "7d" => "7天",
        "30d" => "30天",
        _ => "近24小时",
    };

    private static string Bucket(string health) => health switch
    {
        "ok" => "ok",
        "warn" => "low",
        "loose" => "loose",
        "uncal" or "unrated" => "unjudged",
        "offline" => "offline",
        _ => "review",
    };

    private static int Rank(string health) => health switch
    {
        "loose" => 0,
        "warn" => 1,
        "fault" or "noprobe" or "shift" or "badbase" => 2,
        "uncal" or "unrated" => 3,
        "ok" or "offline" or "pending" => -1,
        _ => 2,
    };

    private static string StatusWord(string health) => health switch
    {
        "warn" => "偏低",
        "loose" => "松动",
        "uncal" or "unrated" => "还不能判断松紧",
        "offline" => "暂时没有新数据",
        _ => "需要现场复核",
    };

    private static decimal? Round2(decimal? value) =>
        value is { } v ? Math.Round(v, 2, MidpointRounding.AwayFromZero) : null;

    private static decimal? Round2(double? value) =>
        value is { } v ? Math.Round((decimal)v, 2, MidpointRounding.AwayFromZero) : null;

    private sealed class BriefJson
    {
        public string Farm { get; init; } = "";
        public string RangeLabel { get; init; } = "";
        public string ForceWord { get; init; } = "";
        public NowJson Now { get; init; } = new();
        public List<ItemJson> Attention { get; init; } = new();
        public int AttentionMore { get; init; }
        public EventsJson Period { get; init; } = new();
    }

    private sealed class NowJson
    {
        public int Bolts { get; init; }
        public int Ok { get; init; }
        public int Low { get; init; }
        public int Loose { get; init; }
        public int Review { get; init; }
        public int Unjudged { get; init; }
        public int Offline { get; init; }
        public int? HealthPct { get; init; }
    }

    private sealed class ItemJson
    {
        public string Place { get; init; } = "";
        public string Status { get; init; } = "";
        public decimal? ForceKn { get; init; }
        public decimal? ChangeKn { get; init; }
        public decimal? RatedKn { get; init; }
        public int? RatioPct { get; init; }
        public PeriodJson? Period { get; init; }
    }

    private sealed class PeriodJson
    {
        public decimal? LowKn { get; init; }
        public decimal? HighKn { get; init; }
        public decimal? AvgKn { get; init; }
    }

    private sealed class EventsJson
    {
        public long BecameLoose { get; init; }
        public long Recovered { get; init; }
        public long ReadingReset { get; init; }
        public long NeedsReview { get; init; }
    }
}
