using System.Security.Claims;

namespace BoltPreload.Api.Auth;

/// <summary>
/// 从 HttpContext 抽出当前用户身份。任何 endpoint 都用这个，
/// 严禁再接收 client 传来的 user_id 参数（历史 user_id = body?.UserId ?? 1L 是审计灾难）。
/// </summary>
public static class UserContext
{
    /// <summary>当前请求的登录用户身份（来自 JWT claims）。</summary>
    public sealed record CurrentUser(long UserId, string Username, string Role);

    public static CurrentUser? TryGet(HttpContext ctx)
    {
        if (ctx?.User?.Identity?.IsAuthenticated != true) return null;
        var idStr = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var name  = ctx.User.FindFirstValue(ClaimTypes.Name);
        var role  = ctx.User.FindFirstValue(ClaimTypes.Role) ?? AuthRoles.Operator;
        if (!long.TryParse(idStr, out var id)) return null;
        return new CurrentUser(id, name ?? "", role);
    }

    /// <summary>
    /// 强制取当前用户，未登录抛 UnauthorizedAccessException。
    /// endpoint 应在 .RequireAuthorization() 保护下调用，正常情况下不会触发异常。
    /// </summary>
    public static CurrentUser Require(HttpContext ctx) =>
        TryGet(ctx) ?? throw new UnauthorizedAccessException("当前请求未携带有效身份");
}
