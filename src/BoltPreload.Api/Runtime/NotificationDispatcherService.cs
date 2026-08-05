using BoltPreload.Application.Diagnostics;
using BoltPreload.Application.Notifications;
using BoltPreload.Infrastructure.Notifications;

namespace BoltPreload.Api.Runtime;

/// <summary>
/// 发件箱派发的宿主壳：5 秒一轮调 <see cref="NotificationDispatcher.ProcessDueAsync"/>，
/// 心跳登记进 BackgroundServiceHealth（它停了通知就停了，/api/health/ready 必须看得见）。
/// 顺带每 6 轮（30 秒）刷新一次通知路由快照——省掉一个只为刷新活着的独立服务；
/// 配置端点变更后另有显式刷新，这里只兜运维直接改表和新建档螺栓的映射补齐。
/// 单轮失败只记日志不退出：DB 短暂不可用时发件箱行原地等着，恢复后自然续跑。
/// </summary>
public sealed class NotificationDispatcherService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private const int RouterRefreshEveryTicks = 6;

    private readonly NotificationDispatcher _dispatcher;
    private readonly INotificationRouter _router;
    private readonly BackgroundServiceHealth _health;
    private readonly ILogger<NotificationDispatcherService> _logger;

    public NotificationDispatcherService(
        NotificationDispatcher dispatcher,
        INotificationRouter router,
        BackgroundServiceHealth health,
        ILogger<NotificationDispatcherService> logger)
    {
        _dispatcher = dispatcher;
        _router = router;
        _health = health;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = 0;
        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            _health.Beat("notification-dispatcher", Interval);
            try
            {
                if (tick % RouterRefreshEveryTicks == 0)
                    await _router.RefreshAsync(stoppingToken);
                await _dispatcher.ProcessDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "通知派发轮次失败，发件箱行保持 pending，下一轮重试");
            }

            tick++;
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
