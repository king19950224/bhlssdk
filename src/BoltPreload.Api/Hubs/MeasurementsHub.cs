using Microsoft.AspNetCore.SignalR;

namespace BoltPreload.Api.Hubs;

/// <summary>
/// 实时测量推送 Hub。
/// <para>
/// 客户端订阅 <c>OnMeasurement</c> 事件即可收到所有 ChannelResult 更新。
/// 简化版：所有客户端订阅同一个流；Phase 2 加 group（按 bolt_id / device_id）。
/// </para>
/// </summary>
public sealed class MeasurementsHub : Hub
{
    // Phase 1 没有客户端方法（纯服务器 → 客户端单向推送）。
    // Phase 2 可加：JoinDevice(deviceId), JoinBolt(boltId) 等订阅 group 的方法。
}
