using System.Collections.Concurrent;
using System.Threading.Channels;
using BoltPreload.Application.Devices;
using BoltPreload.Application.Ingestion;
using BoltPreload.Fx;
using BoltPreload.Fx.Models;

namespace BoltPreload.Tests.Devices;

/// <summary>
/// 一台假设备的连接器：记下收到的下发调用，不做任何事。
/// 供需要"设备在运行时里且已连接"但不打算接真机的用例共用。
/// </summary>
internal sealed class FakeConnector : IFxConnector
{
    private readonly Channel<FxDecodedEnvelope> _channel = Channel.CreateUnbounded<FxDecodedEnvelope>();

    public FakeConnector(long deviceId) => DeviceId = deviceId;

    public ConcurrentQueue<(FxWorkMode Mode, ushort Channel)> WorkModeCalls { get; } = new();
    public ConcurrentQueue<ushort[]> McrConfigCalls { get; } = new();

    /// <summary>收到的 <see cref="ReadChannelParamAsync"/> 调用，按发生顺序。</summary>
    public ConcurrentQueue<(string Property, ushort Channel)> ChannelParamReads { get; } = new();

    /// <summary>可设：断线/重连场景的用例直接拨这个状态。</summary>
    public FxConnectorState State { get; set; } = FxConnectorState.Connected;
    public long DeviceId { get; }
    public ChannelReader<FxDecodedEnvelope> Results => _channel.Reader;

    /// <summary>模拟设备上报一条信封，供消费循环读到。</summary>
    public void Emit(FxDecodedEnvelope envelope) => _channel.Writer.TryWrite(envelope);
    public long DroppedCount => 0;
    public Func<FxDecodedEnvelope, ValueTask>? OverflowSink { get; set; }

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ReconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task SetWorkModeAsync(
        FxWorkMode mode, ushort channel = 0, ushort address = 1, CancellationToken ct = default)
    {
        WorkModeCalls.Enqueue((mode, channel));
        return Task.CompletedTask;
    }

    public Task ReadDeviceParamAsync(string propertyName, ushort address = 1, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ReadChannelParamAsync(
        string propertyName, ushort channel = 1, ushort address = 1, CancellationToken ct = default)
    {
        ChannelParamReads.Enqueue((propertyName, channel));
        return Task.CompletedTask;
    }

    public Task ConfigureMultiChannelPollingAsync(
        IReadOnlyCollection<ushort> enabledChannels, ushort? singleCount = null, ushort? dailyCount = null,
        uint? startSeconds = null, CancellationToken ct = default)
    {
        McrConfigCalls.Enqueue(enabledChannels.ToArray());
        SessionMcr = enabledChannels.ToArray();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 设备端当前的 MCR 轮询集。真机上这是下发后的会话状态，所以下发要改它——
    /// 不跟着改的话"已是全通道就不重下发"这类判断在测试里永远走不到。
    /// </summary>
    public IReadOnlyList<ushort> SessionMcr { get; set; } = [];

    public IReadOnlyList<ushort> GetSessionMcrChannels() => SessionMcr;

    public Task ConfigureChannelAsync(
        ushort channel, ChannelConfigUpdate updates, ushort address = 1, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<RecalibrateResult> RecalibrateChannelAsync(
        ushort channel, ushort address = 1, decimal? explicitRefTimeNs = null,
        decimal? explicitRefTempC = null, CancellationToken ct = default)
        => Task.FromResult(new RecalibrateResult(channel, address, 0, 0, 0, DateTimeOffset.UtcNow));

    public IReadOnlyList<FxSessionChannelConfigSnapshot> GetSessionChannelConfigs() => [];

    /// <summary>是否已被释放（supervisor 摘除设备后必须释放连接器，热增删用例据此断言）。</summary>
    public bool Disposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>只装一台设备的运行时管理器。</summary>
internal sealed class FakeDeviceManager : IDeviceConnectorManager
{
    private DeviceRuntimeContext _ctx;

    /// <param name="mode">运行模式。真机路径专有的逻辑（如波形主动采集）只对 <c>Real</c> 生效。</param>
    /// <param name="model">设备型号。MCR 多通道轮询只有 model=3 支持。</param>
    /// <param name="channelCount">物理通道数。0 表示没有这项信息（老配置路径）。</param>
    public FakeDeviceManager(
        IFxConnector connector, string mode = "Sim", ushort model = 3, ushort channelCount = 0)
    {
        _ctx = new DeviceRuntimeContext(
            DeviceId: connector.DeviceId,
            Name: "fake",
            IsPrimary: true,
            Connector: connector,
            RuntimeStatus: new IngestionRuntimeStatus(),
            Options: new DeviceRuntimeOptions(mode, "n/a", 1, 1, model, channelCount));
    }

    public IReadOnlyCollection<DeviceRuntimeContext> Runtimes => [_ctx];
    public DeviceRuntimeContext Primary => _ctx;
    public DeviceRuntimeContext? Find(long deviceId) => deviceId == _ctx.DeviceId ? _ctx : null;

    public bool TryUpdateChannelCount(long deviceId, ushort channelCount)
    {
        if (deviceId != _ctx.DeviceId) return false;
        _ctx = _ctx with { Options = _ctx.Options with { ChannelCount = channelCount } };
        return true;
    }
}
