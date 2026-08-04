using System.Collections.Concurrent;
using System.Threading.Channels;
using BoltPreload.Application.Devices;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Application.Ingestion;
using BoltPreload.Fx;
using BoltPreload.Fx.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BoltPreload.Tests.Devices.ProbeScan;

/// <summary>
/// 验证 Orchestrator 的核心不变量：
/// 1. 每个 requested channel 都收到了 SetWorkMode(SingleMeas) 调用；
/// 2. 主循环结束（无论 completed / aborted / failed）都通过 finally 恢复 MultiMeas；
/// 3. ProbeScanState 在结束时被释放，可以再次 TryBegin；
/// 4. SaveResultAsync 与 UpdateChannelHardwareStateAsync 都被调到。
/// 不验证 SQL，只验证编排逻辑。
/// </summary>
public class ProbeScanOrchestratorTests
{
    [Fact]
    public async Task RunAsync_ScansAllChannels_AndRestoresMultiMeas()
    {
        var connector = new FakeConnector(deviceId: 1);
        var deviceManager = new FakeDeviceManager(connector);
        var repo = new FakeRepository();
        var state = new ProbeScanState();
        var options = Options.Create(new ProbeScanOptions
        {
            DefaultSampleSeconds = 1,
            DefaultSettleSeconds = 0,
            DefaultMinSamples = 1
        });

        var orchestrator = new ProbeScanOrchestrator(
            deviceManager, repo, state, new BoltPreload.Application.Devices.Acceptance.AcceptanceSessionState(), options,
            NullLogger<ProbeScanOrchestrator>.Instance);

        var req = new ProbeScanRequest(
            DeviceId: 1,
            Channels: new ushort[] { 1, 2, 3 },
            SettleSeconds: 0,
            SampleSeconds: 1,
            Strategy: "single_meas",
            Thresholds: new ProbeScanThresholds(1, 20, 5, 600));

        var view = await orchestrator.StartAsync(req, requestedByUserId: null, CancellationToken.None);
        view.Status.Should().Be("pending");

        // 等扫描跑完（3 通道 × 1s = ~3s，给 8s 余量）
        await WaitForAsync(() => repo.GetStatus(view.ScanRunId) is "completed" or "failed" or "aborted",
            TimeSpan.FromSeconds(8));

        repo.GetStatus(view.ScanRunId).Should().Be("completed");

        // 每个通道都被 SingleMeas 选过 + 最后 MultiMeas 恢复
        var modeCalls = connector.WorkModeCalls.ToArray();
        modeCalls.Where(c => c.Mode == FxWorkMode.SingleMeas)
            .Select(c => c.Channel).Should().BeEquivalentTo(new ushort[] { 1, 2, 3 });
        modeCalls.Last().Mode.Should().Be(FxWorkMode.MultiMeas);
        modeCalls.Last().Channel.Should().Be((ushort)0);

        repo.SavedSlots.Should().HaveCount(3);
        repo.HardwareStateUpdates.Should().HaveCount(3);
        state.IsRunning(1).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_AbortMidway_StillRestoresMultiMeas()
    {
        var connector = new FakeConnector(deviceId: 1);
        var deviceManager = new FakeDeviceManager(connector);
        var repo = new FakeRepository();
        var state = new ProbeScanState();
        var options = Options.Create(new ProbeScanOptions
        {
            DefaultSampleSeconds = 1,
            DefaultSettleSeconds = 0,
            DefaultMinSamples = 1
        });

        var orchestrator = new ProbeScanOrchestrator(
            deviceManager, repo, state, new BoltPreload.Application.Devices.Acceptance.AcceptanceSessionState(), options,
            NullLogger<ProbeScanOrchestrator>.Instance);

        var req = new ProbeScanRequest(
            DeviceId: 1,
            Channels: new ushort[] { 1, 2, 3, 4, 5 },
            SettleSeconds: 0,
            SampleSeconds: 2,
            Strategy: "single_meas",
            Thresholds: new ProbeScanThresholds(1, 20, 5, 600));

        var view = await orchestrator.StartAsync(req, null, CancellationToken.None);

        // 大约 1 秒后中止 —— 至少第 1 个通道应该跑完了
        await Task.Delay(1500);
        orchestrator.TryAbort(1, view.ScanRunId).Should().BeTrue();

        await WaitForAsync(() => repo.GetStatus(view.ScanRunId) is "completed" or "failed" or "aborted",
            TimeSpan.FromSeconds(8));

        repo.GetStatus(view.ScanRunId).Should().Be("aborted");

        // 关键不变量：abort 后 MultiMeas 必须被恢复
        connector.WorkModeCalls.Last().Mode.Should().Be(FxWorkMode.MultiMeas);
        state.IsRunning(1).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_McrSingleStrategy_RestoresOriginalMcrAndMultiMeas()
    {
        var connector = new FakeConnector(deviceId: 1);
        var deviceManager = new FakeDeviceManager(connector);
        var repo = new FakeRepository
        {
            // 扫描前 MCR 启用通道集 = 1-40，扫描完后必须恢复回来
            OriginalEnabledChannels = Enumerable.Range(1, 40).Select(i => (ushort)i).ToArray()
        };
        var state = new ProbeScanState();
        var options = Options.Create(new ProbeScanOptions
        {
            DefaultSampleSeconds = 1,
            DefaultSettleSeconds = 0,
            DefaultMinSamples = 1
        });

        var orchestrator = new ProbeScanOrchestrator(
            deviceManager, repo, state, new BoltPreload.Application.Devices.Acceptance.AcceptanceSessionState(), options,
            NullLogger<ProbeScanOrchestrator>.Instance);

        var req = new ProbeScanRequest(
            DeviceId: 1,
            Channels: new ushort[] { 3, 5 },
            SettleSeconds: 0,
            SampleSeconds: 1,
            Strategy: "mcr_single",
            Thresholds: new ProbeScanThresholds(1, 20, 5, 600));

        var view = await orchestrator.StartAsync(req, null, CancellationToken.None);

        await WaitForAsync(() => repo.GetStatus(view.ScanRunId) is "completed" or "failed" or "aborted",
            TimeSpan.FromSeconds(8));

        repo.GetStatus(view.ScanRunId).Should().Be("completed");

        // 扫描中每个 ch 都应有一次 EnabledChannels=[ch] 的 MCR 下发
        connector.McrConfigCalls.Should().Contain(call => call.SequenceEqual(new ushort[] { 3 }));
        connector.McrConfigCalls.Should().Contain(call => call.SequenceEqual(new ushort[] { 5 }));

        // 关键：最后一次 MCR 必须是原始的 1-40 全集
        connector.McrConfigCalls.Last().Should().BeEquivalentTo(Enumerable.Range(1, 40).Select(i => (ushort)i));

        // 工作模式最后必须是 MultiMeas
        connector.WorkModeCalls.Last().Mode.Should().Be(FxWorkMode.MultiMeas);
    }

    [Fact]
    public async Task StartAsync_DeviceAlreadyHasOpenRun_ThrowsConflict()
    {
        var connector = new FakeConnector(deviceId: 1);
        var deviceManager = new FakeDeviceManager(connector);
        var repo = new FakeRepository();
        repo.PreSeedOpenRun(deviceId: 1, scanRunId: 99);
        var state = new ProbeScanState();
        var options = Options.Create(new ProbeScanOptions());

        var orchestrator = new ProbeScanOrchestrator(
            deviceManager, repo, state, new BoltPreload.Application.Devices.Acceptance.AcceptanceSessionState(), options,
            NullLogger<ProbeScanOrchestrator>.Instance);

        var req = new ProbeScanRequest(
            DeviceId: 1,
            Channels: new ushort[] { 1 },
            SettleSeconds: 0,
            SampleSeconds: 1,
            Strategy: "single_meas",
            Thresholds: new ProbeScanThresholds(1, 20, 5, 600));

        var act = async () => await orchestrator.StartAsync(req, null, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<ProbeScanConflictException>();
        ex.Which.ExistingScanRunId.Should().Be(99);
    }

    [Fact]
    public async Task RunAsync_McrSingle_SnapshotLoadFails_AbortsWithoutRewritingMcr()
    {
        var connector = new FakeConnector(deviceId: 1);
        var deviceManager = new FakeDeviceManager(connector);
        // 读取原始 MCR 通道快照失败：mcr_single 若继续扫描会把 MCR 改成单通道且事后无法恢复
        var repo = new FakeRepository { ThrowOnLoadEnabledChannels = true };
        var state = new ProbeScanState();
        var options = Options.Create(new ProbeScanOptions
        {
            DefaultSampleSeconds = 1, DefaultSettleSeconds = 0, DefaultMinSamples = 1
        });

        var orchestrator = new ProbeScanOrchestrator(
            deviceManager, repo, state, new BoltPreload.Application.Devices.Acceptance.AcceptanceSessionState(), options, NullLogger<ProbeScanOrchestrator>.Instance);

        var req = new ProbeScanRequest(
            DeviceId: 1, Channels: new ushort[] { 3, 5 },
            SettleSeconds: 0, SampleSeconds: 1, Strategy: "mcr_single",
            Thresholds: new ProbeScanThresholds(1, 20, 5, 600));

        var view = await orchestrator.StartAsync(req, null, CancellationToken.None);
        await WaitForAsync(() => repo.GetStatus(view.ScanRunId) is "completed" or "failed" or "aborted",
            TimeSpan.FromSeconds(8));

        // 关键：必须在改动任何 MCR 之前失败，绝不能留下"扫描后只监测最后一个通道"的隐患
        repo.GetStatus(view.ScanRunId).Should().Be("failed");
        connector.McrConfigCalls.Should().BeEmpty();
        state.IsRunning(1).Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_ThenImmediateAbort_StillReleasesStateAndRestoresMultiMeas()
    {
        var connector = new FakeConnector(deviceId: 1);
        var deviceManager = new FakeDeviceManager(connector);
        var repo = new FakeRepository();
        var state = new ProbeScanState();
        var options = Options.Create(new ProbeScanOptions
        {
            DefaultSampleSeconds = 2, DefaultSettleSeconds = 0, DefaultMinSamples = 1
        });

        var orchestrator = new ProbeScanOrchestrator(
            deviceManager, repo, state, new BoltPreload.Application.Devices.Acceptance.AcceptanceSessionState(), options, NullLogger<ProbeScanOrchestrator>.Instance);

        var req = new ProbeScanRequest(
            DeviceId: 1, Channels: new ushort[] { 1, 2, 3 },
            SettleSeconds: 0, SampleSeconds: 2, Strategy: "single_meas",
            Thresholds: new ProbeScanThresholds(1, 20, 5, 600));

        var view = await orchestrator.StartAsync(req, null, CancellationToken.None);
        // 启动后立刻中止：Task.Run(CancellationToken.None) 保证主体仍执行、finally 一定清理占位（不会永久卡 IsRunning）
        orchestrator.TryAbort(1, view.ScanRunId).Should().BeTrue();

        await WaitForAsync(() => repo.GetStatus(view.ScanRunId) is "completed" or "failed" or "aborted",
            TimeSpan.FromSeconds(8));

        state.IsRunning(1).Should().BeFalse();
        connector.WorkModeCalls.Last().Mode.Should().Be(FxWorkMode.MultiMeas);
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate()) return;
            await Task.Delay(50);
        }
    }

    // ============ 测试替身 ============


    private sealed class FakeRepository : IProbeScanRepository
    {
        private readonly ConcurrentDictionary<long, string> _status = new();
        private long? _openRunForDevice1;
        private long _nextId = 1;
        public ConcurrentQueue<ProbeScanResultSlot> SavedSlots = new();
        public ConcurrentQueue<(ushort Ch, ProbeScanVerdict V)> HardwareStateUpdates = new();

        public void PreSeedOpenRun(long deviceId, long scanRunId)
        {
            if (deviceId == 1) _openRunForDevice1 = scanRunId;
            _status[scanRunId] = "running";
        }
        public string? GetStatus(long runId) => _status.TryGetValue(runId, out var s) ? s : null;

        public Task<long?> FindOpenRunAsync(long deviceId, CancellationToken ct)
            => Task.FromResult(deviceId == 1 ? _openRunForDevice1 : null);

        public Task<long> CreatePendingAsync(ProbeScanRequest req, long? userId, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _nextId);
            _status[id] = "pending";
            return Task.FromResult(id);
        }
        public Task MarkRunningAsync(long id, DateTimeOffset at, CancellationToken ct)
        { _status[id] = "running"; return Task.CompletedTask; }
        public Task MarkCompletedAsync(long id, ProbeScanSummary s, DateTimeOffset at, CancellationToken ct)
        { _status[id] = "completed"; return Task.CompletedTask; }
        public Task MarkFailedAsync(long id, string err, DateTimeOffset at, CancellationToken ct)
        { _status[id] = "failed"; return Task.CompletedTask; }
        public Task MarkAbortedAsync(long id, DateTimeOffset at, CancellationToken ct)
        { _status[id] = "aborted"; return Task.CompletedTask; }

        public Task SaveResultAsync(long runId, long deviceId, ProbeScanResultSlot slot, CancellationToken ct)
        { SavedSlots.Enqueue(slot); return Task.CompletedTask; }

        public Task UpdateChannelHardwareStateAsync(long deviceId, ushort ch, ProbeScanVerdict v, DateTimeOffset at, CancellationToken ct)
        { HardwareStateUpdates.Enqueue((ch, v)); return Task.CompletedTask; }

        public Task<ProbeScanWindowStats> LoadWindowStatsAsync(long deviceId, ushort ch, DateTimeOffset s, DateTimeOffset e, CancellationToken ct)
            => Task.FromResult(new ProbeScanWindowStats(0, 0, null, null, null, null, null));

        public Task<ProbeScanRunView?> LoadRunAsync(long id, CancellationToken ct)
        {
            var st = GetStatus(id) ?? "pending";
            return Task.FromResult<ProbeScanRunView?>(new ProbeScanRunView(
                ScanRunId: id, DeviceId: 1, Status: st, Strategy: "single_meas",
                RequestedChannels: Array.Empty<ushort>(), SettleSeconds: 0, SampleSeconds: 1,
                Thresholds: new ProbeScanThresholds(1, 20, 5, 600),
                RequestedAt: DateTimeOffset.UtcNow, StartedAt: null, FinishedAt: null,
                ErrorMessage: null, Summary: null,
                Results: Array.Empty<ProbeScanChannelResultView>()));
        }

        public Task<ProbeScanRunView?> LoadLatestRunAsync(long deviceId, CancellationToken ct)
            => Task.FromResult<ProbeScanRunView?>(null);

        public Task<int> CleanStaleRunsAsync(CancellationToken ct) => Task.FromResult(0);

        public IReadOnlyList<ushort> OriginalEnabledChannels { get; set; } = Array.Empty<ushort>();
        public bool ThrowOnLoadEnabledChannels { get; set; }
        public Task<IReadOnlyList<ushort>> LoadEnabledChannelsAsync(long deviceId, CancellationToken ct)
            => ThrowOnLoadEnabledChannels
                ? Task.FromException<IReadOnlyList<ushort>>(new InvalidOperationException("模拟读取 MCR 快照失败"))
                : Task.FromResult(OriginalEnabledChannels);
    }
}
