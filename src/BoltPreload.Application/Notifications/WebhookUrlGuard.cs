using System.Net;
using System.Net.Sockets;

namespace BoltPreload.Application.Notifications;

/// <summary>
/// Webhook URL SSRF 防护：禁止指向本机/私网/链路本地等。
/// 配置入库时与发送前各拦一道——库里若已有历史脏数据，发送时仍要挡。
/// </summary>
public static class WebhookUrlGuard
{
    public static string? Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "Url 必填";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "Url 必须是 http(s) 绝对地址";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "Url 不得包含用户名/密码";
        if (uri.IsLoopback || IsLiteralPrivateHost(uri.Host))
            return "Url 不得指向本机或私网地址（防 SSRF）";

        try
        {
            var addrs = Dns.GetHostAddresses(uri.DnsSafeHost);
            if (addrs.Length == 0)
                return "Url 主机名无法解析";
            if (addrs.Any(IsPrivateOrLocal))
                return "Url 解析到本机或私网地址（防 SSRF）";
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
            return IsPrivateOrLocal(ip);
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivateOrLocal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
        // ULA fc00::/7
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b6 = ip.GetAddressBytes();
            if ((b6[0] & 0xfe) == 0xfc) return true;
        }
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || b[0] == 127;
    }
}
