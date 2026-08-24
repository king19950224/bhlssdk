using BoltPreload.Api.Auth;
using BoltPreload.Api.Runtime;
using BoltPreload.Application.Devices;
using BoltPreload.Application.Devices.ProbeScan;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using System.Security.Claims;

namespace BoltPreload.Api.Endpoints;

/// <summary>
/// 通道自动检测 API。详见 资料/工程文档/probe-scan-design.md §6。
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
            IDeviceConnectorManager deviceManager,
            NpgsqlDataSource ds,
            IConfiguration configuration,
            ILoggerFactory loggerFactory,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var opt = options.Value;
            var logger = loggerFactory.CreateLogger("BoltPreload.Api.Endpoints.ProbeScan");
            var runtime = deviceManager.Find(deviceId);

            // channels 缺省 = 1..channel_count（取设备运行时的物理通道数，48 通道设备扫全 48；
            // 运行时缺通道数信息时回退 1..40 的历史行为）
            var knownChannelCount = (int)(runtime?.Options.ChannelCount ?? 0);
            var defaultCount = knownChannelCount > 0 ? knownChannelCount : 40;
            ushort[] channels = body?.Channels is { Length: > 0 }
                ? body.Channels.Where(c => c > 0).Distinct().OrderBy(c => c).ToArray()
                : Enumerable.Range(1, defaultCount).Select(x => (ushort)x).ToArray();

            if (channels.Length == 0)
                return Results.BadRequest(new { error = "channels 不能为空" });
            if (channels.Any(c => c < 1 || c > DeviceLimits.MaxChannelsPerDevice))
                return Results.BadRequest(new { error = $"通道号超出 1..{DeviceLimits.MaxChannelsPerDevice} 范围" });
            // 按设备实际物理通道数校验（能拿到时）：扫不存在的口每个白耗 settle+sample 秒，
            // 还会留下一排误导性的 not_reporting 结果行
            if (knownChannelCount > 0 && channels.Any(c => c > knownChannelCount))
                return Results.BadRequest(new { error = $"通道号超出设备通道数 1..{knownChannelCount}" });

            var settle = body?.SettleSeconds ?? opt.DefaultSettleSeconds;
            var sample = body?.SampleSeconds ?? opt.DefaultSampleSeconds;
            if (settle < 0 || settle > 30)
                return Results.BadRequest(new { error = "settleSeconds 必须在 0..30 之间" });
            if (sample < 1 || sample > 60)
                return Results.BadRequest(new { error = "sampleSeconds 必须在 1..60 之间" });

            var strategy = body?.Strategy?.ToLowerInvariant() ?? opt.DefaultStrategy;
            if (strategy != "single_meas" && strategy != "mcr_single")
                return Results.BadRequest(new { error = $"未知 strategy: {strategy}" });

            // 墓碑（软删）通道的数据在入库路径被直接丢弃（按设计不自动复活），扫出来必为 not_reporting，
            // 与"口子上真没探头"无法区分——从扫描集剔除并在响应里明示，别让报告误导排障
            ushort[] skippedTombstones = [];
            try
            {
                var tombstones = await LoadTombstonedChannelsAsync(ds, deviceId, ct);
                if (tombstones.Count > 0)
                {
                    skippedTombstones = channels.Where(tombstones.Contains).ToArray();
                    if (skippedTombstones.Length > 0)
                    {
                        channels = channels.Where(c => !tombstones.Contains(c)).ToArray();
                        logger.LogWarning(
                            "扫描请求剔除 {Count} 个墓碑通道 device={DeviceId} channels=[{Channels}]（其数据在入库时被丢弃，扫描无意义）",
                            skippedTombstones.Length, deviceId, string.Join(",", skippedTombstones));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "查询墓碑通道失败，跳过剔除 device={DeviceId}", deviceId);
            }
            if (channels.Length == 0)
                return Results.BadRequest(new
                {
                    error = "请求的通道均为已删除（墓碑）通道，其数据在入库时被丢弃，扫描无意义；请先在通道管理恢复通道",
                    skippedTombstoneChannels = skippedTombstones
                });

            // SDK 只对 SessionContext 里有 ChannelConfig 的通道计算 EchoTime；运行中途自动发现的通道行
            // 要等进程重启才进装载清单，直接扫会因 EchoTime 恒为 null 被误判 active_no_echo。
            // 按 channels 表现状"只补缺"地补配会话（sendCommand=false，不写设备参数），失败不挡扫描。
            if (runtime is not null)
            {
                try
                {
                    var inits = FxInitialChannelConfigLoader.Load(
                        ds, configuration, runtime.Options.Address, deviceId, logger,
                        includeAppSettingsFallback: false);
                    await runtime.Connector.ProvisionSessionChannelsAsync(inits, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "扫描前补配会话通道配置失败 device={DeviceId}（未预置的通道可能因 EchoTime=null 被误判 active_no_echo）", deviceId);
                }
            }

            var thresholds = new ProbeScanThresholds(
                MinSamples: body?.MinSamples ?? opt.DefaultMinSamples,
                SnrGoodDb: body?.SnrGoodDb ?? opt.DefaultSnrGoodDb,
                EchoStdGoodNs: body?.EchoStdGoodNs ?? opt.DefaultEchoStdGoodNs,
                PeakToPeakMin: body?.PeakToPeakMin ?? opt.DefaultPeakToPeakMin,
                EchoRateGood: body?.EchoRateGood ?? opt.DefaultEchoRateGood,
                EchoRateNoEcho: body?.EchoRateNoEcho ?? opt.DefaultEchoRateNoEcho);

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
                // 每通道实际耗时 = settle + sample + 入库宽限（窗口内补读波形与采样并行，不计入）
                var estSeconds = (int)Math.Ceiling(
                    channels.Length * (settle + sample + Math.Max(0, opt.DbFlushGraceMs) / 1000.0));
                var warning = $"扫描期间设备业务监测会中断，预计 {estSeconds} 秒";
                if (skippedTombstones.Length > 0)
                    warning += $"；已剔除 {skippedTombstones.Length} 个已删除（墓碑）通道：{string.Join(",", skippedTombstones)}";
                return Results.Ok(new
                {
                    scanRunId = view.ScanRunId,
                    deviceId,
                    status = view.Status,
                    strategy = view.Strategy,
                    requestedChannels = view.RequestedChannels,
                    skippedTombstoneChannels = skippedTombstones,
                    estimatedSeconds = estSeconds,
                    settleSeconds = view.SettleSeconds,
                    sampleSeconds = view.SampleSeconds,
                    thresholds = view.Thresholds,
                    requestedAt = view.RequestedAt,
                    warning
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

    /// <summary>
    /// 该设备的墓碑通道号集合：存在软删行且没有同号的活跃行（活跃行存在说明通道已被重新启用）。
    /// </summary>
    private static async Task<HashSet<ushort>> LoadTombstonedChannelsAsync(
        NpgsqlDataSource ds, long deviceId, CancellationToken ct)
    {
        const string sql = @"
            SELECT DISTINCT c.channel_no
            FROM channels c
            WHERE c.device_id = @device_id
              AND c.deleted_at IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM channels l
                  WHERE l.device_id = c.device_id
                    AND l.channel_no = c.channel_no
                    AND l.deleted_at IS NULL)";
        var result = new HashSet<ushort>();
        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add((ushort)reader.GetInt16(0));
        return result;
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

    /// <summary>回波率合格线（0~1）。</summary>
    public double? EchoRateGood { get; set; }

    /// <summary>回波率"判无回波"下限（0~1）。</summary>
    public double? EchoRateNoEcho { get; set; }
}
