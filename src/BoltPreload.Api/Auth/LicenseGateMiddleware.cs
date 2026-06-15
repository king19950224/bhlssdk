using BoltPreload.Application.Licensing;

namespace BoltPreload.Api.Auth;

/// <summary>
/// License 网关中间件：所有写接口必须通过 License 校验，读接口和健康检查放行。
/// 没拿 license 时整套系统能跑（方便业主试看），但 POST/PUT/DELETE 一律返回 403。
/// </summary>
public sealed class LicenseGateMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILicenseService _license;
    private readonly ILogger<LicenseGateMiddleware> _logger;

    // 路径白名单：永远不查 license。健康检查、登录、license 自描述。
    private static readonly string[] WhitelistPaths =
    {
        "/api/health",
        "/api/auth",
        "/api/license",
        "/swagger",
        "/hubs"  // SignalR 握手不在这里挡，Hub 内部有 [Authorize] + 校验
    };

    public LicenseGateMiddleware(RequestDelegate next, ILicenseService license, ILogger<LicenseGateMiddleware> logger)
    {
        _next = next;
        _license = license;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (IsWhitelisted(ctx.Request.Path) || IsReadOnly(ctx.Request.Method))
        {
            await _next(ctx);
            return;
        }

        var status = _license.GetStatus();
        if (!status.Valid)
        {
            _logger.LogWarning("License 无效，拒绝写请求 {Method} {Path}：{Warning}",
                ctx.Request.Method, ctx.Request.Path, status.Warning);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "license_invalid",
                detail = status.Warning ?? "授权未通过校验",
                machineCode = status.MachineCode
            });
            return;
        }

        await _next(ctx);
    }

    private static bool IsWhitelisted(PathString path)
    {
        foreach (var prefix in WhitelistPaths)
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool IsReadOnly(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
}
