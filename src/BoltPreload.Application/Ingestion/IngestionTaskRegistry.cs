namespace BoltPreload.Application.Ingestion;

/// <summary>
/// 采集任务登记表：IngestionService 的 supervisor 每起/停一个设备的消费任务就在这里登记。
/// <para>
/// 存在的理由是让设备管理端点能确定性地等到"任务真停了、连接器真释放了"再做后续清理
/// （闭合告警、失效缓存）——摘字典只是断了对账来源，任务要到 supervisor 下一拍才取消，
/// 端点不等这个信号就返回，"禁用成功"和 TCP 实际断开之间会有几秒的说谎窗口。
/// </para>
/// </summary>
public sealed class IngestionTaskRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<long, TaskCompletionSource> _stopSignals = new();

    /// <summary>该设备当前是否有消费任务在跑。</summary>
    public bool IsRunning(long deviceId)
    {
        lock (_gate) return _stopSignals.ContainsKey(deviceId);
    }

    internal void MarkStarted(long deviceId)
    {
        lock (_gate)
            _stopSignals[deviceId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal void MarkStopped(long deviceId)
    {
        lock (_gate)
        {
            if (_stopSignals.Remove(deviceId, out var tcs))
                tcs.TrySetResult();
        }
    }

    /// <summary>
    /// 等该设备的消费任务退出（含连接器释放）。设备本就没在跑返回 true；超时返回 false，
    /// 调用方自行决定是继续清理（连接器由停机路径兜底释放）还是报错。
    /// </summary>
    public async Task<bool> WaitStoppedAsync(long deviceId, TimeSpan timeout, CancellationToken ct = default)
    {
        TaskCompletionSource? tcs;
        lock (_gate)
        {
            if (!_stopSignals.TryGetValue(deviceId, out tcs))
                return true;
        }
        try
        {
            await tcs.Task.WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
