using BoltPreload.Fx.Models;

namespace BoltPreload.Application.Ingestion;

/// <summary>
/// 测量数据落地接收方接口（Application 层定义，Infrastructure 层实现）。
/// 抽出这一层是为了：1) Application 不依赖具体持久化技术；2) 单测可以 mock。
/// </summary>
public interface IMeasurementSink
{
    Task WriteAsync(FxDecodedEnvelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// 写入测量，可选是否评估告警。WAL 重放时传 <c>evaluateAlarms=false</c>：
    /// 重放的是历史/乱序数据，绝不能再喂给有状态的告警状态机（否则会用过期数据误触发/误恢复告警）。
    /// </summary>
    Task WriteAsync(FxDecodedEnvelope envelope, bool evaluateAlarms, CancellationToken cancellationToken = default);
}
