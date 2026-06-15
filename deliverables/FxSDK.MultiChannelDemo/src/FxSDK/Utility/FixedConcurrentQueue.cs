using System.Collections.Concurrent;

namespace FxSDK.Utility;

/// <summary>
/// 定长队列
/// </summary>
/// <typeparam name="T"></typeparam>
public class FixedConcurrentQueue<T> : ConcurrentQueue<T>
{
    private readonly int _maxSize;
    public FixedConcurrentQueue(int maxSize)
    {
        _maxSize = maxSize;
    }
    public new void Enqueue(T item)
    {
        base.Enqueue(item);
        while (Count > _maxSize)
        {
            TryDequeue(out _);
        }
    }
}