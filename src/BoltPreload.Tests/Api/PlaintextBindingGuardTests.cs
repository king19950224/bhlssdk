using BoltPreload.Api.Auth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BoltPreload.Tests.Api;

/// <summary>
/// 明文监听防线（<see cref="PlaintextBindingGuard"/>）。
/// <para>
/// 判松了这道防线就白设：现场把地址改成 0.0.0.0 照样起得来，JWT 明文过局域网。
/// 判严了更糟——装完起不来，人第一反应是把开关关掉，之后所有部署都带着放行标记。
/// 所以通配写法（*、+、0.0.0.0、省略主机）必须全部认成对外，而 loopback 的各种写法一个都不能误判。
/// </para>
/// </summary>
public class PlaintextBindingGuardTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5000", false, "install-service.ps1 的默认形态，只给本机")]
    [InlineData("http://localhost:5000", false, "框架默认，只给本机")]
    [InlineData("http://[::1]:5000", false, "IPv6 回环")]
    [InlineData("http://127.0.0.5:5000", false, "整个 127/8 都是回环")]
    [InlineData("https://0.0.0.0:8443", false, "对外但走 HTTPS，正是 setup-https.ps1 配出来的样子")]
    [InlineData("https://boltpreload.local", false, "HTTPS 一律放行")]
    [InlineData("http://0.0.0.0:5000", true, "明文绑全网卡")]
    [InlineData("http://*:5000", true, "通配写法，Uri 解析不了必须手工认出来")]
    [InlineData("http://+:5000", true, "另一种通配写法")]
    [InlineData("http://192.168.1.10:5000", true, "明文绑具体内网地址")]
    [InlineData("http://plant-hmi:5000", true, "明文绑主机名")]
    [InlineData("http://:5000", true, "省略主机等价于通配")]
    public void IsPlaintextNonLoopback_ClassifiesEachBindingForm(string url, bool exposed, string why)
        => PlaintextBindingGuard.IsPlaintextNonLoopback(url).Should().Be(exposed, why);

    [Fact]
    public void KestrelEndpoints_WinOverUrls()
    {
        // setup-https.ps1 写的是 Kestrel 节，它会覆盖 ASPNETCORE_URLS（启动日志有 Overriding address(es)）。
        // 若这里还按 Urls 判，配好 HTTPS 的机器反而会被拦下来。
        var config = Build(new Dictionary<string, string?>
        {
            ["Urls"] = "http://0.0.0.0:5000",
            ["Kestrel:Endpoints:Https:Url"] = "https://0.0.0.0:8443",
            ["Kestrel:Endpoints:LocalHttp:Url"] = "http://127.0.0.1:5000",
        });

        PlaintextBindingGuard.ResolveBindings(config).Should().BeEquivalentTo(
            ["https://0.0.0.0:8443", "http://127.0.0.1:5000"]);
        PlaintextBindingGuard.FindPlaintextNonLoopback(PlaintextBindingGuard.ResolveBindings(config))
            .Should().BeEmpty("对外只开了 HTTPS，明文口只绑本机");
    }

    [Fact]
    public void NoBindingConfigured_IsSafe()
    {
        // 两个都没配就是框架默认的 localhost，本来就出不了本机
        var config = Build([]);

        PlaintextBindingGuard.ResolveBindings(config).Should().BeEmpty();
        var act = () => PlaintextBindingGuard.EnsureSafe(config, isProduction: true);
        act.Should().NotThrow();
    }

    [Fact]
    public void Production_WithPlaintextExposure_RefusesToStart()
    {
        var config = Build(new Dictionary<string, string?> { ["Urls"] = "http://0.0.0.0:5000" });

        var act = () => PlaintextBindingGuard.EnsureSafe(config, isProduction: true);

        // 错误信息要能让现场直接照做，不能只说不行
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*http://0.0.0.0:5000*")
            .WithMessage("*setup-https.ps1*")
            .WithMessage($"*{PlaintextBindingGuard.AllowKey}*");
    }

    [Fact]
    public void Development_IsNotBlocked()
    {
        // 本机调试常绑 0.0.0.0 给手机或同事看，拦这个纯属添乱
        var config = Build(new Dictionary<string, string?> { ["Urls"] = "http://0.0.0.0:5000" });

        var act = () => PlaintextBindingGuard.EnsureSafe(config, isProduction: false);
        act.Should().NotThrow();
    }

    [Fact]
    public void ExplicitOptOut_LetsIsolatedNetworksThrough()
    {
        // 监测网物理隔离、现场书面确认不上 HTTPS 的场合：放行，但这个决定留在配置里能被审计看到
        var config = Build(new Dictionary<string, string?>
        {
            ["Urls"] = "http://0.0.0.0:5000",
            [PlaintextBindingGuard.AllowKey] = "true",
        });

        var act = () => PlaintextBindingGuard.EnsureSafe(config, isProduction: true);
        act.Should().NotThrow();
    }

    [Fact]
    public void MixedBindings_ReportEveryExposedOne()
    {
        var config = Build(new Dictionary<string, string?>
        {
            ["Urls"] = "http://127.0.0.1:5000;http://0.0.0.0:5001;https://0.0.0.0:8443;http://*:9000",
        });

        // 只报一个的话，现场改完一个重启又被拦，来回折腾
        PlaintextBindingGuard.FindPlaintextNonLoopback(PlaintextBindingGuard.ResolveBindings(config))
            .Should().BeEquivalentTo(["http://0.0.0.0:5001", "http://*:9000"]);
    }

    private static IConfiguration Build(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
