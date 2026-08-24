using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 通道参数变更工作流（<c>/api/config-changes</c>）：requested → approved → executed → verified，
/// 外加 rejected / failed 两个终态。
/// <para>
/// 2026-08-19 取消审批环节：新参数变更走 commission 直发不进本队列，本工作流只剩存量待办的处理用途，
/// 异人限制随之移除（自批放行）。仍要守住的是状态机顺序（执行前必须 approved、验证前必须 executed）
/// 和责任链（谁批的、谁执行的都落库落审计）。
/// </para>
/// <para>
/// 状态守卫在设备查找之前，所以不接真机也能测到 execute/verify 的越级拦截；下发本身另有真机联调覆盖。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class ConfigChangeApprovalIntegrationTests
{
    private readonly PostgresFixture _fx;
    public ConfigChangeApprovalIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task Approve_BySelf_IsAllowed_ForLegacyPending()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("自审批");
        var reviewer = await _fx.SeedUserAsync("reviewer");
        var id = await SeedRequestAsync(seed, requestedBy: reviewer);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewer, "reviewer");

        var resp = await client.PostAsync($"/api/config-changes/{id}/approve", null);

        // 2026-08-19 取消审批环节：存量待办任何复核员可批准（含提交者本人），责任人照落库
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "异人限制已随审批环节取消移除");
        (await StatusOfAsync(id)).Should().Be("approved");
        (await ApprovedByAsync(id)).Should().Be(reviewer, "自批也要落责任人");
    }

    [PostgresFact]
    public async Task Approve_ByAnotherReviewer_MovesToApproved_AndWritesAudit()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("异人审批");
        var requester = await _fx.SeedUserAsync("operator");
        var reviewer = await _fx.SeedUserAsync("reviewer");
        var id = await SeedRequestAsync(seed, requestedBy: requester);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewer, "reviewer");

        var resp = await client.PostAsync($"/api/config-changes/{id}/approve", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(id)).Should().Be("approved");
        (await ApprovedByAsync(id)).Should().Be(reviewer, "审批人要落库，事后追责靠它");
        (await AuditCountAsync("config_change_approve", seed.ChannelId))
            .Should().Be(1, "审批与审计同事务，责任链不能断");
    }

    [PostgresFact]
    public async Task Approve_Twice_IsRefused_SoOneReviewerCannotReplaceAnother()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("重复审批");
        var id = await SeedRequestAsync(seed, requestedBy: await _fx.SeedUserAsync("operator"));
        var first = await _fx.SeedUserAsync("reviewer");
        var second = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        using var c1 = TestApi.AuthedClient(factory, first, "reviewer");
        using var c2 = TestApi.AuthedClient(factory, second, "reviewer");

        (await c1.PostAsync($"/api/config-changes/{id}/approve", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await c2.PostAsync($"/api/config-changes/{id}/approve", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "只有 requested 能审批");

        (await ApprovedByAsync(id)).Should().Be(first, "审批人还得是第一个人，不能被后来者顶掉");
    }

    [PostgresFact]
    public async Task Execute_WithoutApproval_IsRefused()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("越级执行");
        var id = await SeedRequestAsync(seed, requestedBy: await _fx.SeedUserAsync("operator"));

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var resp = await client.PostAsync($"/api/config-changes/{id}/execute", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "未审批就执行必须拒。放过去等于操作员单方面改了设备参数，整套审批白设");
        (await resp.Content.ReadAsStringAsync()).Should().Contain("必须先审批");
        (await StatusOfAsync(id)).Should().Be("requested");
    }

    [PostgresFact]
    public async Task Verify_BeforeExecute_IsRefused()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("越级验证");
        var id = await SeedRequestAsync(seed, requestedBy: await _fx.SeedUserAsync("operator"), status: "approved");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var resp = await client.PostAsync($"/api/config-changes/{id}/verify", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, "还没下发就标成已验证，等于凭空给出一份读回比对结论");
        (await StatusOfAsync(id)).Should().Be("approved");
    }

    [PostgresFact]
    public async Task Reject_RecordsReasonAndReviewer_AndBlocksLaterApproval()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("驳回");
        var id = await SeedRequestAsync(seed, requestedBy: await _fx.SeedUserAsync("operator"));
        var reviewer = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewer, "reviewer");

        const string reason = "增益调得过高，回波会削顶";
        var resp = await client.PostAsJsonAsync($"/api/config-changes/{id}/reject", new { reason });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(id)).Should().Be("rejected");
        (await FailureReasonAsync(id)).Should().Be(reason, "原因要原样留着，申请人得知道改什么再报");
        (await ApprovedByAsync(id)).Should().Be(reviewer, "驳回人也记在 approved_by 上（见 0016 迁移的列注释）");
        (await AuditCountAsync("config_change_reject", seed.ChannelId)).Should().Be(1);

        // 已驳回的单子不能再被审批复活
        (await client.PostAsync($"/api/config-changes/{id}/approve", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// <c>RejectBody</c> 声明成可空、代码里也走 <c>body?.Reason</c>，即"原因可不填"。
    /// 这里连字段整个不给和只给空白两种都过一遍，确认那条分支是活的，且 failure_reason 不会留空
    /// ——留空的话审批单上"驳回但没写原因"和"还没处理"就分不开了。
    /// </summary>
    [PostgresFact]
    public async Task Reject_WithoutReason_StillRecordsThatNoneWasGiven()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("驳回无原因");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        foreach (var body in new object[] { new { }, new { reason = "   " } })
        {
            var id = await SeedRequestAsync(seed, requestedBy: await _fx.SeedUserAsync("operator"));

            var resp = await client.PostAsJsonAsync($"/api/config-changes/{id}/reject", body);

            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            (await StatusOfAsync(id)).Should().Be("rejected");
            (await FailureReasonAsync(id)).Should().NotBeNullOrWhiteSpace().And.Contain("未填写");
        }
    }

    [PostgresFact]
    public async Task Approve_ByOperator_IsForbidden()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("越权审批");
        var id = await SeedRequestAsync(seed, requestedBy: await _fx.SeedUserAsync("operator"));

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var approve = await client.PostAsync($"/api/config-changes/{id}/approve", null);
        var reject = await client.PostAsJsonAsync($"/api/config-changes/{id}/reject", new { reason = "越权驳回" });

        approve.StatusCode.Should().Be(HttpStatusCode.Forbidden, "approve 是 reviewer 以上的权限");
        reject.StatusCode.Should().Be(HttpStatusCode.Forbidden, "reject 同样是 reviewer 以上的权限");
        (await StatusOfAsync(id)).Should().Be("requested");
    }

    [PostgresFact]
    public async Task PendingQueue_ShowsBothAwaitingApprovalAndAwaitingExecution()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("待办队列");
        var waiting = await SeedRequestAsync(seed, requestedBy: await _fx.SeedUserAsync("operator"));
        var approved = await SeedRequestAsync(seed, requestedBy: await _fx.SeedUserAsync("operator"), status: "approved");
        var done = await SeedRequestAsync(seed, requestedBy: await _fx.SeedUserAsync("operator"), status: "verified");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var body = await client.GetFromJsonAsync<JsonElement>("/api/config-changes?status=pending&limit=500");
        var ids = body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetInt64()).ToHashSet();

        // pending 是"还需要人动手的"，包含待审批和已审批待执行两档；漏掉后者会让单子卡在半路没人管
        ids.Should().Contain(waiting).And.Contain(approved);
        ids.Should().NotContain(done, "已验证的不该再出现在待办里");
    }

    [PostgresFact]
    public async Task Detail_ReportsWhoDidWhat_AndUnknownIdIs404()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("审批单详情");
        var requester = await _fx.SeedUserAsync("operator");
        var id = await SeedRequestAsync(seed, requestedBy: requester);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("reviewer"), "reviewer");

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/config-changes/{id}");
        body.GetProperty("requestedBy").GetInt64().Should().Be(requester);
        // 审批单上要显示人名。只给 user_id 的话，客户对着"用户 7"没法核对是谁改的参数
        body.GetProperty("requestedByName").GetString().Should().NotBeNullOrEmpty();

        (await client.GetAsync("/api/config-changes/999999999")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    // ---- helpers ----

    private async Task<long> SeedRequestAsync(
        IntegrationSeed.CalibratableChannel seed, long requestedBy, string status = "requested")
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO channel_config_change_requests
                (device_id, channel_id, channel_no, requested_by, requested_jsonb, status)
            VALUES (@dev, @ch, 5, @by, @json::jsonb, @st)
            RETURNING id");
        cmd.Parameters.AddWithValue("dev", NpgsqlDbType.Bigint, seed.DeviceId);
        cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Bigint, seed.ChannelId);
        cmd.Parameters.AddWithValue("by", NpgsqlDbType.Bigint, requestedBy);
        cmd.Parameters.AddWithValue("json", NpgsqlDbType.Jsonb, """{"gain":20,"threshold":-480}""");
        cmd.Parameters.AddWithValue("st", NpgsqlDbType.Varchar, status);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<string> StatusOfAsync(long id) =>
        _fx.ScalarAsync<string>("SELECT status FROM channel_config_change_requests WHERE id=@arg", id);

    private async Task<long?> ApprovedByAsync(long id)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "SELECT approved_by FROM channel_config_change_requests WHERE id=@id");
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
        return await cmd.ExecuteScalarAsync() is long v ? v : null;
    }

    private async Task<string?> FailureReasonAsync(long id)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "SELECT failure_reason FROM channel_config_change_requests WHERE id=@id");
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private Task<long> AuditCountAsync(string action, long channelId) =>
        _fx.ScalarAsync<long>(
            $"SELECT count(*) FROM audit_log WHERE action='{action}' AND target_type='channel' AND target_id=@arg",
            channelId);
}
