using System.Collections.Concurrent;

namespace FxSDK.Utility;

public static class ConcurrentQueueExtensions
{
    /// <summary>
    /// 从队列中移除指定数量的元素。
    /// </summary>
    /// <typeparam name="T">队列中的元素类型。</typeparam>
    /// <param name="queue">要操作的队列。</param>
    /// <param name="count">要移除的元素数量。</param>
    public static int RemoveRange<T>(this ConcurrentQueue<T> queue, int count)
    {
        if (count <= 0) return 0;
    
        var removed = 0;
        // 直接尝试出队 `count` 次，无论队列当前长度
        while (removed < count && queue.TryDequeue(out _))
        {
            removed++;
        }
        return removed;
    }

    /// <summary>
    /// 将多个元素一次性添加到队列中。
    /// </summary>
    /// <typeparam name="T">队列中的元素类型。</typeparam>
    /// <param name="queue">要操作的队列。</param>
    /// <param name="items">要添加的元素集合。</param>
    public static void EnqueueRange<T>(this ConcurrentQueue<T> queue, IEnumerable<T> items)
    {
        if (items == null)
        {
            throw new ArgumentNullException(nameof(items), "Items cannot be null.");
        }
        foreach (var item in items)
        {
            queue.Enqueue(item);
        }
    }
}