using System.Globalization;
using System.Text;

namespace FxMonitor;

/// <summary>「测回」记下的一行：厂家"手动测量结果"表的一条。</summary>
internal sealed class ManualRecord
{
    public DateTime Time { get; set; }
    public ushort Ch { get; set; }
    public string Specimen { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public decimal? TempC { get; set; }
    public decimal? EchoNs { get; set; }
    public decimal? DeltaNs { get; set; }
    public decimal? ElongUm { get; set; }
    public decimal? AxialKn { get; set; }
    public decimal? StdNs { get; set; }
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// 手动测量结果（厂家「测回」那张表）的存盘：exe 旁 data\手动测量结果.csv，UTF-8 带 BOM，Excel 直接打开。
/// 每记一条追加一行；删行/改备注整文件重写。文件坏了一行跳过一行，不影响别的。
/// </summary>
internal sealed class ManualRecords
{
    private static readonly string[] Header =
        { "时间", "通道", "试件", "分组", "温度℃", "回波声时ns", "声时差ns", "伸长量μm", "U1轴向力kN", "标准差ns", "备注" };

    private static readonly Encoding Utf8Bom = new UTF8Encoding(true);
    private static readonly System.Buffers.SearchValues<char> NeedsQuote = System.Buffers.SearchValues.Create(",\"\n\r");
    private readonly object _lock = new();

    public string Path { get; }

    public List<ManualRecord> Items { get; } = new();

    /// <summary>读不懂的行，原样留着。改备注或删行重写文件时还写回去，不让 Excel 另存坏的那几行消失。</summary>
    public int KeptLines => _kept.Count;

    /// <summary>整个文件打不开。这时禁止整文件重写，避免把原文件盖成空表。</summary>
    public bool FileUnreadable { get; private set; }

    private readonly List<string> _kept = new();

    private ManualRecords(string path)
    {
        Path = path;
    }

    public static string PathFor(string baseDir) => System.IO.Path.Combine(baseDir, "data", "手动测量结果.csv");

    public static ManualRecords Load(string path)
    {
        var store = new ManualRecords(path);
        try
        {
            if (!File.Exists(path)) return store;
            var first = true;
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (first)
                {
                    first = false;
                    if (line.TrimStart('\uFEFF').StartsWith("时间", StringComparison.Ordinal)) continue;
                }

                if (line.Trim().Length == 0) continue;
                if (Parse(line) is { } r) store.Items.Add(r);
                else store._kept.Add(line);
            }
        }
        catch (Exception)
        {
            // 整个文件打不开就当空表接着记，但禁止整文件重写，免得把原文件盖掉
            store.FileUnreadable = true;
            store.Items.Clear();
            store._kept.Clear();
        }

        return store;
    }

    public void Append(ManualRecord r)
    {
        lock (_lock)
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
            var fresh = !File.Exists(Path) || new FileInfo(Path).Length == 0;
            using var w = new StreamWriter(Path, append: true, Utf8Bom);
            if (fresh) w.WriteLine(string.Join(",", Header));
            w.WriteLine(Line(r));
            Items.Add(r);
        }
    }

    /// <summary>整文件重写（删行、改备注后）。先写临时文件再改名，中途断电不丢原文件。</summary>
    public void Save()
    {
        if (FileUnreadable)
            throw new IOException("原来的手动测量结果读不了，没有覆盖文件");
        lock (_lock)
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, Render(Items, _kept), Utf8Bom);
            File.Move(tmp, Path, overwrite: true);
        }
    }

    public static void Export(IEnumerable<ManualRecord> rows, string path) => File.WriteAllText(path, Render(rows, null), Utf8Bom);

    private static string Render(IEnumerable<ManualRecord> rows, IReadOnlyList<string>? kept)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Header));
        foreach (var r in rows) sb.AppendLine(Line(r));
        if (kept != null)
        {
            foreach (var line in kept) sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static string Line(ManualRecord r) => string.Join(",", new[]
    {
        r.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        r.Ch.ToString(CultureInfo.InvariantCulture),
        Quote(r.Specimen),
        Quote(r.Group),
        Num(r.TempC, "0.00"),
        Num(r.EchoNs, "0.00"),
        Num(r.DeltaNs, "0.00"),
        Num(r.ElongUm, "0.00"),
        Num(r.AxialKn, "0.00"),
        Num(r.StdNs, "0.00"),
        Quote(r.Note),
    });

    private static string Num(decimal? v, string fmt) => v is { } n ? n.ToString(fmt, CultureInfo.InvariantCulture) : string.Empty;

    private static string Quote(string s)
    {
        if (s.AsSpan().IndexOfAny(NeedsQuote) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static ManualRecord? Parse(string line)
    {
        var f = Split(line);
        if (f.Count < 11) return null;
        if (!DateTime.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return null;
        if (!ushort.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ch)) return null;
        return new ManualRecord
        {
            Time = t, Ch = ch, Specimen = f[2], Group = f[3],
            TempC = Dec(f[4]), EchoNs = Dec(f[5]), DeltaNs = Dec(f[6]), ElongUm = Dec(f[7]), AxialKn = Dec(f[8]), StdNs = Dec(f[9]),
            Note = f[10],
        };
    }

    private static decimal? Dec(string s) => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>最简 CSV 拆分：支持双引号包裹和 "" 转义。</summary>
    private static List<string> Split(string line)
    {
        var out_ = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { out_.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }

        out_.Add(sb.ToString());
        return out_;
    }
}
