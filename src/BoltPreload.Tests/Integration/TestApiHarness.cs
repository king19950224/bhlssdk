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
    public static WebApplicationFactory<Program> CreateFactory(PostgresFixture fx) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Postgres", fx.ConnectionString);
            builder.UseSetting("Fx:Mode", "Simulated");
            builder.ConfigureTestServices(services =>
            {
                // 测试里不跑后台服务（采集/看门狗/信号健康/WAL 重放/仿真标定供给等）
                services.RemoveAll<IHostedService>();
                // license 恒有效，放行写操作（LicenseGate 只拦无效 license 的写）
                services.RemoveAll<ILicenseService>();
                services.AddSingleton<ILicenseService, AlwaysValidLicenseService>();
                // 用测试认证方案覆盖默认（JWT）：按 X-Test-UserId / X-Test-Role 请求头认证
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
