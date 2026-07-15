namespace BoltPreload.Application.Ingestion;

/// <summary>
/// 采集运行态指标（进程内）。
/// <para>
/// 用于 Watchdog 判断"连接还在但已经无数据"的假死状态，也给健康检查复用。
/// 这不是持久化状态，进程重启后从零开始。
/// </para>
/// </summary>
public interface IIngestionRuntimeStatus
{
    DateTimeOffset? LastEnvelopeAt { get; }
    DateTimeOffset? LastChannelResultAt { get; }
    long EnvelopeCount { get; }
    long ChannelResultCount { get; }

    /// <summary>入库写失败累计次数。Phase 1 无 WAL，写失败即丢数；本计数让丢数可观测（经 /api/runtime/devices 暴露）。</summary>
    long WriteFailureCount { get; }

    void MarkEnvelope(DateTimeOffset at, bool isChannelResult);

    /// <summary>记录一次入库写失败（该 measurement 已丢失，无 WAL 兜底）。</summary>
    void MarkWriteFailure();

    /// <summary>消费者是否正在执行一次入库写入。Watchdog 用它在慢 DB 写入期间避免把设备误判为"无数据"而重连。</summary>
    bool IsWriting { get; }
    void MarkWriteStart();
    void MarkWriteEnd();
}

public sealed class IngestionRuntimeStatus : IIngestionRuntimeStatus
{
    private long _lastEnvelopeTicks;
    private long _lastChannelResultTicks;
    private long _envelopeCount;
    private long _channelResultCount;
    private long _writeFailureCount;

    public DateTimeOffset? LastEnvelopeAt => ReadTicks(_lastEnvelopeTicks);
    public DateTimeOffset? LastChannelResultAt => ReadTicks(_lastChannelResultTicks);
    public long EnvelopeCount => Interlocked.Read(ref _envelopeCount);
    public long ChannelResultCount => Interlocked.Read(ref _channelResultCount);
    public long WriteFailureCount => Interlocked.Read(ref _writeFailureCount);

    public void MarkEnvelope(DateTimeOffset at, bool isChannelResult)
    {
        Interlocked.Exchange(ref _lastEnvelopeTicks, at.UtcTicks);
        Interlocked.Increment(ref _envelopeCount);

        if (isChannelResult)
        {
            Interlocked.Exchange(ref _lastChannelResultTicks, at.UtcTicks);
            Interlocked.Increment(ref _channelResultCount);
        }
    }

    public void MarkWriteFailure() => Interlocked.Increment(ref _writeFailureCount);

    private long _writing;
    public bool IsWriting => Interlocked.Read(ref _writing) != 0;
    public void MarkWriteStart() => Interlocked.Exchange(ref _writing, 1);
    public void MarkWriteEnd() => Interlocked.Exchange(ref _writing, 0);

    private static DateTimeOffset? ReadTicks(long ticks)
        => ticks <= 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
}
