using System.Net;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Auth;

/// <summary>
/// 当前请求客户端 IP（AsyncLocal）。plan §9.5 要求 audit_log.ip_addr 必填，
/// 各处 INSERT 通过 <see cref="Bind"/> 写入，避免每个端点各自解析 X-Forwarded-For。
/// </summary>
public static class AuditClientIp
{
    private static readonly AsyncLocal<IPAddress?> CurrentIp = new();

    public static IPAddress? Current
    {
        get => CurrentIp.Value;
        set => CurrentIp.Value = value;
    }

    /// <summary>从 HttpContext 抓远端 IP（本机部署工控机直连为主；有反向代理时取 RemoteIpAddress，由 ForwardedHeaders 中间件还原）。</summary>
    public static void Capture(HttpContext http)
        => Current = http.Connection.RemoteIpAddress;

    /// <summary>把当前 IP 绑到名为 <paramref name="name"/> 的 inet 参数；无 IP 时写 NULL（系统任务如自动建档）。</summary>
    public static void Bind(NpgsqlParameterCollection parameters, string name = "ip")
    {
        if (parameters.IndexOf(name) >= 0) return;
        parameters.AddWithValue(name, NpgsqlDbType.Inet, (object?)Current ?? DBNull.Value);
    }
}
