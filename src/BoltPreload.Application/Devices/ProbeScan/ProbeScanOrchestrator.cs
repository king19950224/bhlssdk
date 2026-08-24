using BoltPreload.Application.Devices.Acceptance;
using BoltPreload.Application.Ingestion;
using BoltPreload.Fx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoltPreload.Application.Devices.ProbeScan;

/// <summary>
/// 通道自动检测主流程。
/// 关键不变量：
/// 1. 必须 try/finally 包扫描循环；finally 用 CancellationToken.None 恢复 MultiMeas，
///    不能因为用户 abort 把恢复也取消掉。
/// 2. 整个过程独占设备工作模式：Watchdog 与其他写 WorkMode 的接口必须通过 ProbeScanState 检查跳过。
/// 3. 不直接消费 connector.Results — 那会与 IngestionService 抢消费；
///    采样数据由 IngestionService → MeasurementSink/WaveStore 落库，扫描器只查 DB 窗口。
/// </summary>
public sealed class ProbeScanOrchestrator : IProbeScanOrchestrator
{
    private readonly IDeviceConnectorManager _deviceManager;
    private readonly IProbeScanRepository _repo;
    private readonly ProbeScanState _state;
    private readonly AcceptanceSessionState _acceptance;
    private readonly ProbeScanOptions _options;
    private readonly ILogger<ProbeScanOrchestrator> _logger;
    private readonly IngestDegradeState? _degrade;

    public ProbeScanOrchestrator(
        IDeviceConnectorManager deviceManager,
        IProbeScanRepository repo,
        ProbeScanState state,
        AcceptanceSessionState acceptance,
        IOptions<ProbeScanOptions> options,
        ILogger<ProbeScanOrchestrator> logger,
        // 可空：判定依赖"窗口内数据已落库"，降级期查库是空的、结果必错——有降级态可看就用（单测可不传）
        IngestDegradeState? degrade = null)
    {
        _deviceManager = deviceManager;
        _repo = repo;
        _state = state;
        _acceptance = acceptance;
        _options = options.Value;
        _logger = logger;
        _degrade = degrade;
    }

    public async Task<ProbeScanRunView> StartAsync(ProbeScanRequest req, long? requestedByUserId, CancellationToken ct)
    {
        var runtime = _deviceManager.Find(req.DeviceId)
            ?? throw new InvalidOperationException($"设备未在运行时管理器中找到：{req.DeviceId}");

        if (runtime.Connector.State != FxConnectorState.Connected)
            throw new InvalidOperationException($"设备未连接，无法启动扫描：state={runtime.Connector.State}");

        // 判定完全依赖"窗口内样本已落库"：DB 写入降级期样本进的是 WAL、要等恢复后才重放，
        // 窗口查询查到的是空集，全部通道会被误判 not_reporting——这种结果比没有更糟，直接拒绝
        if (_degrade is { DbWriteDegraded: true })
            throw new InvalidOperationException(
                "采集入库处于降级状态（DB 连续写入失败，样本走 WAL 兜底暂不可查），扫描判定会全部失真；请先恢复数据库再扫描");

        // 与接入验收会话互斥：两者都要独占改写 MCR 轮询集，并发会把对方的通道集踩掉
        if (_acceptance.IsActive(req.DeviceId))
            throw new InvalidOperationException($"设备 {req.DeviceId} 正在进行接入验收会话，请先结束验收再启动扫描");

        var existingOpenId = await _repo.FindOpenRunAsync(req.DeviceId, ct);
        if (existingOpenId is not null)
            throw new ProbeScanConflictException(
                $"设备 {req.DeviceId} 已有进行中的扫描 scanRunId={existingOpenId.Value}",
                existingOpenId.Value);

        var runId = await _repo.CreatePendingAsync(req, requestedByUserId, ct);

        var handle = _state.TryBegin(req.DeviceId, runId);
        if (handle is null)
        {
            // DB 里检查通过但进程里被其他线程抢了 — 把 DB 记录置 failed，让 API 409
            await _repo.MarkFailedAsync(runId, "ProbeScanState 占位失败：另一线程已开始扫描",
                DateTimeOffset.UtcNow, ct);
            throw new ProbeScanConflictException("ProbeScanState 占位失败", runId);
        }

        // Task.Run 跑主流程；不要让 HTTP 请求 hold 几分钟。
        // scanCt（ProbeScanState 里的 Cts）是扫描的"主取消令牌"，作为参数传进 RunInternalAsync，
        // 由其内部循环 / finally 响应取消并清理。
        // 调度用 CancellationToken.None：若在此前一刻被 TryAbort 取消，用 scanCt 调度会让 Task.Run
        // 直接判为 Canceled 而【不执行方法体】→ RunInternalAsync 的 finally（含 _state.End）永不运行，
        // 设备会被永久卡在 IsRunning。用 None 调度可保证方法体一定执行、finally 一定清理。
        var scanCt = handle.Cts.Token;
        _ = Task.Run(() => RunInternalAsync(req, runId, scanCt), CancellationToken.None);

        return await _repo.LoadRunAsync(runId, ct)
               ?? throw new InvalidOperationException("插入后查询不到 scan run");
    }

