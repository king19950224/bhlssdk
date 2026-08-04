using System.Security.Claims;
using System.Text.Encodings.Web;
using BoltPreload.Api.Auth;
using BoltPreload.Application.Licensing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// HTTP 端点集成测试共用 harness（EndpointIntegrationTests / BoltProvisioningIntegrationTests 共享）：
/// WebApplicationFactory 配测试替身——测试认证方案（按 X-Test-UserId/X-Test-Role 请求头认证，
/// 绕开真实 JWT + token_version 校验）、恒有效 license（放行写操作）、移除所有后台服务。
/// </summary>
internal static class TestApi
{
    /// <summary>测试里用的固定签名密钥，免得依赖 Development 环境的兜底 key。</summary>
    private const string TestSigningKey = "integration_test_signing_key_do_not_use_in_production_64_bytes!!";

    /// <param name="configure">
    /// 个别用例要改配置时用（如把波形根目录指到临时目录）。在默认设置之后执行，可以覆盖它们。
    /// </param>
    public static WebApplicationFactory<Program> CreateFactory(
        PostgresFixture fx, Action<IWebHostBuilder>? configure = null) =>
        BuildFactory(fx, configure, useTestAuth: true);

    /// <summary>
    /// 保留<b>真实 JWT 鉴权链</b>的工厂：签发、签名/有效期校验、以及 OnTokenValidated 里
    /// 按 users.token_version 与 enabled 的吊销复核，全部照生产路径走。
    /// <para>
    /// 为什么要单独有这一个：<see cref="CreateFactory"/> 把认证方案整个换成了
    /// <c>TestAuthHandler</c>，好处是铺数据便宜，代价是真实鉴权那一段一行都跑不到。
    /// 于是"停用/改密/登出后旧令牌立即失效"这条安全红线曾经零覆盖——库里 token_version
    /// 有没有自增测了，自增之后那个旧令牌到底还能不能用，没有任何测试问过。
    /// 只有验这条红线的用例才该用这个工厂，其余的用 <see cref="CreateFactory"/> 更省事。
    /// </para>
    /// </summary>
    public static WebApplicationFactory<Program> CreateRealAuthFactory(
        PostgresFixture fx, Action<IWebHostBuilder>? configure = null) =>
        BuildFactory(fx, configure, useTestAuth: false);

    private static WebApplicationFactory<Program> BuildFactory(
        PostgresFixture fx, Action<IWebHostBuilder>? configure, bool useTestAuth) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Postgres", fx.ConnectionString);
            builder.UseSetting("Fx:Mode", "Simulated");
            builder.UseSetting("Jwt:SigningKey", TestSigningKey);
            configure?.Invoke(builder);
            builder.ConfigureTestServices(services =>
            {
                // 测试里不跑后台服务（采集/看门狗/信号健康/WAL 重放/仿真标定供给等）
                services.RemoveAll<IHostedService>();
                // license 恒有效，放行写操作（LicenseGate 只拦无效 license 的写）
                services.RemoveAll<ILicenseService>();
                services.AddSingleton<ILicenseService, AlwaysValidLicenseService>();
                // 用测试认证方案覆盖默认（JWT）：按 X-Test-UserId / X-Test-Role 请求头认证
                if (useTestAuth)
                    services.AddAuthentication(TestAuthHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            });
        });

    public static HttpClient AuthedClient(WebApplicationFactory<Program> factory, long userId, string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", userId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }

    // 测试认证处理器：按请求头认证为指定用户/角色，满足 RequireRole 策略（不走真实 JWT / token_version 校验）。
    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestAuth";

        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-UserId", out var uid) || string.IsNullOrEmpty(uid))
                return Task.FromResult(AuthenticateResult.NoResult());

            var role = Request.Headers.TryGetValue("X-Test-Role", out var r) && !string.IsNullOrEmpty(r)
                ? r.ToString()
                : AuthRoles.Operator;

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, uid.ToString()),
                new Claim(ClaimTypes.Name, "test-" + role),
                new Claim(ClaimTypes.Role, role),
            };
            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    // 恒有效授权：MaxDevices/MaxChannels 为 null（无容量上限），Valid=true → LicenseGate 放行写操作。
    private sealed class AlwaysValidLicenseService : ILicenseService
    {
        public string MachineCode => "test-machine";
        public LicenseStatus GetStatus() => new(
            Found: true, Valid: true, Expired: false, MachineCode: "test-machine",
            LicensedTo: "integration-test", LicenseId: "test", ExpiresAt: DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
            MaxDevices: null, MaxChannels: null, Modules: Array.Empty<string>(), Warning: null, Document: null);
    }
}
