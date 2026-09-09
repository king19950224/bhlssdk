using System.Globalization;
using System.Text;
using System.Threading.Channels;
using FxDevice;
using FxSDK.Models;
using Microsoft.Data.Sqlite;

/// <summary>
/// 本地历史库：测点每 5 秒落一条，状态变化记事件。关软件还在，库在 exe 旁 data/history.db。
/// </summary>
/// <remarks>
/// 线程模型：SDK 测量回调只把一帧的几个数字丢进队列就返回，落盘由一条后台写线程独占一个连接完成；
/// 页面查询各自开连接（SQLite WAL 下读写互不阻塞）。之前读写共用一把锁，一次 30 天导出或
/// 一次 COUNT(*) 就把 SDK 回调顶住几秒，帧停够 90 秒看门狗还会误判断线重连。
///
/// 数据口径：没有标定文件就不算力值——kn 为空、状态记 uncal。声时差 dt 原样保存，
/// 标定系数每次启动记入 cal_log，任何一条历史的力值都能追溯到当时用的是哪份标定、也都能重算。
/// </remarks>
internal sealed class HistoryStore : IDisposable
{
    private const int SampleMs = 5000;
    private const int EventDebounceMs = 10000;

    /// <summary>采样保留 90 天（每 5 秒一条，体量大）；事件是报警记录，保留 2 年。</summary>
    private const int RetainSampleDays = 90;
    private const int RetainEventDays = 730;
    private const int QueueCapacity = 4096;
    private static readonly TimeSpan PurgeEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan ErrorLogEvery = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 声时差的物理上限（ns）。预紧力从零拧到额定只有几十上百 ns，温度全程漂移 ±200 ns；
    /// 超过 2 µs 只可能是探头贴到了别的螺栓、重新耦合过或基线错了（旧平台 echo_shift 同一门限）。
    /// </summary>
    public const decimal MaxPlausibleDeltaTofNs = 2000m;

    public static bool IsBaselineMismatch(decimal? deltaTof) => deltaTof is { } d && Math.Abs(d) > MaxPlausibleDeltaTofNs;

    /// <summary>一帧里落盘要用到的东西。在回调线程上抓一份就走，别让回调等磁盘。</summary>
    private readonly record struct Frame(long At, int Ch, bool Normal, decimal? DeltaTof, decimal? EchoTime, decimal? Temp);

    private readonly string _connString;

    /// <summary>只有写线程碰它。</summary>
    private readonly SqliteConnection _write;

    private readonly Channel<Frame> _queue;
    private readonly Task _writer;
    private readonly CancellationTokenSource _stop = new();

    // 下面这些只在写线程上读写，不需要锁
    private readonly Dictionary<int, long> _lastSampleAt = new();
    private readonly Dictionary<int, string> _lastHealth = new();
    private readonly Dictionary<int, long> _lastEventAt = new();
    private long _lastPurgeAt;
    private long _lastErrorLogAt;

    // 给 Stats 看的计数。启动数一次、之后随写随加、每小时清理后重数一次；
    // 不再每次页面刷新都对千万行做 COUNT(*)
    private long _sampleCount;
    private long _eventCount;
    private long _dropped;
    private long _writeErrors;
    private volatile string _lastError = string.Empty;

    // 标定、口径与阈值：启动时设一次，之后只读。空 = 还没设，一律按未标定
    private volatile CalPlan? _plan;
    private volatile bool _disposed;

    public HistoryStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        DbPath = dbPath;
        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        _write = new SqliteConnection(_connString);
        _write.Open();
        using (var pragma = _write.CreateCommand())
        {
            // FULL：每次提交都落到盘上。工控机断电常见，NORMAL 会丢最后几秒；一秒一条写入，FULL 的代价可以忽略
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
            pragma.ExecuteNonQuery();
        }

