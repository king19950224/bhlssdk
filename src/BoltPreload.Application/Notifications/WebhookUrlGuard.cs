using System.Net;
using System.Net.Sockets;

namespace BoltPreload.Application.Notifications;

/// <summary>
/// Webhook URL SSRF 防护：配置入库时与发送前各拦一道——库里若已有历史脏数据，发送时仍要挡。
/// <para>
/// 两档拦截：本机/链路本地（含云元数据 169.254.169.254）<b>永远</b>封杀——推给自己等于拿服务进程
/// 当跳板打自己的 API；RFC1918 私网段默认也拦，但风场现场的通知接收器（SCADA 网内的消息桥）
/// 本来就常在内网，业主确有此需时用 Notifications:AllowPrivateWebhookTargets 显式放行，责任留痕在配置。
/// </para>
/// </summary>
public static class WebhookUrlGuard
{
    public static string? Validate(string? url, bool allowPrivateTargets = false)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "Url 必填";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "Url 必须是 http(s) 绝对地址";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "Url 不得包含用户名/密码";

        if (uri.IsLoopback)
            return "Url 不得指向本机地址（防 SSRF）";
        if (!allowPrivateTargets && IsLiteralPrivateHost(uri.Host))
            return "Url 不得指向私网地址（防 SSRF；内网接收器需在配置开启 AllowPrivateWebhookTargets）";

        try
        {
            var addrs = Dns.GetHostAddresses(uri.DnsSafeHost);
            if (addrs.Length == 0)
                return "Url 主机名无法解析";
            if (addrs.Any(IsAlwaysBlocked))
                return "Url 解析到本机或链路本地地址（防 SSRF）";
            if (!allowPrivateTargets && addrs.Any(IsPrivateRange))
                return "Url 解析到私网地址（防 SSRF；内网接收器需在配置开启 AllowPrivateWebhookTargets）";
        }
        catch (SocketException)
        {
            return "Url 主机名无法解析";
        }

        return null;
    }

    private static bool IsLiteralPrivateHost(string host)
    {
        if (IPAddress.TryParse(host, out var ip))
            return IsPrivateRange(ip);
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>无论什么配置都不放行：本机回环、链路本地（169.254.0.0/16 含云元数据、IPv6 fe80::）。</summary>
    private static bool IsAlwaysBlocked(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6LinkLocal) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return (b[0] == 169 && b[1] == 254) || b[0] == 127;
        }
        return false;
    }

    /// <summary>RFC1918 / IPv6 ULA / site-local：默认拦，AllowPrivateWebhookTargets=true 时放行。</summary>
    private static bool IsPrivateRange(IPAddress ip)
    {
        if (IsAlwaysBlocked(ip)) return true;
#pragma warning disable CS0618 // IsIPv6SiteLocal 已废弃但存量地址仍可能出现，保守拦截
        if (ip.IsIPv6SiteLocal) return true;
#pragma warning restore CS0618
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b6 = ip.GetAddressBytes();
            if ((b6[0] & 0xfe) == 0xfc) return true; // ULA fc00::/7
        }
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168);
    }
}
