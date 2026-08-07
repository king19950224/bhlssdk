using System.Collections.Concurrent;
using System.Net;

namespace BoltPreload.Api.Auth;

/// <summary>
/// 登录 IP 滑动窗口限速（与账号级 failed_attempts 锁定互补）。
/// 防同一来源对多账号喷洒撞库——账号锁挡不住换用户名的暴力尝试。
/// </summary>
public sealed class LoginIpRateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly int _maxFailures;
    private readonly TimeSpan _window;

    public LoginIpRateLimiter(int maxFailures = 30, int windowMinutes = 15)
    {
        _maxFailures = Math.Max(5, maxFailures);
        _window = TimeSpan.FromMinutes(Math.Max(1, windowMinutes));
    }

    public bool IsLimited(IPAddress? ip)
    {
        if (ip is null) return false;
        var key = ip.ToString();
        if (!_windows.TryGetValue(key, out var w)) return false;
        Prune(w);
        return w.Fails.Count >= _maxFailures;
    }

    public void RecordFailure(IPAddress? ip)
    {
        if (ip is null) return;
        var w = _windows.GetOrAdd(ip.ToString(), _ => new Window());
        lock (w)
        {
            Prune(w);
            w.Fails.Add(DateTimeOffset.UtcNow);
        }
    }

    public void RecordSuccess(IPAddress? ip)
    {
        if (ip is null) return;
        _windows.TryRemove(ip.ToString(), out _);
    }

    private void Prune(Window w)
    {
        var cutoff = DateTimeOffset.UtcNow - _window;
        w.Fails.RemoveAll(t => t < cutoff);
    }

    private sealed class Window
    {
        public List<DateTimeOffset> Fails { get; } = new();
    }
}
