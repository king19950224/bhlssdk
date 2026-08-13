using System.Net;
using System.Text.Json;
using BoltPreload.Api.Runtime;
using BoltPreload.Infrastructure.Notifications;
using FluentAssertions;
using Xunit;

namespace BoltPreload.Tests.Api;

/// <summary>
/// 系统告警从判定结果到一个真实 HTTP 请求的整条链。
/// <para>
/// 单独钉这一段，是因为它是新代码与旧代码的接缝：渠道配置由 <see cref="SystemAlertService"/> 从
/// appsettings 拼出来，解析和发送却复用业务通知那套 <see cref="WebhookNotificationTransport"/>。
/// 两边对 config 形状的理解只要差一个字段名，表现就是"配了但从来没发出去过"——
/// 而这条路径平时不发消息，没人会注意到它坏了，等到库真挂那天才发现，那时已经晚了。
/// </para>
/// <para>接收地址用 TEST-NET-3（203.0.113.0/24，文档专用段）：不是私网所以过得了 SSRF 门禁，
/// 又是 IP 字面量不查 DNS，测试不依赖网络。</para>
/// </summary>
public class SystemAlertDeliveryTests
{
    private const string HookUrl = "https://203.0.113.10/hook";

    private static SystemHealthVerdict DbDown() => new(["db_down"], [], 42.5);

    [Fact]
    public async Task RawFormat_ReachesTheEndpointWithTitleAndLines()
    {
        var capture = new CapturingHandler();
        var transport = new WebhookNotificationTransport(new StubHttpClientFactory(capture));
        var options = new SystemAlertOptions
        {
            WebhookUrl = HookUrl,
            Format = "raw",
            Secret = "s3cr3t",
            SiteName = "某某风场 3 号机",
        };

        var (title, lines) = SystemAlertService.BuildMessage(
            SystemAlertAction.Fault, DbDown(), SystemAlertService.ResolveSiteName(options), DateTimeOffset.Now);
        await transport.SendAsync(SystemAlertService.BuildChannel(options), title, lines);

        capture.Request.Should().NotBeNull("配置若解析不出 url，适配器会抛而不是发出去");
        capture.Request!.RequestUri!.ToString().Should().Be(HookUrl);
        capture.Request.Headers.GetValues("X-BoltPreload-Token").Should().ContainSingle().Which.Should().Be("s3cr3t");

        using var doc = JsonDocument.Parse(capture.Body!);
        doc.RootElement.GetProperty("title").GetString().Should().Contain("某某风场 3 号机");
        var sent = doc.RootElement.GetProperty("lines").EnumerateArray().Select(x => x.GetString()).ToArray();
        sent.Should().Contain(x => x!.Contains("站点：某某风场 3 号机"));
        // 正文要说人话，不能只丢一个 db_down 代码给值班
        sent.Should().Contain(x => x!.Contains("数据库不可达"));
    }

    [Fact]
    public async Task WecomFormat_BuildsMarkdownPayload()
    {
        var capture = new CapturingHandler();
        var transport = new WebhookNotificationTransport(new StubHttpClientFactory(capture));
        var options = new SystemAlertOptions { WebhookUrl = HookUrl, Format = "wecom", SiteName = "3 号机" };

        var (title, lines) = SystemAlertService.BuildMessage(
            SystemAlertAction.Fault, DbDown(), SystemAlertService.ResolveSiteName(options), DateTimeOffset.Now);
        await transport.SendAsync(SystemAlertService.BuildChannel(options), title, lines);

        using var doc = JsonDocument.Parse(capture.Body!);
        doc.RootElement.GetProperty("msgtype").GetString().Should().Be("markdown");
        doc.RootElement.GetProperty("markdown").GetProperty("content").GetString()
            .Should().Contain("3 号机").And.Contain("数据库不可达");
    }

    [Fact]
    public async Task LoopbackTarget_IsRefusedBySsrfGuard()
    {
        // 现场图省事填了本机地址时要当场报错，而不是每轮默默失败
        var capture = new CapturingHandler();
        var transport = new WebhookNotificationTransport(new StubHttpClientFactory(capture));
        var options = new SystemAlertOptions { WebhookUrl = "http://127.0.0.1:9000/hook" };

        var act = async () => await transport.SendAsync(SystemAlertService.BuildChannel(options), "t", ["l"]);

        await act.Should().ThrowAsync<InvalidOperationException>();
        capture.Request.Should().BeNull();
    }

    [Fact]
    public void RecoveredMessage_SaysItIsBackAndCarriesDiskNumber()
    {
        var options = new SystemAlertOptions { SiteName = "3 号机" };

        var (title, lines) = SystemAlertService.BuildMessage(
            SystemAlertAction.Recovered, new SystemHealthVerdict([], [], 42.5),
            SystemAlertService.ResolveSiteName(options), DateTimeOffset.Now);

        title.Should().Contain("恢复");
        lines.Should().Contain(x => x.Contains("恢复正常"));
        lines.Should().Contain(x => x.Contains("42.5"));
    }

    [Fact]
    public void DeadServiceMessage_NamesTheServices()
    {
        var v = new SystemHealthVerdict([], ["signal-health", "ingestion"], 100.0);

        var (_, lines) = SystemAlertService.BuildMessage(
            SystemAlertAction.Fault, v, "3 号机", DateTimeOffset.Now);

        // 光说"有服务挂了"没用，值班得知道挂的是哪个、意味着什么能力没了
        lines.Should().Contain(x => x.Contains("signal-health") && x.Contains("ingestion"));
    }

    [Fact]
    public void LowDiskMessage_CarriesTheActualNumber()
    {
        var v = new SystemHealthVerdict(["low_disk"], [], 1.2);

        var (_, lines) = SystemAlertService.BuildMessage(
            SystemAlertAction.Fault, v, "3 号机", DateTimeOffset.Now);

        lines.Should().Contain(x => x.Contains("1.2"));
    }

    [Fact]
    public void SiteName_FallsBackToMachineName()
        => SystemAlertService.ResolveSiteName(new SystemAlertOptions())
            .Should().Be(Environment.MachineName);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"errcode\":0}") };
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
