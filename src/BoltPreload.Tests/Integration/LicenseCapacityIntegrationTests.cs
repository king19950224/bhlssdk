using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoltPreload.Api.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 授权容量超限时 <c>LicenseGateMiddleware</c> 的实际行为。
/// <para>
/// 核心承诺是“超容量不停采集、只拦写操作”：安全监测绝不能因为授权问题静默失效，
/// 但配置/标定/下发得停下来逼业主补授权。此前 harness 的 <c>AlwaysValidLicenseService</c>
/// 把 MaxDevices/MaxChannels 全设成 null，整条执法路径在测试里一次都没走过——
/// 无论是"该拦的没拦"还是"把只读也一起拦了"，都不会有测试变红。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class LicenseCapacityIntegrationTests
{
    private readonly PostgresFixture _fx;
    public LicenseCapacityIntegrationTests(PostgresFixture fx) => _fx = fx;

    private const long AnyBoltId = 999999999L;

    [PostgresFact]
    public async Task OverCapacity_BlocksWrites_WithCapacityCode()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateFactory(_fx);
        OverCapacity(factory);
        using var client = TestApi.AuthedClient(factory, 1, "operator");

        var resp = await client.PostAsJsonAsync($"/api/bolts/{AnyBoltId}/settings", new { targetPreloadN = 1m });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // 断 code 而不只断 403：这个螺栓本来就不存在，只看状态码分不清是闸门拦的还是端点 404
        body.GetProperty("code").GetString().Should().Be("license_capacity_exceeded");
        body.GetProperty("error").GetString().Should().Contain("通道数 100 超过授权上限 40");
        body.GetProperty("currentChannels").GetInt32().Should().Be(100);
    }

    [PostgresFact]
    public async Task WithinCapacity_SameWriteReachesEndpoint()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateFactory(_fx);
        Enforcement(factory).Set(currentDevices: 1, currentChannels: 40, maxDevices: 2, maxChannels: 40);
        using var client = TestApi.AuthedClient(factory, 1, "operator");

        var resp = await client.PostAsJsonAsync($"/api/bolts/{AnyBoltId}/settings", new { targetPreloadN = 1m });

        // 用满额度（40/40）不该被拦；请求穿过闸门到达端点，才会得到端点自己的 404
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "恰好用满授权容量必须放行");
    }

    [PostgresFact]
    public async Task OverCapacity_StillServesReads()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateFactory(_fx);
        OverCapacity(factory);
        using var client = TestApi.AuthedClient(factory, 1, "operator");

        // "超容量不停监测"：读接口必须照常，否则业主会在授权到期那天丢掉整个安全监测画面
        (await client.GetAsync("/api/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/bolts/{AnyBoltId}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "读请求应穿过闸门，由端点回 404，而不是被闸门 403");
    }

    [PostgresFact]
    public async Task OverCapacity_StillAllowsAccountWrites()
    {
        _fx.EnsureAvailable();
        using var factory = TestApi.CreateFactory(_fx);
        OverCapacity(factory);
        using var client = TestApi.AuthedClient(factory, 1, "admin");

        // /api/auth 在白名单里：容量超限也不能把人锁在门外，否则连改密、换账号都做不了
        var resp = await client.PostAsJsonAsync("/api/auth/users", new { username = "", password = "", role = "operator" });

        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "白名单路径不受容量闸门约束");
    }

    /// <summary>
    /// 覆盖启动时评估出的容量状态。取的是 DI 里那个 singleton，与中间件用的是同一个实例；
    /// 访问 <c>factory.Services</c> 会先把应用启起来（启动评估随之跑完），所以这一步必然后写生效。
    /// </summary>
    private static void OverCapacity(WebApplicationFactory<Program> factory) =>
        Enforcement(factory).Set(currentDevices: 1, currentChannels: 100, maxDevices: 2, maxChannels: 40);

    private static LicenseEnforcementState Enforcement(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<LicenseEnforcementState>();
}
