using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 报告的生成—签发闭环（<c>/api/reports</c>）。
/// <para>
/// 报告是交给业主的东西，签发即锁定为正式记录，规矩和标定会签是同一条：编制人不能自己签。
/// 另有一条防篡改承诺——落盘 HTML 的 sha256 存在库里，下载时逐字节复核，改过的文件不给下。
/// </para>
/// <para>
/// 这整组端点此前没有任何测试请求过：自签放行不会报错（照样返回 200、报告照样变"已签发"），
/// 校验失效也不会报错（篡改过的报告照常下载），两种坏法都要等到出事追责时才发现。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class ReportSigningIntegrationTests
{
    private readonly PostgresFixture _fx;
    public ReportSigningIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task Sign_ByItsOwnAuthor_IsRejected_AndReportStaysDraft()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateFactory(_fx);
        var (reportId, authorId) = await GenerateReportAsync(factory);

        using var author = TestApi.AuthedClient(factory, authorId, "reviewer");
        var resp = await author.PostAsync($"/api/reports/{reportId}/sign", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "两人复核底线：编制人不能签自己出的报告");
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("签发人必须与编制人不同");

        // 被拒后必须完全没发生：状态、签发人、审计三处都不能留痕
        (await StatusOfAsync(reportId)).Should().Be("generated");
        (await _fx.ScalarAsync<bool>("SELECT signed_by IS NULL AND signed_at IS NULL FROM reports WHERE id=@arg", reportId))
            .Should().BeTrue();
        (await SignAuditCountAsync(reportId)).Should().Be(0);
    }

    [PostgresFact]
    public async Task Sign_ByAnotherReviewer_LocksReportAndWritesAudit()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateFactory(_fx);
        var (reportId, _) = await GenerateReportAsync(factory);
        var signerId = await _fx.SeedUserAsync("reviewer");

        using var signer = TestApi.AuthedClient(factory, signerId, "reviewer");
        var resp = await signer.PostAsync($"/api/reports/{reportId}/sign", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("signed");

        (await StatusOfAsync(reportId)).Should().Be("signed");
        (await _fx.ScalarAsync<long>("SELECT signed_by FROM reports WHERE id=@arg", reportId)).Should().Be(signerId);
        (await _fx.ScalarAsync<bool>("SELECT signed_at IS NOT NULL FROM reports WHERE id=@arg", reportId))
            .Should().BeTrue();

        // 签发与审计同事务：不允许出现"已签发但审计缺条"的断链
        (await SignAuditCountAsync(reportId)).Should().Be(1, "签发动作必须在审计里留条，这是事后追责的唯一入口");
    }

    [PostgresFact]
    public async Task Sign_AlreadySignedReport_Conflicts()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateFactory(_fx);
        var (reportId, _) = await GenerateReportAsync(factory);
        var firstSignerId = await _fx.SeedUserAsync("reviewer");
        var secondSignerId = await _fx.SeedUserAsync("reviewer");

        using var first = TestApi.AuthedClient(factory, firstSignerId, "reviewer");
        (await first.PostAsync($"/api/reports/{reportId}/sign", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        // 签发即锁定：已正式的报告不能被后来者改签，否则归档件的责任人会被悄悄换掉
        using var second = TestApi.AuthedClient(factory, secondSignerId, "reviewer");
        var resp = await second.PostAsync($"/api/reports/{reportId}/sign", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("仅草稿可签发");
        (await _fx.ScalarAsync<long>("SELECT signed_by FROM reports WHERE id=@arg", reportId))
            .Should().Be(firstSignerId, "签发人必须还是第一位");
        (await SignAuditCountAsync(reportId)).Should().Be(1, "被拒的第二次签发不该多记一条审计");
    }

    [PostgresFact]
    public async Task Download_ServesGeneratedFile_ButRefusesItAfterOnDiskTamper()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateFactory(_fx);
        var (reportId, _, fileUrl) = await GenerateReportDetailedAsync(factory);
        using var reader = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var ok = await reader.GetAsync($"/api/reports/{reportId}/download");
        ok.StatusCode.Should().Be(HttpStatusCode.OK, "刚生成的报告任何登录角色都该能下");
        (await ok.Content.ReadAsStringAsync()).Should().Contain("扭矩", "下回来的应当是报告正文而不是空壳");

        // 绕过 API 直接改盘上的文件，模拟归档件被人手工动过
        var onDisk = ResolveReportPath(factory, fileUrl);
        await File.AppendAllTextAsync(onDisk, "<!-- 事后追加的一行 -->");

        var tampered = await reader.GetAsync($"/api/reports/{reportId}/download");
        tampered.StatusCode.Should().Be(HttpStatusCode.BadRequest, "sha256 对不上就不能再把它当正式件发出去");
        (await tampered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("完整性校验未通过");
    }

    [PostgresFact]
    public async Task Generate_WithoutAnyTorquePoints_Returns400()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("验收报告");
        var authorId = await _fx.SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        using var author = TestApi.AuthedClient(factory, authorId, "reviewer");

        // 一个点都没有还能出报告的话，交出去的就是一份空表
        var resp = await author.PostAsJsonAsync($"/api/reports/torque-step/{s.ChannelId}", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("没有扭矩实验点");
        (await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM reports WHERE subject_type='channel' AND subject_id=@arg", s.ChannelId))
            .Should().Be(0);
    }

    [PostgresFact]
    public async Task GenerateAndSign_RequireReviewer_WhileReadingDoesNot()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateFactory(_fx);
        var (reportId, _) = await GenerateReportAsync(factory);
        var operatorId = await _fx.SeedUserAsync("operator");

        using var op = TestApi.AuthedClient(factory, operatorId, "operator");

        // 出报告和签报告是 reviewer+；看报告是 LoggedIn（现场要对着报告核数据）。
        // 这条分界是刻意的，收紧或放宽都会有人立刻不满意，所以钉在测试里。
        (await op.PostAsJsonAsync("/api/reports/torque-step/1", new { })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "operator 不得出报告");
        (await op.PostAsync($"/api/reports/{reportId}/sign", null)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "operator 不得签发");
        (await op.GetAsync("/api/reports/")).StatusCode
            .Should().Be(HttpStatusCode.OK, "operator 要能查阅报告列表");
        (await op.GetAsync($"/api/reports/{reportId}")).StatusCode
            .Should().Be(HttpStatusCode.OK, "operator 要能查阅报告详情");
    }

    // ===================== 生成与读库 =====================

    private async Task<(long ReportId, long AuthorId)> GenerateReportAsync(WebApplicationFactory<Program> factory)
    {
        var (reportId, authorId, _) = await GenerateReportDetailedAsync(factory);
        return (reportId, authorId);
    }

    /// <summary>铺一路带扭矩阶梯数据的通道，出一份草稿报告。</summary>
    private async Task<(long ReportId, long AuthorId, string FileUrl)> GenerateReportDetailedAsync(
        WebApplicationFactory<Program> factory)
    {
        var s = await _fx.SeedCalibratableChannelAsync("验收报告");
        await _fx.SeedTorquePointsAsync(s, d: 0.5m);
        var authorId = await _fx.SeedUserAsync("reviewer");

        using var author = TestApi.AuthedClient(factory, authorId, "reviewer");
        var resp = await author.PostAsJsonAsync($"/api/reports/torque-step/{s.ChannelId}",
            new { title = "扭矩阶梯验证报告（集成测试）" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "前置生成必须成功，否则后面的断言无从谈起");

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("reportId").GetInt64(), authorId, body.GetProperty("fileUrl").GetString()!);
    }

    /// <summary>把 reports.file_url 还原成盘上的绝对路径（服务端按 ContentRoot/data 拼）。</summary>
    private static string ResolveReportPath(WebApplicationFactory<Program> factory, string fileUrl)
    {
        var contentRoot = factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
        return Path.Combine(contentRoot, "data", fileUrl.Replace('/', Path.DirectorySeparatorChar));
    }

    private Task<string> StatusOfAsync(long reportId) =>
        _fx.ScalarAsync<string>("SELECT status FROM reports WHERE id=@arg", reportId);

    private async Task<long> SignAuditCountAsync(long reportId)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "SELECT count(*) FROM audit_log WHERE action='report_sign' AND target_type='report' AND target_id=@id");
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, reportId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
