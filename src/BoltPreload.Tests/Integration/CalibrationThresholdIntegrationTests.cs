using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 临时扭矩阶梯拟合的 R² 三段判定（apply-torque-fit，门槛值见 <c>CalibrationThresholds</c>）。
/// <list type="bullet">
/// <item>R² ≥ 0.98：通过，激活并顶替旧标定</item>
/// <item>0.95 ≤ R² &lt; 0.98：留档 passed=false，但不激活，通道继续沿用原标定</item>
/// <item>R² &lt; 0.95：拒绝，400，什么都不写</item>
/// </list>
/// <para>
/// 中间那档最容易出事：判错方向会把在产的好标定 supersede 掉，而 sink 对 passed=false 不出预紧力，
/// 通道就从能出数变成无数据，现场看到的是一片空白而不是报错。
/// </para>
/// <para>
/// 这三段只适用于临时扭矩法，正式载荷标定没有中间层，见
/// <see cref="LoadCalibrationCountersignIntegrationTests"/>。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class CalibrationThresholdIntegrationTests
{
    private readonly PostgresFixture _fx;
    public CalibrationThresholdIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task ApplyTorqueFit_R2AbovePassLine_ActivatesOnChannel()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("标定门槛");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        await _fx.SeedTorquePointsAsync(s, d: 0.5m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/apply-torque-fit", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("rSquared").GetDecimal().Should().BeApproximately(CalibrationFitData.ExpectedR2(0.5m), 0.001m);
        body.GetProperty("passed").GetBoolean().Should().BeTrue("R² 已过 0.98 合格线");

        var calibId = body.GetProperty("calibrationId").GetGuid();
        (await _fx.ActiveCalibrationAsync(s.ChannelId)).Should().Be(calibId, "达标的拟合必须激活到通道");
        (await _fx.ScalarAsync<bool>("SELECT passed FROM calibration_records WHERE id = @arg", calibId))
            .Should().BeTrue();
    }

    [PostgresFact]
    public async Task ApplyTorqueFit_R2InWarnBand_KeepsRecordButDoesNotSupersedeActive()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("标定门槛");
        var reviewerId = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        // 先用一组达标数据建立"在产的好标定"
        await _fx.SeedTorquePointsAsync(s, d: 0.5m);
        var good = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/apply-torque-fit", new { });
        good.StatusCode.Should().Be(HttpStatusCode.OK);
        var goodId = (await good.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("calibrationId").GetGuid();
        (await _fx.ActiveCalibrationAsync(s.ChannelId)).Should().Be(goodId);

        // 再补一组落在 [0.95, 0.98) 的数据重拟合
        await ExcludeExistingPointsAsync(s.ChannelId);
        await _fx.SeedTorquePointsAsync(s, d: 1.0m);
        var warn = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/apply-torque-fit", new { });

        warn.StatusCode.Should().Be(HttpStatusCode.OK, "警告层是留档，不是报错");
        var body = await warn.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("rSquared").GetDecimal().Should().BeApproximately(CalibrationFitData.ExpectedR2(1.0m), 0.001m);
        body.GetProperty("passed").GetBoolean().Should().BeFalse("未达 0.98 合格线");

        var warnId = body.GetProperty("calibrationId").GetGuid();
        warnId.Should().NotBe(goodId);
        (await _fx.ScalarAsync<bool>("SELECT passed FROM calibration_records WHERE id = @arg", warnId))
            .Should().BeFalse("留档记录本身要如实标记不合格");

        (await _fx.ActiveCalibrationAsync(s.ChannelId)).Should().Be(goodId,
            "未达合格线的拟合只留档不激活，通道必须仍指向原来那份好标定");
        (await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM calibration_records WHERE id = @arg AND superseded_by IS NOT NULL", goodId))
            .Should().Be(0, "好标定不该被劣质拟合 supersede");
    }

    [PostgresFact]
    public async Task ApplyTorqueFit_R2BelowRejectLine_Returns400AndWritesNothing()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("标定门槛");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        await _fx.SeedTorquePointsAsync(s, d: 1.5m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/apply-torque-fit", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "R² 低于 0.95 拒绝下限");
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("拒绝应用为标定");

        // 拒绝路径整体回滚：既不留档，也不动通道
        (await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM calibration_records WHERE bolt_id = @arg", s.BoltId))
            .Should().Be(0, "被拒的拟合不该留下标定档案");
        (await _fx.ActiveCalibrationAsync(s.ChannelId)).Should().BeNull();
    }

    /// <summary>把已有点标成 excluded，让下一次拟合只看新插的那组（拟合只取 excluded=false）。</summary>
    private async Task ExcludeExistingPointsAsync(long channelId)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "UPDATE torque_test_points SET excluded = true WHERE channel_id = @cid");
        cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
        await cmd.ExecuteNonQueryAsync();
    }
}
