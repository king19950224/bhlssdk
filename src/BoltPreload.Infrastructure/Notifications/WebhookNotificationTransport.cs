using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoltPreload.Application.Notifications;
using Microsoft.Extensions.Configuration;

namespace BoltPreload.Infrastructure.Notifications;

/// <summary>
/// webhook 渠道适配器。config_jsonb：{url, format, secret}，format 决定报文形状：
/// <list type="bullet">
/// <item>raw——通用 JSON（{title, lines, sentAt}），给客户自有系统对接；secret 放请求头 X-BoltPreload-Token；</item>
/// <item>wecom——企业微信群机器人 markdown（key 在 url 里，无签名）；</item>
/// <item>dingtalk——钉钉群机器人 markdown；secret 走加签（timestamp + HMAC-SHA256）。</item>
/// </list>
/// 企微/钉钉的坑：HTTP 200 里带 errcode≠0 也是失败（key 错、被限流），必须解析响应体，
/// 不然发丢了发送记录还显示成功。
/// </summary>
public sealed class WebhookNotificationTransport : INotificationTransport
{
    /// <summary>Program 里注册的命名 HttpClient（10 秒超时）。</summary>
    public const string HttpClientName = "notification-webhook";

    private readonly IHttpClientFactory _httpFactory;
    private readonly bool _allowPrivateTargets;

    public WebhookNotificationTransport(IHttpClientFactory httpFactory, IConfiguration? configuration = null)
    {
        _httpFactory = httpFactory;
        // 与 NotificationEndpoints 同一把开关：入库校验放行的内网地址，发送侧不能又拦回去
        _allowPrivateTargets = configuration?.GetValue("Notifications:AllowPrivateWebhookTargets", false) ?? false;
    }

    public string Kind => "webhook";

    public async Task SendAsync(NotificationChannelInfo channel, string title, IReadOnlyList<string> lines, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(channel.ConfigJson);
        var cfg = doc.RootElement;
        var url = cfg.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException($"渠道 '{channel.Name}' 的 config 缺 url");
        // 发送前再验一轮：历史脏配置 / 手工改库 不能绕过 SSRF 门禁
        var ssrf = WebhookUrlGuard.Validate(url, _allowPrivateTargets);
        if (ssrf is not null)
            throw new InvalidOperationException($"渠道 '{channel.Name}' URL 被拒绝：{ssrf}");
        var format = cfg.TryGetProperty("format", out var f) ? f.GetString() ?? "raw" : "raw";
        var secret = cfg.TryGetProperty("secret", out var s) ? s.GetString() : null;

        var client = _httpFactory.CreateClient(HttpClientName);
        using var request = format switch
        {
            "wecom" => BuildWecom(url, title, lines),
            "dingtalk" => BuildDingtalk(url, secret, title, lines),
            _ => BuildRaw(url, secret, title, lines),
        };

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}：{Truncate(body)}");

        // 企微/钉钉都是 200 + errcode 报错的路数
        if (format is "wecom" or "dingtalk" && TryGetErrCode(body, out var errCode, out var errMsg) && errCode != 0)
            throw new HttpRequestException($"{format} errcode={errCode}：{Truncate(errMsg)}");
    }

    private static HttpRequestMessage BuildRaw(string url, string? secret, string title, IReadOnlyList<string> lines)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { title, lines, sentAt = DateTimeOffset.Now }),
        };
        if (!string.IsNullOrEmpty(secret))
            req.Headers.Add("X-BoltPreload-Token", secret);
        return req;
    }

    private static HttpRequestMessage BuildWecom(string url, string title, IReadOnlyList<string> lines)
    {
        var content = new StringBuilder("**").Append(title).Append("**");
        foreach (var line in lines)
            content.Append("\n> ").Append(line);
        return new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { msgtype = "markdown", markdown = new { content = content.ToString() } }),
        };
    }

    private static HttpRequestMessage BuildDingtalk(string url, string? secret, string title, IReadOnlyList<string> lines)
    {
        if (!string.IsNullOrEmpty(secret))
        {
            // 钉钉加签：sign = base64(HMAC-SHA256(secret, "{timestamp}\n{secret}"))，拼进查询串
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var sign = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}\n{secret}")));
            url += (url.Contains('?') ? "&" : "?") + $"timestamp={timestamp}&sign={Uri.EscapeDataString(sign)}";
        }
        var text = new StringBuilder("### ").Append(title);
        foreach (var line in lines)
            text.Append("\n\n- ").Append(line);
        return new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { msgtype = "markdown", markdown = new { title, text = text.ToString() } }),
        };
    }

    private static bool TryGetErrCode(string body, out int errCode, out string errMsg)
    {
        errCode = 0;
        errMsg = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("errcode", out var code))
            {
                errCode = code.GetInt32();
                errMsg = doc.RootElement.TryGetProperty("errmsg", out var msg) ? msg.GetString() ?? "" : "";
                return true;
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应体：按 HTTP 状态码的判定为准
        }
        return false;
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
