using BoltPreload.Application.Ingestion;
using BoltPreload.Infrastructure.Ingestion;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Runtime;

/// <summary>
/// 波形保留清理服务。
/// <para>
/// 波形是辅助诊断数据，<c>data/waves</c> 与 <c>wave_features</c> 无 retention 会随采集无限增长
/// （撑爆工业控制器系统盘 / 拖慢查询）。本服务周期删除早于 <c>RetentionDays</c> 的历史波形文件与
/// <c>wave_features</c> 行，并清扫崩溃残留的 <c>.tmp</c>。
/// </para>
/// <para>
/// 关键：<b>仍在用的参考波形（reference_waves 引用）即便比 RetentionDays 更老也必须保留</b>——
/// 文件按保护集合跳过，<c>wave_features</c> 行按 <c>NOT IN reference_waves</c> 排除，避免清掉 /compare 的基线。
/// </para>
/// <para>启动即跑一轮（含 .tmp 清扫），随后每 <c>CleanupIntervalHours</c> 跑一次。</para>
/// </summary>
public sealed class WaveRetentionService : BackgroundService
{
    private static readonly IReadOnlySet<string> EmptyProtected = new HashSet<string>();

    private readonly IWaveStore _store;
    private readonly NpgsqlDataSource _ds;
    private readonly LocalFileWaveStoreOptions _opt;
    private readonly ILogger<WaveRetentionService> _logger;

    public WaveRetentionService(
        IWaveStore store,
        NpgsqlDataSource ds,
        IOptions<LocalFileWaveStoreOptions> opt,
        ILogger<WaveRetentionService> logger)
    {
        _store = store;
        _ds = ds;
        _opt = opt.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = Math.Max(1, _opt.CleanupIntervalHours);
        _logger.LogInformation(
            "WaveRetention 启动：每 {Hours}h 清理早于 {Days} 天的历史波形并清扫残留 .tmp",
            intervalHours, _opt.RetentionDays);

        // 启动即跑一轮（清扫 .tmp + 删过期日目录）
        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(intervalHours));
        while (true)
        {
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) { return; }
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            if (_opt.RetentionDays <= 0)
            {
                // 关闭"按天清理"：cutoff=MinValue 不删任何归档文件，但仍清扫 .tmp；不动数据库。
                await _store.PurgeOlderThanAsync(DateTimeOffset.MinValue, EmptyProtected, ct);
                return;
            }

            var cutoff = DateTimeOffset.UtcNow.AddDays(-_opt.RetentionDays);
            // 参考波形文件可能比 cutoff 更老但仍用于 /compare，先取保护集合再清，确保不被误删。
            var protectedPaths = await LoadProtectedPathsAsync(ct);
            await _store.PurgeOlderThanAsync(cutoff, protectedPaths, ct);
            await PurgeWaveFeaturesAsync(cutoff, ct);
        }
        catch (OperationCanceledException) { /* 停机，正常退出 */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WaveRetention 一轮清理异常");
        }
    }

    /// <summary>取所有被 reference_waves 引用的波形文件相对路径——这些是 /compare 的基线，不能清。</summary>
    private async Task<IReadOnlySet<string>> LoadProtectedPathsAsync(CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = _ds.CreateCommand(@"
            SELECT DISTINCT wf.wave_blob_url
            FROM reference_waves rw
            JOIN wave_features wf ON wf.id = rw.wave_feature_id");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (!r.IsDBNull(0)) set.Add(r.GetString(0));
        return set;
    }

    /// <summary>删除早于 cutoff 的 wave_features 行，但保留被 reference_waves 引用的（FK 也会拦，这里显式排除）。</summary>
    private async Task PurgeWaveFeaturesAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(@"
            DELETE FROM wave_features
            WHERE ts < @cutoff
              AND id NOT IN (SELECT wave_feature_id FROM reference_waves)");
        cmd.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, cutoff);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows > 0)
            _logger.LogInformation("波形特征清理：删除 {Rows} 行 wave_features（cutoff={Cutoff:yyyy-MM-dd}）", rows, cutoff.UtcDateTime);
    }
}
