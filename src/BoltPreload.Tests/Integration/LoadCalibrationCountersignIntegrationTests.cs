using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 正式载荷标定的提交—会签两步流程（<c>load-calibrate</c> + <c>countersign</c>，GB/T 43232 路径 B/C）。
/// <para>
/// 合规等级最高的一条路径，验收报告上的预紧力数字最后都回溯到这里的 K_L。规矩三条：
/// R² 必须 ≥0.98（没有临时扭矩法那个 [0.95,0.98) 留档层）、提交本身不生效、生效得由另一个人会签。
/// 前两条错了会当场出错数；第三条错了什么都不报，一个人自己提交自己签，接口照常 200、标定照常生效，
/// 只有事后翻审计才看得出双签是假的。这三条以前一条测试都没有。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class LoadCalibrationCountersignIntegrationTests
{
    private readonly PostgresFixture _fx;
    public LoadCalibrationCountersignIntegrationTests(PostgresFixture fx) => _fx = fx;

    // 刻意与 seed 里通道自带的基线（27200 / 22）取不同值：这样"会签把基线写进通道"才是可证的，
    // 否则断言即使在完全没写的情况下也会通过。
    private const decimal RequestRefTimeNs = 26_500m;
    private const decimal RequestRefTempC = 18.5m;

    [PostgresFact]
    public async Task Submit_RecordsPending_ButDoesNotActivateAnything()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("正式载荷标定");
        var operatorId = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        var resp = await SubmitAsync(factory, operatorId, s.ChannelId, d: 0.5m);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("reviewState").GetString().Should().Be("pending");
        body.GetProperty("kL").GetDecimal().Should().BeApproximately(CalibrationFitData.Slope, 1e-9m);
        body.GetProperty("rSquared").GetDecimal().Should().BeApproximately(CalibrationFitData.ExpectedR2(0.5m), 0.001m);

        var calibId = body.GetProperty("calibrationId").GetGuid();
        (await ReviewStateOfAsync(calibId)).Should().Be("pending");
        (await _fx.ScalarAsync<bool>(
            "SELECT reviewer_user_id IS NULL FROM calibration_records WHERE id = @arg", calibId))
            .Should().BeTrue("尚未会签，不该有复核人");

        // 一个人单独提交，不能有任何东西进入生产
        (await _fx.ActiveCalibrationAsync(s.ChannelId)).Should().BeNull("提交只是入队待审，不生效");
        (await _fx.ScalarAsync<bool>("SELECT k_l IS NULL FROM channels WHERE id = @arg", s.ChannelId))
            .Should().BeTrue("未会签前不得把 K_L 写进通道");
        (await _fx.ScalarAsync<string>("SELECT state FROM bolts WHERE id = @arg", s.BoltId))
            .Should().Be("uncalibrated", "未会签前螺栓不算已标定");
    }

    [PostgresFact]
    public async Task Countersign_BySubmitterThemselves_IsRejected_AndStaysPending()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("正式载荷标定");
        var operatorId = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        var calibId = await SubmitOkAsync(factory, operatorId, s.ChannelId, d: 0.5m);

        // 同一个人回来给自己会签
        using var self = TestApi.AuthedClient(factory, operatorId, "reviewer");
        var resp = await self.PostAsync(CountersignUrl(s.ChannelId, calibId), null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "两人复核底线：不允许自签自用");
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("会签人必须与提交人不同");

        // 被拒之后整条流程必须原地不动，不能出现"报了错但其实签上了"
        (await ReviewStateOfAsync(calibId)).Should().Be("pending");
        (await _fx.ActiveCalibrationAsync(s.ChannelId)).Should().BeNull();
        (await _fx.ScalarAsync<string>("SELECT state FROM bolts WHERE id = @arg", s.BoltId))
            .Should().Be("uncalibrated");
        (await AuditCountAsync(s.ChannelId, "load_calibrate_countersign")).Should().Be(0, "自签失败不该留下会签审计");
    }

    [PostgresFact]
    public async Task Countersign_ByAnotherReviewer_ActivatesAndWritesBaselineToChannel()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("正式载荷标定");
        var operatorId = await _fx.SeedUserAsync("reviewer");
        var reviewerId = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        var calibId = await SubmitOkAsync(factory, operatorId, s.ChannelId, d: 0.5m);

        using var reviewer = TestApi.AuthedClient(factory, reviewerId, "reviewer");
        var resp = await reviewer.PostAsync(CountersignUrl(s.ChannelId, calibId), null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("activated").GetBoolean().Should().BeTrue();
        body.GetProperty("operatorUserId").GetInt64().Should().Be(operatorId);
        body.GetProperty("reviewerUserId").GetInt64().Should().Be(reviewerId);

        (await ReviewStateOfAsync(calibId)).Should().Be("approved");
        (await _fx.ScalarAsync<bool>(
            "SELECT reviewer_signature_at IS NOT NULL FROM calibration_records WHERE id = @arg", calibId))
            .Should().BeTrue("会签时刻要落章，验收时要能出示");

        // 生效意味着这三样一起到位：通道指向它、K_L 与基线写进通道、螺栓转已标定
        (await _fx.ActiveCalibrationAsync(s.ChannelId)).Should().Be(calibId);
        var ch = await ChannelBaselineAsync(s.ChannelId);
        ch.KL.Should().BeApproximately(CalibrationFitData.Slope, 1e-9m);
        ch.Intercept.Should().BeApproximately(0m, 1e-6m, "对称残差数据的最小二乘截距为 0");
        ch.RefTimeNs.Should().Be(RequestRefTimeNs, "基线要用标定提交的那份，而不是通道原有的");
        ch.RefTempC.Should().Be(RequestRefTempC);
        (await _fx.ScalarAsync<string>("SELECT state FROM bolts WHERE id = @arg", s.BoltId))
            .Should().Be("calibrated");

        (await AuditCountAsync(s.ChannelId, "load_calibrate_submit")).Should().Be(1);
        (await AuditCountAsync(s.ChannelId, "load_calibrate_countersign")).Should().Be(1);
    }

    [PostgresFact]
    public async Task Countersign_Twice_IsRejectedAsConflict()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("正式载荷标定");
        var operatorId = await _fx.SeedUserAsync("reviewer");
        var firstReviewerId = await _fx.SeedUserAsync("reviewer");
        var secondReviewerId = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        var calibId = await SubmitOkAsync(factory, operatorId, s.ChannelId, d: 0.5m);

        using var first = TestApi.AuthedClient(factory, firstReviewerId, "reviewer");
        (await first.PostAsync(CountersignUrl(s.ChannelId, calibId), null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // 已生效的标定不能被第二个人再签一次：否则审计里的复核人会被后来者覆盖掉
        using var second = TestApi.AuthedClient(factory, secondReviewerId, "reviewer");
        var resp = await second.PostAsync(CountersignUrl(s.ChannelId, calibId), null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("仅 pending 可会签");
        (await _fx.ScalarAsync<long>(
            "SELECT reviewer_user_id FROM calibration_records WHERE id = @arg", calibId))
            .Should().Be(firstReviewerId, "复核人必须还是第一个签的那位");
    }

    [PostgresFact]
    public async Task Submit_R2InTemporaryWarnBand_IsRejectedOutright()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("正式载荷标定");
        var operatorId = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        // d=1.0 → R²≈0.969。同一组数据在临时扭矩法那条路径上是"留档不激活"（200），
        // 正式载荷标定这里必须直接拒收——两条路径的判定差异就在这一条上。
        var resp = await SubmitAsync(factory, operatorId, s.ChannelId, d: 1.0m);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("低于正式载荷标定合格线");
        (await _fx.ScalarAsync<long>("SELECT count(*) FROM calibration_records WHERE bolt_id = @arg", s.BoltId))
            .Should().Be(0, "不合格的正式标定连档案都不留");
    }

    [PostgresFact]
    public async Task Countersign_RestartsValidityFromActivationDate()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("正式载荷标定");
        var operatorId = await _fx.SeedUserAsync("reviewer");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        const int validDays = 180;

        using var factory = TestApi.CreateFactory(_fx);
        var calibId = await SubmitOkAsync(factory, operatorId, s.ChannelId, d: 0.5m, expiresDays: validDays);

        // 模拟"提交后压了 30 天才有人来会签"
        await BackdateSubmissionAsync(calibId, days: 30);

        using var reviewer = TestApi.AuthedClient(factory, reviewerId, "reviewer");
        (await reviewer.PostAsync(CountersignUrl(s.ChannelId, calibId), null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // 有效期从激活起算：审批期间不能偷偷消耗掉。按提交时刻固定的话这里只剩 150 天，
        // 现场表现是一份刚生效的标定莫名其妙提前到期。
        var daysLeft = await _fx.ScalarAsync<int>(
            "SELECT (expires_at - CURRENT_DATE)::int FROM calibration_records WHERE id = @arg", calibId);
        daysLeft.Should().Be(validDays, "pending 期间不消耗有效期，会签当天重新起算满 180 天");
    }

    [PostgresFact]
    public async Task Reject_MarksRejected_AndLeavesChannelUntouched()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("正式载荷标定");
        var operatorId = await _fx.SeedUserAsync("reviewer");
        var reviewerId = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        var calibId = await SubmitOkAsync(factory, operatorId, s.ChannelId, d: 0.5m);

        const string reason = "加载装置超检";
        using var reviewer = TestApi.AuthedClient(factory, reviewerId, "reviewer");
        var resp = await reviewer.PostAsync(
            $"/api/channels/{s.ChannelId}/calibrations/{calibId}/reject?reason={Uri.EscapeDataString(reason)}", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReviewStateOfAsync(calibId)).Should().Be("rejected");
        (await _fx.ScalarAsync<string>("SELECT notes FROM calibration_records WHERE id = @arg", calibId))
            .Should().Contain($"驳回:{reason}", "驳回原因要留在档案里，否则复采时没人知道上次为什么被打回");
        (await _fx.ActiveCalibrationAsync(s.ChannelId)).Should().BeNull("驳回不动通道");

        // 驳回后不能再改判成通过：pending 队列的状态机是单向的
        (await reviewer.PostAsync(CountersignUrl(s.ChannelId, calibId), null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ===================== 请求与读库 =====================

    private static string CountersignUrl(long channelId, Guid calibrationId) =>
        $"/api/channels/{channelId}/calibrations/{calibrationId}/countersign";

    private static async Task<HttpResponseMessage> SubmitAsync(
        WebApplicationFactory<Program> factory, long operatorId, long channelId, decimal d, int? expiresDays = null)
    {
        using var client = TestApi.AuthedClient(factory, operatorId, "reviewer");
        var deltas = CalibrationFitData.DeltaTofs(d);
        return await client.PostAsJsonAsync($"/api/channels/{channelId}/load-calibrate", new
        {
            points = CalibrationFitData.Forces
                .Select((f, i) => new { forceN = (double)f, deltaTofNs = (double)deltas[i] })
                .ToArray(),
            refTimeNs = RequestRefTimeNs,
            refTempC = RequestRefTempC,
            systematicRelativeUncertainty = 0.005m,
            loadingDevice = "拉伸机 XLJ-600",
            loadingDeviceCert = "JJ-2026-0417",
            expiresDays,
        });
    }

    private static async Task<Guid> SubmitOkAsync(
        WebApplicationFactory<Program> factory, long operatorId, long channelId, decimal d, int? expiresDays = null)
    {
        var resp = await SubmitAsync(factory, operatorId, channelId, d, expiresDays);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "前置提交必须成功，否则后面的断言无从谈起");
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("calibrationId").GetGuid();
    }

    private Task<string> ReviewStateOfAsync(Guid calibrationId) =>
        _fx.ScalarAsync<string>("SELECT review_state FROM calibration_records WHERE id = @arg", calibrationId);

    private sealed record Baseline(decimal KL, decimal Intercept, decimal RefTimeNs, decimal RefTempC);

    private async Task<Baseline> ChannelBaselineAsync(long channelId)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "SELECT k_l, k_l_intercept_ns, ref_time_ns, ref_temp_c FROM channels WHERE id = @cid");
        cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
        await using var r = await cmd.ExecuteReaderAsync();
        (await r.ReadAsync()).Should().BeTrue();
        return new Baseline(r.GetDecimal(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3));
    }

    private async Task<long> AuditCountAsync(long channelId, string action)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "SELECT count(*) FROM audit_log WHERE target_type='channel' AND target_id=@cid AND action=@a");
        cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
        cmd.Parameters.AddWithValue("a", NpgsqlDbType.Varchar, action);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>把提交时刻连同当时算出的到期日整体前推，模拟久审后才会签。</summary>
    private async Task BackdateSubmissionAsync(Guid calibrationId, int days)
    {
        // expires_at 是 date 列：减整数得 date，减 interval 会变成 timestamp 而写不回去
        await using var cmd = _fx.DataSource.CreateCommand(@"
            UPDATE calibration_records
               SET created_at = created_at - make_interval(days => @d),
                   expires_at = expires_at - @d
             WHERE id = @id");
        cmd.Parameters.AddWithValue("d", NpgsqlDbType.Integer, days);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, calibrationId);
        (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
    }
}
