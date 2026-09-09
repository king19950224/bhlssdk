using System.Globalization;
using System.Text;
using FxSDK.Models;
using Microsoft.Data.Sqlite;

/// <summary>
/// 本地历史库：测点每 5 秒落一条，状态变化记事件。关软件还在，库在 exe 旁 data/history.db。
/// </summary>
internal sealed class HistoryStore : IDisposable
{
    private const int SampleMs = 5000;
    private const int EventDebounceMs = 10000;
    private const int RetainDays = 90;

    /// <summary>
    /// 声时差的物理上限（ns）。预紧力从零拧到额定只有几十上百 ns，温度全程漂移 ±200 ns；
    /// 超过 2 µs 只可能是探头贴到了别的螺栓、重新耦合过或基线错了（旧平台 echo_shift 同一门限）。
    /// </summary>
    public const decimal MaxPlausibleDeltaTofNs = 2000m;

    public static bool IsBaselineMismatch(decimal? deltaTof) => deltaTof is { } d && Math.Abs(d) > MaxPlausibleDeltaTofNs;

    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, long> _lastSampleAt = new();
    private readonly Dictionary<int, string> _lastHealth = new();
    private readonly Dictionary<int, long> _lastEventAt = new();

    private decimal _k = 0.8919m;
    private decimal _b;
    private decimal _rated;
    private decimal _warnRatio = 0.85m;
    private decimal _alarmRatio = 0.7m;
    private int _writes;
    private bool _disposed;

    public HistoryStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _conn.Open();
        using (var pragma = _conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }

