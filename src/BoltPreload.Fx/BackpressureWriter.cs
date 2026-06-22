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
        for (var attempt = 0; attempt < 16 && !channel.Writer.TryWrite(envelope); attempt++)
        {
            if (channel.Reader.TryRead(out var oldest))
            {
                Interlocked.Increment(ref droppedCount);
                dropped++;
                if (overflowSink is not null)
                    FireAndForgetOverflow(overflowSink, oldest);
            }
        }
        return dropped;
    }

    /// <summary>
    /// 把被挤掉的最旧测量交给兜底（落 WAL），fire-and-forget：不阻塞采集回调线程。
    /// ValueTask 必须被完整消费（CA2012），故在局部 async 中 await 并吞掉异常（兜底失败不得影响采集主链路）。
    /// </summary>
    private static void FireAndForgetOverflow(Func<FxDecodedEnvelope, ValueTask> sink, FxDecodedEnvelope envelope)
    {
        _ = ConsumeAsync();

        async Task ConsumeAsync()
        {
            try { await sink(envelope).ConfigureAwait(false); }
            catch { /* 兜底失败不影响采集主链路 */ }
        }
    }
}
