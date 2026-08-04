using BoltPreload.Application.Diagnostics;
using BoltPreload.Application.Ingestion;
using BoltPreload.Fx.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BoltPreload.Tests.Ingestion;

/// <summary>
/// WAL 重放服务（<see cref="WalReplayService"/>）。
/// <para>
/// WAL 本身有 <see cref="LocalFileMeasurementWalTests"/> 覆盖，这里只管服务这一层的两个决定。
/// </para>
/// <para>
/// 重的是 <c>evaluateAlarms=false</c>。库恢复后积压的是几小时前的数据，喂给告警状态机等于
/// 拿旧读数重判一遍：早已消失的越限会重新触发，on-delay 的连续计数被乱序数据冲乱，
/// 现场收到一串"刚刚发生"的告警，对应的却是昨晚的工况。这一条只在这里做，
/// <c>IMeasurementSink</c> 的两个重载看起来一样好用，接错了功能全对、数据全进库，
/// 只是告警多了一批凭空来的。
/// </para>
/// </summary>
public sealed class WalReplayServiceTests
{
    [Fact]
    public async Task Replay_WritesWithoutFeedingTheAlarmStateMachine()
    {
        var wal = new FakeWal(pending: 1);
        var sink = new RecordingSink();

        await RunUntilAsync(wal, sink, () => sink.Writes.Count > 0);

        sink.Writes.Should().ContainSingle();
        sink.Writes[0].EvaluateAlarms.Should().BeFalse(
            "重放的是几小时前的数据，评估告警会拿旧读数重新触发一遍");
    }

    [Fact]
    public async Task Replay_WithNothingPending_DoesNotTouchTheWal()
    {
        var wal = new FakeWal(pending: 0);
        var sink = new RecordingSink();
        var health = new BackgroundServiceHealth();

        // 等到确实进过一轮再断言，否则"没重放"也可能只是定时器还没响。
        // 信号取心跳而不是"读过 PendingCount"：后者在启动日志里就读了一次，
        // 把短路判断整个删掉这个信号照样立刻成立，测试就废了
        await RunUntilAsync(wal, sink,
            () => health.Snapshot().Any(s => s.Name == "wal-replay"), health: health);

        wal.ReplayCalls.Should().Be(0, "队列空是正常运行时的常态，每轮都去扫一遍是白开销");
        sink.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_WhenTheWalIsDisabled_NeverRuns()
    {
        // WAL 默认关（Noop 实现 Enabled=false）。不看这个标志的话服务会照常起定时器空转，
        // 而 PendingCount 永远是 0，日志和心跳都显示它在正常工作
        var wal = new FakeWal(pending: 5) { Enabled = false };
        var sink = new RecordingSink();
        var log = new CapturingLogger<WalReplayService>();

        await RunUntilAsync(wal, sink,
            () => log.Messages(LogLevel.Information, "未启用").Count > 0, log);

        wal.ReplayCalls.Should().Be(0);
    }

    /// <summary>起服务，等 <paramref name="until"/> 成立（定时器间隔取下界 1 秒），再停掉。</summary>
    private static async Task RunUntilAsync(
        FakeWal wal, RecordingSink sink, Func<bool> until,
        ILogger<WalReplayService>? logger = null, BackgroundServiceHealth? health = null)
    {
        var svc = new WalReplayService(
            wal, sink, health ?? new BackgroundServiceHealth(),
            Options.Create(new MeasurementWalOptions { ReplayIntervalSeconds = 1 }),
            logger ?? NullLogger<WalReplayService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!until() && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            until().Should().BeTrue("等了 10 秒还没发生，服务没按预期跑起来");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FakeWal : IMeasurementWal
    {
        private long _pending;
        public FakeWal(long pending) => _pending = pending;

        public bool Enabled { get; init; } = true;
        public long PendingCount => _pending;
        public int ReplayCalls { get; private set; }

        public ValueTask AppendAsync(FxDecodedEnvelope envelope, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public async Task<int> ReplayAsync(
            Func<FxDecodedEnvelope, CancellationToken, Task> writeAsync, CancellationToken ct = default)
        {
            ReplayCalls++;
            var n = 0;
            while (_pending > 0)
            {
                await writeAsync(Measurement(), ct);
                _pending--;
                n++;
            }
            return n;
        }

        private static FxDecodedEnvelope Measurement()
        {
            var ts = DateTimeOffset.UtcNow.AddHours(-3);   // 积压数据本来就是旧的
            var cr = new FxChannelResultDto(
                Address: 1, Channel: 9, StatusCode: 0,
                EchoTimeNs: 32000m, EchoTime2Ns: null, SpTempC: 16.7m, DeltaTofNs: 12.3m,
                Gain: 16f, EchoWave: null, LowAi: null, HighAi: null, Angle: null, MidResults: null);
            return new FxDecodedEnvelope(1, ts, ts, FxResultType.ChannelResult, ChannelResult: cr);
        }
    }

    private sealed class RecordingSink : IMeasurementSink
    {
        private readonly List<(FxDecodedEnvelope Envelope, bool EvaluateAlarms)> _writes = new();

        public IReadOnlyList<(FxDecodedEnvelope Envelope, bool EvaluateAlarms)> Writes
        {
            get { lock (_writes) return _writes.ToList(); }
        }

        public Task WriteAsync(FxDecodedEnvelope envelope, CancellationToken ct = default)
            => WriteAsync(envelope, evaluateAlarms: true, ct);

        public Task WriteAsync(FxDecodedEnvelope envelope, bool evaluateAlarms, CancellationToken ct = default)
        {
            lock (_writes) _writes.Add((envelope, evaluateAlarms));
            return Task.CompletedTask;
        }
    }
}
