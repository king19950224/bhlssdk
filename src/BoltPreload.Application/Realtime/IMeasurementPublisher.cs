using BoltPreload.Fx.Models;

namespace BoltPreload.Application.Realtime;

/// <summary>
/// 实时测量推送抽象。
/// <para>
/// Phase 1 用 SignalR 实现；Phase 2 可换 MQTT、Kafka、WebSocket bridge 等。
/// IngestionService 不直接依赖 SignalR，便于在没有 web 层的部署（纯采集网关）里关掉推送。
/// </para>
/// </summary>
public interface IMeasurementPublisher
{
    /// <summary>
    /// 推送一条解码后的测量。实现应做到「失败不抛」，避免阻塞主入库流水。
    /// </summary>
    Task PublishAsync(FxDecodedEnvelope envelope, CancellationToken ct = default);
}

/// <summary>空实现：当未启用实时推送时使用，零开销。</summary>
public sealed class NoopMeasurementPublisher : IMeasurementPublisher
{
    public Task PublishAsync(FxDecodedEnvelope envelope, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>
/// 告警事件推送（区别于测量推送：告警频率低但严重，UI 要弹窗/响铃）。
/// </summary>
public interface IAlarmPublisher
{
    Task PublishAsync(BoltPreload.Application.Alarm.AlarmEvent ev, CancellationToken ct = default);
}

public sealed class NoopAlarmPublisher : IAlarmPublisher
{
    public Task PublishAsync(BoltPreload.Application.Alarm.AlarmEvent ev, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>
/// 通道在位（探头热插拔）事件推送。频率极低（现场插拔探头时才有），实现失败不抛。
/// </summary>
public interface IChannelPresencePublisher
{
    /// <param name="change">在位变化事件</param>
    /// <param name="channelId">channels.id（若已登记）</param>
    /// <param name="boltId">绑定螺栓 id（未绑定 = null）</param>
    /// <param name="boltCode">绑定螺栓编号（未绑定 = null）</param>
    Task PublishAsync(
        Ingestion.ChannelPresenceChange change,
        long? channelId, long? boltId, string? boltCode,
        CancellationToken ct = default);
}

public sealed class NoopChannelPresencePublisher : IChannelPresencePublisher
{
    public Task PublishAsync(Ingestion.ChannelPresenceChange change, long? channelId, long? boltId, string? boltCode, CancellationToken ct = default)
        => Task.CompletedTask;
}
