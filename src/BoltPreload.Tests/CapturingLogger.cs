using Microsoft.Extensions.Logging;

namespace BoltPreload.Tests;

/// <summary>
/// 捕获日志条目（级别 + 已格式化消息），供断言。
/// 有些地方日志级别本身就是需求：推送失败记成 Debug，默认级别下等于没记，功能测试照样全绿。
/// 这种只能靠断言级别来钉。
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    /// <summary>命中关键字的 Warning 消息。返回空就说明这件事没记在 Warning 上。</summary>
    public List<string> Warnings(string contains) => Messages(LogLevel.Warning, contains);

    public List<string> Messages(LogLevel level, string contains)
    {
        lock (_entries)
        {
            return _entries
                .Where(e => e.Level == level && e.Message.Contains(contains, StringComparison.Ordinal))
                .Select(e => e.Message)
                .ToList();
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // 加锁：测量推送的泵在后台线程写入，断言在测试线程读取
        lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
    }
}
