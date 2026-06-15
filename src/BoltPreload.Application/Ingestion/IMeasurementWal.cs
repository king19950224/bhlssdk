using BoltPreload.Fx.Models;

namespace BoltPreload.Application.Ingestion;

/// <summary>
/// 测量数据预写日志（WAL）兜底。入库失败时把信封持久化到本地，DB 恢复后由后台服务重放补写。
/// <para>
/// 关键安全约束：重放走 <see cref="IMeasurementSink.WriteAsync(FxDecodedEnvelope, bool, CancellationToken)"/>
/// 且 <c>evaluateAlarms=false</c> —— 重放的是历史/乱序数据，绝不能再喂给有状态的告警状态机。
/// </para>
/// <para>
/// 仅持久化测量类信封（ChannelResult / MultiChannelResult）。DeviceParameter 是诊断数据且含运行时
/// 类型（<c>object?</c>），不可靠地 JSON 往返，故不入 WAL。
/// </para>
/// </summary>
public interface IMeasurementWal
{
    /// <summary>是否启用。Noop 实现返回 <c>false</c>。</summary>
    bool Enabled { get; }

    /// <summary>当前待重放条目数（可观测，经 /api/runtime/devices 暴露）。</summary>
    long PendingCount { get; }

    /// <summary>
    /// 持久化一条入库失败的信封。非测量类信封会被忽略（返回但不落盘）。
    /// 实现需保证落盘的原子性（崩溃不留半截文件）。
    /// </summary>
    ValueTask AppendAsync(FxDecodedEnvelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// 重放待处理条目（按落盘先后顺序）。对每条调用 <paramref name="writeAsync"/>：
    /// <list type="bullet">
    /// <item>成功 → 丢弃该条；</item>
    /// <item>写入抛异常（通常 DB 仍不可用）→ 停止本轮、保留剩余条目，下一轮再试；</item>
    /// <item>条目损坏无法反序列化 → 隔离该条（不阻塞队头），继续后续。</item>
    /// </list>
    /// 返回本轮成功重放条数。
    /// </summary>
    Task<int> ReplayAsync(Func<FxDecodedEnvelope, CancellationToken, Task> writeAsync, CancellationToken cancellationToken = default);
}

/// <summary>WAL 关闭时的空实现：不落盘、不重放。</summary>
public sealed class NoopMeasurementWal : IMeasurementWal
{
    public static readonly NoopMeasurementWal Instance = new();

    public bool Enabled => false;
    public long PendingCount => 0;

    public ValueTask AppendAsync(FxDecodedEnvelope envelope, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public Task<int> ReplayAsync(Func<FxDecodedEnvelope, CancellationToken, Task> writeAsync, CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}

/// <summary>测量 WAL 配置（绑定 appsettings 的 <c>MeasurementWal</c> 段）。</summary>
public sealed class MeasurementWalOptions
{
    public const string SectionName = "MeasurementWal";

    /// <summary>是否启用 WAL 兜底。安全关键系统建议开启。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>WAL 文件根目录（相对工作目录或绝对路径）。</summary>
    public string RootPath { get; set; } = "data/wal";

    /// <summary>
    /// 最大待重放条目数。超过则丢弃最旧条目（drop-oldest，有界、不撑爆磁盘），并告警。
    /// 注意：含波形的条目体积较大，按设备速率与可容忍停机时长估算。
    /// </summary>
    public int MaxEntries { get; set; } = 50_000;

    /// <summary>重放轮询间隔（秒）。</summary>
    public int ReplayIntervalSeconds { get; set; } = 15;
}
