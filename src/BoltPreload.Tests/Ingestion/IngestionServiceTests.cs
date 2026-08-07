using BoltPreload.Application.Ingestion;
using BoltPreload.Application.Realtime;
using BoltPreload.Fx.Models;
using BoltPreload.Tests.Devices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BoltPreload.Tests.Ingestion;

/// <summary>
/// 采集消费循环（<see cref="IngestionService"/>）。
/// <para>
/// 从连接器读信封、入库、推前端，全系统的数据都从这条循环过。要守的三条都是入库失败时的行为：
/// 落 WAL 让 DB 恢复后能补回来、别推送没入库的数据、别让这一条把整条循环带停。
/// </para>
/// <para>
/// 三条错了都不报错。不落 WAL 是那段时间的数据直接没了；推了未入库的数据是大屏上的数字
/// 翻历史查不到；循环停掉是这台设备从此不再采集，而进程活着、心跳照打。整个服务此前零覆盖。
/// </para>
/// </summary>
public sealed class IngestionServiceTests
{
    [Fact]
    public async Task WriteFailure_GoesToTheWal_AndIsNotPushedToTheFrontend()
    {
        var connector = new FakeConnector(deviceId: 1);
        var sink = new FlakySink { FailNext = true };
        var wal = new RecordingWal();
        var publisher = new RecordingPublisher();

        await using var run = await StartAsync(connector, sink, wal, publisher);
        connector.Emit(Measurement(channel: 5));
        await run.WaitUntilAsync(() => sink.Attempts == 1);

        wal.Appended.Should().HaveCount(1, "库写不进去时不落 WAL，这段时间的数据就真的没了");
        publisher.Published.Should().BeEmpty(
            "没入库的数据不能推：大屏上有这个数字，翻历史却查不到，对不上账");
    }

    [Fact]
    public async Task WriteFailure_DoesNotStopTheLoop()
    {
        var connector = new FakeConnector(deviceId: 1);
        var sink = new FlakySink { FailNext = true };
        var wal = new RecordingWal();
        var publisher = new RecordingPublisher();

        await using var run = await StartAsync(connector, sink, wal, publisher);
        connector.Emit(Measurement(channel: 5));
        await run.WaitUntilAsync(() => sink.Attempts == 1);

        connector.Emit(Measurement(channel: 6));
        await run.WaitUntilAsync(() => sink.Written.Count == 1);

        publisher.Published.Should().HaveCount(1, "上一条失败不该影响下一条：这台设备会从此不再出数");
    }

    [Fact]
    public async Task NonMeasurementEnvelope_IsNotBufferedForReplay()
    {
        var connector = new FakeConnector(deviceId: 1);
        var sink = new FlakySink { FailNext = true };
        var wal = new RecordingWal();

        await using var run = await StartAsync(connector, sink, wal, new RecordingPublisher());
        connector.Emit(new FxDecodedEnvelope(
            1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, FxResultType.DeviceParameter,
            DeviceParameter: new FxDeviceParameterDto(1, 0, "Version", "1.0")));
        await run.WaitUntilAsync(() => sink.Attempts == 1);

        wal.Appended.Should().BeEmpty(
            "WAL 是给测量数据兜底的，重放路径只认测量；塞别的进去重放时只能当坏条目隔离");
    }

    [Fact]
    public async Task MultiChannelResult_FeedsPresenceForEveryChannelInIt()
    {
        var connector = new FakeConnector(deviceId: 1);
        var sink = new FlakySink();
        var presence = new ChannelPresenceTracker(Options.Create(new ChannelPresenceOptions()));

        await using var run = await StartAsync(
            connector, sink, new RecordingWal(), new RecordingPublisher(), presence);

        // 一条多通道包里有三路，在位判定要逐路记；只认第一路的话另外两路上的探头永远判不出在位，
        // 识别页显示"待接入"、自动建档也不会触发。默认连续 3 条有回波才判插入，所以发 3 轮
        for (var i = 0; i < 3; i++) connector.Emit(MultiMeasurement(1, [5, 6, 7]));
        await run.WaitUntilAsync(() => sink.Written.Count == 3);

        var events = new List<ChannelPresenceChange>();
        while (presence.Events.TryRead(out var ev)) events.Add(ev);

        events.Should().OnlyContain(e => e.Present);
        events.Select(e => e.ChannelNo).Should().BeEquivalentTo(new ushort[] { 5, 6, 7 });
    }

