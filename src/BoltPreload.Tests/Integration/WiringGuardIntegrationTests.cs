using BoltPreload.Api.Runtime;
using BoltPreload.Application.Devices.Acceptance;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Application.Diagnostics;
using BoltPreload.Application.Realtime;
using BoltPreload.Infrastructure.Alarm;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 接线指纹守卫（<see cref="WiringGuardService"/>）。
/// <para>
/// 检修换接电缆之后台账失真的唯一自动发现手段：比对通道近期声时均值与验收时记下的指纹，
/// 持续超差就报 wiring_suspect。没有它，一路被换接的通道会继续出数、继续判告警、
/// 继续进验收资料，只是数字记到了别的位置名下。
/// </para>
/// <para>
/// 判定的两侧都要守。漏报是台账错了没人知道；误报同样要紧——阈值只盖得住"换了不同长度螺栓"
/// 这一级的粗差，温度漂移和松开复紧都在容差内，报多了现场就把它当噪声关掉，等于没有。
/// 所以样本不足时既不报也不闭合：探头拔下来重接的当口正是没数据的时候，那会儿把已开的告警
/// 自动闭合掉，恰好抹掉要留的痕。整个服务此前零覆盖。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class WiringGuardIntegrationTests
{
    private readonly PostgresFixture _fx;
    public WiringGuardIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task Check_SoundTimeOffTheFingerprint_RaisesWiringSuspect()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("接线守卫");
        await SetFingerprintAsync(seed.ChannelId, 27_200);
        await SeedWindowAsync(seed, tofNs: 30_000);   // 偏 2800ns，远超默认 500ns 容差

        await NewGuard().CheckAsync(default);

        var alarms = await OpenWiringAlarmsAsync(seed.BoltId);
        alarms.Should().ContainSingle();
        alarms[0].Should().Contain("重新验收",
            "报了还得说清怎么处理：核对接线，确认没错就重新验收刷新指纹");
    }

    [PostgresFact]
    public async Task Check_ChannelWithoutAnAcceptanceFingerprint_IsLeftAlone()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("未验收通道");
        // accepted_tof_ns 留空，即这路还没验收过
        await SeedWindowAsync(seed, tofNs: 30_000);

        await NewGuard().CheckAsync(default);

        (await OpenWiringAlarmsAsync(seed.BoltId)).Should().BeEmpty(
            "没有指纹就没有比对基准，拿 0 当基准的话每一路未验收通道都会立刻报接线可疑");
    }

    [PostgresFact]
    public async Task Check_WithTooFewSamples_NeitherRaisesNorCloses()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("样本不足");
        await SetFingerprintAsync(seed.ChannelId, 27_200);
        await SeedWindowAsync(seed, tofNs: 30_000, samples: 2);   // 默认要 3 条

        await NewGuard().CheckAsync(default);
        (await OpenWiringAlarmsAsync(seed.BoltId)).Should().BeEmpty("证据不够就不报");

        // 反向：已经报了，通道随后彻底没数（拔下探头重接就是这样），不能当成"恢复正常"
        await RaiseWiringAlarmAsync(seed.BoltId, seed.ChannelId);
        await ClearMeasurementsAsync(seed.ChannelId);

        await NewGuard().CheckAsync(default);

        (await OpenWiringAlarmsAsync(seed.BoltId)).Should().HaveCount(1,
            "没数据不是回到容差内。这时闭合掉，正好把要留的那条痕抹了");
    }

    [PostgresFact]
    public async Task Check_BackInsideTolerance_ClosesTheAlarm()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("恢复一致");
        await SetFingerprintAsync(seed.ChannelId, 27_200);
        await RaiseWiringAlarmAsync(seed.BoltId, seed.ChannelId);
        await SeedWindowAsync(seed, tofNs: 27_300);   // 偏 100ns，在容差内

        await NewGuard().CheckAsync(default);

        (await OpenWiringAlarmsAsync(seed.BoltId)).Should().BeEmpty(
            "重新验收或接线恢复后要能自动闭合，否则这条告警只能人工消，久了就没人看");
    }

    [PostgresFact]
    public async Task Check_StillOffWithTheAlarmOpen_RefreshesItInsteadOfInsertingAgain()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("持续超差");
        await SetFingerprintAsync(seed.ChannelId, 27_200);
        await RaiseWiringAlarmAsync(seed.BoltId, seed.ChannelId);   // 一小时前开的，last_active_at 也是一小时前
        await SeedWindowAsync(seed, tofNs: 30_000);                 // 偏差还在

        var log = new CapturingLogger<WiringGuardService>();
        await NewGuard(logger: log).CheckAsync(default);

        (await LastActiveAgeSecondsAsync(seed.BoltId)).Should().BeLessThan(60,
            "仍在超差要续 last_active_at，不然告警页上这条看着像一小时前就没动静的旧账");
        // 重复不重复其实由 ux_alarms_open 唯一索引兜着，所以这里断言的是别去撞它：
        // 撞一次记一条 ERROR，每分钟一轮，日志里全是它，真出问题的那条就淹了
        log.Messages(LogLevel.Error, "wiring_suspect").Should().BeEmpty(
            "已经开着就不该再插一条，撞唯一索引会把日志刷成一片 ERROR");
        (await OpenWiringAlarmsAsync(seed.BoltId)).Should().HaveCount(1);
    }

    [PostgresFact]
    public async Task Check_WhileTheDeviceIsBeingScanned_SkipsIt()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("扫描期间");
        await SetFingerprintAsync(seed.ChannelId, 27_200);
        await SeedWindowAsync(seed, tofNs: 30_000);

        // 自动检测会逐通道切 SingleMeas，这段时间的声时不能拿来判接线
        var scan = new ProbeScanState();
        scan.TryBegin(seed.DeviceId, scanRunId: 1);

        await NewGuard(scan: scan).CheckAsync(default);

        (await OpenWiringAlarmsAsync(seed.BoltId)).Should().BeEmpty(
            "扫描/验收会话会改写设备的轮询通道集，此时的数据判出来的是自己造成的偏差");
    }

    [PostgresFact]
    public async Task Check_ShelvedBolt_IsNotRaised()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("维护抑制");
        await SetFingerprintAsync(seed.ChannelId, 27_200);
        await SeedWindowAsync(seed, tofNs: 30_000);
        await ShelveAsync(seed.BoltId, await _fx.SeedUserAsync("reviewer"));

        await NewGuard().CheckAsync(default);

        (await OpenWiringAlarmsAsync(seed.BoltId)).Should().BeEmpty(
            "计划检修期间本来就在动接线，这条告警要跟其它类型走同一套抑制口径");
    }

    // ---- helpers ----

    private WiringGuardService NewGuard(
        ProbeScanState? scan = null, ILogger<WiringGuardService>? logger = null) => new(
        _fx.DataSource,
        new PostgresAlarmSink(_fx.DataSource, NullLogger<PostgresAlarmSink>.Instance),
        new NoopAlarmPublisher(),
        scan ?? new ProbeScanState(),
        new AcceptanceSessionState(),
        new BackgroundServiceHealth(),
        Options.Create(new WiringGuardOptions()),
        logger ?? NullLogger<WiringGuardService>.Instance);

    private async Task SetFingerprintAsync(long channelId, decimal tofNs)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "UPDATE channels SET accepted_tof_ns = @tof, accepted_at = now() WHERE id = @id");
        cmd.Parameters.AddWithValue("tof", NpgsqlDbType.Numeric, tofNs);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 铺满一个比对窗口的测量。判定要求窗口跨度过半（默认 5 分钟窗 → 至少 150 秒），
    /// 所以样本摊在 4 分钟前到 30 秒前之间，而不是挤在一个时刻。
    /// </summary>
    private async Task SeedWindowAsync(IntegrationSeed.CalibratableChannel seed, decimal tofNs, int samples = 5)
    {
        for (var i = 0; i < samples; i++)
        {
            var ageSeconds = 240 - i * (210 / Math.Max(1, samples - 1));
            await using var cmd = _fx.DataSource.CreateCommand(@"
                INSERT INTO measurements (bolt_id, channel_id, ts, ingested_at, echo_time_ns,
                                          gain, status_code, algo_version)
                VALUES (@bolt, @ch, now() - make_interval(secs => @age), now(), @tof, 16, 0, 'test')");
            cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, seed.BoltId);
            cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Bigint, seed.ChannelId);
            cmd.Parameters.AddWithValue("age", NpgsqlDbType.Double, (double)ageSeconds);
            cmd.Parameters.AddWithValue("tof", NpgsqlDbType.Numeric, tofNs);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task ClearMeasurementsAsync(long channelId)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "DELETE FROM measurements WHERE channel_id = @id");
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task RaiseWiringAlarmAsync(long boltId, long channelId)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO alarms (bolt_id, channel_id, type, severity, triggered_at, last_active_at, message)
            VALUES (@b, @c, 'wiring_suspect', 'warn', now() - interval '1 hour',
                    now() - interval '1 hour', '已开的接线可疑告警')");
        cmd.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
        cmd.Parameters.AddWithValue("c", NpgsqlDbType.Bigint, channelId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ShelveAsync(long boltId, long shelvedBy)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO alarm_shelvings (bolt_id, alarm_type, shelved_by, shelved_at, expires_at, reason)
            VALUES (@b, 'wiring_suspect', @by, now(), now() + interval '2 hours', '计划检修')");
        cmd.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
        cmd.Parameters.AddWithValue("by", NpgsqlDbType.Bigint, shelvedBy);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> OpenWiringAlarmsAsync(long boltId)
    {
        var list = new List<string>();
        await using var cmd = _fx.DataSource.CreateCommand(@"
            SELECT coalesce(message, '') FROM alarms
            WHERE bolt_id = @b AND type = 'wiring_suspect' AND cleared_at IS NULL");
        cmd.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }

    private Task<double> LastActiveAgeSecondsAsync(long boltId) =>
        _fx.ScalarAsync<double>(@"
            SELECT EXTRACT(EPOCH FROM (now() - max(last_active_at)))::double precision
            FROM alarms WHERE bolt_id = @arg AND type = 'wiring_suspect' AND cleared_at IS NULL",
            boltId);
}
