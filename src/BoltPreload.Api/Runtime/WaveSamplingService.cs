using BoltPreload.Application.Devices;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Application.Diagnostics;
using BoltPreload.Fx;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Runtime;

/// <summary>波形定时抽样配置（appsettings 的 Ingestion:WaveSampling 节）。</summary>
public sealed class WaveSamplingOptions
{
    /// <summary>
    /// 是否启用周期性波形采集。默认关（bool 默认 false）。
    /// 2026-08-17 真机实测：model=3 在 MultiMeas 下【每条测量都随帧带波形】（93 条测量对 93 条 wave_features），
    /// wave_features 的主数据源是常规测量链路，本服务只是固件行为不同/波形缺失时的补采后备。
    /// </summary>
    public bool Enabled { get; set; }
    /// <summary>采集周期（秒）。波形诊断是辅助指标，低频即可，避免刷爆设备上行。</summary>
    public int IntervalSeconds { get; set; } = 120;
    /// <summary>逐通道读取之间的间隔（毫秒），避免一次性灌爆设备上行链路。</summary>
    public int PerChannelDelayMs { get; set; } = 200;
    /// <summary>启动后宽限（秒），等设备连接 + 会话 provision 完成再开始采集。</summary>
    public int StartupGraceSeconds { get; set; } = 45;
}

/// <summary>
/// 波形定时补采服务（§8.6 波形诊断的<b>后备</b>数据源）。
/// <para>
/// 注意：本类曾记载"真机 MultiMeas 不随测量上报波形"，且引用的 DecodeMultiMeasResults 方法并不存在——
/// <b>2026-08-17 真机实测证伪</b>：model=3 在 MultiMeas 下每条测量都带波形（与 sdk-api-facts.md §268
/// "设备只传波形、EchoTime 主机算"一致），wave_features 由常规测量链路持续产生。
/// 本服务保留为后备：固件行为不同、或个别通道波形缺失时，低频、<b>不切工作模式</b>地对采样通道
/// 主动 <c>ReadChannelParam("LEchoWave")</c> 补 <c>wave_features</c>（落库幂等，与随测量波形共存无害）。
/// </para>
/// <para>
/// 不打断 MultiMeas 监测（不切模式）；与探头扫描互斥（扫描中的设备跳过，扫描窗口内的补读由
/// ProbeScanOrchestrator 自行触发）；只对已连接的设备采。<b>默认关闭</b>，按需开启
/// <c>Ingestion:WaveSampling:Enabled</c>。
/// </para>
/// </summary>
public sealed class WaveSamplingService : BackgroundService
{
    private readonly IDeviceConnectorManager _manager;
    private readonly NpgsqlDataSource _ds;
    private readonly ProbeScanState _probeScan;
    private readonly WaveSamplingOptions _opt;
    private readonly BackgroundServiceHealth _health;
    private readonly ILogger<WaveSamplingService> _logger;

    public WaveSamplingService(
        IDeviceConnectorManager manager,
        NpgsqlDataSource ds,
        ProbeScanState probeScan,
        IOptions<WaveSamplingOptions> opt,
        BackgroundServiceHealth health,
        ILogger<WaveSamplingService> logger)
    {
        _manager = manager;
        _ds = ds;
        _probeScan = probeScan;
        _opt = opt.Value;
        _health = health;
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
            // 心跳：本服务停摆则 wave_features 不再补采，波形诊断页的数据源静默断流
            // （非主监测链路，但同样不该只能靠翻日志发现）。禁用时上面已 return，不会登记成失联。
            _health.Beat("wave-sampling", TimeSpan.FromSeconds(Math.Max(5, _opt.IntervalSeconds)));
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
            // 未连接跳过；扫描中跳过（避免抢上行/工作模式）。
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