    // ---- 脚手架 ----

    private static FxDecodedEnvelope Measurement(ushort channel)
    {
        var ts = DateTimeOffset.UtcNow;
        return new FxDecodedEnvelope(1, ts, ts, FxResultType.ChannelResult, ChannelResult: Channel(channel));
    }

    private static FxDecodedEnvelope MultiMeasurement(long deviceId, ushort[] channels)
    {
        var ts = DateTimeOffset.UtcNow;
        var multi = new FxMultiChannelResultDto(channels.ToDictionary(c => c, Channel));
        return new FxDecodedEnvelope(
            deviceId, ts, ts, FxResultType.MultiChannelResult, MultiChannelResult: multi);
    }

    private static FxChannelResultDto Channel(ushort channel) => new(
        Address: 1, Channel: channel, StatusCode: 0,
        EchoTimeNs: 32000m, EchoTime2Ns: null, SpTempC: 16.7m, DeltaTofNs: 12.3m,
        Gain: 16f, EchoWave: null, LowAi: null, HighAi: null, Angle: null, MidResults: null);

    private static async Task<RunningService> StartAsync(
        FakeConnector connector, FlakySink sink, RecordingWal wal, RecordingPublisher publisher,
        ChannelPresenceTracker? presence = null)
    {
        var svc = new IngestionService(
            new FakeDeviceManager(connector),
            sink, publisher, wal,
            presence ?? new ChannelPresenceTracker(Options.Create(new ChannelPresenceOptions())),
            new IngestionTaskRegistry(),
            new BoltPreload.Application.Diagnostics.BackgroundServiceHealth(),
            NullLogger<IngestionService>.Instance);
        await svc.StartAsync(CancellationToken.None);
        return new RunningService(svc);
    }

    private sealed class RunningService : IAsyncDisposable
    {
        private readonly IngestionService _svc;
        public RunningService(IngestionService svc) => _svc = svc;

        /// <summary>等条件成立。消费循环在后台线程上跑，断言前得让它把这一条处理完。</summary>
        public async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
            condition().Should().BeTrue("等了 5 秒消费循环还没处理这一条");
            await Task.Delay(50);   // 推送是 fire-and-forget，给它一点时间落到记录里
        }

        public async ValueTask DisposeAsync() => await _svc.StopAsync(CancellationToken.None);
    }

    /// <summary>可指定下一次写入失败的 sink。</summary>
    internal sealed class FlakySink : IMeasurementSink
    {
        private readonly List<FxDecodedEnvelope> _written = [];

        public bool FailNext { get; set; }
        public int Attempts { get; private set; }
        public IReadOnlyList<FxDecodedEnvelope> Written
        {
            get { lock (_written) return _written.ToList(); }
        }

        public Task WriteAsync(FxDecodedEnvelope envelope, CancellationToken ct = default)
            => WriteAsync(envelope, evaluateAlarms: true, ct);

        public Task WriteAsync(FxDecodedEnvelope envelope, bool evaluateAlarms, CancellationToken ct = default)
        {
            lock (_written)
            {
                Attempts++;
                if (FailNext)
                {
                    FailNext = false;
                    return Task.FromException(new InvalidOperationException("库写不进去"));
                }
                _written.Add(envelope);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWal : IMeasurementWal
    {
        private readonly List<FxDecodedEnvelope> _appended = [];

        public bool Enabled => true;
        public long PendingCount => _appended.Count;
        public IReadOnlyList<FxDecodedEnvelope> Appended
        {
            get { lock (_appended) return _appended.ToList(); }
        }

        public ValueTask AppendAsync(FxDecodedEnvelope envelope, CancellationToken ct = default)
        {
            lock (_appended) _appended.Add(envelope);
            return ValueTask.CompletedTask;
        }

        public Task<int> ReplayAsync(
            Func<FxDecodedEnvelope, CancellationToken, Task> writeAsync, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class RecordingPublisher : IMeasurementPublisher
    {
        private readonly List<FxDecodedEnvelope> _published = [];

        public IReadOnlyList<FxDecodedEnvelope> Published
        {
            get { lock (_published) return _published.ToList(); }
        }

        public Task PublishAsync(FxDecodedEnvelope envelope, CancellationToken ct = default)
        {
            lock (_published) _published.Add(envelope);
            return Task.CompletedTask;
        }
    }
}
