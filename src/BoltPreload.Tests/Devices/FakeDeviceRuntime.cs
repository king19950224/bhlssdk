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

    public FxConnectorState State => FxConnectorState.Connected;
    public long DeviceId { get; }
    public ChannelReader<FxDecodedEnvelope> Results => _channel.Reader;
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
        return Task.CompletedTask;
    }

    public Task ConfigureChannelAsync(
        ushort channel, ChannelConfigUpdate updates, ushort address = 1, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<RecalibrateResult> RecalibrateChannelAsync(
        ushort channel, ushort address = 1, decimal? explicitRefTimeNs = null,
        decimal? explicitRefTempC = null, CancellationToken ct = default)
        => Task.FromResult(new RecalibrateResult(channel, address, 0, 0, 0, DateTimeOffset.UtcNow));

    public IReadOnlyList<FxSessionChannelConfigSnapshot> GetSessionChannelConfigs() => [];

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>只装一台设备的运行时管理器。</summary>
internal sealed class FakeDeviceManager : IDeviceConnectorManager
{
    private readonly DeviceRuntimeContext _ctx;

    /// <param name="mode">运行模式。真机路径专有的逻辑（如波形主动采集）只对 <c>Real</c> 生效。</param>
    public FakeDeviceManager(IFxConnector connector, string mode = "Sim")
    {
        _ctx = new DeviceRuntimeContext(
            DeviceId: connector.DeviceId,
            Name: "fake",
            IsPrimary: true,
            Connector: connector,
            RuntimeStatus: new IngestionRuntimeStatus(),
            Options: new DeviceRuntimeOptions(mode, "n/a", 1, 1, 3));
    }

    public IReadOnlyCollection<DeviceRuntimeContext> Runtimes => [_ctx];
    public DeviceRuntimeContext Primary => _ctx;
    public DeviceRuntimeContext? Find(long deviceId) => deviceId == _ctx.DeviceId ? _ctx : null;
    public bool TryUpdateChannelCount(long deviceId, ushort channelCount) => deviceId == _ctx.DeviceId;
}
