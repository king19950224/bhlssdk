using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 正式载荷标定（<c>load-calibrate</c>）与遗留 pending 处理（<c>countersign</c>/<c>reject</c>）。
/// <para>
/// 2026-08-19 用户拍板取消全部审批环节：提交即自签生效（review_state='self_signed'），
/// 不再等待异人会签。本套测试锁定的新规矩：
/// ① R² 必须 ≥0.98（拒收线不因免审批而松动）；
/// ② 提交即生效——通道基线/K_L、active_calibration_id、bolts.state 三样一个事务内到位；
/// ③ countersign 只用于激活取消审批前遗留的 pending 记录，异人限制已移除，
///    且对非 pending 状态一律 409（不能重签/改签，审计不可覆盖）。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class LoadCalibrationCountersignIntegrationTests
{
    private readonly PostgresFixture _fx;
    public LoadCalibrationCountersignIntegrationTests(PostgresFixture fx) => _fx = fx;

    // 刻意与 seed 里通道自带的基线（27200 / 22）取不同值：这样"激活把基线写进通道"才是可证的，
    // 否则断言即使在完全没写的情况下也会通过。
    private const decimal RequestRefTimeNs = 26_500m;
    private const decimal RequestRefTempC = 18.5m;

    [PostgresFact]
    public async Task Submit_ActivatesImmediately_SelfSigned()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("正式载荷标定");
        var operatorId = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        var resp = await SubmitAsync(factory, operatorId, s.ChannelId, d: 0.5m);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("reviewState").GetString().Should().Be("self_signed", "免审批：提交即自签生效");
        body.GetProperty("activated").GetBoolean().Should().BeTrue();
        body.GetProperty("kL").GetDecimal().Should().BeApproximately(CalibrationFitData.Slope, 1e-9m);
        body.GetProperty("rSquared").GetDecimal().Should().BeApproximately(CalibrationFitData.ExpectedR2(0.5m), 0.001m);

        var calibId = body.GetProperty("calibrationId").GetGuid();
        (await ReviewStateOfAsync(calibId)).Should().Be("self_signed");
        (await _fx.ScalarAsync<bool>(
            "SELECT reviewer_signature_at IS NOT NULL FROM calibration_records WHERE id = @arg", calibId))
            .Should().BeTrue("自签也要落章，验收时要能出示签署时刻");

        // 生效三件套一个事务内到位：通道指向它、K_L 与基线写进通道、螺栓转已标定
        (await _fx.ActiveCalibrationAsync(s.ChannelId)).Should().Be(calibId);
        var ch = await ChannelBaselineAsync(s.ChannelId);
        ch.KL.Should().BeApproximately(CalibrationFitData.Slope, 1e-9m);
        ch.Intercept.Should().BeApproximately(0m, 1e-6m, "对称残差数据的最小二乘截距为 0");
        ch.RefTimeNs.Should().Be(RequestRefTimeNs, "基线要用标定提交的那份，而不是通道原有的");
        ch.RefTempC.Should().Be(RequestRefTempC);
        (await _fx.ScalarAsync<string>("SELECT state FROM bolts WHERE id = @arg", s.BoltId))
            .Should().Be("calibrated");
        (await AuditCountAsync(s.ChannelId, "load_calibrate_submit")).Should().Be(1);
    }

    [PostgresFact]
    public async Task Countersign_OnSelfSignedRecord_IsConflict_NoResign()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("正式载荷标定");
        var operatorId = await _fx.SeedUserAsync("reviewer");
        var otherId = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        var calibId = await SubmitOkAsync(factory, operatorId, s.ChannelId, d: 0.5m);

        // 已生效（self_signed）的记录不能被再签：签署人信息是审计的一部分，不许覆盖
        using var other = TestApi.AuthedClient(factory, otherId, "reviewer");
        var resp = await other.PostAsync(CountersignUrl(s.ChannelId, calibId), null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("仅 pending 可会签");
        (await _fx.ScalarAsync<long>(
            "SELECT reviewer_user_id FROM calibration_records WHERE id = @arg", calibId))
            .Should().Be(operatorId, "自签记录的签署人必须还是提交者");
    }

    [PostgresFact]
    public async Task Countersign_LegacyPending_BySubmitterThemselves_NowActivates()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("正式载荷标定");
        var operatorId = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        var calibId = await SubmitOkAsync(factory, operatorId, s.ChannelId, d: 0.5m);
        await DemoteToLegacyPendingAsync(calibId, s.ChannelId, s.BoltId);

        // 取消审批环节后：遗留 pending 由任何 reviewer 激活，含提交者本人（原异人限制在单人运维下是死锁）
        using var self = TestApi.AuthedClient(factory, operatorId, "reviewer");
        var resp = await self.PostAsync(CountersignUrl(s.ChannelId, calibId), null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "异人限制已移除，自己激活自己的遗留 pending 应放行");
        (await ReviewStateOfAsync(calibId)).Should().Be("approved");
        (await _fx.ActiveCalibrationAsync(s.ChannelId)).Should().Be(calibId);
        (await _fx.ScalarAsync<string>("SELECT state FROM bolts WHERE id = @arg", s.BoltId))
            .Should().Be("calibrated");
        (await AuditCountAsync(s.ChannelId, "load_calibrate_countersign")).Should().Be(1);
    }

    [PostgresFact]
    public async Task Submit_R2InTemporaryWarnBand_IsRejectedOutright()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("正式载荷标定");
        var operatorId = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        // d=1.0 → R²≈0.969。同一组数据在临时扭矩法那条路径上是"留档不激活"（200），
        // 正式载荷标定这里必须直接拒收——免审批不等于免判据，R² 线一毫米都不松。
        var resp = await SubmitAsync(factory, operatorId, s.ChannelId, d: 1.0m);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("低于正式载荷标定合格线");
        (await _fx.ScalarAsync<long>("SELECT count(*) FROM calibration_records WHERE bolt_id = @arg", s.BoltId))
            .Should().Be(0, "不合格的正式标定连档案都不留");
    }

    [PostgresFact]
    public async Task Countersign_LegacyPending_RestartsValidityFromActivationDate()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("正式载荷标定");
        var operatorId = await _fx.SeedUserAsync("reviewer");
        var reviewerId = await _fx.SeedUserAsync("reviewer");
        const int validDays = 180;

        using var factory = TestApi.CreateFactory(_fx);
        var calibId = await SubmitOkAsync(factory, operatorId, s.ChannelId, d: 0.5m, expiresDays: validDays);
        await DemoteToLegacyPendingAsync(calibId, s.ChannelId, s.BoltId);

        // 模拟"提交后压了 30 天才被激活"的遗留场景
        await BackdateSubmissionAsync(calibId, days: 30);

        using var reviewer = TestApi.AuthedClient(factory, reviewerId, "reviewer");
        (await reviewer.PostAsync(CountersignUrl(s.ChannelId, calibId), null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // 有效期从激活起算：pending 压着的日子不消耗有效期
        var daysLeft = await _fx.ScalarAsync<int>(
            "SELECT (expires_at - CURRENT_DATE)::int FROM calibration_records WHERE id = @arg", calibId);
        daysLeft.Should().Be(validDays, "pending 期间不消耗有效期，激活当天重新起算满 180 天");
    }

    [PostgresFact]
    public async Task Reject_LegacyPending_MarksRejected_AndLeavesChannelUntouched()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("正式载荷标定");
        var operatorId = await _fx.SeedUserAsync("reviewer");
        var reviewerId = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        var calibId = await SubmitOkAsync(factory, operatorId, s.ChannelId, d: 0.5m);
        await DemoteToLegacyPendingAsync(calibId, s.ChannelId, s.BoltId);

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

    /// <summary>
    /// 把一条（新流程下自签生效的）记录降级回"取消审批前遗留的 pending"形态：
    /// 记录回 pending、清签署，通道撤激活与基线快照、螺栓回未标定——复原两步流程时代的中间态。
    /// </summary>
    private async Task DemoteToLegacyPendingAsync(Guid calibrationId, long channelId, long boltId)
    {
        await using (var cmd = _fx.DataSource.CreateCommand(@"
            UPDATE calibration_records
               SET review_state='pending', reviewer_user_id=NULL, reviewer_signature_at=NULL, superseded_by=NULL
             WHERE id=@id"))
        {
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, calibrationId);
            (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
        }
        await using (var cmd = _fx.DataSource.CreateCommand(@"
            UPDATE channels SET active_calibration_id=NULL, k_l=NULL, k_l_uncertainty=NULL, k_l_intercept_ns=NULL
             WHERE id=@cid"))
        {
            cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
            (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
        }
        await using (var cmd = _fx.DataSource.CreateCommand(
            "UPDATE bolts SET state='uncalibrated' WHERE id=@bid"))
        {
            cmd.Parameters.AddWithValue("bid", NpgsqlDbType.Bigint, boltId);
            (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
        }
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

    /// <summary>把提交时刻连同当时算出的到期日整体前推，模拟久压后才被激活。</summary>
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