        using (var cmd = _write.CreateCommand())
        {
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS samples (
                  ts INTEGER NOT NULL,
                  ch INTEGER NOT NULL,
                  kn REAL,
                  temp REAL,
                  dt REAL,
                  echo REAL,
                  normal INTEGER NOT NULL,
                  health TEXT NOT NULL,
                  PRIMARY KEY (ts, ch)
                );
                CREATE INDEX IF NOT EXISTS ix_samples_ch_ts ON samples(ch, ts);
                CREATE TABLE IF NOT EXISTS events (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  ts INTEGER NOT NULL,
                  ch INTEGER NOT NULL,
                  from_h TEXT NOT NULL,
                  to_h TEXT NOT NULL,
                  kn REAL
                );
                CREATE INDEX IF NOT EXISTS ix_events_ts ON events(ts);
                CREATE TABLE IF NOT EXISTS cal_log (
                  ts INTEGER NOT NULL,
                  name TEXT NOT NULL,
                  k REAL,
                  b REAL,
                  points INTEGER NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // 老库补列：dkn = 较基线变化量（k·Δt）；cal_log.mode = 基线口径、scope = 作用的法兰/通道、rated = 当时额定。ALTER 只加列不动数据
        AddColumnIfMissing("samples", "dkn", "REAL");
        AddColumnIfMissing("cal_log", "mode", "TEXT");
        AddColumnIfMissing("cal_log", "scope", "TEXT");
        AddColumnIfMissing("cal_log", "rated", "REAL");

        LoadLastHealth();
        _sampleCount = Scalar(_write, "SELECT COUNT(*) FROM samples");
        _eventCount = Scalar(_write, "SELECT COUNT(*) FROM events");
        _lastPurgeAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 写不过来时丢新帧：测点本来就是 5 秒一条采样，掉一帧无所谓，回调线程一毫秒都不能等
        _queue = Channel.CreateBounded<Frame>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        _writer = Task.Run(WriterLoopAsync);
    }

    public string DbPath { get; }

    /// <summary>
    /// 记下本次启动生效的标定与口径：每个接在设备上的法兰一条（scope = 法兰名 + 通道），没有标定文件也记。
    /// 同一 scope 和上一条不同才写，cal_log 就是各法兰标定变更的流水，历史里任何一个 kn 都能对回当时的系数和口径。
    /// </summary>
    public void SetPlan(CalPlan plan)
    {
        _plan = plan;
        var mode = plan.Mode == BaselineMode.Anchor ? "anchor" : "free";

        var entries = plan.Flanges.Where(f => f.OnDevice)
            .Select(f => (Cal: f.Cal, Scope: $"{f.Name}（{string.Join("、", f.Channels.Select(c => "CH" + c))}）", Rated: f.Rated))
            .ToList();
        if (entries.Count == 0)
        {
            entries.Add((plan.GlobalCal, "默认（CH1–CH4）", plan.GlobalRated));
        }

        // 写线程已经在跑了，不能碰 _write；另开一个连接写，SQLite 自己会排队
        using var conn = OpenRead();
        foreach (var (cal, scope, rated) in entries)
        {
            var name = cal?.FileName ?? "未标定";
            var points = cal?.Points ?? 0;
            var b = cal?.B ?? 0m;
            using (var last = conn.CreateCommand())
            {
                last.CommandText = "SELECT name, k, b, points, mode, rated FROM cal_log WHERE scope = $scope ORDER BY ts DESC, rowid DESC LIMIT 1";
                last.Parameters.AddWithValue("$scope", scope);
                using var r = last.ExecuteReader();
                if (r.Read()
                    && r.GetString(0) == name
                    && (r.IsDBNull(1) ? (double?)null : r.GetDouble(1)) == (cal is null ? (double?)null : (double)cal.K)
                    && (r.IsDBNull(2) ? 0d : r.GetDouble(2)) == (double)b
                    && r.GetInt32(3) == points
                    && (r.IsDBNull(4) ? null : r.GetString(4)) == mode
                    && (r.IsDBNull(5) ? 0d : r.GetDouble(5)) == (double)rated)
                {
                    continue;
                }
            }

            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO cal_log(ts, name, k, b, points, mode, scope, rated) VALUES ($ts, $name, $k, $b, $points, $mode, $scope, $rated)";
            ins.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            ins.Parameters.AddWithValue("$name", name);
            ins.Parameters.AddWithValue("$k", cal is null ? DBNull.Value : (double)cal.K);
            ins.Parameters.AddWithValue("$b", (double)b);
            ins.Parameters.AddWithValue("$points", points);
            ins.Parameters.AddWithValue("$mode", mode);
            ins.Parameters.AddWithValue("$scope", scope);
            ins.Parameters.AddWithValue("$rated", (double)rated);
            ins.ExecuteNonQuery();
        }
    }

    private void AddColumnIfMissing(string table, string column, string type)
    {
        using (var info = _write.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table})";
            using var r = info.ExecuteReader();
            while (r.Read())
            {
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
        }

        using var alter = _write.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }

