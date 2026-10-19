using System.Globalization;
using System.Threading.Channels;
using FxDevice;
using FxSDK.Models;
using Microsoft.Data.Sqlite;

namespace FxMonitor;

/// <summary>一帧要落盘的读数。</summary>
internal sealed class TraceSample
{
    public long Ts;
    public ushort Ch;
    public string Specimen = "";
    public string Group = "";
    public double? Temp;
    public double? Echo;
    public double? Delta;
    public double? Elong;
    public double? Kn;
    public double? RefNs;
    public double? RefTemp;
    public long RunId;
    public double? Std;
    public double? Snr;
    public string Status = "";
}

internal readonly record struct TraceSpan(long Frames, long LiveWaves, long RefWaves, long WaveBytes, int MaxPoints)
{
    public long Waves => LiveWaves + RefWaves;
    public double WaveMegabytes => WaveBytes / 1048576.0;
}

internal sealed class TraceRun
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public bool Open { get; init; }
    public override string ToString() => Name;
}

internal readonly record struct TraceExportResult(long Frames, string Channels, string Path);

/// <summary>
/// 连续记录按段分开。原有数据在「全部」里。可以「从今天开始」把今天 0 点起归成新的一段，
/// 或「单独记录」从现在起另开一段。导出只出下拉框里选中的那一段。
/// 测量帧保留 <see cref="RetainDays"/> 天。写库在后台线程。
/// </summary>
internal sealed class TraceStore : IDisposable
{
    internal const int DefaultRetainDays = 14;

    private const int BatchSize = 32;
    private const long PurgeEveryMs = 60 * 60 * 1000;

    private static readonly string[] EmptySheet = { "测量数据" };
    private const long RoundGapMs = 90_000;

    /// <summary>同一通道的各项挨在一起，再接下一个通道。顺序：温度、回波声时、声时差、伸长量、轴向力、参考声时、参考温度。</summary>
    private static readonly (string Title, double Width)[] WideMetrics =
    {
        ("温度", 14),
        ("回波声时(ns)", 18),
        ("声时差(ns)", 16),
        ("伸长量(μm)", 16),
        ("轴向力(kN)", 16),
        ("参考声时(ns)", 18),
        ("参考温度(℃)", 16),
        ("标准差(ns)", 14),
        ("信噪比(dB)", 14),
    };

    private readonly string _connString;
    private readonly Channel<TraceSample> _queue;
    private readonly Channel<long> _recounts;
    private readonly Task _writer;
    private long _runId;

    private long _frames;
    private long _dropped;
    private readonly object _runCountLock = new();
    private long _countedRun;
    private long _runFrames;
    private string _runName = "";
    private long _lastPurgeTick;
    private volatile string _lastError = "";
    private volatile bool _disposed;
    private int _disposeOnce;

    static TraceStore()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public TraceStore(string path, int retainDays = DefaultRetainDays)
    {
        Path = path;
        RetainDays = Math.Clamp(retainDays, 1, 3650);
        var dir = System.IO.Path.GetDirectoryName(path);
        if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        using (var conn = Open())
        {
            InitSchema(conn);
            _runId = EnsureRuns(conn);
            Interlocked.Exchange(ref _frames, Scalar(conn, "SELECT COUNT(*) FROM frames"));
            RememberRun(conn, _runId);
        }

        _queue = Channel.CreateBounded<TraceSample>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        _recounts = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _writer = Task.Run(WriterLoop);
    }

    public string Path { get; }

    public int RetainDays { get; }

    public long Frames => Interlocked.Read(ref _frames);

    public long Dropped => Interlocked.Read(ref _dropped);

    public string LastError => _lastError;

    public long CurrentRunId => Interlocked.Read(ref _runId);

    public static string PathFor(string baseDir) => System.IO.Path.Combine(baseDir, "data", "连续记录.db");

    public void Enqueue(TraceSample sample)
    {
        if (_disposed) return;
        if (!_queue.Writer.TryWrite(sample)) Interlocked.Increment(ref _dropped);
    }

