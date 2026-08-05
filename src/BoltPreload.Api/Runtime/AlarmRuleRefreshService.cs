using BoltPreload.Application.Alarm;

namespace BoltPreload.Api.Runtime;

/// <summary>
/// 告警规则快照的定期刷新（30 秒一轮）。批准/驳回端点会显式调 RefreshAsync 让改动立即生效，
/// 这里兜的是三件事：启动时的首次加载、运维绕过 API 直接改表、以及新建档螺栓的 bolt→group
/// 映射补齐。刷新失败保留旧快照下一轮再试——评估器拿旧阈值继续判，不会停。
/// </summary>
public sealed class AlarmRuleRefreshService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IAlarmRuleProvider _provider;
    private readonly ILogger<AlarmRuleRefreshService> _logger;

    public AlarmRuleRefreshService(IAlarmRuleProvider provider, ILogger<AlarmRuleRefreshService> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _provider.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "告警规则快照刷新失败，沿用旧快照，30 秒后重试");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
