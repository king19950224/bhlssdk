using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 波形文件下载（<c>GET /api/waves/file?path=...</c>）。
/// <para>
/// 这是全系统唯一一处"把用户传进来的字符串当路径去读盘"的端点。读到框外面去，
/// 泄的就是 appsettings.json 里的库连接串和 JWT 签名密钥——攻击者拿到后不必再打接口。
/// </para>
/// <para>
/// <c>LocalFileWaveStore.TryResolveSafePath</c> 有单元测试，但端点有没有真的接上它此前没测过：
/// 换成 <c>File.ReadAllBytes(Path.Combine(root, path))</c> 一样跑得通，一样返回波形，
/// 只是顺带把整块盘也交出去了。这里要钉的是接线，不是那个函数本身。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class WaveFileDownloadIntegrationTests : IDisposable
{
    private const string SecretMarker = "connection-string-and-signing-key";

    private readonly PostgresFixture _fx;
    private readonly string _base;
    private readonly string _root;
    private readonly string _legitRelPath;

    public WaveFileDownloadIntegrationTests(PostgresFixture fx)
    {
        _fx = fx;
        _base = Path.Combine(Path.GetTempPath(), "wavedl_" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "waves");

        // 框内：一条正常波形
        _legitRelPath = "1/9/20260521/1747828800000_8_abcdef12.json";
        var legitAbs = Path.Combine(_root, _legitRelPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(legitAbs)!);
        File.WriteAllText(legitAbs, """{"pointCount":8,"waveData":[1,2,3,4,5,6,7,8]}""");

        // 框外：攻击者想要的东西
        File.WriteAllText(Path.Combine(_base, "secrets.json"), $$"""{"note":"{{SecretMarker}}"}""");

        // 框外但目录名以根目录名开头。根路径前缀比对时若没带上分隔符，
        // "…/waves_backup/x" 会被判成在 "…/waves" 里面，整个备份目录随之敞开。
        Directory.CreateDirectory(Path.Combine(_base, "waves_backup"));
        File.WriteAllText(Path.Combine(_base, "waves_backup", "leak.json"), $$"""{"note":"{{SecretMarker}}"}""");
    }

    [PostgresFact]
    public async Task Download_ServesWaveFileInsideRoot()
    {
        _fx.EnsureAvailable();
        using var factory = CreateFactory();
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var resp = await client.GetAsync($"/api/waves/file?path={Uri.EscapeDataString(_legitRelPath)}");

        // 这条先立住，下面那组 404 才有意义：端点整个坏掉时也会全返 404，
        // 只断言"拒绝"的安全测试会在那种情况下一片绿。
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("\"pointCount\":8");
    }

    [PostgresTheory]
    [InlineData("../secrets.json", "上一级")]
    [InlineData("1/9/../../../secrets.json", "先进目录再退出来")]
    [InlineData("%2e%2e%2fsecrets.json", "URL 编码的 ../")]
    [InlineData("../waves_backup/leak.json", "同名前缀的兄弟目录")]
    public async Task Download_RefusesPathsThatLeaveTheWaveRoot(string attackPath, string shape)
    {
        _fx.EnsureAvailable();
        using var factory = CreateFactory();
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        // attackPath 直接进 query：EscapeDataString 会把 %2e%2e%2f 再编一层，就测不到解码后的形状了
        var resp = await client.GetAsync($"/api/waves/file?path={attackPath}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, $"不该能用“{shape}”读到根目录外面");
        (await resp.Content.ReadAsStringAsync()).Should().NotContain(SecretMarker,
            $"“{shape}”把框外的文件内容送出去了");
    }

    [PostgresFact]
    public async Task Download_RefusesAbsolutePaths()
    {
        _fx.EnsureAvailable();
        using var factory = CreateFactory();
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var absolute = Path.Combine(_base, "secrets.json");
        var resp = await client.GetAsync($"/api/waves/file?path={Uri.EscapeDataString(absolute)}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await resp.Content.ReadAsStringAsync()).Should().NotContain(SecretMarker);
    }

    [PostgresFact]
    public async Task Download_RequiresLogin()
    {
        _fx.EnsureAvailable();
        using var factory = CreateFactory();
        using var anonymous = factory.CreateClient();

        var resp = await anonymous.GetAsync($"/api/waves/file?path={Uri.EscapeDataString(_legitRelPath)}");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CreateFactory() =>
        TestApi.CreateFactory(_fx, b => b.UseSetting("WaveStore:RootPath", _root));

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
        GC.SuppressFinalize(this);
    }
}
