using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoltPreload.Application.Ingestion;

/// <summary>
/// 周期性把 WAL 中入库失败的测量重放补写回 DB。
/// <para>
/// 重放统一走 <see cref="IMeasurementSink.WriteAsync(Fx.Models.FxDecodedEnvelope, bool, CancellationToken)"/>
/// 且 <c>evaluateAlarms=false</c>：补的是历史数据，不能再触发告警状态机。
/// </para>
/// </summary>
public sealed class WalReplayService : BackgroundService
{
    private readonly IMeasurementWal _wal;
    private readonly IMeasurementSink _sink;
    private readonly ILogger<WalReplayService> _logger;
    private readonly TimeSpan _interval;

    public WalReplayService(
        IMeasurementWal wal,
        IMeasurementSink sink,
        IOptions<MeasurementWalOptions> options,
        ILogger<WalReplayService> logger)
    {
        _wal = wal;
        _sink = sink;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.ReplayIntervalSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_wal.Enabled)
        {
            _logger.LogInformation("WAL 未启用，重放服务空转退出");
            return;
        }

        _logger.LogInformation("WAL 重放服务启动 interval={Interval}s pending={Pending}",
            _interval.TotalSeconds, _wal.PendingCount);

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (_wal.PendingCount == 0)
                    continue;

                var replayed = await _wal.ReplayAsync(
                    (env, ct) => _sink.WriteAsync(env, evaluateAlarms: false, ct),
                    stoppingToken);

                if (replayed > 0)
                    _logger.LogInformation("WAL 重放补写 {Count} 条，剩余 {Pending}", replayed, _wal.PendingCount);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WAL 重放轮次异常，下一轮再试");
            }
        }

        _logger.LogInformation("WAL 重放服务停止");
    }
}