        using var cmd = _conn.CreateCommand();
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
            """;
        cmd.ExecuteNonQuery();

        LoadLastHealth();
    }

    public string DbPath => _conn.DataSource;

    public void SetCal(decimal k, decimal b)
    {
        _k = k;
        _b = b;
    }

    public void SetThresholds(decimal ratedKn, decimal warnRatio, decimal alarmRatio)
    {
        _rated = ratedKn;
        _warnRatio = warnRatio;
        _alarmRatio = alarmRatio;
    }

    /// <summary>测量回调里调用：不抛，不挡推流。</summary>
    public void Ingest(ChannelResult m)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _gate.Wait();
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var ch = (int)m.Channel;
                // 声时偏离置零基线超出物理可能（预紧变化只有几十上百 ns、温度全程 ±200 ns）：
                // 探头位置或耦合变了，这帧不能算成力值，按异常入库
                var mismatch = IsBaselineMismatch(m.DeltaTof);
                var normal = m.StatusCode == MeasStatusCode.Normal && !mismatch;
                var kn = !mismatch && m.DeltaTof is { } dt ? (double)(dt * _k + _b) : (double?)null;
                var temp = (double?)(m.SpTemp ?? m.RawTemp);
                var health = Judge(normal, kn);
                WriteEventIfChanged(now, ch, health, kn);

                if (!_lastSampleAt.TryGetValue(ch, out var prev) || now - prev >= SampleMs)
                {
                    InsertSample(now, ch, kn, temp, (double?)m.DeltaTof, (double?)m.EchoTime, normal, health);
                    _lastSampleAt[ch] = now;
                    _writes++;
                    if (_writes % 200 == 0)
                    {
                        Purge();
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception)
        {
            // 落盘失败不能拖垮实时推流
        }
    }

    public object QuerySamples(string range)
    {
        var (from, to) = Bounds(range);
        var bucket = BucketMs(to - from);
        _gate.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT (ts / $b) * $b AS t, ch,
                       AVG(kn) AS kn, AVG(temp) AS temp
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
                    temp = r.IsDBNull(3) ? (double?)null : r.GetDouble(3),
                });
            }

            return new { range, from, to, bucketSec = bucket / 1000, points };
        }
        finally
        {
            _gate.Release();
        }
    }

    public object QueryEvents(string range)
    {
        var (from, to) = Bounds(range);
        _gate.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
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
        finally
        {
            _gate.Release();
        }
    }

    public object QueryReport(string range)
    {
        var (from, to) = Bounds(range);
        _gate.Wait();
        try
        {
            var channels = new List<object>();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText =
                    """
                    SELECT ch, COUNT(*), MIN(kn), MAX(kn), AVG(kn), AVG(temp)
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
                    });
                }
            }

            long eventCount;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM events WHERE ts >= $from AND ts <= $to";
                cmd.Parameters.AddWithValue("$from", from);
                cmd.Parameters.AddWithValue("$to", to);
                eventCount = (long)cmd.ExecuteScalar()!;
            }

            return new { range, from, to, eventCount, channels };
        }
        finally
        {
            _gate.Release();
        }
    }

    public byte[] ExportCsv(string range)
    {
        var (from, to) = Bounds(range);
        _gate.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT ts, ch, kn, temp, dt, health
                FROM samples
                WHERE ts >= $from AND ts <= $to
                ORDER BY ts, ch
                """;
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            var sb = new StringBuilder();
            sb.Append('\uFEFF');
            sb.AppendLine("时间,通道,预紧力kN,温度C,声时差ns,状态");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var t = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)).ToLocalTime();
                sb.Append(t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(r.GetInt32(1));
                sb.Append(',');
                sb.Append(r.IsDBNull(2) ? "" : r.GetDouble(2).ToString("F3", CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(r.IsDBNull(3) ? "" : r.GetDouble(3).ToString("F2", CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(r.IsDBNull(4) ? "" : r.GetDouble(4).ToString("F2", CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(HealthZh(r.GetString(5)));
                sb.AppendLine();
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }
        finally
        {
            _gate.Release();
        }
    }

    public object Stats()
    {
        _gate.Wait();
        try
        {
            long samples = Scalar("SELECT COUNT(*) FROM samples");
            long events = Scalar("SELECT COUNT(*) FROM events");
            long? oldest = NullableScalar("SELECT MIN(ts) FROM samples");
            var mb = 0d;
            try
            {
                var fi = new FileInfo(_conn.DataSource);
                if (fi.Exists)
                {
                    mb = Math.Round(fi.Length / 1024d / 1024d, 2);
                }
            }
            catch (Exception)
            {
                // 文件信息读不到就报 0
            }

            return new { samples, events, oldest, retainDays = RetainDays, dbMb = mb };
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Wait();
        try
        {
            _conn.Dispose();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void LoadLastHealth()
    {
        using var cmd = _conn.CreateCommand();
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

    private void InsertSample(long ts, int ch, double? kn, double? temp, double? dt, double? echo, bool normal, string health)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT OR REPLACE INTO samples(ts, ch, kn, temp, dt, echo, normal, health)
            VALUES ($ts, $ch, $kn, $temp, $dt, $echo, $normal, $health)
            """;
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$ch", ch);
        cmd.Parameters.AddWithValue("$kn", kn is { } k ? k : DBNull.Value);
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

        // 刚从离线恢复不算事件；轻状态 10 秒内不重复记，恶化成报警/异常的立刻记
        if (prev == "offline")
        {
            _lastHealth[ch] = health;
            return;
        }

        if (health is not ("loose" or "fault")
            && _lastEventAt.TryGetValue(ch, out var last)
            && now - last < EventDebounceMs)
        {
            return;
        }

        using var cmd = _conn.CreateCommand();
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
    }

    private string Judge(bool normal, double? kn)
    {
        if (!normal || kn is null)
        {
            return "fault";
        }

        if (_rated > 0)
        {
            if ((decimal)kn.Value < _rated * _alarmRatio)
            {
                return "loose";
            }

            if ((decimal)kn.Value < _rated * _warnRatio)
            {
                return "warn";
            }
        }

        return "ok";
    }

    private void Purge()
    {
        var cut = DateTimeOffset.UtcNow.AddDays(-RetainDays).ToUnixTimeMilliseconds();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM samples WHERE ts < $cut; DELETE FROM events WHERE ts < $cut;";
        cmd.Parameters.AddWithValue("$cut", cut);
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private long? NullableScalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
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

    internal static long ParseRange(string range) => range.Trim().ToLowerInvariant() switch
    {
        "5m" => 5L * 60 * 1000,
        "1h" => 60L * 60 * 1000,
        "24h" or "1d" or "today" => 24L * 60 * 60 * 1000,
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
        _ => "离线",
    };
}