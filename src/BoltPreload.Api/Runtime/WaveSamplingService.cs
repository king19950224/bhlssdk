using BoltPreload.Application.Devices;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Fx;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Runtime;

/// <summary>波形定时抽样配置（appsettings 的 Ingestion:WaveSampling 节）。</summary>
public sealed class WaveSamplingOptions
{
    /// <summary>是否启用周期性波形采集。默认关（bool 默认 false）——仅真机现场需要（真机 MultiMeas 模式不随测量带波形）。</summary>
    public bool Enabled { get; set; }
    /// <summary>采集周期（秒）。波形诊断是辅助指标，低频即可，避免刷爆设备上行。</summary>
    public int IntervalSeconds { get; set; } = 120;
    /// <summary>逐通道读取之间的间隔（毫秒），避免一次性灌爆设备上行链路。</summary>
    public int PerChannelDelayMs { get; set; } = 200;
    /// <summary>启动后宽限（秒），等设备连接 + 会话 provision 完成再开始采集。</summary>
    public int StartupGraceSeconds { get; set; } = 45;
}

/// <summary>
/// 波形采集服务（§8.6 波形诊断在真机上的数据源）。
/// <para>
/// 真机在常规 <c>MultiMeas</c> 监测模式下【不随测量上报波形】（SDK 带宽考量，见
/// <c>external/FxSDK/Codec/MultiChIotProtocolHandler.cs</c> 的 DecodeMultiMeasResults 注释）。
/// 本服务低频、<b>不切工作模式</b>地对各采样通道主动 <c>ReadChannelParam("LEchoWave")</c>，
/// 回波经 PostgresMeasurementSink 落 <c>wave_features</c>，让 A-Scan / 相似度 / 主频诊断在真机上也有数据。
/// </para>
/// <para>
/// 不打断 MultiMeas 监测（不切模式）；与探头扫描互斥（扫描中的设备跳过）；仅对 Real 模式设备采
/// （模拟器自带波形，无需主动读）。<b>默认关闭</b>，现场接真机后按需开启
/// <c>Ingestion:WaveSampling:Enabled</c>。
/// </para>
/// </summary>
public sealed class WaveSamplingService : BackgroundService
{
    private readonly IDeviceConnectorManager _manager;
    private readonly NpgsqlDataSource _ds;
    private readonly ProbeScanState _probeScan;
    private readonly WaveSamplingOptions _opt;
    private readonly ILogger<WaveSamplingService> _logger;

    public WaveSamplingService(
        IDeviceConnectorManager manager,
        NpgsqlDataSource ds,
        ProbeScanState probeScan,
        IOptions<WaveSamplingOptions> opt,
        ILogger<WaveSamplingService> logger)
    {
        _manager = manager;
        _ds = ds;
        _probeScan = probeScan;
        _opt = opt.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled)
        {
            _logger.LogInformation(
                "WaveSampling 已禁用（默认关）。真机现场需要波形诊断数据时，开启 Ingestion:WaveSampling:Enabled。");
            return;
        }

        _logger.LogInformation(
            "WaveSampling 启动：每 {Interval}s 对各采样通道主动读 LEchoWave 补波形诊断（不切工作模式）",
            _opt.IntervalSeconds);

        try { await Task.Delay(TimeSpan.FromSeconds(_opt.StartupGraceSeconds), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _opt.IntervalSeconds)));
        while (true)
        {
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) { return; }
            try { await SampleOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogError(ex, "WaveSampling 一轮采集异常"); }
        }
    }

    // internal 供集成测试直调单轮采集（BoltPreload.Tests，见 csproj InternalsVisibleTo）
    internal async Task SampleOnceAsync(CancellationToken ct)
    {
        foreach (var rt in _manager.Runtimes)
        {
            if (ct.IsCancellationRequested) return;
            // 仅真机采（模拟器自带波形）；未连接跳过；扫描中跳过（避免抢上行/工作模式）。
            if (!string.Equals(rt.Options.Mode, "Real", StringComparison.OrdinalIgnoreCase)) continue;
            if (rt.Connector.State != FxConnectorState.Connected) continue;
            if (_probeScan.IsRunning(rt.DeviceId)) continue;

            var channels = await LoadSamplingChannelsAsync(rt.DeviceId, ct);
            if (channels.Count == 0) continue;

            foreach (var ch in channels)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    await rt.Connector.ReadChannelParamAsync("LEchoWave", ch, rt.Options.Address, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "读 LEchoWave 失败 device={Dev} ch={Ch}", rt.DeviceId, ch);
                }
                try { await Task.Delay(Math.Max(0, _opt.PerChannelDelayMs), ct); }
                catch (OperationCanceledException) { return; }
            }
            _logger.LogDebug("WaveSampling 已触发 device={Dev} 的 {N} 个通道 LEchoWave 读取", rt.DeviceId, channels.Count);
        }
    }

    private async Task<List<ushort>> LoadSamplingChannelsAsync(long deviceId, CancellationToken ct)
    {
        var list = new List<ushort>();
        await using var cmd = _ds.CreateCommand(@"
            SELECT channel_no FROM channels
            WHERE device_id = @d AND deleted_at IS NULL AND sampling_enabled = true
            ORDER BY channel_no");
        cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add((ushort)r.GetInt16(0));
        return list;
    }
}
