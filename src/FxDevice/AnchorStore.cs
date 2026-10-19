using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FxDevice;

/// <summary>
/// 自动法（MeasMode=0）的置零锚点：包络粗定位时间 refEnvNs 和载波周期 refPeriodNs。
/// 设备存储区放不下这两个值，存在 data\anchors.json，按（设备、通道、参考声时）索引，参考声时变了就作废。
/// 没有锚点时 SDK 用下次连接的第一帧重新捕获，期间螺栓松紧超过半个周期会锁到相邻上升沿，声时差错一个周期（约 415～450 ns）。
/// </summary>
public sealed class AnchorStore
{
    public sealed record Anchor(decimal RefTime, decimal EnvNs, decimal PeriodNs, DateTime At);

    private readonly string _host;
    private readonly Dictionary<ushort, Anchor> _map = new();
    private readonly object _lock = new();

    public string Path { get; }

    private AnchorStore(string path, string host)
    {
        Path = path;
        _host = host;
    }

    public static string PathFor(string baseDir) => System.IO.Path.Combine(baseDir, "data", "anchors.json");

    /// <summary>读 data\anchors.json 里这台设备（按 IP）的锚点；文件不在或读坏了就是空的，不影响连接。</summary>
    public static AnchorStore Open(string baseDir, string endpoint)
    {
        var store = new AnchorStore(PathFor(baseDir), SiteConfig.Host(endpoint));
        try
        {
            if (File.Exists(store.Path)
                && JsonNode.Parse(File.ReadAllText(store.Path)) is JsonObject root
                && root[store._host] is JsonObject dev)
            {
                foreach (var kv in dev)
                {
                    if (!ushort.TryParse(kv.Key, out var ch) || kv.Value is not JsonObject a) continue;
                    var refTime = Dec(a["refTime"]);
                    var env = Dec(a["refEnvNs"]);
                    var period = Dec(a["refPeriodNs"]);
                    if (refTime is null || env is null || period is null || period <= 0) continue;
                    var at = DateTime.TryParse(a["at"]?.GetValue<string>(), out var t) ? t : DateTime.MinValue;
                    store._map[ch] = new Anchor(refTime.Value, env.Value, period.Value, at);
                }
            }
        }
        catch (Exception)
        {
            // 读不了就当没有：SDK 自己会重新捕获锚点
        }

        return store;
    }

    public int Count
    {
        get { lock (_lock) return _map.Count; }
    }

    /// <summary>这一路当前设备上的参考声时是 refTime 时才把锚点给出去；对不上说明基线已经换过，锚点作废。</summary>
    public Anchor? Get(ushort ch, decimal? refTime)
    {
        lock (_lock)
        {
            return _map.TryGetValue(ch, out var a) && refTime is { } r && Math.Abs(a.RefTime - r) < 1m ? a : null;
        }
    }

    /// <summary>置零成功后记锚点：先合并文件里别的设备的内容再写，写临时文件后改名。</summary>
    public void Save(ushort ch, Anchor anchor)
    {
        lock (_lock)
        {
            _map[ch] = anchor;
            var root = new JsonObject();
            try
            {
                if (File.Exists(Path) && JsonNode.Parse(File.ReadAllText(Path)) is JsonObject existing) root = existing;
            }
            catch (Exception)
            {
                // 坏文件重写
            }

            var dev = root[_host] as JsonObject ?? new JsonObject();
            dev[ch.ToString()] = new JsonObject
            {
                ["refTime"] = anchor.RefTime,
                ["refEnvNs"] = anchor.EnvNs,
                ["refPeriodNs"] = anchor.PeriodNs,
                ["at"] = anchor.At.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            root[_host] = dev;

            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
            File.Move(tmp, Path, overwrite: true);
        }
    }

    private static decimal? Dec(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<decimal>(out var d)) return d;
        if (v.TryGetValue<double>(out var f)) return (decimal)f;
        return null;
    }
}
