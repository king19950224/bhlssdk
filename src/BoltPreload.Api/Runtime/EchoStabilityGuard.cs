using BoltPreload.Calibration;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Runtime;

/// <summary>
/// 采样稳定性护栏（calibration-hardening-design §2）：判断一个通道最近窗口内的声时
/// 是否稳定到可以当标定输入。
/// <para>
/// 背景：标定/采点/重设基线取的都是"窗口平均声时"，接口原先只检查样本数量，不检查这些
/// 样本是否属于同一受力稳态。现场"拧完立刻点"时窗口混入加载前/加载过程的数据，平均值
/// 落在两个扭矩档之间，K_L 错一个数量级而系统毫无察觉——08-12 真机实测 CH1/CH2/CH8
/// 全中此坑。本类把"窗口干不干净"变成一条可拒绝、可解释的硬判据。
/// </para>
/// <para>
/// 两条判据同时达标才算稳（阈值见 <see cref="CalibrationThresholds"/>，含论证）：
/// stddev 抓信号抖动，前后半窗均值差抓单调趋势。松弛中的窗口 stddev 能溜过第一条
/// （实测 0.53ns），只有第二条拦得住。
/// </para>
/// <para>
/// 数据源按 channel_id 同时查 measurements 与 unbound_measurements：护栏问的是
/// "这路通道的信号稳不稳"，与螺栓绑定状态无关（重设基线允许发生在未建档通道上）。
/// </para>
/// </summary>
public static class EchoStabilityGuard
{
    /// <summary>
    /// 一次窗口稳定性测量的结果。均值/温度顺带带回，调用方（如带载检查）不用再查一遍。
    /// </summary>
    /// <param name="SampleCount">窗口内有效声时样本数。</param>
    /// <param name="AvgEchoNs">窗口平均声时（ns），无样本为 null。</param>
    /// <param name="AvgTempC">窗口平均温度（°C），无样本为 null。</param>
    /// <param name="StddevNs">窗口内声时标准差（ns）。</param>
    /// <param name="FirstHalfAvgNs">前半窗平均声时。</param>
    /// <param name="SecondHalfAvgNs">后半窗平均声时。</param>
    /// <param name="WindowSeconds">实际使用的窗口长度（秒）。</param>
    public sealed record Snapshot(
        long SampleCount,
        decimal? AvgEchoNs,
        decimal? AvgTempC,
        decimal? StddevNs,
        decimal? FirstHalfAvgNs,
        decimal? SecondHalfAvgNs,
        int WindowSeconds)
    {
        /// <summary>后半窗均值 − 前半窗均值（ns）。任一半窗无样本为 null。</summary>
        public decimal? HalfWindowDriftNs =>
            FirstHalfAvgNs is { } a && SecondHalfAvgNs is { } b ? b - a : null;

        /// <summary>
        /// 是否稳定：两条判据同时达标，且两个半窗都有样本（半窗断流说明数据分布畸形，
        /// 均值差算不出来就不能宣称"稳"）。样本数量下限由各调用方按自己的 minSamples 把守，
        /// 这里只管"稳不稳"。
        /// </summary>
        public bool Stable =>
            StddevNs is { } sd && sd <= CalibrationThresholds.EchoStddevMaxNs
            && HalfWindowDriftNs is { } drift && Math.Abs(drift) <= CalibrationThresholds.EchoHalfWindowDriftMaxNs;

        /// <summary>给 400 响应用的诊断载荷（前端直接展示，现场知道差多少、还要等）。</summary>
        public object ToDiagnostics() => new
        {
            stable = Stable,
            sampleCount = SampleCount,
            stddevNs = StddevNs,
            halfWindowDriftNs = HalfWindowDriftNs,
            stddevMaxNs = CalibrationThresholds.EchoStddevMaxNs,
            halfWindowDriftMaxNs = CalibrationThresholds.EchoHalfWindowDriftMaxNs,
            windowSeconds = WindowSeconds,
        };
    }

    /// <summary>
    /// 测量指定通道最近 <paramref name="windowSeconds"/> 秒的声时稳定性。
    /// 一条 SQL 算完全部统计量，不额外多跑查询。
    /// </summary>
    public static async Task<Snapshot> MeasureAsync(
        NpgsqlDataSource ds, long channelId, int windowSeconds, CancellationToken ct)
    {
        var seconds = Math.Clamp(windowSeconds, 5, 600);
        // FILTER 的分界点用 now()-半窗：前半窗 [now-w, now-w/2)，后半窗 [now-w/2, now]
        const string sql = @"
            WITH samples AS (
                SELECT ts, echo_time_ns, sp_temp_c FROM measurements
                WHERE channel_id = @cid AND echo_time_ns IS NOT NULL
                  AND ts > now() - make_interval(secs => @w)
                UNION ALL
                SELECT ts, echo_time_ns, sp_temp_c FROM unbound_measurements
                WHERE channel_id = @cid AND echo_time_ns IS NOT NULL
                  AND ts > now() - make_interval(secs => @w)
            )
            SELECT COUNT(*),
                   ROUND(AVG(echo_time_ns), 6),
                   ROUND(AVG(sp_temp_c), 6),
                   ROUND(STDDEV_POP(echo_time_ns), 3),
                   ROUND(AVG(echo_time_ns) FILTER (WHERE ts <  now() - make_interval(secs => @half)), 6),
                   ROUND(AVG(echo_time_ns) FILTER (WHERE ts >= now() - make_interval(secs => @half)), 6)
            FROM samples";

        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
        cmd.Parameters.AddWithValue("w", NpgsqlDbType.Double, (double)seconds);
        cmd.Parameters.AddWithValue("half", NpgsqlDbType.Double, seconds / 2.0);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new Snapshot(
            SampleCount: r.GetInt64(0),
            AvgEchoNs: r.IsDBNull(1) ? null : r.GetDecimal(1),
            AvgTempC: r.IsDBNull(2) ? null : r.GetDecimal(2),
            StddevNs: r.IsDBNull(3) ? null : r.GetDecimal(3),
            FirstHalfAvgNs: r.IsDBNull(4) ? null : r.GetDecimal(4),
            SecondHalfAvgNs: r.IsDBNull(5) ? null : r.GetDecimal(5),
            WindowSeconds: seconds);
    }
}
