using System.Text.Json;
using BoltPreload.Application.Devices.ProbeScan;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Infrastructure.Devices.ProbeScan;

/// <summary>
/// Postgres 实现。所有 SQL 显式列出列名，避免 SELECT * 在 schema 演进时静默错位。
/// 设计文档：资料/工程文档/probe-scan-design.md §5。
/// </summary>
public sealed class PostgresProbeScanRepository : IProbeScanRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresProbeScanRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<long?> FindOpenRunAsync(long deviceId, CancellationToken ct)
    {
        const string sql = @"
            SELECT id
            FROM channel_probe_scan_runs
            WHERE device_id = @device_id
              AND status IN ('pending','running')
            ORDER BY requested_at DESC
            LIMIT 1";
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : Convert.ToInt64(v);
    }

    public async Task<long> CreatePendingAsync(ProbeScanRequest req, long? requestedByUserId, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO channel_probe_scan_runs (
                device_id, requested_by, status, strategy, requested_channels,
                settle_seconds, sample_seconds, thresholds_jsonb)
            VALUES (
                @device_id, @requested_by, 'pending', @strategy, @channels,
                @settle, @sample, @thresholds::jsonb)
            RETURNING id";
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, req.DeviceId);
        cmd.Parameters.AddWithValue("requested_by", NpgsqlDbType.Bigint,
            (object?)requestedByUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("strategy", NpgsqlDbType.Varchar, req.Strategy);
        cmd.Parameters.Add(new NpgsqlParameter("channels", NpgsqlDbType.Array | NpgsqlDbType.Smallint)
        {
            Value = req.Channels.Select(c => (short)c).ToArray()
        });
        cmd.Parameters.AddWithValue("settle", NpgsqlDbType.Smallint, (short)req.SettleSeconds);
        cmd.Parameters.AddWithValue("sample", NpgsqlDbType.Smallint, (short)req.SampleSeconds);
        cmd.Parameters.AddWithValue("thresholds", NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(req.Thresholds));
        var v = await cmd.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException("插入 channel_probe_scan_runs 未返回 id");
        return Convert.ToInt64(v);
    }

    public async Task MarkRunningAsync(long scanRunId, DateTimeOffset startedAt, CancellationToken ct)
    {
        const string sql = @"
            UPDATE channel_probe_scan_runs
               SET status = 'running', started_at = @started
             WHERE id = @id AND status = 'pending'";
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, scanRunId);
        cmd.Parameters.AddWithValue("started", NpgsqlDbType.TimestampTz, startedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkCompletedAsync(long scanRunId, ProbeScanSummary summary, DateTimeOffset finishedAt, CancellationToken ct)
    {
        const string sql = @"
            UPDATE channel_probe_scan_runs
               SET status = 'completed',
                   finished_at = @finished,
                   summary_jsonb = @summary::jsonb
             WHERE id = @id AND status IN ('running','pending')";
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, scanRunId);
        cmd.Parameters.AddWithValue("finished", NpgsqlDbType.TimestampTz, finishedAt);
        cmd.Parameters.AddWithValue("summary", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(summary));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFailedAsync(long scanRunId, string errorMessage, DateTimeOffset finishedAt, CancellationToken ct)
    {
        const string sql = @"
            UPDATE channel_probe_scan_runs
               SET status = 'failed',
                   finished_at = @finished,
                   error_message = @err
             WHERE id = @id AND status IN ('running','pending')";
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, scanRunId);
        cmd.Parameters.AddWithValue("finished", NpgsqlDbType.TimestampTz, finishedAt);
        cmd.Parameters.AddWithValue("err", NpgsqlDbType.Text, errorMessage);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkAbortedAsync(long scanRunId, DateTimeOffset finishedAt, CancellationToken ct)
    {
        const string sql = @"
            UPDATE channel_probe_scan_runs
               SET status = 'aborted',
                   finished_at = @finished
             WHERE id = @id AND status IN ('running','pending')";
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, scanRunId);
        cmd.Parameters.AddWithValue("finished", NpgsqlDbType.TimestampTz, finishedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveResultAsync(long scanRunId, long deviceId, ProbeScanResultSlot slot, CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO channel_probe_scan_results (
                scan_run_id, channel_no, channel_id, verdict,
                samples, samples_with_echo,
                echo_rate_pct, echo_avg_ns, echo_std_ns,
                peak_to_peak_avg, snr_avg_db,
                bound_bolt_code, started_at, finished_at)
            VALUES (
                @run, @ch,
                (SELECT id FROM channels WHERE device_id = @device_id AND channel_no = @ch AND deleted_at IS NULL LIMIT 1),
                @verdict,
                @samples, @samples_echo,
                @echo_rate, @echo_avg, @echo_std,
                @pp_avg, @snr_avg,
                @bound_bolt, @started, @finished)
            ON CONFLICT (scan_run_id, channel_no) DO UPDATE SET
                verdict = EXCLUDED.verdict,
                samples = EXCLUDED.samples,
                samples_with_echo = EXCLUDED.samples_with_echo,
                echo_rate_pct = EXCLUDED.echo_rate_pct,
                echo_avg_ns = EXCLUDED.echo_avg_ns,
                echo_std_ns = EXCLUDED.echo_std_ns,
                peak_to_peak_avg = EXCLUDED.peak_to_peak_avg,
                snr_avg_db = EXCLUDED.snr_avg_db,
                bound_bolt_code = EXCLUDED.bound_bolt_code,
                started_at = EXCLUDED.started_at,
                finished_at = EXCLUDED.finished_at";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("run", NpgsqlDbType.Bigint, scanRunId);
        cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
        cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Smallint, (short)slot.ChannelNo);
        cmd.Parameters.AddWithValue("verdict", NpgsqlDbType.Varchar,
            ProbeScanVerdictNames.Format(slot.Verdict));
        cmd.Parameters.AddWithValue("samples", NpgsqlDbType.Integer, slot.Stats.Samples);
        cmd.Parameters.AddWithValue("samples_echo", NpgsqlDbType.Integer, slot.Stats.SamplesWithEcho);
        var echoRate = slot.Stats.Samples > 0
            ? Math.Round(100.0m * slot.Stats.SamplesWithEcho / slot.Stats.Samples, 2)
            : (decimal?)null;
        cmd.Parameters.AddWithValue("echo_rate", NpgsqlDbType.Numeric, (object?)echoRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("echo_avg", NpgsqlDbType.Numeric, (object?)slot.Stats.EchoAvgNs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("echo_std", NpgsqlDbType.Numeric, (object?)slot.Stats.EchoStdNs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("pp_avg", NpgsqlDbType.Numeric, (object?)slot.Stats.PeakToPeakAvg ?? DBNull.Value);
        cmd.Parameters.AddWithValue("snr_avg", NpgsqlDbType.Numeric, (object?)slot.Stats.SnrAvgDb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("bound_bolt", NpgsqlDbType.Varchar,
            (object?)slot.Stats.BoundBoltCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("started", NpgsqlDbType.TimestampTz, slot.StartedAt);
        cmd.Parameters.AddWithValue("finished", NpgsqlDbType.TimestampTz, slot.FinishedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateChannelHardwareStateAsync(
        long deviceId, ushort channelNo, ProbeScanVerdict verdict, DateTimeOffset at, CancellationToken ct)
    {
        var state = ProbeScanResultJudge.ToHardwareState(verdict);
        if (state is null) return;       // inconclusive 不更新

        const string sql = @"
            UPDATE channels
               SET hardware_state = @state,
                   last_seen_at = @at,
                   updated_at = now()
             WHERE device_id = @device_id
               AND channel_no = @ch
               AND deleted_at IS NULL";
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("state", NpgsqlDbType.Varchar, state);
        cmd.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, at);
        cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
        cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Smallint, (short)channelNo);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ProbeScanWindowStats> LoadWindowStatsAsync(
        long deviceId, ushort channelNo, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct)
    {
        // 测量行可能在 measurements（已绑 bolt）或 unbound_measurements（未绑）；
        // 用 UNION ALL 把两边合起来再聚合，避免漏数。
        const string sqlMeas = @"
            WITH meas AS (
                SELECT m.echo_time_ns, b.code AS bolt_code
                FROM measurements m
                JOIN channels c ON c.id = m.channel_id
                LEFT JOIN bolts b ON b.id = m.bolt_id
                WHERE c.device_id = @device_id
                  AND c.channel_no = @ch
                  AND m.ts >= @start AND m.ts <= @end
                UNION ALL
                SELECT echo_time_ns, NULL::varchar
                FROM unbound_measurements
                WHERE device_id = @device_id
                  AND channel_no = @ch
                  AND ts >= @start AND ts <= @end
            ),
            wave AS (
                SELECT wf.peak_to_peak, wf.snr_db
                FROM wave_features wf
                JOIN channels c ON c.id = wf.channel_id
                WHERE c.device_id = @device_id
                  AND c.channel_no = @ch
                  AND wf.ts >= @start AND wf.ts <= @end
            ),
            bound AS (
                SELECT MAX(b.code) AS code
                FROM bolts b
                JOIN channels c ON c.id = b.channel_id
                WHERE c.device_id = @device_id
                  AND c.channel_no = @ch
                  AND b.deleted_at IS NULL
            ),
            ch_meta AS (
                SELECT MAX(name) AS name
                FROM channels
                WHERE device_id = @device_id
                  AND channel_no = @ch
                  AND deleted_at IS NULL
            )
            SELECT
                (SELECT COUNT(*) FROM meas)::int AS samples,
                (SELECT COUNT(echo_time_ns) FROM meas)::int AS samples_with_echo,
                (SELECT ROUND(AVG(echo_time_ns)::numeric, 2) FROM meas) AS echo_avg,
                (SELECT ROUND(STDDEV_SAMP(echo_time_ns)::numeric, 3) FROM meas) AS echo_std,
                (SELECT ROUND(AVG(peak_to_peak)::numeric, 1) FROM wave) AS pp_avg,
                (SELECT ROUND(AVG(snr_db)::numeric, 2) FROM wave) AS snr_avg,
                (SELECT code FROM bound) AS bound_bolt_code,
                (SELECT name FROM ch_meta) AS channel_name";

        await using var cmd = _dataSource.CreateCommand(sqlMeas);
        cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
        cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Smallint, (short)channelNo);
        cmd.Parameters.AddWithValue("start", NpgsqlDbType.TimestampTz, windowStart);
        cmd.Parameters.AddWithValue("end", NpgsqlDbType.TimestampTz, windowEnd);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            return new ProbeScanWindowStats(0, 0, null, null, null, null, null, null);
        }
        return new ProbeScanWindowStats(
            Samples:          r.IsDBNull(0) ? 0 : r.GetInt32(0),
            SamplesWithEcho:  r.IsDBNull(1) ? 0 : r.GetInt32(1),
            EchoAvgNs:        r.IsDBNull(2) ? null : r.GetDecimal(2),
            EchoStdNs:        r.IsDBNull(3) ? null : r.GetDecimal(3),
            PeakToPeakAvg:    r.IsDBNull(4) ? null : r.GetDecimal(4),
            SnrAvgDb:         r.IsDBNull(5) ? null : r.GetDecimal(5),
            BoundBoltCode:    r.IsDBNull(6) ? null : r.GetString(6),
            ChannelName:      r.IsDBNull(7) ? null : r.GetString(7));
    }

    public async Task<ProbeScanRunView?> LoadRunAsync(long scanRunId, CancellationToken ct)
    {
        return await LoadRunInternalAsync(scanRunId: scanRunId, deviceId: null, ct);
    }

    public async Task<ProbeScanRunView?> LoadLatestRunAsync(long deviceId, CancellationToken ct)
    {
        return await LoadRunInternalAsync(scanRunId: null, deviceId: deviceId, ct);
    }

    private async Task<ProbeScanRunView?> LoadRunInternalAsync(long? scanRunId, long? deviceId, CancellationToken ct)
    {
        string sql = scanRunId.HasValue
            ? @"SELECT id, device_id, status, strategy, requested_channels,
                       settle_seconds, sample_seconds, thresholds_jsonb,
                       requested_at, started_at, finished_at, error_message, summary_jsonb
                FROM channel_probe_scan_runs WHERE id = @id"
            : @"SELECT id, device_id, status, strategy, requested_channels,
                       settle_seconds, sample_seconds, thresholds_jsonb,
                       requested_at, started_at, finished_at, error_message, summary_jsonb
                FROM channel_probe_scan_runs WHERE device_id = @id
                ORDER BY requested_at DESC LIMIT 1";

        long runId;
        long devId;
        string status;
        string strategy;
        IReadOnlyList<ushort> channels;
        int settle, sample;
        ProbeScanThresholds thresholds;
        DateTimeOffset requestedAt;
        DateTimeOffset? startedAt, finishedAt;
        string? errorMessage;
        ProbeScanSummary? summary;

        await using (var cmd = _dataSource.CreateCommand(sql))
        {
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, scanRunId ?? deviceId!.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;

            runId = r.GetInt64(0);
            devId = r.GetInt64(1);
            status = r.GetString(2);
            strategy = r.GetString(3);
            var arr = (short[])r.GetValue(4);
            channels = arr.Select(s => (ushort)s).ToArray();
            settle = r.GetInt16(5);
            sample = r.GetInt16(6);
            var thresholdsJson = r.GetString(7);
            // 兜底取 ProbeScanOptions 的默认值，别在这里抄一份字面量：
            // 抄过的那份不会跟着选项默认值一起改，回溯老记录时会静默给出另一套阈值
            thresholds = JsonSerializer.Deserialize<ProbeScanThresholds>(thresholdsJson)
                         ?? ProbeScanThresholds.FromOptions(new ProbeScanOptions());
            requestedAt = r.GetFieldValue<DateTimeOffset>(8);
            startedAt = r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9);
            finishedAt = r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10);
            errorMessage = r.IsDBNull(11) ? null : r.GetString(11);
            summary = r.IsDBNull(12) ? null
                : JsonSerializer.Deserialize<ProbeScanSummary>(r.GetString(12));
        }

        // 加载已写入的 results；channel.name LEFT JOIN 取当前值，
        // 不锁定到扫描时刻——改名后历史扫描也能看到最新名（运维更直观）
        var results = new List<ProbeScanChannelResultView>();
        const string resultsSql = @"
            SELECT r.channel_no, c.name, r.verdict, r.samples, r.samples_with_echo,
                   r.echo_rate_pct, r.echo_avg_ns, r.echo_std_ns,
                   r.peak_to_peak_avg, r.snr_avg_db, r.bound_bolt_code,
                   r.started_at, r.finished_at
            FROM channel_probe_scan_results r
            LEFT JOIN channel_probe_scan_runs run ON run.id = r.scan_run_id
            LEFT JOIN channels c
                   ON c.device_id = run.device_id
                  AND c.channel_no = r.channel_no
                  AND c.deleted_at IS NULL
            WHERE r.scan_run_id = @run
            ORDER BY r.channel_no";
        await using (var cmd = _dataSource.CreateCommand(resultsSql))
        {
            cmd.Parameters.AddWithValue("run", NpgsqlDbType.Bigint, runId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                results.Add(new ProbeScanChannelResultView(
                    ChannelNo:        r.GetInt16(0),
                    Name:             r.IsDBNull(1) ? null : r.GetString(1),
                    Verdict:          r.GetString(2),
                    Samples:          r.GetInt32(3),
                    SamplesWithEcho:  r.GetInt32(4),
                    EchoRatePct:      r.IsDBNull(5) ? null : r.GetDecimal(5),
                    EchoAvgNs:        r.IsDBNull(6) ? null : r.GetDecimal(6),
                    EchoStdNs:        r.IsDBNull(7) ? null : r.GetDecimal(7),
                    PeakToPeakAvg:    r.IsDBNull(8) ? null : r.GetDecimal(8),
                    SnrAvgDb:         r.IsDBNull(9) ? null : r.GetDecimal(9),
                    BoundBoltCode:    r.IsDBNull(10) ? null : r.GetString(10),
                    StartedAt:        r.GetFieldValue<DateTimeOffset>(11),
                    FinishedAt:       r.GetFieldValue<DateTimeOffset>(12)));
            }
        }

        return new ProbeScanRunView(
            ScanRunId: runId,
            DeviceId: devId,
            Status: status,
            Strategy: strategy,
            RequestedChannels: channels,
            SettleSeconds: settle,
            SampleSeconds: sample,
            Thresholds: thresholds,
            RequestedAt: requestedAt,
            StartedAt: startedAt,
            FinishedAt: finishedAt,
            ErrorMessage: errorMessage,
            Summary: summary,
            Results: results);
    }

    public async Task<int> CleanStaleRunsAsync(CancellationToken ct)
    {
        const string sql = @"
            UPDATE channel_probe_scan_runs
               SET status = 'failed',
                   finished_at = COALESCE(finished_at, now()),
                   error_message = COALESCE(error_message, 'API restarted while scan was running')
             WHERE status IN ('pending','running')";
        await using var cmd = _dataSource.CreateCommand(sql);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ushort>> LoadEnabledChannelsAsync(long deviceId, CancellationToken ct)
    {
        const string sql = @"
            SELECT channel_no
            FROM channels
            WHERE device_id = @device_id
              AND sampling_enabled = true
              AND deleted_at IS NULL
            ORDER BY channel_no";
        var list = new List<ushort>();
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add((ushort)r.GetInt16(0));
        return list;
    }
}
