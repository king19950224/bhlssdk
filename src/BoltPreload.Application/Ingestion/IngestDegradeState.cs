namespace BoltPreload.Application.Ingestion;

/// <summary>
/// 进程级采集降级状态（纯内存）。
/// <para>
/// DB 故障窗口内 warn/on-delay 级预紧力告警不评估（评估挂在 measurement INSERT 成功之后，
/// 见 PostgresMeasurementSink），此期间监测结论不可信。但告警表本身在 DB 里——库都挂了，
/// "系统降级"这条告警自己就写不进去，走告警管线是死路。所以降级态放进程内存：
/// /api/health/ready 据此返回 degraded（前端全局横幅、运维探针都看这里），
/// 恢复后由 sink 在下一次成功写库时补一条 system_degraded 审计留痕（audit_log.user_id 允许 NULL）。
/// 彻底修（告警评估脱离 INSERT 依赖 + 独立告警 WAL）排交付后维护版。
/// </para>
/// <para>多设备消费循环并发调用，全部走 Interlocked / CAS；恢复窗口单槽用小锁。</para>
/// </summary>
public sealed class IngestDegradeState
{
    /// <summary>连续入库失败达到该值进入降级态。单条坏数据（超长波形、字段溢出）不该拉响全站降级，
    /// 连续三条失败基本可断定是 DB / 磁盘级故障而非数据个例。</summary>
    private const int EnterThreshold = 3;

    /// <summary>WAL 丢弃后降级理由的粘滞窗口：丢弃是瞬时事件，但它意味着这段时间的数据已不完整，
    /// 不能丢完一条日志就当没发生过。</summary>
    private static readonly TimeSpan WalDropStickyWindow = TimeSpan.FromMinutes(10);

    private long _consecutiveDbFailures;
    private long _totalDbFailures;
    private long _degradedSinceTicks;   // 0 = 未降级
    private long _failuresAtEnter;      // 进入降级时的累计失败数，用于算窗口内失败条数
    private long _walDroppedAtEnter;
    private long _walDroppedTotal;
    private long _walLastDropTicks;

    private readonly object _windowLock = new();
    private RecoveredWindow? _pendingWindow;

    /// <summary>一次已结束的降级窗口，供恢复后补审计。</summary>
    public sealed record RecoveredWindow(DateTimeOffset From, DateTimeOffset To, long FailureCount, long WalDropped);

    /// <summary>当前是否处于 DB 写入降级态。</summary>
    public bool DbWriteDegraded => Interlocked.Read(ref _degradedSinceTicks) != 0;

    /// <summary>进入降级态的时刻（未降级为 null）。</summary>
    public DateTimeOffset? DegradedSinceUtc
    {
        get
        {
            var t = Interlocked.Read(ref _degradedSinceTicks);
            return t == 0 ? null : new DateTimeOffset(t, TimeSpan.Zero);
        }
    }

    /// <summary>当前连续入库失败次数（成功一次即清零）。</summary>
    public long ConsecutiveDbWriteFailures => Interlocked.Read(ref _consecutiveDbFailures);

    /// <summary>进程启动以来累计入库失败次数。</summary>
    public long TotalDbWriteFailures => Interlocked.Read(ref _totalDbFailures);

    /// <summary>WAL 容量满 drop-oldest 累计丢弃条数（这些测量已永久丢失）。</summary>
    public long WalDroppedTotal => Interlocked.Read(ref _walDroppedTotal);

    /// <summary>最近一次 WAL 丢弃时刻（从未丢弃为 null）。</summary>
    public DateTimeOffset? WalLastDropAtUtc
    {
        get
        {
            var t = Interlocked.Read(ref _walLastDropTicks);
            return t == 0 ? null : new DateTimeOffset(t, TimeSpan.Zero);
        }
    }

    /// <summary>近 10 分钟内发生过 WAL 丢弃（readiness 的降级理由之一）。</summary>
    public bool WalDroppedRecently
    {
        get
        {
            var t = Interlocked.Read(ref _walLastDropTicks);
            return t != 0 && DateTimeOffset.UtcNow - new DateTimeOffset(t, TimeSpan.Zero) <= WalDropStickyWindow;
        }
    }

    /// <summary>有待补写审计的已恢复窗口（sink 在写库成功路径上做廉价检查用）。</summary>
    public bool HasPendingRecoveredWindow
    {
        get { lock (_windowLock) return _pendingWindow is not null; }
    }

    /// <summary>
    /// 记一次入库失败。返回 true 表示本次调用触发了 未降级→降级 的转移（调用方记 Critical 日志，只记一次）。
    /// </summary>
    public bool ReportDbWriteFailure()
    {
        Interlocked.Increment(ref _totalDbFailures);
        var consecutive = Interlocked.Increment(ref _consecutiveDbFailures);
        if (consecutive < EnterThreshold)
            return false;

        var now = DateTimeOffset.UtcNow.UtcTicks;
        if (Interlocked.CompareExchange(ref _degradedSinceTicks, now, 0) != 0)
            return false; // 已在降级态

        // 窗口起点的基线：进入前的失败与 WAL 丢弃不算进本窗口
        Interlocked.Exchange(ref _failuresAtEnter, Interlocked.Read(ref _totalDbFailures) - EnterThreshold);
        Interlocked.Exchange(ref _walDroppedAtEnter, Interlocked.Read(ref _walDroppedTotal));
        return true;
    }

    /// <summary>
    /// 记一次入库成功。若此前处于降级态则结束窗口并返回它（同时排入待审计单槽）；否则返回 null。
    /// </summary>
    public RecoveredWindow? ReportDbWriteSuccess()
    {
        Interlocked.Exchange(ref _consecutiveDbFailures, 0);

        var since = Interlocked.Exchange(ref _degradedSinceTicks, 0);
        if (since == 0)
            return null; // 常态路径：一次原子交换，无锁无分配

        var window = new RecoveredWindow(
            From: new DateTimeOffset(since, TimeSpan.Zero),
            To: DateTimeOffset.UtcNow,
            FailureCount: Interlocked.Read(ref _totalDbFailures) - Interlocked.Read(ref _failuresAtEnter),
            WalDropped: Interlocked.Read(ref _walDroppedTotal) - Interlocked.Read(ref _walDroppedAtEnter));

        lock (_windowLock)
        {
            // 单槽：极端情况下（审计还没补写又发生第二轮降级恢复）合并成一个大窗口，不丢事实
            _pendingWindow = _pendingWindow is null
                ? window
                : new RecoveredWindow(
                    _pendingWindow.From, window.To,
                    _pendingWindow.FailureCount + window.FailureCount,
                    _pendingWindow.WalDropped + window.WalDropped);
        }
        return window;
    }

    /// <summary>取走待审计的恢复窗口（取走即清槽）。没有则返回 false。</summary>
    public bool TryTakeRecoveredWindow(out RecoveredWindow window)
    {
        lock (_windowLock)
        {
            if (_pendingWindow is null)
            {
                window = null!;
                return false;
            }
            window = _pendingWindow;
            _pendingWindow = null;
            return true;
        }
    }

    /// <summary>WAL 容量满丢弃了 <paramref name="count"/> 条最旧测量（永久丢失）。</summary>
    public void ReportWalDropped(long count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _walDroppedTotal, count);
        Interlocked.Exchange(ref _walLastDropTicks, DateTimeOffset.UtcNow.UtcTicks);
    }
}
