namespace BoltPreload.Application.Notifications;

/// <summary>
/// 出站通知的渠道适配器。按 <see cref="NotificationChannelInfo.Kind"/> 分发，
/// 本轮只有 webhook 实现；smtp / sms 等渠道定了再补适配器，接口不用动。
/// 发送失败必须抛异常——派发器据此退避重试，静默吞掉就等于丢通知。
/// </summary>
public interface INotificationTransport
{
    /// <summary>本适配器处理的渠道类型（对应 notification_channels.kind）。</summary>
    string Kind { get; }

    /// <summary>发送一条（可能已合并多个事件的）消息。非 2xx / 超时 / 网络错误抛异常。</summary>
    Task SendAsync(NotificationChannelInfo channel, string title, IReadOnlyList<string> lines, CancellationToken ct = default);
}

/// <summary>渠道信息（发送所需的最小集）。ConfigJson 即 notification_channels.config_jsonb 原文。</summary>
public sealed record NotificationChannelInfo(long Id, string Name, string Kind, string ConfigJson);
