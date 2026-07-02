using System.Threading.Channels;
using BoltPreload.Fx.Models;

namespace BoltPreload.Fx;

/// <summary>
/// 有界结果通道的背压写入助手（H3）。
/// <para>
/// 通道须以 <see cref="BoundedChannelFullMode.Wait"/> 创建：这样 <see cref="ChannelWriter{T}.TryWrite"/>
/// 在满时返回 false（可检测），从而能<b>精确统计丢弃数</b>，并在丢弃前把最旧测量交给兜底（落 WAL）。
/// 旧实现用 DropOldest：TryWrite 永远返回 true、丢弃完全静默，连"通道已满"日志都成了死代码。
/// </para>
/// <para>保留"丢旧保新"的实时优先语义：满时丢弃<b>最旧</b>一条腾位给最新数据。</para>
/// </summary>
internal static class BackpressureWriter
{
    /// <summary>
    /// 写入一条测量；满时丢弃最旧条目腾位（计数 + 可选兜底），返回本次实际丢弃的条数（用于节流告警）。
    /// </summary>
    public static int WriteWithBackpressure(
        Channel<FxDecodedEnvelope> channel,
        FxDecodedEnvelope envelope,
        ref long droppedCount,
        Func<FxDecodedEnvelope, ValueTask>? overflowSink)
    {
        if (channel.Writer.TryWrite(envelope))
            return 0;

        var dropped = 0;
        // 满：丢最旧腾位后重试。限制尝试次数，避免极端并发下空转。
        var written = false;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            if (channel.Writer.TryWrite(envelope)) { written = true; break; }
            if (channel.Reader.TryRead(out var oldest))
            {
                Interlocked.Increment(ref droppedCount);
                dropped++;
                if (overflowSink is not null)
                    FireAndForgetOverflow(overflowSink, oldest);
            }
        }

        // 16 次仍写不进（读侧竞争剧烈 / 持续满）：当前这条【最新】测量也必须计数并落兜底，
        // 否则会静默丢失最新数据（安全监测大忌）。旧实现只兜底被挤掉的最旧条，最新条无声消失。
        if (!written && !channel.Writer.TryWrite(envelope))
        {
            Interlocked.Increment(ref droppedCount);
            dropped++;
            if (overflowSink is not null)
                FireAndForgetOverflow(overflowSink, envelope);
        }
        return dropped;
    }

    // 在途 overflow 落盘任务的并发上限：持续背压（DB 长时间不可用 + 高频多通道）时，每条被挤掉的测量
    // 都 fire-and-forget 一个落 WAL 任务，无界会在线程池 / 内存里堆积（WAL 内部本就串行，任务只是排队等锁）。
    // 用信号量封顶在途任务数；取不到配额即放弃本条兜底（已计入 droppedCount），保证背压路径自身不被拖垮。
    private const int MaxConcurrentOverflowWrites = 64;
    private static readonly SemaphoreSlim OverflowGate = new(MaxConcurrentOverflowWrites, MaxConcurrentOverflowWrites);

    /// <summary>
    /// 把被挤掉的最旧测量交给兜底（落 WAL），fire-and-forget：不阻塞采集回调线程。
    /// ValueTask 必须被完整消费（CA2012），故在局部 async 中 await 并吞掉异常（兜底失败不得影响采集主链路）。
    /// 在途任务数由 <see cref="OverflowGate"/> 封顶，避免持续背压时无界堆积。
    /// </summary>
    private static void FireAndForgetOverflow(Func<FxDecodedEnvelope, ValueTask> sink, FxDecodedEnvelope envelope)
    {
        if (!OverflowGate.Wait(0))
            return; // 在途落盘任务已达上限，放弃本条兜底（已计入丢弃计数），不再无界创建任务

        _ = ConsumeAsync();

        async Task ConsumeAsync()
        {
            try { await sink(envelope).ConfigureAwait(false); }
            catch { /* 兜底失败不影响采集主链路 */ }
            finally { OverflowGate.Release(); }
        }
    }
}
