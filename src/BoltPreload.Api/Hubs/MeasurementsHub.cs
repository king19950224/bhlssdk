using Microsoft.AspNetCore.SignalR;

namespace BoltPreload.Api.Hubs;

/// <summary>
/// 实时测量推送 Hub。
/// <para>
/// 客户端订阅 <c>OnMeasurement</c> 事件即可收到所有 ChannelResult 更新。
/// 简化版：所有客户端订阅同一个流，未做分组订阅——当前单站部署、客户端数量少，广播足够；
/// 若客户端规模上来，可加 JoinDevice(deviceId) / JoinBolt(boltId) 等 group 订阅方法减少无效推送。
/// </para>
/// </summary>
public sealed class MeasurementsHub : Hub
{
    // 纯服务器 → 客户端单向推送，无客户端方法。
}
