using System.Collections.Concurrent;

namespace BoltPreload.Application.Devices.ProbeScan;

/// <summary>
/// 进程内单例，记录正在跑的通道扫描，防止：
/// 1. 同一设备并发两次扫描；
/// 2. Watchdog 在扫描期把工作模式强制拉回 MultiMeas；
/// 3. 其他写 WorkMode 的 Diagnostic endpoint 与扫描器抢通道。
/// </summary>
public sealed class ProbeScanState
{
    private readonly ConcurrentDictionary<long, ScanRunHandle> _running = new();

    /// <summary>尝试占位。已存在返回 null（API 应回 409）。</summary>
    public ScanRunHandle? TryBegin(long deviceId, long scanRunId)
    {
        var cts = new CancellationTokenSource();
        var handle = new ScanRunHandle(deviceId, scanRunId, cts);
        return _running.TryAdd(deviceId, handle) ? handle : null;
    }

    /// <summary>查询该设备是否正在扫描（Watchdog / 其他写 WorkMode 接口必须先检查）。</summary>
    public bool IsRunning(long deviceId) => _running.ContainsKey(deviceId);

    /// <summary>取出当前扫描句柄（用于 abort）。</summary>
    public ScanRunHandle? Get(long deviceId)
        => _running.TryGetValue(deviceId, out var h) ? h : null;

    /// <summary>扫描结束后释放占位（无论 completed/failed/aborted）。</summary>
    public void End(long deviceId)
    {
        if (_running.TryRemove(deviceId, out var handle))
            handle.Cts.Dispose();
    }

    /// <summary>用户/系统请求中止当前扫描；返回是否真的存在该扫描。</summary>
    public bool TryAbort(long deviceId, long scanRunId)
    {
        if (!_running.TryGetValue(deviceId, out var handle)) return false;
        if (handle.ScanRunId != scanRunId) return false;
        try { handle.Cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    /// <summary>当前所有运行中的扫描快照（仅诊断用）。</summary>
    public IReadOnlyCollection<ScanRunHandle> Snapshot() => _running.Values.ToList();
}

public sealed class ScanRunHandle
{
    public long DeviceId { get; }
    public long ScanRunId { get; }
    public CancellationTokenSource Cts { get; }
    public DateTimeOffset StartedAt { get; }

    public ScanRunHandle(long deviceId, long scanRunId, CancellationTokenSource cts)
    {
        DeviceId = deviceId;
        ScanRunId = scanRunId;
        Cts = cts;
        StartedAt = DateTimeOffset.UtcNow;
    }
}
