namespace BoltPreload.Application.Devices.ProbeScan;

/// <summary>POST /probe-scan 请求体。</summary>
public sealed record ProbeScanRequest(
    long DeviceId,
    IReadOnlyList<ushort> Channels,
    int SettleSeconds,
    int SampleSeconds,
    string Strategy,
    ProbeScanThresholds Thresholds);

/// <summary>扫描运行记录的视图（API 返回 + Orchestrator 内部用）。</summary>
public sealed record ProbeScanRunView(
    long ScanRunId,
    long DeviceId,
    string Status,
    string Strategy,
    IReadOnlyList<ushort> RequestedChannels,
    int SettleSeconds,
    int SampleSeconds,
    ProbeScanThresholds Thresholds,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? ErrorMessage,
    ProbeScanSummary? Summary,
    IReadOnlyList<ProbeScanChannelResultView> Results);

/// <summary>一次扫描各判定结果的通道数统计。</summary>
public sealed record ProbeScanSummary(
    int ActiveGood,
    int ActiveWeak,
    int ActiveNoEcho,
    int NotReporting,
    int Inconclusive);

/// <summary>单通道扫描结果：判定 + 回波统计指标（回波率/均值/抖动/峰峰值/SNR）。</summary>
public sealed record ProbeScanChannelResultView(
    int ChannelNo,
    string? Name,
    string Verdict,
    int Samples,
    int SamplesWithEcho,
    decimal? EchoRatePct,
    decimal? EchoAvgNs,
    decimal? EchoStdNs,
    decimal? PeakToPeakAvg,
    decimal? SnrAvgDb,
    string? BoundBoltCode,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt);

/// <summary>Repository 写入用的单通道判定槽位。</summary>
public sealed record ProbeScanResultSlot(
    ushort ChannelNo,
    ProbeScanVerdict Verdict,
    ProbeScanWindowStats Stats,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt);

/// <summary>扫描运行记录的存取。Npgsql 实现在 Infrastructure；抽出接口是为了单测能用内存假仓库跑 Orchestrator。</summary>
public interface IProbeScanRepository
{
    /// <summary>检查该设备是否已有 pending/running 扫描；有则返回 scan_run_id。</summary>
    Task<long?> FindOpenRunAsync(long deviceId, CancellationToken ct);

    /// <summary>新建一条 pending 扫描记录，返回 scan_run_id。</summary>
    Task<long> CreatePendingAsync(ProbeScanRequest req, long? requestedByUserId, CancellationToken ct);

    Task MarkRunningAsync(long scanRunId, DateTimeOffset startedAt, CancellationToken ct);

    Task MarkCompletedAsync(long scanRunId, ProbeScanSummary summary, DateTimeOffset finishedAt, CancellationToken ct);

    Task MarkFailedAsync(long scanRunId, string errorMessage, DateTimeOffset finishedAt, CancellationToken ct);

    Task MarkAbortedAsync(long scanRunId, DateTimeOffset finishedAt, CancellationToken ct);

    Task SaveResultAsync(long scanRunId, long deviceId, ProbeScanResultSlot slot, CancellationToken ct);

    /// <summary>
    /// 把 verdict 映射到 channels.hardware_state，并刷 last_seen_at（仅当 verdict != inconclusive）。
    /// 找不到对应 channel 行直接跳过，不抛错。
    /// </summary>
    Task UpdateChannelHardwareStateAsync(
        long deviceId,
        ushort channelNo,
        ProbeScanVerdict verdict,
        DateTimeOffset at,
        CancellationToken ct);

    /// <summary>查询采样窗口内 measurements + unbound_measurements + wave_features 的聚合。</summary>
    Task<ProbeScanWindowStats> LoadWindowStatsAsync(
        long deviceId,
        ushort channelNo,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken ct);

    /// <summary>查询扫描视图（含已写入的部分 results；用于运行中轮询和完成后回看）。</summary>
    Task<ProbeScanRunView?> LoadRunAsync(long scanRunId, CancellationToken ct);

    /// <summary>该设备最近一次扫描。</summary>
    Task<ProbeScanRunView?> LoadLatestRunAsync(long deviceId, CancellationToken ct);

    /// <summary>启动时清理 status IN (pending,running) 的脏记录，避免 API 崩溃后挂在那里。</summary>
    Task<int> CleanStaleRunsAsync(CancellationToken ct);

    /// <summary>
    /// 读取该设备 channels 表中 sampling_enabled=true 的通道列表。
    /// 用于 mcr_single 策略扫描完成后，把设备端 MCR 恢复到扫描前的"启用通道集"。
    /// </summary>
    Task<IReadOnlyList<ushort>> LoadEnabledChannelsAsync(long deviceId, CancellationToken ct);
}

/// <summary>探头扫描编排：启动/查询/中止一次全通道探头识别扫描（同一设备同一时刻只允许一次）。</summary>
public interface IProbeScanOrchestrator
{
    /// <summary>同步启动一次扫描：插记录、占位 ProbeScanState、Task.Run 跑主流程，立刻返回 run view。</summary>
    Task<ProbeScanRunView> StartAsync(ProbeScanRequest req, long? requestedByUserId, CancellationToken ct);

    Task<ProbeScanRunView?> GetAsync(long scanRunId, CancellationToken ct);

    Task<ProbeScanRunView?> GetLatestAsync(long deviceId, CancellationToken ct);

    /// <summary>中止当前扫描；返回 true 表示成功发出 cancel 信号。</summary>
    bool TryAbort(long deviceId, long scanRunId);
}
