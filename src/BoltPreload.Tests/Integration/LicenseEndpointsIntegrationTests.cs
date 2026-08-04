using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 授权端点（<c>/api/license</c>）的匿名边界。
/// <para>
/// 这组端点必须在"还没登录、还没装授权"时可用，否则装机和排障进行不下去，所以整组匿名。
/// 代价是匿名与登录的分界全靠人记住：<c>status</c> 只回答"装好了没"，客户名、授权号、
/// 容量上限、启用模块这些合同信息要走 <c>detail</c>，而 detail 应当需要登录。
/// </para>
/// <para>
/// 这条分界此前没有任何测试守着。它一旦破了不会有任何症状——端点照常返回 200，
/// 页面照常显示，只是合同信息对着网络上任何人敞开。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class LicenseEndpointsIntegrationTests
{
    private readonly PostgresFixture _fx;
    public LicenseEndpointsIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task MachineCodeAndStatus_StayAnonymous()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateFactory(_fx);
        using var anonymous = factory.CreateClient();

        // 装机时要靠这两个自检，收紧了现场就装不上
        (await anonymous.GetAsync("/api/license/machine-code")).StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await anonymous.GetAsync("/api/license/status");
        status.StatusCode.Should().Be(HttpStatusCode.OK);

        // 匿名精简版的字段边界：只有有效性与机器码，不能捎带合同信息出去
        var body = await status.Content.ReadAsStringAsync();
        body.Should().Contain("valid");
        foreach (var leaked in new[] { "licensedTo", "licenseId", "maxDevices", "maxChannels", "modules", "expiresAt" })
            body.Should().NotContain(leaked, $"匿名 status 不该带 {leaked}，那是 detail 的字段");
    }

    [PostgresFact]
    public async Task Detail_RequiresLogin()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateFactory(_fx);
        using var anonymous = factory.CreateClient();

        var resp = await anonymous.GetAsync("/api/license/detail");

        // detail 带客户名、授权号、到期日、容量上限、启用模块——都是合同信息。
        // 端点上写了 RequireAuthorization(LoggedIn)，但整组的 AllowAnonymous 会让授权中间件
        // 直接短路，谁先谁后都不影响：只要端点元数据里有 IAllowAnonymous，鉴权就不做了。
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "detail 是登录版，匿名不该拿到客户名与容量");
        (await resp.Content.ReadAsStringAsync()).Should().NotContain("licensedTo");
    }

    [PostgresFact]
    public async Task Detail_ServesFullFieldsToLoggedInUser()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var resp = await client.GetAsync("/api/license/detail");

        // 上面那条断言的是 401，这条保证收紧之后登录用户还拿得到——
        // 否则把 detail 彻底堵死也能让上面那条变绿。
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("licensedTo").GetString().Should().NotBeNullOrEmpty();
        body.TryGetProperty("maxChannels", out _).Should().BeTrue();
    }
}