    /// <summary>测量回调里调用：只入队，不抛，不等磁盘。</summary>
    public void Ingest(ChannelResult m)
    {
        if (_disposed)
        {
            return;
        }

        var frame = new Frame(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            m.Channel,
            m.StatusCode == MeasStatusCode.Normal,
            m.DeltaTof,
            m.EchoTime,
            m.SpTemp ?? m.RawTemp);
        if (!_queue.Writer.TryWrite(frame))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    // ── 写线程 ──────────────────────────────────────────────────────────────

    private async Task WriterLoopAsync()
    {
        try
        {
            await foreach (var frame in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    Process(frame);
                }
                catch (Exception ex)
                {
                    // 一帧落盘失败不能停掉写线程，但也不能静默：计数、记最后一条原因、控制台一分钟最多喊一次
                    Interlocked.Increment(ref _writeErrors);
                    _lastError = ex.Message;
                    if (frame.At - _lastErrorLogAt >= (long)ErrorLogEvery.TotalMilliseconds)
                    {
                        _lastErrorLogAt = frame.At;
                        Console.WriteLine($"{DateTime.Now:HH:mm:ss} 历史库写入失败（累计 {Interlocked.Read(ref _writeErrors)} 次）：{ex.Message}");
                    }
                }

                if (frame.At - _lastPurgeAt >= (long)PurgeEvery.TotalMilliseconds)
                {
                    _lastPurgeAt = frame.At;
                    try
                    {
                        Purge();
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _writeErrors);
                        _lastError = "清理失败：" + ex.Message;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose 超时后强停
        }
    }

    private void Process(Frame f)
    {
        // 声时偏离置零基线超出物理可能（预紧变化只有几十上百 ns、温度全程 ±200 ns）：
        // 探头位置或耦合变了，这帧不能算成力值。单独记成 shift，和页面上"基线失配"叫法一致
        var mismatch = IsBaselineMismatch(f.DeltaTof);
        var normal = f.Normal && !mismatch;
        var plan = _plan;
        var ch = (ushort)f.Ch;
        // kn：预紧力（自由置零 = 绝对；锚定 = 按锚定时=额定估算，没额定就空）；dkn：较基线变化量 k·Δt。系数/额定按通道所属法兰取
        var kn = mismatch ? null : (double?)plan?.ForceKn(ch, f.DeltaTof);
        var dkn = mismatch ? null : (double?)plan?.DeltaKn(ch, f.DeltaTof);
        var health = Judge(normal, mismatch, plan, ch, kn);
        WriteEventIfChanged(f.At, f.Ch, health, kn);

        if (!_lastSampleAt.TryGetValue(f.Ch, out var prev) || f.At - prev >= SampleMs)
        {
            InsertSample(f.At, f.Ch, kn, dkn, (double?)f.Temp, (double?)f.DeltaTof, (double?)f.EchoTime, normal, health);
            _lastSampleAt[f.Ch] = f.At;
            Interlocked.Increment(ref _sampleCount);
        }
    }

    private void LoadLastHealth()
    {
        using var cmd = _write.CreateCommand();
        cmd.CommandText =
            """
            SELECT s.ch, s.health FROM samples s
            JOIN (SELECT ch, MAX(ts) AS ts FROM samples GROUP BY ch) x
              ON s.ch = x.ch AND s.ts = x.ts
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            _lastHealth[r.GetInt32(0)] = r.GetString(1);
        }
    }

    private void InsertSample(long ts, int ch, double? kn, double? dkn, double? temp, double? dt, double? echo, bool normal, string health)
    {
        using var cmd = _write.CreateCommand();
        cmd.CommandText =
            """
            INSERT OR REPLACE INTO samples(ts, ch, kn, dkn, temp, dt, echo, normal, health)
            VALUES ($ts, $ch, $kn, $dkn, $temp, $dt, $echo, $normal, $health)
            """;
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$ch", ch);
        cmd.Parameters.AddWithValue("$kn", kn is { } k ? k : DBNull.Value);
        cmd.Parameters.AddWithValue("$dkn", dkn is { } dk ? dk : DBNull.Value);
        cmd.Parameters.AddWithValue("$temp", temp is { } tp ? tp : DBNull.Value);
        cmd.Parameters.AddWithValue("$dt", dt is { } d ? d : DBNull.Value);
        cmd.Parameters.AddWithValue("$echo", echo is { } e ? e : DBNull.Value);
        cmd.Parameters.AddWithValue("$normal", normal ? 1 : 0);
        cmd.Parameters.AddWithValue("$health", health);
        cmd.ExecuteNonQuery();
    }

    private void WriteEventIfChanged(long now, int ch, string health, double? kn)
    {
        if (!_lastHealth.TryGetValue(ch, out var prev))
        {
            _lastHealth[ch] = health;
            return;
        }

        if (prev == health)
        {
            return;
        }

        // 轻状态 10 秒内不重复记，恶化成报警/异常的立刻记
        if (health is not ("loose" or "fault" or "shift")
            && _lastEventAt.TryGetValue(ch, out var last)
            && now - last < EventDebounceMs)
        {
            return;
        }

        using var cmd = _write.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO events(ts, ch, from_h, to_h, kn)
            VALUES ($ts, $ch, $from, $to, $kn)
            """;
        cmd.Parameters.AddWithValue("$ts", now);
        cmd.Parameters.AddWithValue("$ch", ch);
        cmd.Parameters.AddWithValue("$from", prev);
        cmd.Parameters.AddWithValue("$to", health);
        cmd.Parameters.AddWithValue("$kn", kn is { } k ? k : DBNull.Value);
        cmd.ExecuteNonQuery();
        _lastHealth[ch] = health;
        _lastEventAt[ch] = now;
        Interlocked.Increment(ref _eventCount);
    }

    /// <summary>
    /// 状态口径（和页面 boltHealth 一致）：先看测不测得到（fault / shift），再看有没有标定（uncal），
    /// 最后才拿力值比阈值。没有标定文件时绝不给出"正常/偏低/松动"这种需要力值才能下的结论；
    /// 没有额定值时不判偏低/松动（锚定口径下连绝对预紧力都估不出来）。
    /// </summary>
    private static string Judge(bool normal, bool mismatch, CalPlan? plan, ushort ch, double? kn)
    {
        if (mismatch)
        {
            return "shift";
        }

        if (!normal)
        {
            return "fault";
        }

        if (plan?.CalFor(ch) is null)
        {
            return "uncal";
        }

        var rated = plan.RatedFor(ch);
        if (rated > 0 && kn is { } f)
        {
            if ((decimal)f < rated * plan.AlarmRatio)
            {
                return "loose";
            }

            if ((decimal)f < rated * plan.WarnRatio)
            {
                return "warn";
            }
        }

        return "ok";
    }

    private void Purge()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (table, days) in new[] { ("samples", RetainSampleDays), ("events", RetainEventDays) })
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table} WHERE ts < $cut";
            cmd.Parameters.AddWithValue("$cut", now.AddDays(-days).ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }

