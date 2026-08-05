using System.Collections.Concurrent;

namespace BoltPreload.Application.Diagnostics;

/// <summary>
/// 后台服务存活登记表：各周期性服务每轮打一次心跳，<c>/api/health/ready</c> 据此判断"监测是否真的还在跑"。
/// <para>
/// 为什么需要：宿主配了 <c>BackgroundServiceExceptionBehavior.Ignore</c>（一个后台服务崩不拖停整机，
/// 对监测系统是对的取舍），但代价是<b>服务死了进程还活着</b> —— 此前 <c>/api/health</c> 依然返回
/// <c>status=ok</c>，运维探针一路绿灯。最危险的是 SignalHealthService：它一旦停了就不再产生
/// signal_lost 告警，探头脱落、接头松动从此无人报。
/// </para>
/// <para>
/// 判定口径：只对<b>已经打过至少一次心跳</b>的服务判超时（按配置关闭、未注册的服务不会被误判为死），
/// 超时阈值取该服务正常间隔的 3 倍，避免一次慢轮询就报警。
/// </para>
/// </summary>
public sealed class BackgroundServiceHealth
{
    private readonly ConcurrentDictionary<string, Heartbeat> _beats = new(StringComparer.Ordinal);

    private readonly record struct Heartbeat(TimeSpan Tolerance, DateTimeOffset At);

    /// <param name="name">服务名，出现在 /api/health/ready 的 backgroundServices 里。</param>
    /// <param name="normalInterval">该服务正常的轮询间隔；超过 3 倍即判为失联。</param>
    public void Beat(string name, TimeSpan normalInterval)
        => _beats[name] = new Heartbeat(normalInterval, DateTimeOffset.UtcNow);

    public IReadOnlyList<BackgroundServiceState> Snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        return _beats
            .Select(kv =>
            {
                var age = (now - kv.Value.At).TotalSeconds;
                return new BackgroundServiceState(
                    kv.Key,
                    Math.Round(age, 1),
                    Stale: age > kv.Value.Tolerance.TotalSeconds * 3);
            })
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToArray();
    }
}

/// <param name="Name">服务名。</param>
/// <param name="LastBeatSecondsAgo">距上次心跳的秒数。</param>
/// <param name="Stale">是否已超过 3 倍正常间隔未心跳（视为失联）。</param>
public sealed record BackgroundServiceState(string Name, double LastBeatSecondsAgo, bool Stale);