    public Task<ProbeScanRunView?> GetAsync(long scanRunId, CancellationToken ct)
        => _repo.LoadRunAsync(scanRunId, ct);

    public Task<ProbeScanRunView?> GetLatestAsync(long deviceId, CancellationToken ct)
        => _repo.LoadLatestRunAsync(deviceId, ct);

    public bool TryAbort(long deviceId, long scanRunId)
        => _state.TryAbort(deviceId, scanRunId);

    private async Task RunInternalAsync(ProbeScanRequest req, long runId, CancellationToken ct)
    {
        var runtime = _deviceManager.Find(req.DeviceId)!;
        var connector = runtime.Connector;
        var address = runtime.Options.Address;
        const FxWorkMode originalMode = FxWorkMode.MultiMeas;

        // 扫描开始前快照"业务期启用通道集"。mcr_single 策略每次只把单个通道写进 MCR，
        // 扫描完成后必须把这份快照恢复回去，否则业务监测会只跑最后扫描的那个通道。
        // 优先用连接器【实时设备 MCR】快照（扫描前设备真实在轮询的通道集）；它比 DB 的 sampling_enabled
        // 标志更可靠（后者可能与设备实际 MCR 不同步）。实时 MCR 为空再回退 DB。
        IReadOnlyList<ushort> originalEnabledChannels = Array.Empty<ushort>();
        bool snapshotFailed = false;
        try
        {
            var liveMcr = connector.GetSessionMcrChannels();
            originalEnabledChannels = liveMcr.Count > 0
                ? liveMcr
                : await _repo.LoadEnabledChannelsAsync(req.DeviceId, ct);
        }
        catch (Exception ex)
        {
            snapshotFailed = true;
            _logger.LogError(ex, "扫描启动：读取原始 MCR 通道快照失败 device={DeviceId}（mcr_single 策略下恢复可能不准）",
                req.DeviceId);
        }

        var summary = new SummaryAccumulator();
        Exception? failure = null;
        bool aborted = false;
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            await _repo.MarkRunningAsync(runId, startedAt, ct);
            _logger.LogInformation(
                "开始通道扫描 device={DeviceId} run={RunId} channels={ChCount} strategy={Strategy} originalMCR={Mcr}",
                req.DeviceId, runId, req.Channels.Count, req.Strategy, originalEnabledChannels.Count);

            // mcr_single 会逐通道改写 MCR，扫完必须用快照恢复原通道集。若快照【读取失败】(异常) 则无从恢复：
            // 与其扫完把业务监测只留在最后一个通道，不如在改动任何 MCR 之前直接失败（此刻尚未动过 MCR）。
            if (req.Strategy == "mcr_single" && snapshotFailed)
                throw new InvalidOperationException(
                    "mcr_single 策略读取原始 MCR 通道快照失败，已在改动 MCR 前中止扫描，避免扫描后无法恢复业务通道集");

            foreach (var ch in req.Channels)
            {
                if (ct.IsCancellationRequested) { aborted = true; break; }

                var slot = await ScanOneChannelAsync(connector, address, req, ch, ct);
                summary.Add(slot.Verdict);

                // 写结果用 CancellationToken.None：哪怕用户在最后一刻 abort，
                // 已扫到的数据也要落库，不能丢
                await _repo.SaveResultAsync(runId, req.DeviceId, slot, CancellationToken.None);
                await _repo.UpdateChannelHardwareStateAsync(
                    req.DeviceId, ch, slot.Verdict, slot.FinishedAt, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            aborted = true;
        }
        catch (Exception ex)
        {
            failure = ex;
            _logger.LogError(ex, "通道扫描异常 device={DeviceId} run={RunId}", req.DeviceId, runId);
        }
        finally
        {
            var finishedAt = DateTimeOffset.UtcNow;

            // 关键：恢复 MultiMeas 必须用 CancellationToken.None，不能跟着上游 ct 一起取消
            // mcr_single 策略每次只把单个通道写进 MCR，必须先恢复 MCR 通道集，再切回 MultiMeas
            try
            {
                if (req.Strategy == "mcr_single")
                {
                    if (originalEnabledChannels.Count > 0)
                    {
                        await connector.ConfigureMultiChannelPollingAsync(
                            originalEnabledChannels,
                            singleCount: 1, dailyCount: 0, startSeconds: null,
                            cancellationToken: CancellationToken.None);
                        _logger.LogInformation(
                            "扫描完成，已恢复 MCR 通道集 device={DeviceId} channels={Count}",
                            req.DeviceId, originalEnabledChannels.Count);
                    }
                    else
                    {
                        // 既无实时 MCR 也无 DB 启用通道可恢复：设备端 MCR 可能仍停在最后扫描通道。
                        // 显式告警而非静默——运维需重新下发业务 MCR 或核对 sampling_enabled。
                        _logger.LogWarning(
                            "扫描完成但无原始 MCR 通道集可恢复 device={DeviceId}：设备端 MCR 可能仅剩最后扫描通道，" +
                            "请重新下发业务多通道轮询配置或核对 channels.sampling_enabled", req.DeviceId);
                    }
                }

                await connector.SetWorkModeAsync(originalMode, channel: 0, address: address, CancellationToken.None);
                _logger.LogInformation("扫描完成，已恢复 {Mode} device={DeviceId} run={RunId}",
                    originalMode, req.DeviceId, runId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "扫描完成后恢复 MultiMeas / MCR 失败 device={DeviceId} — Watchdog 会接管重连",
                    req.DeviceId);
            }

            _state.End(req.DeviceId);

            try
            {
                if (failure is not null)
                    await _repo.MarkFailedAsync(runId, failure.Message, finishedAt, CancellationToken.None);
                else if (aborted)
                    await _repo.MarkAbortedAsync(runId, finishedAt, CancellationToken.None);
                else
                    await _repo.MarkCompletedAsync(runId, summary.ToSummary(), finishedAt, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "标记扫描结束状态失败 run={RunId}（状态可能停在 running，启动时会清理）", runId);
            }
        }
    }

    private async Task<ProbeScanResultSlot> ScanOneChannelAsync(
        IFxConnector connector, ushort address, ProbeScanRequest req, ushort channel, CancellationToken ct)
    {
        // 切到目标通道
        if (req.Strategy == "mcr_single")
        {
            await connector.ConfigureMultiChannelPollingAsync(
                enabledChannels: new[] { channel },
                singleCount: 1, dailyCount: 0, startSeconds: null, cancellationToken: ct);
            await connector.SetWorkModeAsync(FxWorkMode.MultiMeas, channel: 0, address: address, ct);
        }
        else
        {
            await connector.SetWorkModeAsync(FxWorkMode.SingleMeas, channel: channel, address: address, ct);
        }

        // 稳定期（避免抓到上一个通道的尾巴）
        await Task.Delay(TimeSpan.FromSeconds(req.SettleSeconds), ct);

        // 竞态防御：热插拔收敛（ChannelPresenceService.EnsureFullChannelPollingAsync）在扫描占位前一刻
        // 在途的全通道下发可能落在上面的单通道 MCR 之后——窗口会被稀释成全通道轮询，
        // 目标通道样本数掉一个数量级、误判 inconclusive/not_reporting。核对会话 MCR，不对就重发再稳定一次。
        // 会话视图为空 = 该实现无 MCR 概念（测试桩），跳过核对。
        if (req.Strategy == "mcr_single")
        {
            var liveMcr = connector.GetSessionMcrChannels();
            if (liveMcr.Count > 0 && (liveMcr.Count != 1 || liveMcr[0] != channel))
            {
                _logger.LogWarning(
                    "扫描单通道 MCR 被并发改写为 {Count} 通道（热插拔全通道收敛的在途下发？），重发 [{Ch}] 并重新等稳定期 device={DeviceId}",
                    liveMcr.Count, channel, req.DeviceId);
                await connector.ConfigureMultiChannelPollingAsync(
                    enabledChannels: new[] { channel },
                    singleCount: 1, dailyCount: 0, startSeconds: null, cancellationToken: ct);
                await connector.SetWorkModeAsync(FxWorkMode.MultiMeas, channel: 0, address: address, ct);
                await Task.Delay(TimeSpan.FromSeconds(req.SettleSeconds), ct);
            }
        }

        // 采样窗口：仅记录起止时间，数据由 IngestionService 正常路径落库。
        // 窗口内主动补读 1~2 次 LEchoWave（真机 MultiMeas 可能不随测量带波形，而 active_good 的
        // SNR/峰峰值判据依赖 wave_features；WaveSamplingService 扫描期又刻意跳过本设备）。
        var sampleStart = DateTimeOffset.UtcNow;
        var waveReads = req.Strategy == "mcr_single"
            ? TriggerWaveReadsAsync(connector, address, channel, req.SampleSeconds, ct)
            : Task.CompletedTask;
        await Task.Delay(TimeSpan.FromSeconds(req.SampleSeconds), ct);
        var sampleEnd = DateTimeOffset.UtcNow;
        await waveReads;

        // 入库宽限：窗口尾部的样本可能还在 Ingestion 管道里（逐条 INSERT + 波形文件 I/O），
        // 等一拍再查；样本 ts 是接收时刻，不会把窗口外的数据算进来
        if (_options.DbFlushGraceMs > 0)
            await Task.Delay(TimeSpan.FromMilliseconds(_options.DbFlushGraceMs), ct);

        var stats = await _repo.LoadWindowStatsAsync(req.DeviceId, channel, sampleStart, sampleEnd, ct);
        var verdict = ProbeScanResultJudge.Judge(stats, req.Thresholds);

        // 扫描中途进入 DB 写入降级（启动时的门禁只挡得住开始前就降级的情况）：
        // 样本在 WAL 里没落库，"查不到"≠"没上报"，not_reporting 会误导运维拔查好探头——降为 inconclusive
        if (verdict == ProbeScanVerdict.NotReporting && _degrade is { DbWriteDegraded: true })
        {
            _logger.LogWarning(
                "通道 {Ch} 窗口内查无样本且采集入库正处于降级态，not_reporting 不可信，降判为 inconclusive device={DeviceId}",
                channel, req.DeviceId);
            verdict = ProbeScanVerdict.Inconclusive;
        }

        _logger.LogInformation(
            "通道扫描结果 device={DeviceId} ch={Ch} verdict={Verdict} samples={N} echoRate={Echo} snr={Snr} pp={PP}",
            req.DeviceId, channel, ProbeScanVerdictNames.Format(verdict),
            stats.Samples,
            stats.Samples > 0 ? (decimal)stats.SamplesWithEcho * 100 / stats.Samples : 0m,
            stats.SnrAvgDb, stats.PeakToPeakAvg);

        return new ProbeScanResultSlot(channel, verdict, stats, sampleStart, sampleEnd);
    }

    /// <summary>
    /// 采样窗口内主动读 LEchoWave 补 wave_features（与 WaveSamplingService 同一手法：不切工作模式）。
    /// 响应经 IngestionService → PostgresMeasurementSink.PersistActiveEchoWaveAsync 落库，
    /// ts 为接收时刻，只要在窗口内回来就会被聚合查询算进去。
    /// 窗口 ≥4s 时在半程再补一次。失败只记 Debug——判定端对"波形指标整体缺失"已有纯回波退化路径兜底。
    /// </summary>
    private async Task TriggerWaveReadsAsync(
        IFxConnector connector, ushort address, ushort channel, int sampleSeconds, CancellationToken ct)
    {
        try
        {
            await connector.ReadChannelParamAsync("LEchoWave", channel, address, ct);
            if (sampleSeconds >= 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(sampleSeconds / 2.0), ct);
                await connector.ReadChannelParamAsync("LEchoWave", channel, address, ct);
            }
        }
        catch (OperationCanceledException) { /* 扫描被中止 */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "扫描窗口内主动读 LEchoWave 失败 ch={Channel}（波形指标可能缺失，判定将退化为纯回波）", channel);
        }
    }

    private sealed class SummaryAccumulator
    {
        private int _good, _weak, _noEcho, _notReporting, _inconclusive;

        public void Add(ProbeScanVerdict v)
        {
            switch (v)
            {
                case ProbeScanVerdict.ActiveGood: _good++; break;
                case ProbeScanVerdict.ActiveWeak: _weak++; break;
                case ProbeScanVerdict.ActiveNoEcho: _noEcho++; break;
                case ProbeScanVerdict.NotReporting: _notReporting++; break;
                default: _inconclusive++; break;
            }
        }

        public ProbeScanSummary ToSummary() =>
            new(_good, _weak, _noEcho, _notReporting, _inconclusive);
    }
}

/// <summary>设备已有扫描在跑时再次 StartAsync 抛出；携带冲突的扫描 id 供 API 返回 409。</summary>
public sealed class ProbeScanConflictException : InvalidOperationException
{
    public long ExistingScanRunId { get; }

    public ProbeScanConflictException(string message, long existingScanRunId) : base(message)
    {
        ExistingScanRunId = existingScanRunId;
    }
}