        // 一小时一次的全量重数，在写线程上做，不碰 SDK 回调也不挡页面查询
        Interlocked.Exchange(ref _sampleCount, Scalar(_write, "SELECT COUNT(*) FROM samples"));
        Interlocked.Exchange(ref _eventCount, Scalar(_write, "SELECT COUNT(*) FROM events"));
    }

    // ── 查询：每次自己开连接，和写线程、和别的查询都不抢锁 ────────────────────

    private SqliteConnection OpenRead()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        return conn;
    }

    public object QuerySamples(string range)
    {
        var (from, to) = Bounds(range);
        var bucket = BucketMs(to - from);
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT (ts / $b) * $b AS t, ch,
                   AVG(kn) AS kn, AVG(dkn) AS dkn, AVG(temp) AS temp
            FROM samples
            WHERE ts >= $from AND ts <= $to
            GROUP BY t, ch
            ORDER BY t, ch
            """;
        cmd.Parameters.AddWithValue("$b", bucket);
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", to);
        var points = new List<object>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            points.Add(new
            {
                t = r.GetInt64(0),
                ch = r.GetInt32(1),
                kn = r.IsDBNull(2) ? (double?)null : r.GetDouble(2),
                dkn = r.IsDBNull(3) ? (double?)null : r.GetDouble(3),
                temp = r.IsDBNull(4) ? (double?)null : r.GetDouble(4),
            });
        }

        return new { range, from, to, bucketSec = bucket / 1000, points };
    }

    public object QueryEvents(string range)
    {
        var (from, to) = Bounds(range);
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT ts, ch, from_h, to_h, kn
            FROM events
            WHERE ts >= $from AND ts <= $to
            ORDER BY ts DESC
            LIMIT 800
            """;
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", to);
        var events = new List<object>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            events.Add(new
            {
                t = r.GetInt64(0),
                ch = r.GetInt32(1),
                from = r.GetString(2),
                to = r.GetString(3),
                kn = r.IsDBNull(4) ? (double?)null : r.GetDouble(4),
            });
        }

        return new { range, from, to, events };
    }

    public object QueryReport(string range)
    {
        var (from, to) = Bounds(range);
        using var conn = OpenRead();
        var channels = new List<object>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT ch, COUNT(*), MIN(kn), MAX(kn), AVG(kn), AVG(temp), MIN(dkn), MAX(dkn), AVG(dkn)
                FROM samples
                WHERE ts >= $from AND ts <= $to
                GROUP BY ch
                ORDER BY ch
                """;
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                channels.Add(new
                {
                    ch = r.GetInt32(0),
                    samples = r.GetInt64(1),
                    knMin = r.IsDBNull(2) ? (double?)null : r.GetDouble(2),
                    knMax = r.IsDBNull(3) ? (double?)null : r.GetDouble(3),
                    knAvg = r.IsDBNull(4) ? (double?)null : r.GetDouble(4),
                    tempAvg = r.IsDBNull(5) ? (double?)null : r.GetDouble(5),
                    dknMin = r.IsDBNull(6) ? (double?)null : r.GetDouble(6),
                    dknMax = r.IsDBNull(7) ? (double?)null : r.GetDouble(7),
                    dknAvg = r.IsDBNull(8) ? (double?)null : r.GetDouble(8),
                });
            }
        }

        long eventCount;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE ts >= $from AND ts <= $to";
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            eventCount = (long)cmd.ExecuteScalar()!;
        }

        return new { range, from, to, eventCount, channels };
    }

    /// <summary>
    /// 边读边写到响应流。30 天 × 8 测点 × 5 秒一条是四百多万行，攒成一个字符串再转字节要一个多 GB，
    /// 工控机上直接内存溢出；流式写峰值只有一个缓冲区。
    /// </summary>
    public async Task ExportCsvAsync(string range, Stream output, CancellationToken ct)
    {
        var (from, to) = Bounds(range);
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT ts, ch, kn, dkn, temp, dt, health
            FROM samples
            WHERE ts >= $from AND ts <= $to
            ORDER BY ts, ch
            """;
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", to);

        // 不能套 StreamWriter：它的缓冲写满会同步 Flush 到 Response.Body，Kestrel 默认禁止同步 IO，
        // 一超过 64 KB 就抛异常断连。这里自己按块攒、显式 WriteAsync。
        const int rowsPerChunk = 2000;
        var sb = new StringBuilder(rowsPerChunk * 90);
        sb.Append('\uFEFF');   // 带 BOM：Excel 双击打开才认中文
        // 表头把口径写明：锚定口径下的预紧力是按“锚定时=额定”估算的，不能和自由置零的绝对值混为一谈
        var knHeader = _plan?.Mode == BaselineMode.Anchor ? "估算预紧力kN(锚定时=额定)" : "预紧力kN";
        sb.AppendLine($"时间,通道,{knHeader},较基线变化kN,温度C,声时差ns,状态");
        using var r = cmd.ExecuteReader();
        var rows = 0;
        while (r.Read())
        {
            var t = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)).ToLocalTime();
            sb.Append(t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(r.GetInt32(1));
            sb.Append(',');
            sb.Append(r.IsDBNull(2) ? "" : r.GetDouble(2).ToString("F3", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(r.IsDBNull(3) ? "" : r.GetDouble(3).ToString("F3", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(r.IsDBNull(4) ? "" : r.GetDouble(4).ToString("F2", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(r.IsDBNull(5) ? "" : r.GetDouble(5).ToString("F2", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(HealthZh(r.GetString(6)));
            sb.AppendLine();
            if (++rows % rowsPerChunk == 0)
            {
                await FlushChunkAsync(sb, output, ct).ConfigureAwait(false);
            }
        }

        await FlushChunkAsync(sb, output, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task FlushChunkAsync(StringBuilder sb, Stream output, CancellationToken ct)
    {
        if (sb.Length == 0) return;
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        sb.Clear();
        await output.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    public object Stats()
    {
        long? oldest;
        object? cal = null;
        using (var conn = OpenRead())
        {
            oldest = NullableScalar(conn, "SELECT MIN(ts) FROM samples");   // 走主键索引，不扫表
            using var cmd = conn.CreateCommand();
            // 每个 scope（法兰）最近一条生效的标定
            cmd.CommandText =
                """
                SELECT c.ts, c.name, c.k, c.b, c.points, c.mode, c.scope, c.rated
                FROM cal_log c
                JOIN (SELECT scope, MAX(ts) AS ts FROM cal_log GROUP BY scope) m ON m.scope IS c.scope AND m.ts = c.ts
                ORDER BY c.scope
                """;
            var list = new List<object>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new
                {
                    since = r.GetInt64(0),
                    name = r.GetString(1),
                    k = r.IsDBNull(2) ? (double?)null : r.GetDouble(2),
                    b = r.IsDBNull(3) ? (double?)null : r.GetDouble(3),
                    points = r.GetInt32(4),
                    mode = r.IsDBNull(5) ? null : r.GetString(5),
                    scope = r.IsDBNull(6) ? null : r.GetString(6),
                    rated = r.IsDBNull(7) ? (double?)null : r.GetDouble(7),
                });
            }

            cal = list;
        }

        var mb = 0d;
        try
        {
            var fi = new FileInfo(DbPath);
            if (fi.Exists)
            {
                mb = Math.Round(fi.Length / 1024d / 1024d, 2);
            }
        }
        catch (Exception)
        {
            // 文件信息读不到就报 0
        }

        return new
        {
            samples = Interlocked.Read(ref _sampleCount),
            events = Interlocked.Read(ref _eventCount),
            oldest,
            retainDays = RetainSampleDays,
            retainEventDays = RetainEventDays,
            dbMb = mb,
            queued = _queue.Reader.Count,
            dropped = Interlocked.Read(ref _dropped),
            writeErrors = Interlocked.Read(ref _writeErrors),
            lastError = _lastError,
            cal,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // 先让写线程把队列里的帧写完再关连接；实在写不完就强停
        _queue.Writer.TryComplete();
        if (!_writer.Wait(TimeSpan.FromSeconds(5)))
        {
            _stop.Cancel();
            try
            {
                _writer.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // 取消引发的，忽略
            }
        }

        _write.Dispose();
        _stop.Dispose();
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static long? NullableScalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    internal static (long From, long To) Bounds(string range)
    {
        var to = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var span = ParseRange(range);
        return (to - span, to);
    }

    /// <summary>把用户传进来的 range 归一成有限的几个值，拼进文件名之类的地方要用这个而不是原串。</summary>
    internal static string CanonicalRange(string? range) => (range ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "5m" => "5m",
        "1h" => "1h",
        "7d" => "7d",
        "30d" => "30d",
        _ => "24h",
    };

    internal static long ParseRange(string range) => CanonicalRange(range) switch
    {
        "5m" => 5L * 60 * 1000,
        "1h" => 60L * 60 * 1000,
        "7d" => 7L * 24 * 60 * 60 * 1000,
        "30d" => 30L * 24 * 60 * 60 * 1000,
        _ => 24L * 60 * 60 * 1000,
    };

    private static long BucketMs(long spanMs)
    {
        if (spanMs <= 15L * 60 * 1000) return 5_000;
        if (spanMs <= 3L * 60 * 60 * 1000) return 15_000;
        if (spanMs <= 24L * 60 * 60 * 1000) return 60_000;
        if (spanMs <= 7L * 24 * 60 * 60 * 1000) return 5 * 60_000;
        if (spanMs <= 30L * 24 * 60 * 60 * 1000) return 15 * 60_000;
        return 60 * 60_000;
    }

    private static string HealthZh(string h) => h switch
    {
        "ok" => "正常",
        "warn" => "预紧力偏低",
        "loose" => "松动报警",
        "fault" => "检测异常",
        "shift" => "基线失配",
        "uncal" => "未标定",
        _ => "离线",
    };
}
