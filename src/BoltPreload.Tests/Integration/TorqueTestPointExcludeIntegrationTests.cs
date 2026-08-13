using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 扭矩阶梯点的剔除 / 恢复（PATCH torque-test-points/{id}）。
/// <para>
/// 现场最常见的坏点是"扳手还在拧就采了"：采样窗口里混着加载过程的声时，均值落在两个扭矩档之间，
/// 悄悄把拟合斜率带偏。拟合与报告一直按 excluded=false 过滤，但在此之前没有接口能写这个标志，
/// 坏点只能靠改库清理。这里锁定的是：剔除立刻改变拟合口径、剔除可逆、剔除必须留下原因和审计。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class TorqueTestPointExcludeIntegrationTests
{
    private readonly PostgresFixture _fx;
    public TorqueTestPointExcludeIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task Exclude_DropsPointFromFit_AndIsReversible()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("阶梯点剔除");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        await _fx.SeedTorquePointsAsync(s, d: 0.5m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var pointId = await FirstPointIdAsync(s.ChannelId);

        var excluded = await client.PatchAsJsonAsync(
            $"/api/channels/{s.ChannelId}/torque-test-points/{pointId}",
            new { excluded = true, reason = "扳手还在拧就采了" });

        excluded.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await excluded.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("excluded").GetBoolean().Should().BeTrue();
        body.GetProperty("remainingValidPoints").GetInt64().Should().Be(3);

        // 拟合口径立刻跟着变，否则剔了等于没剔
        var afterExclude = await client.GetFromJsonAsync<JsonElement>(
            $"/api/channels/{s.ChannelId}/torque-test-points/summary");
        afterExclude.GetProperty("pointCount").GetInt64().Should().Be(3);

        (await _fx.ScalarAsync<string>(
            "SELECT exclude_reason FROM torque_test_points WHERE id = @arg", pointId))
            .Should().Be("扳手还在拧就采了", "原因要进档案，事后得说得清这个点为什么不算数");
        (await AuditCountAsync(s.ChannelId)).Should().Be(1);

        // 误剔能退回来（不填原因也允许——恢复不需要辩解）
        var restored = await client.PatchAsJsonAsync(
            $"/api/channels/{s.ChannelId}/torque-test-points/{pointId}", new { excluded = false });

        restored.StatusCode.Should().Be(HttpStatusCode.OK);
        (await restored.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("remainingValidPoints").GetInt64().Should().Be(4);
        (await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM torque_test_points WHERE channel_id = @arg AND excluded = false", s.ChannelId))
            .Should().Be(4);
        (await AuditCountAsync(s.ChannelId)).Should().Be(2, "剔除和恢复都要留痕");
    }

    [PostgresFact]
    public async Task ExcludedPoint_IsNotUsedByApplyTorqueFit()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("剔除后拟合");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        await _fx.SeedTorquePointsAsync(s, d: 0.5m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var pointId = await FirstPointIdAsync(s.ChannelId);
        (await client.PatchAsJsonAsync($"/api/channels/{s.ChannelId}/torque-test-points/{pointId}",
            new { excluded = true, reason = "该时段信号丢" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var fit = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/apply-torque-fit", new { });
        fit.StatusCode.Should().Be(HttpStatusCode.OK);
        var calibId = (await fit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("calibrationId").GetGuid();

        // 存档的原始点快照必须与拟合口径一致：被剔的点不能出现在证据链里
        (await _fx.ScalarAsync<int>(
            "SELECT jsonb_array_length(points_jsonb) FROM calibration_records WHERE id = @arg", calibId))
            .Should().Be(3);
    }

    [PostgresFact]
    public async Task Exclude_RequiresReason_AndRejectsForeignPoint()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("剔除入参");
        var other = await _fx.SeedCalibratableChannelAsync("别的通道");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        await _fx.SeedTorquePointsAsync(s, d: 0.5m);
        await _fx.SeedTorquePointsAsync(other, d: 0.5m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var pointId = await FirstPointIdAsync(s.ChannelId);
        var url = $"/api/channels/{s.ChannelId}/torque-test-points/{pointId}";

        (await client.PatchAsJsonAsync(url, new { excluded = true }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "剔除不给原因等于毁证据");
        (await client.PatchAsJsonAsync(url, new { excluded = true, reason = "   " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "空白原因不算原因");
        (await client.PatchAsJsonAsync(url, new { }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 拿别的通道的点 id 打过来：不能越过通道边界改数据
        var foreignPointId = await FirstPointIdAsync(other.ChannelId);
        (await client.PatchAsJsonAsync(
            $"/api/channels/{s.ChannelId}/torque-test-points/{foreignPointId}",
            new { excluded = true, reason = "越权" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fx.ScalarAsync<bool>(
            "SELECT excluded FROM torque_test_points WHERE id = @arg", foreignPointId))
            .Should().BeFalse("别的通道的点不能被动到");
    }

    [PostgresFact]
    public async Task Exclude_IsReviewerOnly()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("剔除权限");
        var operatorId = await _fx.SeedUserAsync("operator");
        await _fx.SeedTorquePointsAsync(s, d: 0.5m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, operatorId, "operator");

        // 剔点直接改变标定依据，与签发标定同权限
        (await client.PatchAsJsonAsync(
            $"/api/channels/{s.ChannelId}/torque-test-points/{await FirstPointIdAsync(s.ChannelId)}",
            new { excluded = true, reason = "越权尝试" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- helpers ----

    private async Task<long> FirstPointIdAsync(long channelId)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "SELECT id FROM torque_test_points WHERE channel_id = @cid ORDER BY id LIMIT 1");
        cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<long> AuditCountAsync(long channelId) => _fx.ScalarAsync<long>(
        "SELECT count(*) FROM audit_log WHERE action = 'torque_point_exclude' AND target_id = @arg",
        channelId);
}
