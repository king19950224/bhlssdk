using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoltPreload.Application.Devices;
using BoltPreload.Tests.Devices;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 采样稳定性护栏（calibration-hardening §2/§3）：标定输入取的是窗口平均声时，
/// 窗口混进拧紧过程/松弛尾巴时必须 400 拒绝，而不是沉默产出错一个数量级的 K_L
/// （08-12 真机 CH1/CH2/CH8 实锅）。
/// <para>
/// 锁定：趋势窗拦（且证明 stddev 单条判据拦不住它）、稳窗放行、force 跳过 + 审计留痕、
/// 阶梯采点同口径、预检接口判定一致、recalibrate 的带载拒绝与基线移动警告。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class EchoStabilityGuardIntegrationTests
{
    private readonly PostgresFixture _fx;
    public EchoStabilityGuardIntegrationTests(PostgresFixture fx) => _fx = fx;

    // 60s 窗 + 每半窗 15 条：够过 minSamples=20，且铺数据快
    private const int WindowSec = 60;

    [PostgresFact]
    public async Task TorqueCalibrate_TrendWindow_Returns400_StddevAloneWouldMiss()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("护栏-趋势窗");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        // 前半窗 27250、后半窗 27251：整体 stddev_pop=0.5 溜得过 0.6 的单条判据，
        // 半窗差 1.0 才拦得住——这正是"松弛中"窗口的形态，两条判据缺一不可的实证
        await SeedEchoWindowAsync(s, firstHalfNs: 27250m, secondHalfNs: 27251m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/torque-calibrate",
            new { torqueNm = 100, sampleWindowSeconds = WindowSec });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("稳定判据");
        var diag = body.GetProperty("stability");
        diag.GetProperty("stable").GetBoolean().Should().BeFalse();
        diag.GetProperty("stddevNs").GetDecimal().Should().BeLessThanOrEqualTo(0.6m,
            "这个窗口 stddev 是达标的——只有半窗趋势判据能拦住它");
        Math.Abs(diag.GetProperty("halfWindowDriftNs").GetDecimal()).Should().BeGreaterThan(0.6m);

        (await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM calibration_records WHERE bolt_id = @arg", s.BoltId))
            .Should().Be(0, "被拦的标定不许留下任何档案");
    }

    [PostgresFact]
    public async Task TorqueCalibrate_StableWindow_Succeeds()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("护栏-稳窗");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        await SeedEchoWindowAsync(s, firstHalfNs: 27250m, secondHalfNs: 27250m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/torque-calibrate",
            new { torqueNm = 100, sampleWindowSeconds = WindowSec });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await _fx.ActiveCalibrationAsync(s.ChannelId)).Should().NotBeNull("稳窗标定应正常生效");
    }

    [PostgresFact]
    public async Task TorqueCalibrate_Force_Bypasses_AndLeavesAuditTrail()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("护栏-force");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        // 混档跳变窗（拧紧过程采样的形态）：正常必拦，force 放行但要留痕
        await SeedEchoWindowAsync(s, firstHalfNs: 27250m, secondHalfNs: 27290m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/torque-calibrate",
            new { torqueNm = 100, sampleWindowSeconds = WindowSec, force = true });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await _fx.ScalarAsync<long>(@"
            SELECT count(*) FROM audit_log
            WHERE action = 'torque_calibrate' AND target_id = @arg
              AND (after_jsonb->>'forcedPastStabilityGuard')::boolean = true", s.ChannelId))
            .Should().Be(1, "强闯护栏必须能从审计里翻出来");
    }

    [PostgresFact]
    public async Task CaptureTorquePoint_MixedWindow_Returns400()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("护栏-采点");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        await SeedEchoWindowAsync(s, firstHalfNs: 27250m, secondHalfNs: 27290m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/torque-test-points",
            new { torqueNm = 100, sampleWindowSeconds = WindowSec });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM torque_test_points WHERE channel_id = @arg", s.ChannelId))
            .Should().Be(0, "拧的过程中采的点不许进拟合数据集");
    }

    [PostgresFact]
    public async Task EchoStabilityEndpoint_MatchesGuardJudgement()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("护栏-预检");
        var operatorId = await _fx.SeedUserAsync("operator");
        await SeedEchoWindowAsync(s, firstHalfNs: 27250m, secondHalfNs: 27251m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, operatorId, "operator");
        var url = $"/api/channels/{s.ChannelId}/echo-stability?seconds={WindowSec}";

        // 前端指示灯与后端护栏必须同口径：这里不稳，采点/标定就一定被拦
        var trending = await client.GetFromJsonAsync<JsonElement>(url);
        trending.GetProperty("stable").GetBoolean().Should().BeFalse();
        trending.GetProperty("halfWindowDriftNs").GetDecimal().Should().BeApproximately(1.0m, 0.15m);

        var inService = await client.GetFromJsonAsync<JsonElement>(url + "&purpose=inservice");
        inService.GetProperty("stable").GetBoolean().Should().BeTrue(
            "1ns 半窗差是现场探头抖动，在役置零必须放行");
        inService.GetProperty("stddevMaxNs").GetDecimal().Should().Be(3.0m);

        await ClearMeasurementsAsync(s.ChannelId);
        await SeedEchoWindowAsync(s, firstHalfNs: 27250m, secondHalfNs: 27250m);
        var stable = await client.GetFromJsonAsync<JsonElement>(url);
        stable.GetProperty("stable").GetBoolean().Should().BeTrue();
    }

    [PostgresFact]
    public async Task Recalibrate_LoadedBolt_WithExpectZeroLoad_Returns400()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("防呆-带载");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        // 通道有在产标定 K_L=5e-4；当前声时 27250 → Δt=50ns → 折算 100kN，远超 max(2kN, 5%目标)
        await ExecAsync("UPDATE channels SET k_l = 0.0005 WHERE id = @arg", s.ChannelId);
        await SeedEchoWindowAsync(s, firstHalfNs: 27250m, secondHalfNs: 27250m);

        using var factory = CreateFactoryWithDevice(s.DeviceId);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync(
            $"/api/devices/{s.DeviceId}/channels/5/recalibrate", new { expectZeroLoad = true });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("还带着载");
        (await _fx.ScalarAsync<decimal>(
            "SELECT ref_time_ns FROM channels WHERE id = @arg", s.ChannelId))
            .Should().Be(27200m, "被拦的重设不许动库里的基线");
    }

    [PostgresFact]
    public async Task Recalibrate_ZeroLoad_Succeeds_AndWarnsOnBaselineShift()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("防呆-零载");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        // 有标定但当前读数折算仅 0.4kN（27200.2 vs 基线 27200）：零载声明成立，放行
        await ExecAsync("UPDATE channels SET k_l = 0.0005 WHERE id = @arg", s.ChannelId);
        await SeedEchoWindowAsync(s, firstHalfNs: 27200.2m, secondHalfNs: 27200.2m);

        using var factory = CreateFactoryWithDevice(s.DeviceId);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync(
            $"/api/devices/{s.DeviceId}/channels/5/recalibrate", new { expectZeroLoad = true });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        // FakeConnector 固定返回新基线 0ns：相对旧基线 27200 移动巨大，必须给出"历史不可比"警告
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("warning").GetString().Should().Contain("基线移动");
    }

    [PostgresFact]
    public async Task Recalibrate_TrendWindow_Returns400()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("防呆-趋势");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        // 在役置零放宽到 5ns 趋势；这里用 20ns 单调爬升，仍应视为有人在扳动
        await SeedEchoWindowAsync(s, firstHalfNs: 27250m, secondHalfNs: 27270m);

        using var factory = CreateFactoryWithDevice(s.DeviceId);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync(
            $"/api/devices/{s.DeviceId}/channels/5/recalibrate", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Contain("稳定判据");
    }

    [PostgresFact]
    public async Task Recalibrate_InService_AllowsFieldJitter()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("在役-现场抖动");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        // 真机常态：sd≈1ns、半窗差 2ns（量化 0.625ns）。实验室 0.6 会拒，在役置零必须放行
        await SeedEchoWindowAsync(s, firstHalfNs: 27200m, secondHalfNs: 27202m);

        using var factory = CreateFactoryWithDevice(s.DeviceId);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync(
            $"/api/devices/{s.DeviceId}/channels/5/recalibrate", new { expectZeroLoad = false });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task Recalibrate_InsufficientSamples_Returns400_WithDistinctMessage()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("防呆-样本不足");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        // 只有 4 条样本（<5 下限）：数值完全平稳也不许当基线——1~2 条样本的 sd/半窗差纯靠运气。
        // 文案必须与"不稳"区分开：设备停报时现场需要的是查设备，不是原地傻等
        await SeedEchoWindowAsync(s, firstHalfNs: 27250m, secondHalfNs: 27250m, perHalf: 2);

        using var factory = CreateFactoryWithDevice(s.DeviceId);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync(
            $"/api/devices/{s.DeviceId}/channels/5/recalibrate", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("样本");
        // 铺 4 条,但最老的一条贴着 60s 窗口边缘,铺数与请求的间隙里可能掉出窗口——
        // 断言意图是"低于 5 条下限被拦",不锁死具体条数
        body.GetProperty("stability").GetProperty("sampleCount").GetInt64().Should().BeInRange(1, 4);
        (await _fx.ScalarAsync<decimal>(
            "SELECT ref_time_ns FROM channels WHERE id = @arg", s.ChannelId))
            .Should().Be(27200m, "被拦的重设不许动库里的基线");
    }

    // ---- helpers ----

    /// <summary>recalibrate 走设备连接器，用假管理器把设备装进运行时（护栏都在设备写之前）。</summary>
    private WebApplicationFactory<Program> CreateFactoryWithDevice(long deviceId) =>
        TestApi.CreateFactory(_fx, builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDeviceConnectorManager>();
            services.AddSingleton<IDeviceConnectorManager>(new FakeDeviceManager(new FakeConnector(deviceId)));
        }));

    /// <summary>
    /// 铺一个 60s 采样窗：前半窗 / 后半窗各 15 条指定声时（带温度，等于基线温度免温补干扰）。
    /// 半窗均值差 = secondHalfNs − firstHalfNs，两值相等即"稳窗"。样本避开半窗分界 ±2s，
    /// 免得铺数据和 SQL 里 now() 的毫秒差把样本挤到另一半窗。
    /// </summary>
    private async Task SeedEchoWindowAsync(
        IntegrationSeed.CalibratableChannel s, decimal firstHalfNs, decimal secondHalfNs, int perHalf = 15)
    {
        for (var i = 0; i < perHalf * 2; i++)
        {
            var firstHalf = i < perHalf;
            // 前半窗 age ∈ [33, 58]，后半窗 age ∈ [3, 28]
            var age = (firstHalf ? 33 : 3) + i % perHalf * 25.0 / Math.Max(1, perHalf - 1);
            await using var cmd = _fx.DataSource.CreateCommand(@"
                INSERT INTO measurements (bolt_id, channel_id, ts, ingested_at, echo_time_ns, sp_temp_c,
                                          gain, status_code, algo_version)
                VALUES (@bolt, @ch, now() - make_interval(secs => @age), now(), @echo, 22, 16, 0, 'test')");
            cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, s.BoltId);
            cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Bigint, s.ChannelId);
            cmd.Parameters.AddWithValue("age", NpgsqlDbType.Double, age);
            cmd.Parameters.AddWithValue("echo", NpgsqlDbType.Numeric, firstHalf ? firstHalfNs : secondHalfNs);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task ClearMeasurementsAsync(long channelId)
    {
        await using var cmd = _fx.DataSource.CreateCommand("DELETE FROM measurements WHERE channel_id = @cid");
        cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ExecAsync(string sql, long arg)
    {
        await using var cmd = _fx.DataSource.CreateCommand(sql.Replace("@arg", "@p"));
        cmd.Parameters.AddWithValue("p", NpgsqlDbType.Bigint, arg);
        await cmd.ExecuteNonQueryAsync();
    }
}
