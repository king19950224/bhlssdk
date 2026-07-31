using System.Collections.Concurrent;

namespace BoltPreload.Application.Devices.Acceptance;

/// <summary>一次接入验收会话（进程内，不落库——会话只是临时轮询窗口，验收结论落 channel_acceptance）。</summary>
public sealed record AcceptanceSession(
    long DeviceId,
    IReadOnlyList<ushort> Channels,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    long StartedByUserId);

/// <summary>
/// 接入验收会话状态（进程内单例）。
/// <para>
/// 用途：现场做"顺序接入验收 / 接头级拔插确认"时，把设备 MCR 临时缩到待验收通道集，
/// 单通道刷新率从 全通道轮转周期（48 通道约 4.8s/条）提升数倍，插拔确认从数十秒缩到秒级。
/// </para>
/// <para>
/// 恢复路径【故意不放在这里】：会话结束/过期后由 ChannelPresenceService 的收敛循环把 MCR
/// 拉回全通道（它看到会话不在了、目标又是全通道，下一轮 sweep（≤5s）自动重下发）。
/// 端点侧可再做一次"立即恢复"提升体验，但正确性不依赖它——进程崩溃、恢复失败都由收敛循环兜底。
/// 因此验收会话要求 Fx:HotPlug:Enabled=true（收敛循环在跑）才允许开启。
/// </para>
/// <para>互斥：与 probe-scan 相互排斥（都要独占 MCR）；由各自的启动入口检查对方。</para>
/// </summary>
public sealed class AcceptanceSessionState
{
    private readonly ConcurrentDictionary<long, AcceptanceSession> _sessions = new();

    /// <summary>尝试开启会话。该设备已有会话时返回 null（API 应回 409）。</summary>
    public AcceptanceSession? TryBegin(long deviceId, IReadOnlyList<ushort> channels, TimeSpan ttl, long userId)
    {
        var session = new AcceptanceSession(
            deviceId, channels, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.Add(ttl), userId);
        return _sessions.TryAdd(deviceId, session) ? session : null;
    }

    /// <summary>当前会话（含已过期未清理的——调用方按 ExpiresAt 判断展示）。</summary>
    public AcceptanceSession? Get(long deviceId)
        => _sessions.TryGetValue(deviceId, out var s) ? s : null;

    /// <summary>是否有【未过期】的会话在进行（ChannelPresenceService 收敛循环 / probe-scan 互斥用）。</summary>
    public bool IsActive(long deviceId)
        => _sessions.TryGetValue(deviceId, out var s) && s.ExpiresAt > DateTimeOffset.UtcNow;

    /// <summary>结束会话，返回被移除的会话（不存在返回 null）。</summary>
    public AcceptanceSession? End(long deviceId)
        => _sessions.TryRemove(deviceId, out var s) ? s : null;

    /// <summary>清理已过期的会话，返回被清理的列表（ChannelPresenceService 每轮 sweep 调用，过期即触发 MCR 收敛回全通道）。</summary>
    public IReadOnlyList<AcceptanceSession> ExpireStale(DateTimeOffset now)
    {
        List<AcceptanceSession>? expired = null;
        foreach (var (deviceId, session) in _sessions)
        {
            if (session.ExpiresAt > now) continue;
            // 按 (键,值) 原子移除：过期判定与移除之间若被 End+新开的会话顶替，不能误删新会话
            if (_sessions.TryRemove(new KeyValuePair<long, AcceptanceSession>(deviceId, session)))
                (expired ??= []).Add(session);
        }
        return expired ?? (IReadOnlyList<AcceptanceSession>)Array.Empty<AcceptanceSession>();
    }
}
