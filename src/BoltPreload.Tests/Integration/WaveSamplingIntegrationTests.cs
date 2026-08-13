using BoltPreload.Api.Runtime;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Tests.Devices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 波形主动采集（<see cref="WaveSamplingService"/>）。
/// <para>
/// 真机在常规监测模式下不随测量上报波形，A-Scan 和相似度诊断的数据只能靠这个服务主动去读。
/// 默认关，现场接真机后才开。
/// </para>
/// <para>
/// 要守的是几道跳过条件，尤其"扫描中的设备不采"：自动检测正逐通道切工作模式判探头好坏，
/// 这时候插进去抢上行，判出来的信号质量是自己搅出来的，而结论会写进通道硬件状态、
/// 进而影响验收。这道互斥破了不会报错，只会让扫描结果变得不可复现。整个服务此前零覆盖。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class WaveSamplingIntegrationTests
{
    private readonly PostgresFixture _fx;
    public WaveSamplingIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task Sample_ReadsOnlyTheChannelsThatAreEnabledForSampling()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("波形采集");
        await AddChannelAsync(seed.DeviceId, channelNo: 7, samplingEnabled: false);
        await AddChannelAsync(seed.DeviceId, channelNo: 8, samplingEnabled: true);
        await SetSamplingAsync(seed.ChannelId, enabled: true);   // 种子通道是 CH5

        var connector = new FakeConnector(seed.DeviceId);
        await NewService(connector).SampleOnceAsync(default);

        var read = connector.ChannelParamReads.ToList();
        read.Should().OnlyContain(c => c.Property == "LEchoWave");
        read.Select(c => c.Channel).Should().BeEquivalentTo(new ushort[] { 5, 8 },
            "关掉采样的通道不该去读：48 通道设备上多读一路就多占一份上行");
    }

    [PostgresFact]
    public async Task Sample_WhileTheDeviceIsBeingScanned_ReadsNothing()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("扫描中不采");
        await SetSamplingAsync(seed.ChannelId, enabled: true);

        var scan = new ProbeScanState();
        scan.TryBegin(seed.DeviceId, scanRunId: 1);

        var connector = new FakeConnector(seed.DeviceId);
        await NewService(connector, scan).SampleOnceAsync(default);

        connector.ChannelParamReads.Should().BeEmpty(
            "扫描正在逐通道切工作模式判探头，插进去抢上行会让判定结果掺进自己造成的干扰");
    }

    // ---- helpers ----

    private WaveSamplingService NewService(
        FakeConnector connector, ProbeScanState? scan = null) => new(
        new FakeDeviceManager(connector),
        _fx.DataSource,
        scan ?? new ProbeScanState(),
        Options.Create(new WaveSamplingOptions { Enabled = true, PerChannelDelayMs = 0 }),
        new BoltPreload.Application.Diagnostics.BackgroundServiceHealth(),
        NullLogger<WaveSamplingService>.Instance);

    private async Task SetSamplingAsync(long channelId, bool enabled)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "UPDATE channels SET sampling_enabled = @on WHERE id = @id");
        cmd.Parameters.AddWithValue("on", NpgsqlDbType.Boolean, enabled);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> AddChannelAsync(long deviceId, short channelNo, bool samplingEnabled)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO channels (device_id, channel_no, probe_freq_mhz, gain, meas_mode, tc_mode,
                                  tof_algorithm, hardware_state, business_state, config_source,
                                  state, discovered_at, sampling_enabled, created_at, updated_at)
            VALUES (@dev, @no, 2.0, 16, 2, 1, 'ThrSignal', 'healthy', 'unbound', 'database',
                    'manual', now(), @on, now(), now())
            RETURNING id");
        cmd.Parameters.AddWithValue("dev", NpgsqlDbType.Bigint, deviceId);
        cmd.Parameters.AddWithValue("no", NpgsqlDbType.Smallint, channelNo);
        cmd.Parameters.AddWithValue("on", NpgsqlDbType.Boolean, samplingEnabled);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
