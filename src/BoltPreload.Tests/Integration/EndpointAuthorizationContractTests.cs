using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 路由表层面的授权契约：不看某一个端点做了什么，而是把全部端点拉出来，检查它们的授权口径本身立不立得住。
/// <para>
/// 起因是 <c>/api/license/detail</c>：端点上明明写了 <c>RequireAuthorization(LoggedIn)</c>，
/// 但整组挂了 <c>AllowAnonymous</c>，授权中间件直接短路，那句话是死代码，
/// 客户名/授权号/容量上限对匿名敞开。写的人以为收紧了，代码读起来也像收紧了。
/// </para>
/// <para>
/// 逐个端点补用例堵不住这类错——下次换个端点还会再犯一次。这里改成对着路由表判：
/// 一个端点同时带 AllowAnonymous 和授权要求，一定是笔误，因为后者恒不生效。
/// 新端点漏写授权口径也在这里拦。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class EndpointAuthorizationContractTests
{
    /// <summary>
    /// 有意匿名的端点，改动这张表等于改一次对外暴露面，要当作安全决定来评审。
    /// <list type="bullet">
    /// <item>健康检查：探活与就绪探针，负载均衡/运维在没有凭据的情况下调。</item>
    /// <item>登录：拿凭据的入口，本身不能要凭据。</item>
    /// <item>机器码与授权状态：装机时"还没登录、还没装授权"就得靠它们自检。</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> IntentionallyAnonymous = new(StringComparer.Ordinal)
    {
        "/api/health",
        "/api/health/ready",
        "/api/auth/login",
        "/api/license/machine-code",
        "/api/license/status",
    };

    private readonly PostgresFixture _fx;
    public EndpointAuthorizationContractTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public void NoEndpoint_CarriesAnAuthorizationRequirementThatCannotTakeEffect()
    {
        _fx.EnsureAvailable();

        var broken = ApiEndpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null
                     && e.Metadata.GetMetadata<IAuthorizeData>() is not null)
            .Select(Describe)
            .ToArray();

        broken.Should().BeEmpty(
            "端点元数据里只要有 AllowAnonymous，授权中间件就整个跳过，同一端点上的 " +
            "RequireAuthorization 不论写在前写在后都不生效。这两样同时出现说明有人以为自己收紧了权限，" +
            "而实际上没有——要么去掉 AllowAnonymous（通常挂在整个 MapGroup 上），要么去掉那句不生效的授权");
    }

    [PostgresFact]
    public void EveryApiEndpoint_TakesAnExplicitStanceOnAuthorization()
    {
        _fx.EnsureAvailable();

        var undeclared = ApiEndpoints()
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Where(e => !IntentionallyAnonymous.Contains(PatternOf(e)))
            .Select(Describe)
            .ToArray();

        undeclared.Should().BeEmpty(
            "新加的 /api 端点必须自己声明授权口径。默认没有 fallback policy，" +
            "漏写就是对全网敞开，而且不会有任何症状——功能照常，只是谁都能调");
    }

    // ===================== 路由表 =====================

    /// <summary>
    /// 取全部 /api 业务端点。排除 catch-all 兜底路由（它只负责把未匹配的 /api 请求变成 404 JSON，
    /// 没有业务语义，也不该要求登录）。
    /// </summary>
    private RouteEndpoint[] ApiEndpoints()
    {
        using var factory = TestApi.CreateFactory(_fx);
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        return endpoints
            .OfType<RouteEndpoint>()
            .Where(e => PatternOf(e).StartsWith("/api/", StringComparison.Ordinal))
            .Where(e => !PatternOf(e).Contains("{**", StringComparison.Ordinal))
            .ToArray();
    }

    private static string PatternOf(RouteEndpoint e) => "/" + e.RoutePattern.RawText!.TrimStart('/').TrimEnd('/');

    private static string Describe(RouteEndpoint e)
    {
        var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
        return $"{string.Join('/', methods)} {PatternOf(e)}";
    }
}
