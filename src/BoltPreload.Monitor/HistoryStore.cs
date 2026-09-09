using System.Globalization;
using System.Text;
using System.Threading.Channels;
using FxDevice;
using FxSDK.Models;
using Microsoft.Data.Sqlite;

/// <summary>
/// 本地历史库：测点每 5 秒落一条，状态变化记事件。关软件还在，库在 exe 旁 data/history.db。
/// 一个大屏接很多台设备，所有表都带机组号 t：同一通道号在不同机组上是不同的螺栓。
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

    /// <summary>
    /// 一帧里落盘要用到的东西。在回调线程上抓一份就走，别让回调等磁盘。力值和状态是服务端（MonitorHub）算好的，这里只记。
    /// Baseline 不为空时这不是测量帧，是"连接时读到的这一路参考声时"，写线程拿它和上次记的比，变了就记基线流水。
    /// </summary>
    private readonly record struct Frame(long At, int T, int Ch, bool Normal, decimal? DeltaTof, decimal? EchoTime, decimal? Temp,
        string Health, double? Kn, double? Dkn, (double? RefTime, double? RefTemp)? Baseline = null);

    private readonly string _connString;

    /// <summary>只有写线程碰它。</summary>
    private readonly SqliteConnection _write;

    private readonly Channel<Frame> _queue;
    private readonly Task _writer;
    private readonly CancellationTokenSource _stop = new();

    // 下面这些只在写线程上读写，不需要锁。键 = (机组, 通道)
    private readonly Dictionary<(int T, int Ch), long> _lastSampleAt = new();
    private readonly Dictionary<(int T, int Ch), string> _lastHealth = new();
    private readonly Dictionary<(int T, int Ch), long> _lastEventAt = new();
    // 每一路最近记下的基线（参考声时/参考温度）：连接时读到的和它不一样 = 现场重新置零过
    private readonly Dictionary<(int T, int Ch), (double? RefTime, double? RefTemp)> _lastBaseline = new();
    private long _lastPurgeAt;
    private long _lastErrorLogAt;

    // 给 Stats 看的计数。启动数一次、之后随写随加、每小时清理后重数一次；
    // 不再每次页面刷新都对千万行做 COUNT(*)
    private long _sampleCount;
    private long _eventCount;
    private long _dropped;
    private long _writeErrors;
    private volatile string _lastError = string.Empty;

    // 基线口径（全场统一），导出表头要写明
    private volatile BaselineMode _mode = BaselineMode.Anchor;
    private volatile bool _disposed;

    /// <param name="legacyTurbine">老库没有机组列时，已有数据归到哪台机组（老写法里 bind.turbine 那台）。</param>
    public HistoryStore(string dbPath, int legacyTurbine = 1)
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
                  t INTEGER NOT NULL,
                  ch INTEGER NOT NULL,
                  kn REAL,
                  dkn REAL,
                  temp REAL,
                  dt REAL,
                  echo REAL,
                  normal INTEGER NOT NULL,
                  health TEXT NOT NULL,
                  PRIMARY KEY (ts, t, ch)
                );
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
                CREATE TABLE IF NOT EXISTS zero_log (
                  ts INTEGER NOT NULL,
                  ch INTEGER NOT NULL,
                  ref_time REAL,
                  ref_temp REAL,
                  prev_ref_time REAL,
                  prev_ref_temp REAL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // 老库升级：加机组列 t。samples 的主键要带 t（两台设备同一毫秒同一通道号的采样不能互相覆盖），
        // SQLite 改不了主键，只能建新表搬数据；其余表 ALTER 加列即可。已有数据全归老写法里的那台机组
        MigrateSamplesToTurbine(legacyTurbine);
        AddColumnIfMissing("events", "t", $"INTEGER NOT NULL DEFAULT {legacyTurbine}");
        AddColumnIfMissing("events", "note", "TEXT");
        AddColumnIfMissing("cal_log", "mode", "TEXT");
        AddColumnIfMissing("cal_log", "scope", "TEXT");
        AddColumnIfMissing("cal_log", "rated", "REAL");
        AddColumnIfMissing("cal_log", "t", $"INTEGER NOT NULL DEFAULT {legacyTurbine}");
        AddColumnIfMissing("zero_log", "t", $"INTEGER NOT NULL DEFAULT {legacyTurbine}");
        using (var idx = _write.CreateCommand())
        {
            idx.CommandText =
                """
                CREATE INDEX IF NOT EXISTS ix_samples_t_ch_ts ON samples(t, ch, ts);
                CREATE INDEX IF NOT EXISTS ix_events_t_ts ON events(t, ts);
                CREATE INDEX IF NOT EXISTS ix_zero_t_ch_ts ON zero_log(t, ch, ts);
                """;
            idx.ExecuteNonQuery();
        }

        LoadLastHealth();
        LoadLastBaselines();
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
    /// 记下本次启动各台设备生效的标定与口径：每台设备每个接在它上面的法兰一条（scope = 法兰名 + 通道），没有标定文件也记。
    /// 同一 (机组, scope) 和上一条不同才写，cal_log 就是各法兰标定变更的流水，历史里任何一个 kn 都能对回当时的系数和口径。
    /// </summary>
    public void SetPlans(IEnumerable<(int Turbine, CalPlan Plan)> plans)
    {
        var list = plans.ToList();
        if (list.Count > 0) _mode = list[0].Plan.Mode;

        // 写线程已经在跑了，不能碰 _write；另开一个连接写，SQLite 自己会排队
        using var conn = OpenRead();
        foreach (var (turbine, plan) in list)
        {
            var mode = plan.Mode == BaselineMode.Anchor ? "anchor" : "free";
            var entries = plan.Flanges.Where(f => f.OnDevice)
                .Select(f => (Cal: f.Cal, Scope: $"{f.Name}（{string.Join("、", f.Channels.Select(c => "CH" + c))}）", Rated: f.Rated))
                .ToList();
            if (entries.Count == 0)
            {
                entries.Add((plan.GlobalCal, "默认（CH1–CH4）", plan.GlobalRated));
            }

            foreach (var (cal, scope, rated) in entries)
            {
                var name = cal?.FileName ?? "未标定";
                var points = cal?.Points ?? 0;
                var b = cal?.B ?? 0m;
                using (var last = conn.CreateCommand())
                {
                    last.CommandText = "SELECT name, k, b, points, mode, rated FROM cal_log WHERE t = $t AND scope = $scope ORDER BY ts DESC, rowid DESC LIMIT 1";
                    last.Parameters.AddWithValue("$t", turbine);
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
                ins.CommandText = "INSERT INTO cal_log(ts, t, name, k, b, points, mode, scope, rated) VALUES ($ts, $t, $name, $k, $b, $points, $mode, $scope, $rated)";
                ins.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                ins.Parameters.AddWithValue("$t", turbine);
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
    }

    private void MigrateSamplesToTurbine(int legacyTurbine)
    {
        if (HasColumn("samples", "t")) return;

        Console.WriteLine($"历史库升级：samples 表加机组列（已有数据归 F{legacyTurbine:00}），数据多时要等一会……");
        using var tx = _write.BeginTransaction();
        using (var cmd = _write.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                $"""
                CREATE TABLE samples_v2 (
                  ts INTEGER NOT NULL,
                  t INTEGER NOT NULL,
                  ch INTEGER NOT NULL,
                  kn REAL,
                  dkn REAL,
                  temp REAL,
                  dt REAL,
                  echo REAL,
                  normal INTEGER NOT NULL,
                  health TEXT NOT NULL,
                  PRIMARY KEY (ts, t, ch)
                );
                INSERT INTO samples_v2(ts, t, ch, kn, dkn, temp, dt, echo, normal, health)
                  SELECT ts, {legacyTurbine}, ch, kn, {(HasColumn("samples", "dkn") ? "dkn" : "NULL")}, temp, dt, echo, normal, health FROM samples;
                DROP INDEX IF EXISTS ix_samples_ch_ts;
                DROP TABLE samples;
                ALTER TABLE samples_v2 RENAME TO samples;
                """;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        Console.WriteLine("历史库升级完成");
    }

    private bool HasColumn(string table, string column)
    {
        using var cmd = _write.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void AddColumnIfMissing(string table, string column, string type)
    {
        if (HasColumn(table, column)) return;
        using var alter = _write.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }

    /// <summary>测量回调里调用：只入队，不抛，不等磁盘。力值与状态是 MonitorHub 用 Health.Judge 算好的那一份。</summary>
    public void Ingest(int turbine, ChannelResult m, string health, decimal? kn, decimal? dkn)
    {
        if (_disposed)
        {
            return;
        }

        var frame = new Frame(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            turbine,
            m.Channel,
            m.StatusCode == MeasStatusCode.Normal,
            m.DeltaTof,
            m.EchoTime,
            m.SpTemp ?? m.RawTemp,
            health,
            (double?)kn,
            (double?)dkn);
        if (!_queue.Writer.TryWrite(frame))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>
    /// 连接/补读时把每一路读到的参考声时、参考温度交进来。和上次记的不一样 = 现场用桌面端重新置零过：
    /// 记一条基线流水，并写一条"基线重置"事件——不然趋势图上力值突然跳一截，谁也说不清是螺栓动了还是基线换了。
    /// 第一次见到的通道只记流水不记事件。
    /// </summary>
    public void RecordBaselines(int turbine, IReadOnlyDictionary<ushort, (decimal? RefTime, decimal? RefTemp)> baselines)
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (ch, b) in baselines)
        {
            var frame = new Frame(now, turbine, ch, true, null, null, null, string.Empty, null, null, ((double?)b.RefTime, (double?)b.RefTemp));
            if (!_queue.Writer.TryWrite(frame))
            {
                Interlocked.Increment(ref _dropped);
            }
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
        var key = (f.T, f.Ch);
        if (f.Baseline is { } b)
        {
            ProcessBaseline(f.At, key, b);
            return;
        }

        // 力值与状态都是服务端 Health.Judge 那一份；这里只负责记。normal 列记"测得到且没失配"，方便报表按它筛
        var normal = f.Normal && !IsBaselineMismatch(f.DeltaTof);
        WriteEventIfChanged(f.At, key, f.Health, f.Kn, null);

        if (!_lastSampleAt.TryGetValue(key, out var prev) || f.At - prev >= SampleMs)
        {
            InsertSample(f.At, f.T, f.Ch, f.Kn, f.Dkn, (double?)f.Temp, (double?)f.DeltaTof, (double?)f.EchoTime, normal, f.Health);
            _lastSampleAt[key] = f.At;
            Interlocked.Increment(ref _sampleCount);
        }
    }

    private void ProcessBaseline(long now, (int T, int Ch) key, (double? RefTime, double? RefTemp) cur)
    {
        var known = _lastBaseline.TryGetValue(key, out var last);
        if (known && Same(last.RefTime, cur.RefTime) && Same(last.RefTemp, cur.RefTemp))
        {
            return;
        }

        using (var cmd = _write.CreateCommand())
        {
            cmd.CommandText =
                """
                INSERT INTO zero_log(ts, t, ch, ref_time, ref_temp, prev_ref_time, prev_ref_temp)
                VALUES ($ts, $t, $ch, $rt, $rtemp, $prt, $prtemp)
                """;
            cmd.Parameters.AddWithValue("$ts", now);
            cmd.Parameters.AddWithValue("$t", key.T);
            cmd.Parameters.AddWithValue("$ch", key.Ch);
            cmd.Parameters.AddWithValue("$rt", cur.RefTime is { } rt ? rt : DBNull.Value);
            cmd.Parameters.AddWithValue("$rtemp", cur.RefTemp is { } rtp ? rtp : DBNull.Value);
            cmd.Parameters.AddWithValue("$prt", known && last.RefTime is { } prt ? prt : DBNull.Value);
            cmd.Parameters.AddWithValue("$prtemp", known && last.RefTemp is { } prtp ? prtp : DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        _lastBaseline[key] = cur;
        if (!known)
        {
            return;   // 第一次见到这一路：只记下基线，不算"重置"
        }

        // 基线变了：记事件，并把这一路的"上一个状态"置成 rezero——下一帧的状态会记成 "rezero → ok/…"，趋势上这一跳就有出处
        var note = $"参考声时 {Fmt(last.RefTime)} → {Fmt(cur.RefTime)} ns，参考温度 {Fmt(last.RefTemp)} → {Fmt(cur.RefTemp)} ℃";
        var from = _lastHealth.TryGetValue(key, out var h) ? h : "unknown";
        InsertEvent(now, key, from, Health.Rezero, null, note);
        _lastHealth[key] = Health.Rezero;
        _lastEventAt[key] = now;

        static bool Same(double? a, double? b) => a is null ? b is null : b is { } bv && Math.Abs(a.Value - bv) < 0.5;
        static string Fmt(double? v) => v is { } x ? x.ToString("0", CultureInfo.InvariantCulture) : "-";
    }

    private void LoadLastBaselines()
    {
        using var cmd = _write.CreateCommand();
        cmd.CommandText =
            """
            SELECT z.t, z.ch, z.ref_time, z.ref_temp FROM zero_log z
            JOIN (SELECT t, ch, MAX(ts) AS ts FROM zero_log GROUP BY t, ch) x
              ON z.t = x.t AND z.ch = x.ch AND z.ts = x.ts
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            _lastBaseline[(r.GetInt32(0), r.GetInt32(1))] = (r.IsDBNull(2) ? null : r.GetDouble(2), r.IsDBNull(3) ? null : r.GetDouble(3));
        }
    }

    private void LoadLastHealth()
    {
        using var cmd = _write.CreateCommand();
        cmd.CommandText =
            """
            SELECT s.t, s.ch, s.health FROM samples s
            JOIN (SELECT t, ch, MAX(ts) AS ts FROM samples GROUP BY t, ch) x
              ON s.t = x.t AND s.ch = x.ch AND s.ts = x.ts
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            _lastHealth[(r.GetInt32(0), r.GetInt32(1))] = r.GetString(2);
        }
    }

    private void InsertSample(long ts, int t, int ch, double? kn, double? dkn, double? temp, double? dt, double? echo, bool normal, string health)
    {
        using var cmd = _write.CreateCommand();
        cmd.CommandText =
            """
            INSERT OR REPLACE INTO samples(ts, t, ch, kn, dkn, temp, dt, echo, normal, health)
            VALUES ($ts, $t, $ch, $kn, $dkn, $temp, $dt, $echo, $normal, $health)
            """;
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$t", t);
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

    private void WriteEventIfChanged(long now, (int T, int Ch) key, string health, double? kn, string? note)
    {
        if (!_lastHealth.TryGetValue(key, out var prev))
        {
            _lastHealth[key] = health;
            return;
        }

        if (prev == health)
        {
            return;
        }

        // 轻状态 10 秒内不重复记，恶化成报警/异常的立刻记
        if (!Health.IsUrgent(health)
            && _lastEventAt.TryGetValue(key, out var last)
            && now - last < EventDebounceMs)
        {
            return;
        }

        InsertEvent(now, key, prev, health, kn, note);
        _lastHealth[key] = health;
        _lastEventAt[key] = now;
    }

    private void InsertEvent(long now, (int T, int Ch) key, string from, string to, double? kn, string? note)
    {
        using var cmd = _write.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO events(ts, t, ch, from_h, to_h, kn, note)
            VALUES ($ts, $t, $ch, $from, $to, $kn, $note)
            """;
        cmd.Parameters.AddWithValue("$ts", now);
        cmd.Parameters.AddWithValue("$t", key.T);
        cmd.Parameters.AddWithValue("$ch", key.Ch);
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", to);
        cmd.Parameters.AddWithValue("$kn", kn is { } k ? k : DBNull.Value);
        cmd.Parameters.AddWithValue("$note", note is { } n ? n : DBNull.Value);
        cmd.ExecuteNonQuery();
        Interlocked.Increment(ref _eventCount);
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
    // turbine 为空 = 全场（报告页、报警中心），给了 = 只看这台机组（机组页、法兰页趋势）

    private SqliteConnection OpenRead()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        return conn;
    }

    private static string TurbineFilter(int? turbine, SqliteCommand cmd)
    {
        if (turbine is not { } t) return string.Empty;
        cmd.Parameters.AddWithValue("$turbine", t);
        return " AND t = $turbine";
    }

    public object QuerySamples(string range, int? turbine)
    {
        var (from, to) = Bounds(range);
        var bucket = BucketMs(to - from);
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        var filter = TurbineFilter(turbine, cmd);
        cmd.CommandText =
            $"""
            SELECT (ts / $b) * $b AS bt, t, ch,
                   AVG(kn) AS kn, AVG(dkn) AS dkn, AVG(temp) AS temp
            FROM samples
            WHERE ts >= $from AND ts <= $to{filter}
            GROUP BY bt, t, ch
            ORDER BY bt, t, ch
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
                tb = r.GetInt32(1),
                ch = r.GetInt32(2),
                kn = r.IsDBNull(3) ? (double?)null : r.GetDouble(3),
                dkn = r.IsDBNull(4) ? (double?)null : r.GetDouble(4),
                temp = r.IsDBNull(5) ? (double?)null : r.GetDouble(5),
            });
        }

        return new { range, from, to, turbine, bucketSec = bucket / 1000, points };
    }

    public object QueryEvents(string range, int? turbine)
    {
        var (from, to) = Bounds(range);
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        var filter = TurbineFilter(turbine, cmd);
        cmd.CommandText =
            $"""
            SELECT ts, t, ch, from_h, to_h, kn, note
            FROM events
            WHERE ts >= $from AND ts <= $to{filter}
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
                tb = r.GetInt32(1),
                ch = r.GetInt32(2),
                from = r.GetString(3),
                to = r.GetString(4),
                kn = r.IsDBNull(5) ? (double?)null : r.GetDouble(5),
                note = r.IsDBNull(6) ? null : r.GetString(6),
            });
        }

        return new { range, from, to, turbine, events };
    }

    public object QueryReport(string range, int? turbine)
    {
        var (from, to) = Bounds(range);
        using var conn = OpenRead();
        var channels = new List<object>();
        using (var cmd = conn.CreateCommand())
        {
            var filter = TurbineFilter(turbine, cmd);
            cmd.CommandText =
                $"""
                SELECT t, ch, COUNT(*), MIN(kn), MAX(kn), AVG(kn), AVG(temp), MIN(dkn), MAX(dkn), AVG(dkn)
                FROM samples
                WHERE ts >= $from AND ts <= $to{filter}
                GROUP BY t, ch
                ORDER BY t, ch
                """;
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                channels.Add(new
                {
                    tb = r.GetInt32(0),
                    ch = r.GetInt32(1),
                    samples = r.GetInt64(2),
                    knMin = r.IsDBNull(3) ? (double?)null : r.GetDouble(3),
                    knMax = r.IsDBNull(4) ? (double?)null : r.GetDouble(4),
                    knAvg = r.IsDBNull(5) ? (double?)null : r.GetDouble(5),
                    tempAvg = r.IsDBNull(6) ? (double?)null : r.GetDouble(6),
                    dknMin = r.IsDBNull(7) ? (double?)null : r.GetDouble(7),
                    dknMax = r.IsDBNull(8) ? (double?)null : r.GetDouble(8),
                    dknAvg = r.IsDBNull(9) ? (double?)null : r.GetDouble(9),
                });
            }
        }

        long eventCount;
        using (var cmd = conn.CreateCommand())
        {
            var filter = TurbineFilter(turbine, cmd);
            cmd.CommandText = $"SELECT COUNT(*) FROM events WHERE ts >= $from AND ts <= $to{filter}";
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            eventCount = (long)cmd.ExecuteScalar()!;
        }

        return new { range, from, to, turbine, eventCount, channels };
    }

    /// <summary>
    /// 边读边写到响应流。30 天 × 8 测点 × 5 秒一条是四百多万行，攒成一个字符串再转字节要一个多 GB，
    /// 工控机上直接内存溢出；流式写峰值只有一个缓冲区。
    /// </summary>
    public async Task ExportCsvAsync(string range, int? turbine, Stream output, CancellationToken ct)
    {
        var (from, to) = Bounds(range);
        using var conn = OpenRead();
        using var cmd = conn.CreateCommand();
        var filter = TurbineFilter(turbine, cmd);
        cmd.CommandText =
            $"""
            SELECT ts, t, ch, kn, dkn, temp, dt, health
            FROM samples
            WHERE ts >= $from AND ts <= $to{filter}
            ORDER BY ts, t, ch
            """;
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", to);

        // 不能套 StreamWriter：它的缓冲写满会同步 Flush 到 Response.Body，Kestrel 默认禁止同步 IO，
        // 一超过 64 KB 就抛异常断连。这里自己按块攒、显式 WriteAsync。
        const int rowsPerChunk = 2000;
        var sb = new StringBuilder(rowsPerChunk * 90);
        sb.Append('\uFEFF');   // 带 BOM：Excel 双击打开才认中文
        // 表头把口径写明：锚定口径下的预紧力是按“锚定时=额定”估算的，不能和自由置零的绝对值混为一谈
        var knHeader = _mode == BaselineMode.Anchor ? "估算预紧力kN(锚定时=额定)" : "预紧力kN";
        sb.AppendLine($"时间,机组,通道,{knHeader},较基线变化kN,温度C,声时差ns,状态");
        using var r = cmd.ExecuteReader();
        var rows = 0;
        while (r.Read())
        {
            var t = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)).ToLocalTime();
            sb.Append(t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.Append(",F");
            sb.Append(r.GetInt32(1).ToString("00", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(r.GetInt32(2));
            sb.Append(',');
            sb.Append(r.IsDBNull(3) ? "" : r.GetDouble(3).ToString("F3", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(r.IsDBNull(4) ? "" : r.GetDouble(4).ToString("F3", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(r.IsDBNull(5) ? "" : r.GetDouble(5).ToString("F2", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(r.IsDBNull(6) ? "" : r.GetDouble(6).ToString("F2", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(HealthZh(r.GetString(7)));
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
        var baselines = new List<object>();
        using (var conn = OpenRead())
        {
            oldest = NullableScalar(conn, "SELECT MIN(ts) FROM samples");   // 走主键索引，不扫表
            using var cmd = conn.CreateCommand();
            // 每台机组每个 scope（法兰）最近一条生效的标定
            cmd.CommandText =
                """
                SELECT c.ts, c.t, c.name, c.k, c.b, c.points, c.mode, c.scope, c.rated
                FROM cal_log c
                JOIN (SELECT t, scope, MAX(ts) AS ts FROM cal_log GROUP BY t, scope) m ON m.t = c.t AND m.scope IS c.scope AND m.ts = c.ts
                ORDER BY c.t, c.scope
                """;
            var list = new List<object>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new
                {
                    since = r.GetInt64(0),
                    tb = r.GetInt32(1),
                    name = r.GetString(2),
                    k = r.IsDBNull(3) ? (double?)null : r.GetDouble(3),
                    b = r.IsDBNull(4) ? (double?)null : r.GetDouble(4),
                    points = r.GetInt32(5),
                    mode = r.IsDBNull(6) ? null : r.GetString(6),
                    scope = r.IsDBNull(7) ? null : r.GetString(7),
                    rated = r.IsDBNull(8) ? (double?)null : r.GetDouble(8),
                });
            }

            cal = list;

            // 每一路当前记在案的基线（参考声时/温度）和重置次数：运维核对"这颗螺栓的基线是什么时候定的"
            using var zc = conn.CreateCommand();
            zc.CommandText =
                """
                SELECT z.t, z.ch, z.ts, z.ref_time, z.ref_temp,
                       (SELECT COUNT(*) FROM zero_log WHERE t = z.t AND ch = z.ch AND prev_ref_time IS NOT NULL) AS rezeros
                FROM zero_log z
                JOIN (SELECT t, ch, MAX(ts) AS ts FROM zero_log GROUP BY t, ch) x ON z.t = x.t AND z.ch = x.ch AND z.ts = x.ts
                ORDER BY z.t, z.ch
                """;
            using var zr = zc.ExecuteReader();
            while (zr.Read())
            {
                baselines.Add(new
                {
                    tb = zr.GetInt32(0),
                    ch = zr.GetInt32(1),
                    since = zr.GetInt64(2),
                    refTime = zr.IsDBNull(3) ? (double?)null : zr.GetDouble(3),
                    refTemp = zr.IsDBNull(4) ? (double?)null : zr.GetDouble(4),
                    rezeros = zr.GetInt64(5),
                });
            }
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
            baselines,
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
        "noprobe" => "探头无信号",
        "shift" => "基线失配",
        "badbase" => "基线可疑",
        "uncal" => "未标定",
        "rezero" => "基线重置",
        _ => "离线",
    };
}
