using BoltPreload.Application.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Runtime;

public sealed class UnboundRetentionOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>未绑定测量的保留天数。绑定建档时会回填该窗口内的暂存数据，超窗的丢弃。</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>清理周期（分钟）。</summary>
    public int SweepIntervalMinutes { get; set; } = 60;

    /// <summary>单批删除行数上限（避免大事务长锁）。</summary>
    public int BatchSize { get; set; } = 5000;
}

/// <summary>
/// unbound_measurements 保留期清理。
/// <para>
/// 背景：全通道轮询下，插着探头但一直未建档的通道会 7×24 往 unbound_measurements 落有回波数据
/// （无回波帧在 Sink 已挡，见 PostgresMeasurementSink），该表此前没有任何清理任务，属无限膨胀点。
/// 保留 N 天（默认 7）：远超"接探头→建档"的现场操作间隔，建档回填价值不受影响；
/// 超窗数据按批删除（LIMIT 子查询，避免一次大 DELETE 长时间锁表/膨胀 WAL）。
/// </para>
/// </summary>
public sealed class UnboundRetentionService : BackgroundService
{
    private readonly NpgsqlDataSource _ds;
    private readonly UnboundRetentionOptions _opt;
    private readonly BackgroundServiceHealth _health;
    private readonly ILogger<UnboundRetentionService> _logger;

    public UnboundRetentionService(
        NpgsqlDataSource ds,
        IOptions<UnboundRetentionOptions> opt,
        BackgroundServiceHealth health,
        ILogger<UnboundRetentionService> logger)
    {
        _ds = ds;
        _opt = opt.Value;
        _health = health;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled)
        {
            _logger.LogInformation("未绑定测量保留清理已禁用（Ingestion:UnboundRetention:Enabled=false）");
            return;
        }
        var interval = TimeSpan.FromMinutes(Math.Max(5, _opt.SweepIntervalMinutes));
        _logger.LogInformation("未绑定测量保留清理启动：保留 {Days} 天，每 {Interval} 清一轮",
            _opt.RetentionDays, interval);

        using var timer = new PeriodicTimer(interval);
        while (true)
        {
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) { return; }

            _health.Beat("unbound-retention", interval);
            try
            {
                var deleted = await SweepAsync(stoppingToken);
                if (deleted > 0)
                    _logger.LogInformation("未绑定测量清理：删除 {Count} 行（保留 {Days} 天）", deleted, _opt.RetentionDays);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogError(ex, "未绑定测量清理异常（下轮重试）"); }
        }
    }

    // internal 供测试直调单轮清理
    internal async Task<long> SweepAsync(CancellationToken ct)
    {
        var days = Math.Max(1, _opt.RetentionDays);
        var batch = Math.Clamp(_opt.BatchSize, 100, 50_000);
        long total = 0;
        while (!ct.IsCancellationRequested)
        {
            await using var cmd = _ds.CreateCommand(@"
                DELETE FROM unbound_measurements
                WHERE id IN (
                    SELECT id FROM unbound_measurements
                    WHERE ts < now() - make_interval(days => @days)
                    LIMIT @batch)");
            cmd.Parameters.AddWithValue("days", NpgsqlDbType.Integer, days);
            cmd.Parameters.AddWithValue("batch", NpgsqlDbType.Integer, batch);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            total += n;
            if (n < batch) break;
        }
        return total;
    }
}