    public DateTime? EarliestLocal()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 过期清理后可能只剩一条很老的参考波形。日期范围跟仍有实时波形（或只有读数）的帧走，不被它拉开。
        cmd.CommandText = "SELECT MIN(ts) FROM frames";
        var o = cmd.ExecuteScalar();
        if (o is null or DBNull) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(o, CultureInfo.InvariantCulture)).LocalDateTime;
    }

    public TraceSpan Count(DateTime fromInclusive, DateTime toExclusive, long runId)
    {
        var a = ToUnixMs(fromInclusive);
        var b = ToUnixMs(toExclusive);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = "SELECT COUNT(*), COUNT(DISTINCT ts) FROM frames WHERE ts >= $a AND ts < $b AND sid = $sid";
        AddRange(cmd, a, b);
        cmd.Parameters.AddWithValue("$sid", runId);
        using var r = cmd.ExecuteReader();
        r.Read();
        var frames = r.GetInt64(0);
        var times = r.GetInt64(1);
        return new TraceSpan(frames, times, 0, 0, 0);
    }

    /// <summary>当前段的帧数。写库时顺手累加，界面定时刷新不再每两秒对整表 COUNT。</summary>
    public (string Name, long Frames) CurrentStat()
    {
        lock (_runCountLock) return (_runName, _runFrames);
    }

    /// <summary>换段后让写库线程去数这一段。界面线程自己 COUNT 会和正在提交的一批抢，数被盖掉或重复加。</summary>
    private void NoteRun(long id, string name)
    {
        lock (_runCountLock)
        {
            _countedRun = id;
            _runName = name;
            _runFrames = 0;
        }

        _recounts.Writer.TryWrite(id);
    }

    private void RememberRun(SqliteConnection conn, long id, string? name = null)
    {
        var resolved = name;
        long frames;
        if (resolved is null)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name, (SELECT COUNT(*) FROM frames WHERE sid = $id) FROM runs WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                lock (_runCountLock)
                {
                    _countedRun = id;
                    _runName = "";
                    _runFrames = 0;
                }

                return;
            }

            resolved = reader.GetString(0);
            frames = reader.GetInt64(1);
        }
        else
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM frames WHERE sid = $id";
            cmd.Parameters.AddWithValue("$id", id);
            frames = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        lock (_runCountLock)
        {
            _countedRun = id;
            _runName = resolved;
            _runFrames = frames;
        }
    }

    /// <summary>只采纳队列里最后一次换段。写库线程上调用，和 WriteBatch 不会同时进行。</summary>
    private void DrainRecounts(SqliteConnection conn)
    {
        long id = 0;
        var any = false;
        while (_recounts.Reader.TryRead(out var next))
        {
            id = next;
            any = true;
        }

        if (!any) return;
        string name;
        long frames;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, (SELECT COUNT(*) FROM frames WHERE sid = $id) FROM runs WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return;
            name = reader.GetString(0);
            frames = reader.GetInt64(1);
        }

        lock (_runCountLock)
        {
            if (_countedRun != id) return;
            _runName = name;
            _runFrames = frames;
        }
    }

    /// <summary>过期清理删过行之后，按库里的实数把当前段计数拨正。清理进行中若已经换了段，就不覆盖新段的数。</summary>
    private void RecountCurrentRun(SqliteConnection conn)
    {
        long id;
        lock (_runCountLock) id = _countedRun;
        if (id == 0) return;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM frames WHERE sid = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var frames = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        lock (_runCountLock)
        {
            if (_countedRun == id) _runFrames = frames;
        }
    }

    public IReadOnlyList<TraceRun> ListRuns()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, open FROM runs ORDER BY id";
        using var reader = cmd.ExecuteReader();
        var list = new List<TraceRun>();
        while (reader.Read())
        {
            list.Add(new TraceRun { Id = reader.GetInt64(0), Name = reader.GetString(1), Open = reader.GetInt64(2) != 0 });
        }

        return list;
    }

    /// <summary>回到已有的一段继续记，不挪动已经记下的行。</summary>
    public TraceRun Resume(long id)
    {
        using var conn = Open();
        string name;
        using (var tx = conn.BeginTransaction())
        {
            using var find = conn.CreateCommand();
            find.Transaction = tx;
            find.CommandText = "SELECT name FROM runs WHERE id = $id";
            find.Parameters.AddWithValue("$id", id);
            var found = find.ExecuteScalar();
            if (found is null or DBNull) throw new InvalidOperationException("这一段已经不在了");
            name = Convert.ToString(found) ?? "";
            using var clear = conn.CreateCommand();
            clear.Transaction = tx;
            clear.CommandText = "UPDATE runs SET open = 0";
            clear.ExecuteNonQuery();
            using var open = conn.CreateCommand();
            open.Transaction = tx;
            open.CommandText = "UPDATE runs SET open = 1 WHERE id = $id";
            open.Parameters.AddWithValue("$id", id);
            open.ExecuteNonQuery();
            tx.Commit();
        }

        Interlocked.Exchange(ref _runId, id);
        NoteRun(id, name);
        return new TraceRun { Id = id, Name = name, Open = true };
    }

    /// <summary>从今天 0 点起另开一段，今天已经记下的行归到这一段。</summary>
    public TraceRun BeginToday()
    {
        var day = DateTime.Today;
        return Begin($"今天 {day:yyyy-MM-dd}", ToUnixMs(day), takeExisting: true, reuseName: true);
    }

    /// <summary>从现在起另开一段，以前的数据留在原来的段里。</summary>
    public TraceRun BeginSeparate(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("段名不能为空", nameof(name));
        return Begin(trimmed, DateTimeOffset.Now.ToUnixTimeMilliseconds(), takeExisting: false, reuseName: false);
    }

    public TraceExportResult ExportExcel(string path, DateTime fromInclusive, DateTime toExclusive, long runId, IProgress<string>? progress)
    {
        var a = ToUnixMs(fromInclusive);
        var b = ToUnixMs(toExclusive);
        List<WideRow> rows;
        List<int> channels;
        using (var conn = Open())
        {
            var samples = ReadSamples(conn, a, b, runId);
            channels = samples.Select(s => s.Ch).Distinct().OrderBy(ch => ch).ToList();
            rows = GroupRounds(samples);
        }

        if (rows.Count > 1_048_575)
            throw new InvalidOperationException("并成一行一个时刻之后仍超过 Excel 的 104 万行，请把日期范围缩短后再导出。");

        var headers = new List<string> { "采集时间" };
        var widths = new List<double> { 22 };
        foreach (var ch in channels)
        {
            foreach (var metric in WideMetrics)
            {
                headers.Add($"通道{ch.ToString(CultureInfo.InvariantCulture)}{metric.Title}");
                widths.Add(metric.Width);
            }

            headers.Add($"通道{ch.ToString(CultureInfo.InvariantCulture)}状态");
            widths.Add(16);
        }

        progress?.Report("正在写入测量数据…");
        XlsxBook.Save(path, EmptySheet, book =>
        {
            using var sheet = book.Open(EmptySheet[0], headers, widths);
            var written = 0;
            foreach (var row in rows)
            {
                sheet.BeginRow();
                sheet.Text(FormatTs(row.Ts));
                foreach (var ch in channels)
                {
                    row.Cells.TryGetValue(ch, out var pack);
                    for (var m = 0; m < WideMetrics.Length; m++)
                        sheet.Dec(pack.Values?[m]);
                    sheet.Text(string.IsNullOrEmpty(pack.Status) ? null : StatusText(pack.Status));
                }

                sheet.EndRow();
                written++;
                if (written % 2000 == 0) progress?.Report($"正在写入测量数据 {written:N0} 行…");
            }
        });

        return new TraceExportResult(rows.Count, EmptySheet[0], path);
    }

    /// <summary>一轮里每个通道只占一格。同一通道再来一帧，或离这一轮起点超过 90 秒，就另起一行。</summary>
    private static List<WideRow> GroupRounds(List<WideSample> samples)
    {
        var rows = new List<WideRow>();
        WideRow? cur = null;
        foreach (var s in samples)
        {
            if (cur == null || cur.Cells.ContainsKey(s.Ch) || s.Ts - cur.Ts > RoundGapMs)
            {
                cur = new WideRow { Ts = s.Ts };
                rows.Add(cur);
            }

            cur.Cells[s.Ch] = (s.Values, s.Status);
        }

        return rows;
    }

    private static List<WideSample> ReadSamples(SqliteConnection conn, long a, long b, long runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = """
            SELECT ts, ch, temp, echo, dt, elong, kn, ref_ns, ref_temp, std, snr, status
            FROM frames WHERE ts >= $a AND ts < $b AND sid = $sid
            ORDER BY ts, ch
            """;
        AddRange(cmd, a, b);
        cmd.Parameters.AddWithValue("$sid", runId);
        var list = new List<WideSample>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new WideSample(
                reader.GetInt64(0),
                (int)reader.GetInt64(1),
                new double?[]
                {
                    ReadReal(reader, 2),
                    ReadReal(reader, 3),
                    ReadReal(reader, 4),
                    ReadReal(reader, 5),
                    ReadReal(reader, 6),
                    ReadReal(reader, 7),
                    ReadReal(reader, 8),
                    ReadReal(reader, 9),
                    ReadReal(reader, 10),
                },
                reader.IsDBNull(11) ? "" : reader.GetString(11)));
        }

        return list;
    }

    private sealed class WideRow
    {
        public long Ts;
        public readonly Dictionary<int, (double?[]? Values, string Status)> Cells = new();
    }

    private readonly record struct WideSample(long Ts, int Ch, double?[] Values, string Status);

    /// <summary>库里的状态代码翻成和界面一样的词。老数据若已经是中文，原样带出。</summary>
    private static string StatusText(string code)
    {
        if (code == "win") return "回波窗不符";
        if (code == "stale") return "无数据";
        if (code.EndsWith("+weak", StringComparison.Ordinal))
            return Health.Text(code[..^5]) + " · 信号弱";
        if (code.StartsWith("fault:", StringComparison.Ordinal)
            && Enum.TryParse<MeasStatusCode>(code["fault:".Length..], out var status))
            return DeviceEngine.DescribeStatus(status);
        var text = Health.Text(code);
        return text == code ? code : text;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0) return;
        _disposed = true;
        _queue.Writer.TryComplete();
        _recounts.Writer.TryComplete();
        try
        {
            if (!_writer.Wait(TimeSpan.FromSeconds(8)))
                _lastError = "连续记录还没写完就退出了";
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }

        SqliteConnection.ClearAllPools();
    }

    private async Task WriterLoop()
    {
        using var conn = Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }

        using var insFrame = conn.CreateCommand();
        insFrame.CommandText = """
            INSERT INTO frames(ts, ch, specimen, grp, temp, echo, dt, elong, kn, std, snr, status, ref_ns, ref_temp, sid)
            VALUES ($ts, $ch, $spec, $grp, $temp, $echo, $dt, $elong, $kn, $std, $snr, $status, $refns, $reftemp, $sid)
            """;
        insFrame.Parameters.Add("$ts", SqliteType.Integer);
        insFrame.Parameters.Add("$ch", SqliteType.Integer);
        insFrame.Parameters.Add("$spec", SqliteType.Text);
        insFrame.Parameters.Add("$grp", SqliteType.Text);
        insFrame.Parameters.Add("$temp", SqliteType.Real);
        insFrame.Parameters.Add("$echo", SqliteType.Real);
        insFrame.Parameters.Add("$dt", SqliteType.Real);
        insFrame.Parameters.Add("$elong", SqliteType.Real);
        insFrame.Parameters.Add("$kn", SqliteType.Real);
        insFrame.Parameters.Add("$std", SqliteType.Real);
        insFrame.Parameters.Add("$snr", SqliteType.Real);
        insFrame.Parameters.Add("$status", SqliteType.Text);
        insFrame.Parameters.Add("$refns", SqliteType.Real);
        insFrame.Parameters.Add("$reftemp", SqliteType.Real);
        insFrame.Parameters.Add("$sid", SqliteType.Integer);

        try
        {
            Purge(conn, insFrame);
        }
        catch (Exception ex)
        {
            _lastError = "清理过期记录失败：" + ex.Message;
        }

        var batch = new List<TraceSample>(BatchSize);
        try
        {
            var samplesOpen = true;
            var sampleReady = _queue.Reader.WaitToReadAsync().AsTask();
            var recountReady = _recounts.Reader.WaitToReadAsync().AsTask();
            while (samplesOpen)
            {
                await Task.WhenAny(sampleReady, recountReady);
                if (recountReady.IsCompleted)
                {
                    if (await recountReady) DrainRecounts(conn);
                    recountReady = _recounts.Reader.WaitToReadAsync().AsTask();
                }

                if (!sampleReady.IsCompleted) continue;
                if (!await sampleReady)
                {
                    samplesOpen = false;
                    break;
                }

                while (batch.Count < BatchSize && _queue.Reader.TryRead(out var sample)) batch.Add(sample);
                if (batch.Count > 0)
                {
                    TryWrite(conn, insFrame, batch);
                    batch.Clear();
                }

                DrainRecounts(conn);
                if (Environment.TickCount64 - _lastPurgeTick >= PurgeEveryMs)
                {
                    try { Purge(conn, insFrame); }
                    catch (Exception ex) { _lastError = "清理过期记录失败：" + ex.Message; }
                }

                sampleReady = _queue.Reader.WaitToReadAsync().AsTask();
            }

            DrainRecounts(conn);
        }
        finally
        {
            try
            {
                using var ck = conn.CreateCommand();
                ck.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                ck.ExecuteNonQuery();
            }
            catch (Exception)
            {
                // 检查点失败只影响文件能不能马上被别的进程独占打开，数据仍在
            }
        }
    }

    private void TryWrite(SqliteConnection conn, SqliteCommand frame, List<TraceSample> batch)
    {
        try
        {
            WriteBatch(conn, frame, batch);
            _lastError = "";
        }
        catch (Exception ex)
        {
            _lastError = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        }
    }

    private void WriteBatch(SqliteConnection conn, SqliteCommand frame, List<TraceSample> batch)
    {
        if (batch.Count == 0) return;
        using var tx = conn.BeginTransaction();
        frame.Transaction = tx;
        foreach (var s in batch)
        {
            frame.Parameters["$ts"].Value = s.Ts;
            frame.Parameters["$ch"].Value = (int)s.Ch;
            frame.Parameters["$spec"].Value = s.Specimen ?? "";
            frame.Parameters["$grp"].Value = s.Group ?? "";
            BindReal(frame.Parameters["$temp"], s.Temp);
            BindReal(frame.Parameters["$echo"], s.Echo);
            BindReal(frame.Parameters["$dt"], s.Delta);
            BindReal(frame.Parameters["$elong"], s.Elong);
            BindReal(frame.Parameters["$kn"], s.Kn);
            BindReal(frame.Parameters["$refns"], s.RefNs);
            BindReal(frame.Parameters["$reftemp"], s.RefTemp);
            frame.Parameters["$sid"].Value = s.RunId;
            BindReal(frame.Parameters["$std"], s.Std);
            BindReal(frame.Parameters["$snr"], s.Snr);
            frame.Parameters["$status"].Value = s.Status ?? "";
            frame.ExecuteNonQuery();
        }

        tx.Commit();
        Interlocked.Add(ref _frames, batch.Count);
        lock (_runCountLock)
        {
            var add = 0;
            foreach (var s in batch)
            {
                if (s.RunId == _countedRun) add++;
            }

            _runFrames += add;
        }
    }

    private void Purge(SqliteConnection conn, SqliteCommand insFrame)
    {
        var cut = DateTimeOffset.Now.AddDays(-RetainDays).ToUnixTimeMilliseconds();
        var framesRemoved = 0;
        while (true)
        {
            int picked;
            using (var tx = conn.BeginTransaction())
            {
                Exec(conn, tx, "CREATE TEMP TABLE IF NOT EXISTS purge_ids (id INTEGER PRIMARY KEY);");
                Exec(conn, tx, "DELETE FROM purge_ids;");
                picked = Exec(conn, tx, """
                    INSERT INTO purge_ids(id)
                    SELECT f.id FROM frames f
                    WHERE f.ts < $cut AND EXISTS (
                        SELECT 1 FROM waves w WHERE w.frame_id = f.id AND w.kind = 0)
                    ORDER BY f.id LIMIT 2000
                    """, cut);
                if (picked > 0)
                    Exec(conn, tx, "DELETE FROM waves WHERE kind = 0 AND frame_id IN (SELECT id FROM purge_ids);");
                tx.Commit();
            }

            DrainQueue(conn, insFrame);
            if (picked == 0) break;
        }

        while (true)
        {
            int removed;
            using (var tx = conn.BeginTransaction())
            {
                removed = Exec(conn, tx, """
                    DELETE FROM frames WHERE id IN (
                        SELECT f.id FROM frames f
                        WHERE f.ts < $cut AND NOT EXISTS (SELECT 1 FROM waves w WHERE w.frame_id = f.id)
                        ORDER BY f.id LIMIT 2000)
                    """, cut);
                tx.Commit();
            }

            if (removed > 0)
            {
                framesRemoved += removed;
                var left = Interlocked.Add(ref _frames, -removed);
                if (left < 0) Interlocked.Exchange(ref _frames, Scalar(conn, "SELECT COUNT(*) FROM frames"));
            }

            DrainQueue(conn, insFrame);
            if (removed == 0) break;
        }

        if (framesRemoved > 0) RecountCurrentRun(conn);
        _lastPurgeTick = Environment.TickCount64;
    }

    private void DrainQueue(SqliteConnection conn, SqliteCommand insFrame)
    {
        var batch = new List<TraceSample>(BatchSize);
        while (_queue.Reader.TryRead(out var sample))
        {
            batch.Add(sample);
            if (batch.Count >= BatchSize)
            {
                TryWrite(conn, insFrame, batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0) TryWrite(conn, insFrame, batch);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private static void InitSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS frames (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              ts INTEGER NOT NULL,
              ch INTEGER NOT NULL,
              specimen TEXT NOT NULL,
              grp TEXT NOT NULL,
              temp REAL,
              echo REAL,
              dt REAL,
              elong REAL,
              kn REAL,
              std REAL,
              snr REAL,
              status TEXT NOT NULL,
              ref_ns REAL,
              ref_temp REAL,
              sid INTEGER
            );
            CREATE INDEX IF NOT EXISTS ix_frames_ts ON frames(ts, ch);
            CREATE TABLE IF NOT EXISTS waves (
              frame_id INTEGER NOT NULL,
              kind INTEGER NOT NULL,
              start_ns INTEGER NOT NULL,
              clock_ns INTEGER NOT NULL,
              data BLOB NOT NULL,
              PRIMARY KEY (frame_id, kind)
            );
            """;
        cmd.ExecuteNonQuery();
        AddColumnIfMissing(conn, "ref_ns", "ALTER TABLE frames ADD COLUMN ref_ns REAL");
        AddColumnIfMissing(conn, "ref_temp", "ALTER TABLE frames ADD COLUMN ref_temp REAL");
        AddColumnIfMissing(conn, "sid", "ALTER TABLE frames ADD COLUMN sid INTEGER");
        using var idx = conn.CreateCommand();
        idx.CommandText = "CREATE INDEX IF NOT EXISTS ix_frames_sid ON frames(sid, ts, ch);";
        idx.ExecuteNonQuery();
    }

    private static long EnsureRuns(SqliteConnection conn)
    {
        using (var make = conn.CreateCommand())
        {
            make.CommandText = """
                CREATE TABLE IF NOT EXISTS runs (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  name TEXT NOT NULL,
                  started INTEGER NOT NULL,
                  open INTEGER NOT NULL DEFAULT 0
                );
                """;
            make.ExecuteNonQuery();
        }

        long openId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM runs WHERE open = 1 ORDER BY id DESC LIMIT 1";
            var o = cmd.ExecuteScalar();
            openId = o is null or DBNull ? 0 : Convert.ToInt64(o, CultureInfo.InvariantCulture);
        }

        if (openId == 0)
        {
            long started;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT MIN(ts) FROM frames";
                var o = cmd.ExecuteScalar();
                started = o is null or DBNull
                    ? DateTimeOffset.Now.ToUnixTimeMilliseconds()
                    : Convert.ToInt64(o, CultureInfo.InvariantCulture);
            }

            using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT INTO runs(name, started, open) VALUES ('全部', $s, 1)";
            insert.Parameters.AddWithValue("$s", started);
            insert.ExecuteNonQuery();
            openId = LastInsertId(conn);
        }

        using (var fill = conn.CreateCommand())
        {
            fill.CommandText = "UPDATE frames SET sid = $id WHERE sid IS NULL";
            fill.Parameters.AddWithValue("$id", openId);
            fill.ExecuteNonQuery();
        }

        return openId;
    }

    private TraceRun Begin(string name, long started, bool takeExisting, bool reuseName)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        long id;
        if (reuseName)
        {
            using var find = conn.CreateCommand();
            find.Transaction = tx;
            find.CommandText = "SELECT id FROM runs WHERE name = $name ORDER BY id DESC LIMIT 1";
            find.Parameters.AddWithValue("$name", name);
            var found = find.ExecuteScalar();
            id = found is null or DBNull ? 0 : Convert.ToInt64(found, CultureInfo.InvariantCulture);
        }
        else
        {
            id = 0;
        }

        using (var clear = conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "UPDATE runs SET open = 0";
            clear.ExecuteNonQuery();
        }

        if (id == 0)
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO runs(name, started, open) VALUES ($name, $s, 1)";
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$s", started);
            insert.ExecuteNonQuery();
            id = LastInsertId(conn, tx);
        }
        else
        {
            using var open = conn.CreateCommand();
            open.Transaction = tx;
            open.CommandText = "UPDATE runs SET open = 1, started = $s WHERE id = $id";
            open.Parameters.AddWithValue("$s", started);
            open.Parameters.AddWithValue("$id", id);
            open.ExecuteNonQuery();
        }

        if (takeExisting)
        {
            using var move = conn.CreateCommand();
            move.Transaction = tx;
            move.CommandText = "UPDATE frames SET sid = $id WHERE ts >= $s";
            move.Parameters.AddWithValue("$id", id);
            move.Parameters.AddWithValue("$s", started);
            move.ExecuteNonQuery();
        }

        tx.Commit();
        tx.Dispose();
        Interlocked.Exchange(ref _runId, id);
        NoteRun(id, name);
        return new TraceRun { Id = id, Name = name, Open = true };
    }

    private static long LastInsertId(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string column, string sql)
    {
        using var info = conn.CreateCommand();
        info.CommandText = "PRAGMA table_info(frames)";
        using var reader = info.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = sql;
        alter.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var o = cmd.ExecuteScalar();
        return o is null or DBNull ? 0 : Convert.ToInt64(o, CultureInfo.InvariantCulture);
    }

    private static void AddRange(SqliteCommand cmd, long a, long b)
    {
        cmd.Parameters.AddWithValue("$a", a);
        cmd.Parameters.AddWithValue("$b", b);
    }

    private static void BindReal(SqliteParameter p, double? v) => p.Value = v is { } d && double.IsFinite(d) ? d : DBNull.Value;

    private static int Exec(SqliteConnection conn, SqliteTransaction tx, string sql, long? cut = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 120;
        cmd.CommandText = sql;
        if (cut is { } c) cmd.Parameters.AddWithValue("$cut", c);
        return cmd.ExecuteNonQuery();
    }

    private static double? ReadReal(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetDouble(i);

    private static long ToUnixMs(DateTime local)
    {
        var t = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(t, TimeZoneInfo.Local.GetUtcOffset(t)).ToUnixTimeMilliseconds();
    }

    private static string FormatTs(long unixMs)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
