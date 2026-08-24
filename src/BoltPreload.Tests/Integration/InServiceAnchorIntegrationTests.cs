using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 在役标定（风机现场主路径）：先采集基准、手动置零，再认定当前 = 档案已设定的目标预紧力，
/// 之后只看动载荷和衰减。热路径 F = F₀ + Δt/K_L（截距 b = −K_L·F₀），置零基线不改写。
/// <para>
/// 锁定：必须先有基准、置零基线保持不动、档案目标可直接起算（不传扭矩）、
/// K_L 三级来源（显式传入 → 同组同规格同长度中位数 → 都没有则 400）、
/// 有效期 365 天、anchor-defaults 预填目标力/同批 K_L/理论参考。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class InServiceAnchorIntegrationTests
{
    private readonly PostgresFixture _fx;
    public InServiceAnchorIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task Anchor_UsesBaselineAsAnchorInstant_AndRequiresBaseline()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("在役-显式KL");
        var reviewerId = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        // 没有基准时拒绝并说清含义：先置零，再认定【基准时刻】= 目标
        await ExecAsync("UPDATE channels SET ref_time_ns = NULL, ref_temp_c = NULL WHERE id = @arg", s.ChannelId);
        var noBaseline = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/torque-calibrate",
            new { anchorInService = true, kL = 0.0005 });
        noBaseline.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await noBaseline.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Contain("基准");

        // 有基准（27200ns/22°C）即可：不采窗、不做稳定性检查（基准那步已把关），
        // 认定当前 = 档案目标 100 kN（种子螺栓 target_preload_n=100000）
        await ExecAsync("UPDATE channels SET ref_time_ns = 27200, ref_temp_c = 22 WHERE id = @arg", s.ChannelId);
        var resp = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/torque-calibrate",
            new { anchorInService = true, kL = 0.0005 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("anchor").GetProperty("anchoredPreloadN").GetDecimal().Should().Be(100_000m);
        body.GetProperty("anchor").GetProperty("anchorBasis").GetString().Should().Be("target_preload");
        body.GetProperty("anchor").GetProperty("kLSource").GetString().Should().Be("request");
        body.GetProperty("warning").GetString().Should().Contain("目标");

        // 置零基线保持 27200；截距 b = −K_L·F₀ = −0.0005·100000 = −50 → F = F₀ + Δt/K_L
        (await _fx.ScalarAsync<decimal>("SELECT ref_time_ns FROM channels WHERE id = @arg", s.ChannelId))
            .Should().Be(27_200m, "手动置零的基准不许被改写成虚拟零载");
        (await _fx.ScalarAsync<decimal>("SELECT k_l_intercept_ns FROM channels WHERE id = @arg", s.ChannelId))
            .Should().Be(-50m);
        (await _fx.ScalarAsync<decimal>("SELECT k_l FROM channels WHERE id = @arg", s.ChannelId))
            .Should().Be(0.0005m);
        (await _fx.ScalarAsync<decimal>("SELECT ref_temp_c FROM channels WHERE id = @arg", s.ChannelId))
            .Should().Be(22m, "基准时刻温度即参考温度（温补因子=1 自洽）");

        (await _fx.ScalarAsync<DateTime>(
            "SELECT expires_at::timestamp FROM calibration_records WHERE bolt_id = @arg AND superseded_by IS NULL", s.BoltId))
            .Date.Should().BeCloseTo(DateTime.UtcNow.Date.AddDays(365), TimeSpan.FromDays(2));

        (await _fx.ScalarAsync<string>(
            "SELECT points_jsonb->0->>'type' FROM calibration_records WHERE bolt_id = @arg AND superseded_by IS NULL", s.BoltId))
            .Should().Be("in_service_anchor");
    }

    [PostgresFact]
    public async Task Anchor_ByTargetPreload_NeedsNoTorqueInput()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("在役-按目标预紧力");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        await ExecAsync("UPDATE bolts SET install_torque_nm = 2800 WHERE id = @arg", s.BoltId);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        // 现场默认路径：不传扭矩/K，直接认定基准时刻 = 设定的目标预紧力 80 kN
        var resp = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/torque-calibrate",
            new { anchorPreloadN = 80_000, anchorInService = true, kL = 0.0005 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("anchor").GetProperty("anchoredPreloadN").GetDecimal().Should().Be(80_000m);
        body.GetProperty("anchor").GetProperty("anchorBasis").GetString().Should().Be("target_preload");

        (await _fx.ScalarAsync<decimal>("SELECT ref_time_ns FROM channels WHERE id = @arg", s.ChannelId))
            .Should().Be(27_200m, "置零基线保持不动");
        (await _fx.ScalarAsync<decimal>("SELECT k_l_intercept_ns FROM channels WHERE id = @arg", s.ChannelId))
            .Should().Be(-40m, "b = −0.0005·80000");
        (await _fx.ScalarAsync<decimal>("SELECT install_torque_nm FROM bolts WHERE id = @arg", s.BoltId))
            .Should().Be(2800m, "没有扭矩输入时不得动档案里的安装扭矩");
        (await _fx.ScalarAsync<string>(
            "SELECT points_jsonb->0->>'anchorBasis' FROM calibration_records WHERE bolt_id = @arg AND superseded_by IS NULL", s.BoltId))
            .Should().Be("target_preload");
    }

    [PostgresFact]
    public async Task Anchor_UsesArchiveTarget_WhenNoAnchorPreloadNSent()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("在役-档案目标");
        var reviewerId = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/torque-calibrate",
            new { anchorInService = true, kL = 0.0005 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("anchor").GetProperty("anchoredPreloadN").GetDecimal()
            .Should().Be(100_000m, "不传 anchorPreloadN 时走档案 target_preload_n");
        body.GetProperty("anchor").GetProperty("anchorBasis").GetString().Should().Be("target_preload");
    }

    [PostgresFact]
    public async Task Anchor_NoTargetAnywhere_Returns400()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("在役-无目标");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        await ExecAsync("UPDATE bolts SET target_preload_n = NULL WHERE id = @arg", s.BoltId);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/torque-calibrate",
            new { anchorInService = true, kL = 0.0005 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Contain("目标预紧力");
        (await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM calibration_records WHERE bolt_id = @arg", s.BoltId))
            .Should().Be(0);
    }

    [PostgresFact]
    public async Task Anchor_WithoutKl_FallsBackToPeerMedian_SameLengthOnly()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("在役-同批沿用");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        var peerSameLen = await SeedPeerBoltAsync(s, channelNo: 6, effectiveLengthMm: 200);
        var peerOtherLen = await SeedPeerBoltAsync(s, channelNo: 7, effectiveLengthMm: 300);
        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");
        (await client.PostAsJsonAsync($"/api/channels/{peerSameLen}/quick-calibrate",
            new { kL = 0.0006, refTimeNs = 27_200, refTempC = 22 }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "先给同批同长度螺栓建一条生效标定");
        (await client.PostAsJsonAsync($"/api/channels/{peerOtherLen}/quick-calibrate",
            new { kL = 0.0009, refTimeNs = 40_800, refTempC = 22 }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "不同长度的螺栓也有标定（但不许被沿用）");

        var resp = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/torque-calibrate",
            new { anchorInService = true });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("anchor").GetProperty("kLSource").GetString().Should().Be("peer_median");
        (await _fx.ScalarAsync<decimal>("SELECT k_l FROM channels WHERE id = @arg", s.ChannelId))
            .Should().Be(0.0006m, "只沿用同长度螺栓的 K_L（0.0006），不同长度的 0.0009 混进来会把中位数带偏");
    }

    [PostgresFact]
    public async Task Anchor_NoKlAnywhere_Returns400_AndExplainsWhatToDo()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("在役-无KL来源");
        var reviewerId = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/torque-calibrate",
            new { anchorInService = true });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("K_L",
            "错误必须说清楚缺什么、去哪拿（同批标定/理论估算），现场不该对着裸 400 猜");
        (await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM calibration_records WHERE bolt_id = @arg", s.BoltId))
            .Should().Be(0, "拒绝的锚定不许留档");
    }

    [PostgresFact]
    public async Task AnchorDefaults_ReturnsArchiveTarget_PeerKl_AndTheory()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("在役预填");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        await ExecAsync("UPDATE bolts SET install_torque_nm = 2800 WHERE id = @arg", s.BoltId);
        var peerChannelId = await SeedPeerBoltAsync(s, channelNo: 7);
        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");
        (await client.PostAsJsonAsync($"/api/channels/{peerChannelId}/quick-calibrate",
            new { kL = 0.0006, refTimeNs = 27_200, refTempC = 22 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var d = await client.GetFromJsonAsync<JsonElement>($"/api/channels/{s.ChannelId}/anchor-defaults");

        d.GetProperty("targetPreloadN").GetDecimal().Should().Be(100_000m, "档案目标要预填，现场不用再填一遍");
        d.GetProperty("installTorqueNm").GetDecimal().Should().Be(2800m);
        d.GetProperty("peerKL").GetDecimal().Should().Be(0.0006m);
        d.GetProperty("peerCount").GetInt64().Should().Be(1);
        d.GetProperty("theoreticalKL").GetDecimal().Should().BeApproximately(
            2.5m * 27_200m / (206_000m * 157m), 0.0001m);
    }

    // ---- helpers ----

    private async Task<long> SeedPeerBoltAsync(
        IntegrationSeed.CalibratableChannel s, int channelNo, decimal effectiveLengthMm = 200)
    {
        long channelId;
        await using (var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO channels (device_id, channel_no, probe_freq_mhz, gain, meas_mode, tc_mode,
                                  tof_algorithm, hardware_state, business_state, config_source, state,
                                  ref_time_ns, ref_temp_c, discovered_at, created_at, updated_at)
            VALUES (@dev, @no, 2.0, 16, 2, 1, 'ThrSignal', 'healthy', 'bound', 'database', 'manual',
                    27200, 22, now(), now(), now())
            RETURNING id"))
        {
            cmd.Parameters.AddWithValue("dev", NpgsqlDbType.Bigint, s.DeviceId);
            cmd.Parameters.AddWithValue("no", NpgsqlDbType.Smallint, (short)channelNo);
            channelId = (long)(await cmd.ExecuteScalarAsync())!;
        }
        await using (var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO bolts (group_id, channel_id, code, nominal_diam, grade, rp02_mpa, material,
                               elastic_modulus_gpa, effective_length_mm, total_length_mm, cross_section_mm2,
                               sound_velocity_ms, target_preload_n, state, enabled, gb_compliant, created_at, updated_at)
            VALUES (@grp, @ch, @code, 'M16', '8.8', 640, '42CrMo',
                    206, @len, @len + 20, 157, 5890, 100000, 'uncalibrated', true, true, now(), now())"))
        {
            cmd.Parameters.AddWithValue("grp", NpgsqlDbType.Bigint, s.GroupId);
            cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Bigint, channelId);
            cmd.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, "PEER-" + Guid.NewGuid().ToString("N")[..8]);
            cmd.Parameters.AddWithValue("len", NpgsqlDbType.Numeric, effectiveLengthMm);
            await cmd.ExecuteNonQueryAsync();
        }
        return channelId;
    }

    private async Task ExecAsync(string sql, long arg)
    {
        await using var cmd = _fx.DataSource.CreateCommand(sql.Replace("@arg", "@p"));
        cmd.Parameters.AddWithValue("p", NpgsqlDbType.Bigint, arg);
        await cmd.ExecuteNonQueryAsync();
    }
}
