using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 扭矩阶梯点的基线作用域：拟合/摘要只认【当前基线下】采的点（ref_time_ns 与通道当前值一致）。
/// <para>
/// 每个点存的 delta_tof_ns 是按采点当时的基线快照算的；基线被重设（recalibrate）后，
/// 旧点的 Δt 参考系整体平移，与新点混拟合时截距吸收不了（"截距免疫基线错位"只对
/// 所有点共用同一基线成立），斜率必然被带偏——而且 R² 崩了都看不出是谁的问题。
/// 这里锁定：旧点在列表中被标记、不进摘要、不进拟合、不进标定档案快照；
/// 只剩旧点时拒绝拟合并把旧点数量说清楚。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class TorquePointBaselineScopeIntegrationTests
{
    private readonly PostgresFixture _fx;
    public TorquePointBaselineScopeIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task StalePoints_FlaggedInList_AndExcludedFromSummary()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("基线作用域-摘要");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        await _fx.SeedTorquePointsAsync(s, d: 0.5m);          // 当前基线（27200）下的 4 个好点
        await SeedStalePointsAsync(s, staleRefTimeNs: 27_100m); // 旧基线下的 2 个点（Δt 参考系已平移）

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var list = await client.GetFromJsonAsync<JsonElement>(
            $"/api/channels/{s.ChannelId}/torque-test-points");
        list.GetProperty("count").GetInt32().Should().Be(6, "列表要能看到全部点（含旧基线点备查）");
        list.GetProperty("currentRefTimeNs").GetDecimal().Should().Be(27_200m);
        var items = list.GetProperty("items").EnumerateArray().ToList();
        items.Count(p => p.GetProperty("baselineMatched").GetBoolean()).Should().Be(4);
        items.Where(p => !p.GetProperty("baselineMatched").GetBoolean())
            .Should().OnlyContain(p => p.GetProperty("refTimeNs").GetDecimal() == 27_100m);

        // 摘要只算当前基线的 4 个点：混入旧点的话斜率/R² 都会被带偏（旧点 Δt 整体平移了 -30ns）
        var summary = await client.GetFromJsonAsync<JsonElement>(
            $"/api/channels/{s.ChannelId}/torque-test-points/summary");
        summary.GetProperty("pointCount").GetInt64().Should().Be(4);
        summary.GetProperty("stalePointCount").GetInt64().Should().Be(2);
        summary.GetProperty("rSquared").GetDouble().Should().BeApproximately(
            (double)CalibrationFitData.ExpectedR2(0.5m), 0.0005,
            "R² 必须与'只有 4 个好点'的闭式解一致——偏了就说明旧点混进了拟合");
        summary.GetProperty("kLByLinearFitNsPerN").GetDouble().Should().BeApproximately(0.0005, 0.00001);
    }

    [PostgresFact]
    public async Task ApplyTorqueFit_UsesOnlyCurrentBaselinePoints()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("基线作用域-拟合");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        await _fx.SeedTorquePointsAsync(s, d: 0.5m);
        await SeedStalePointsAsync(s, staleRefTimeNs: 27_100m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/apply-torque-fit", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("pointCount").GetInt64().Should().Be(4);
        body.GetProperty("kL").GetDecimal().Should().BeApproximately(0.0005m, 0.00001m,
            "旧基线点混进来的话斜率会被平移点拉偏");
        body.GetProperty("passed").GetBoolean().Should().BeTrue();

        // 标定档案的原始点快照 = 证据链，旧基线点不能出现在里面
        var calibId = body.GetProperty("calibrationId").GetGuid();
        (await _fx.ScalarAsync<int>(
            "SELECT jsonb_array_length(points_jsonb) FROM calibration_records WHERE id = @arg", calibId))
            .Should().Be(4);
    }

    [PostgresFact]
    public async Task ApplyTorqueFit_OnlyStalePoints_Returns400_AndExplains()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("基线作用域-全旧点");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        // 只有旧基线点（模拟：采完点后有人重设了基线）——数量够 4 个也不许拟合
        await SeedStalePointsAsync(s, staleRefTimeNs: 27_100m, count: 4);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/apply-torque-fit", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("stalePointCount").GetInt64().Should().Be(4);
        body.GetProperty("error").GetString().Should().Contain("旧基线",
            "错误信息必须解释'点为什么不算数'，现场才知道接下来该重新采点而不是反复重试");

        (await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM calibration_records WHERE bolt_id = @arg", s.BoltId))
            .Should().Be(0, "被拒的拟合不许留档");
    }

    // ---- helpers ----

    /// <summary>
    /// 插入"旧基线时期"的阶梯点，模拟 08-12 CH3 的真实事故形态：旧基线是带载/错位状态下设的
    /// （比真零载 27200 低 shift），点的 delta_tof_ns 是按错位基线算的快照 → 相对当前参考系
    /// 整体偏移 +shift。之后基线被修正为 27200，这些点若仍被混入拟合，截距吸收不了这批
    /// 系统性偏移的点，斜率/R² 必被带偏——测试靠这一点甄别过滤是否真的生效。
    /// 数据自洽：echo=真零载+F·a（物理声时），delta=echo−旧基线。
    /// </summary>
    private async Task SeedStalePointsAsync(
        IntegrationSeed.CalibratableChannel s, decimal staleRefTimeNs, int count = 2)
    {
        var shift = 27_200m - staleRefTimeNs; // 旧基线相对真零载的错位量
        for (var i = 0; i < count; i++)
        {
            var force = 15_000m + i * 10_000m;
            var echo = 27_200m + force * CalibrationFitData.Slope;
            await using var cmd = _fx.DataSource.CreateCommand(@"
                INSERT INTO torque_test_points (
                    bolt_id, channel_id, torque_nm, nut_factor, nominal_diameter_mm,
                    estimated_preload_n, sample_window_seconds, sample_count,
                    window_start, window_end, avg_echo_time_ns, avg_temp_c,
                    ref_time_ns, ref_temp_c, normalized_echo_time_ns, delta_tof_ns
                ) VALUES (
                    @bolt, @ch, @torque, 0.20, 16,
                    @force, 60, 100,
                    now() - interval '2 hours', now() - interval '2 hours', @echo, 22,
                    @ref, 22, @echo, @delta
                )");
            cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, s.BoltId);
            cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Bigint, s.ChannelId);
            cmd.Parameters.AddWithValue("torque", NpgsqlDbType.Numeric, force / 1000m);
            cmd.Parameters.AddWithValue("force", NpgsqlDbType.Numeric, force);
            cmd.Parameters.AddWithValue("ref", NpgsqlDbType.Numeric, staleRefTimeNs);
            cmd.Parameters.AddWithValue("echo", NpgsqlDbType.Numeric, echo);
            cmd.Parameters.AddWithValue("delta", NpgsqlDbType.Numeric, force * CalibrationFitData.Slope + shift);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
