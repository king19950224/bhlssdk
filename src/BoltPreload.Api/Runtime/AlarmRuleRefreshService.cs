using BoltPreload.Application.Alarm;
using BoltPreload.Application.Diagnostics;

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
    private readonly BackgroundServiceHealth _health;
    private readonly ILogger<AlarmRuleRefreshService> _logger;
    private int _consecutiveFailures;

    public AlarmRuleRefreshService(
        IAlarmRuleProvider provider,
        BackgroundServiceHealth health,
        ILogger<AlarmRuleRefreshService> logger)
    {
        _provider = provider;
        _health = health;
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
                _consecutiveFailures = 0;
                // 心跳只在刷新成功时打。这里的"活着"不等于线程还在转，而是"评估器手上的阈值是新的"：
                // 首次刷新若失败，快照为空 → Resolve 回落 appsettings 出厂兜底，
                // 现场经两步异人复核批准的阈值（可能比出厂值更严）就此不生效，而此前只有一行 Warning。
                // 按 plan §8.14"算法降级必须显式标记，绝不能静默退化"，让 /api/health/ready 直接看得见。
                _health.Beat("alarm-rule-refresh", Interval);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                // 偶发一次是 DB 抖动，连续失败意味着阈值已经停在旧值/出厂值上，级别要能引起注意
                if (_consecutiveFailures == 1)
                    _logger.LogWarning(ex, "告警规则快照刷新失败，沿用旧快照，30 秒后重试");
                else
                    _logger.LogError(ex, "告警规则快照已连续 {Count} 轮刷新失败（约 {Minutes} 分钟），"
                        + "评估器仍在用旧快照；若进程启动后一次都没成功过，用的是 appsettings 出厂阈值而非库内已批准阈值",
                        _consecutiveFailures, _consecutiveFailures * Interval.TotalMinutes);
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
