namespace BoltPreload.Api.Auth;

/// <summary>
/// 最小角色模型，与 users.role CHECK 约束完全一致。
/// </summary>
public static class AuthRoles
{
    public const string Operator = "operator";
    public const string Reviewer = "reviewer";
    public const string Admin    = "admin";

    /// <summary>所有已知角色（按权限由低到高）。</summary>
    public static readonly string[] All = { Operator, Reviewer, Admin };
}

/// <summary>
/// Policy 名称常量，方便 endpoint .RequireAuthorization(AuthPolicies.WriteAccess) 写法统一。
/// </summary>
public static class AuthPolicies
{
    /// <summary>读接口：登录即可。</summary>
    public const string LoggedIn = "logged_in";

    /// <summary>普通写接口（采集运行、ack 告警等）：operator+。</summary>
    public const string WriteAccess = "write_access";

    /// <summary>需复核的写接口（标定提交 / 报告签发）：reviewer+。</summary>
    public const string ReviewerAccess = "reviewer_access";

    /// <summary>管理接口（用户、License、设备拓扑）：admin。</summary>
    public const string AdminAccess = "admin_access";
}
