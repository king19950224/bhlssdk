using BoltPreload.Api.Auth;
using BoltPreload.Application.Devices.ProbeScan;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 通道自动检测 API。详见 docs/probe-scan-design.md §6。
/// 所有写入路径要求 WriteAccess（扫描会中断业务监测）。
/// </summary>
public static class ProbeScanEndpoints
{
    public static IEndpointRouteBuilder MapProbeScanEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices/{deviceId:long}/channels/probe-scan")
            .WithTags("ProbeScan");

        group.MapPost("/", async (
            long deviceId,
            ProbeScanRequestBody? body,
            IProbeScanOrchestrator orchestrator,
            IOptions<ProbeScanOptions> options,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var opt = options.Value;

            // channels 缺省 = 1..40（与设备 channel_count 对齐由 0012 迁移保证）
            ushort[] channels = body?.Channels is { Length: > 0 }
                ? body.Channels.Where(c => c > 0).Distinct().OrderBy(c => c).ToArray()
                : Enumerable.Range(1, 40).Select(x => (ushort)x).ToArray();

            if (channels.Length == 0)
                return Results.BadRequest(new { error = "channels 不能为空" });
            if (channels.Any(c => c < 1 || c > 64))
                return Results.BadRequest(new { error = "通道号超出 1..64 范围" });

            var settle = body?.SettleSeconds ?? opt.DefaultSettleSeconds;
            var sample = body?.SampleSeconds ?? opt.DefaultSampleSeconds;
            if (settle < 0 || settle > 30)
                return Results.BadRequest(new { error = "settleSeconds 必须在 0..30 之间" });
            if (sample < 1 || sample > 60)
                return Results.BadRequest(new { error = "sampleSeconds 必须在 1..60 之间" });

            var strategy = body?.Strategy?.ToLowerInvariant() ?? opt.DefaultStrategy;
            if (strategy != "single_meas" && strategy != "mcr_single")
                return Results.BadRequest(new { error = $"未知 strategy: {strategy}" });

            var thresholds = new ProbeScanThresholds(
                MinSamples: body?.MinSamples ?? opt.DefaultMinSamples,
                SnrGoodDb: body?.SnrGoodDb ?? opt.DefaultSnrGoodDb,
                EchoStdGoodNs: body?.EchoStdGoodNs ?? opt.DefaultEchoStdGoodNs,
                PeakToPeakMin: body?.PeakToPeakMin ?? opt.DefaultPeakToPeakMin);

            var req = new ProbeScanRequest(
                DeviceId: deviceId,
                Channels: channels,
                SettleSeconds: settle,
                SampleSeconds: sample,
                Strategy: strategy,
                Thresholds: thresholds);

            long? requestedByUserId = TryGetUserId(user);

            try
            {
                var view = await orchestrator.StartAsync(req, requestedByUserId, ct);
                var estSeconds = channels.Length * (settle + sample);
                return Results.Ok(new
                {
                    scanRunId = view.ScanRunId,
                    deviceId,
                    status = view.Status,
                    strategy = view.Strategy,
                    requestedChannels = view.RequestedChannels,
                    estimatedSeconds = estSeconds,
                    settleSeconds = view.SettleSeconds,
                    sampleSeconds = view.SampleSeconds,
                    thresholds = view.Thresholds,
                    requestedAt = view.RequestedAt,
                    warning = $"扫描期间设备业务监测会中断，预计 {estSeconds} 秒"
                });
            }
            catch (ProbeScanConflictException ex)
            {
                return Results.Conflict(new
                {
                    error = ex.Message,
                    existingScanRunId = ex.ExistingScanRunId
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("StartProbeScan")
        .WithSummary("启动通道自动检测（逐通道 SetWorkMode SingleMeas + 短窗口判定）");

        group.MapGet("/latest", async (
            long deviceId,
            IProbeScanOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            var view = await orchestrator.GetLatestAsync(deviceId, ct);
            return view is null ? Results.NotFound() : Results.Ok(view);
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("GetLatestProbeScan")
        .WithSummary("该设备最近一次扫描结果");

        group.MapGet("/{scanRunId:long}", async (
            long deviceId,
            long scanRunId,
            IProbeScanOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            var view = await orchestrator.GetAsync(scanRunId, ct);
            if (view is null) return Results.NotFound();
            if (view.DeviceId != deviceId)
                return Results.BadRequest(new { error = "scanRunId 不属于此设备" });
            return Results.Ok(view);
        })
        .RequireAuthorization(AuthPolicies.LoggedIn)
        .WithName("GetProbeScan")
        .WithSummary("查询指定扫描的当前状态与已完成的部分结果");

        group.MapPost("/{scanRunId:long}/abort", (
            long deviceId,
            long scanRunId,
            IProbeScanOrchestrator orchestrator) =>
        {
            var ok = orchestrator.TryAbort(deviceId, scanRunId);
            return ok
                ? Results.Ok(new { aborted = true, scanRunId })
                : Results.NotFound(new { error = "未找到运行中的扫描", scanRunId });
        })
        .RequireAuthorization(AuthPolicies.WriteAccess)
        .WithName("AbortProbeScan")
        .WithSummary("中止运行中的扫描，恢复 MultiMeas");

        return app;
    }

    private static long? TryGetUserId(ClaimsPrincipal user)
    {
        var sub = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? user.FindFirst("sub")?.Value;
        return long.TryParse(sub, out var v) ? v : null;
    }
}

/// <summary>POST /probe-scan 请求体（扁平结构，便于 curl 调）。</summary>
public sealed class ProbeScanRequestBody
{
    public ushort[]? Channels { get; set; }
    public int? SettleSeconds { get; set; }
    public int? SampleSeconds { get; set; }
    public string? Strategy { get; set; }
    public int? MinSamples { get; set; }
    public double? SnrGoodDb { get; set; }
    public double? EchoStdGoodNs { get; set; }
    public int? PeakToPeakMin { get; set; }
}
