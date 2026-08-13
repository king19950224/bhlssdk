namespace BoltPreload.Api.Auth;

/// <summary>
/// 明文监听防线：不许在生产环境把 HTTP 端口开到本机之外。
/// <para>
/// 起因是装完的默认形态就是明文 HTTP（install-service.ps1 写 <c>http://127.0.0.1:5000</c>），
/// 而把它改成对外地址只需要动一个环境变量，<c>setup-https.ps1</c> 是另一个可选步骤。
/// 两者之间没有任何硬约束——现场为了让操作员能访问，顺手把地址改成 0.0.0.0 再开防火墙，
/// JWT 就明文过局域网了，而且不会有任何提示。
/// </para>
/// <para>
/// 所以这里按"生产环境默认拒绝"处理，和生产禁 Simulated 一个路数。监测网物理隔离、
/// 现场确认不上 HTTPS 的场合，配 <c>Security:AllowPlaintextOnNonLoopback=true</c> 显式声明一次，
/// 让它成为一个写在配置里、能被审计看到的决定，而不是默默就那样跑了。
/// </para>
/// </summary>
public static class PlaintextBindingGuard
{
    /// <summary>显式放行明文对外监听的配置键。</summary>
    public const string AllowKey = "Security:AllowPlaintextOnNonLoopback";

    /// <summary>
    /// 解析进程实际会监听的地址。<c>Kestrel:Endpoints</c> 存在时以它为准
    /// （它会覆盖 ASPNETCORE_URLS，setup-https.ps1 走的就是这条路），否则看 <c>Urls</c>。
    /// 两个都没有时返回空——用框架默认的 localhost，本来就是 loopback。
    /// </summary>
    public static IReadOnlyList<string> ResolveBindings(IConfiguration config)
    {
        var endpoints = config.GetSection("Kestrel:Endpoints").GetChildren().ToArray();
        if (endpoints.Length > 0)
        {
            var fromKestrel = endpoints
                .Select(e => e["Url"])
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u!)
                .ToArray();
            if (fromKestrel.Length > 0)
                return fromKestrel;
        }

        var urls = config["Urls"];
        return string.IsNullOrWhiteSpace(urls)
            ? []
            : urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>挑出其中"明文且不只对本机"的监听地址。</summary>
    public static IReadOnlyList<string> FindPlaintextNonLoopback(IEnumerable<string> bindings)
        => bindings.Where(IsPlaintextNonLoopback).ToArray();

    /// <summary>这条监听地址是不是明文 HTTP 且绑到了本机之外。</summary>
    public static bool IsPlaintextNonLoopback(string binding)
    {
        if (string.IsNullOrWhiteSpace(binding)) return false;
        var url = binding.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;

        // 手工切 host：通配形式 http://*:5000 与 http://+:5000 不是合法 Uri，Uri.TryCreate 解析不了。
        var rest = url["http://".Length..];
        var slash = rest.IndexOf('/');
        if (slash >= 0) rest = rest[..slash];

        string host;
        if (rest.StartsWith('['))
        {
            // IPv6 字面量：[::1]:5000
            var close = rest.IndexOf(']');
            host = close > 0 ? rest[1..close] : rest[1..];
        }
        else
        {
            var colon = rest.IndexOf(':');
            host = colon >= 0 ? rest[..colon] : rest;
        }

        return !IsLoopbackHost(host);
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Length == 0) return false;                                   // http://:5000 等价于通配，按对外算
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host is "::1") return true;
        return System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// 生产环境下发现明文对外监听且未显式放行时抛异常。开发环境不拦（本机调试常绑 0.0.0.0 给手机看）。
    /// </summary>
    public static void EnsureSafe(IConfiguration config, bool isProduction)
    {
        if (!isProduction) return;
        if (config.GetValue(AllowKey, false)) return;

        var exposed = FindPlaintextNonLoopback(ResolveBindings(config));
        if (exposed.Count == 0) return;

        throw new InvalidOperationException(
            $"生产环境检测到明文 HTTP 对外监听：{string.Join("、", exposed)}。" +
            "JWT 令牌会以明文经过局域网，任何一台接入交换机的设备都能截获后冒充操作员。" +
            "处理方式二选一：(1) 跑 deploy/windows/setup-https.ps1 配好 HTTPS（推荐，脚本会把自签证书装进本机受信根）；" +
            $"(2) 监测网物理隔离、现场书面确认不上 HTTPS 的，配 {AllowKey}=true 显式放行并写进验收记录。" +
            "只给本机用请把地址改回 http://127.0.0.1:5000。");
    }
}
