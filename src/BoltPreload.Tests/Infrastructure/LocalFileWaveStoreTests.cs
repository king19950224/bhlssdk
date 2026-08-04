using BoltPreload.Fx.Models;
using BoltPreload.Infrastructure.Ingestion;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BoltPreload.Tests.Infrastructure;

public class LocalFileWaveStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly LocalFileWaveStore _store;

    public LocalFileWaveStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "wavetest_" + Guid.NewGuid().ToString("N"));
        var opt = Options.Create(new LocalFileWaveStoreOptions { RootPath = _tempRoot, MaxFileBytes = 1_000_000 });
        _store = new LocalFileWaveStore(opt, NullLogger<LocalFileWaveStore>.Instance);
    }

    [Fact]
    public async Task Save_then_Read_round_trips()
    {
        var wave = new FxEchoWaveDto(
            Fc: 5,
            EchoStart: 23000,
            ClockInt: 25,
            WaveData: new short[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            Threshold: -490);

        var ts = new DateTimeOffset(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);
        var path = await _store.SaveAsync(deviceId: 1, channelNo: 9, ts, wave);

        path.Should().StartWith("1/9/20260521/");
        path.Should().EndWith(".json");

        var content = await _store.ReadRawAsync(path);
        content.Should().NotBeNull();

        var json = System.Text.Encoding.UTF8.GetString(content!);
        json.Should().Contain("\"pointCount\":8");
        json.Should().Contain("\"clockInt\":25");
    }

    [Fact]
    public async Task Read_rejects_path_traversal()
    {
        var bad = await _store.ReadRawAsync("../../etc/passwd");
        bad.Should().BeNull();
    }

    [Fact]
    public async Task Read_follows_symlinks_pointing_out_of_root()
    {
        // 这条记录的是一个已知边界，不是在肯定它：TryResolveSafePath 用 Path.GetFullPath 做词法归一化，
        // 而 GetFullPath 不解析符号链接，所以根目录里的一条软链能把读取带到框外。
        // 没有去堵，是因为要放进这条软链本身就得先有根目录的写权限——到那一步攻击者已经能直接读盘了，
        // 堵它换不来实际安全，却要给每次读加一次 ResolveLinkTarget。
        // 留这条测试是为了：behavior 变了要有人知道，且注释别再声称"防了符号链接"。
        var outside = Path.Combine(Path.GetTempPath(), "wavetest_outside_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(outside, """{"leaked":true}""");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_tempRoot, "link.json"), outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // 建软链需要权限（Windows 默认没有），拿不到就不判
        }

        try
        {
            var content = await _store.ReadRawAsync("link.json");
            content.Should().NotBeNull("词法归一化看不见软链——这是当前的真实行为");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Theory]
    [InlineData("", "空路径")]
    [InlineData("   ", "全空白")]
    [InlineData("C:bad.json", "Windows 盘符相对路径")]
    [InlineData("../wavetest_sibling/x.json", "同名前缀的兄弟目录")]
    public async Task Read_rejects_paths_outside_the_root_contract(string bad, string shape)
    {
        (await _store.ReadRawAsync(bad)).Should().BeNull($"“{shape}”不该被当成根目录下的相对路径");
    }

    [Fact]
    public async Task Read_rejects_absolute_paths_even_when_they_point_inside_root()
    {
        // 约定是"根目录下的相对路径"，绝对路径一律不认。指向框内的绝对路径没有安全后果，
        // 靠前缀比对也拦不住（Path.Combine 遇到绝对路径会丢掉前一段），全靠 IsPathRooted 这道门。
        var wave = new FxEchoWaveDto(Fc: 5, EchoStart: 23000, ClockInt: 25, WaveData: [1, 2, 3, 4], Threshold: -490);
        var rel = await _store.SaveAsync(deviceId: 3, channelNo: 11, DateTimeOffset.UtcNow, wave);
        (await _store.ReadRawAsync(rel)).Should().NotBeNull("先确认这个文件确实读得到");

        var absolute = Path.Combine(_tempRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        (await _store.ReadRawAsync(absolute)).Should().BeNull("同一个文件换成绝对路径就不认了");
    }

    [Fact]
    public async Task Read_returns_null_for_missing_file()
    {
        var missing = await _store.ReadRawAsync("1/9/20260101/0_0.json");
        missing.Should().BeNull();
    }

    [Fact]
    public async Task Save_truncates_when_over_limit()
    {
        // 用小 MaxFileBytes 强制触发截断
        var opt = Options.Create(new LocalFileWaveStoreOptions { RootPath = _tempRoot, MaxFileBytes = 500 });
        var store = new LocalFileWaveStore(opt, NullLogger<LocalFileWaveStore>.Instance);

        var bigWave = new FxEchoWaveDto(
            Fc: 5, EchoStart: 23000, ClockInt: 25,
            WaveData: Enumerable.Range(0, 5000).Select(i => (short)i).ToArray(),
            Threshold: -490);

        var ts = DateTimeOffset.UtcNow;
        var path = await store.SaveAsync(2, 10, ts, bigWave);

        var content = await store.ReadRawAsync(path);
        content.Should().NotBeNull();
        var json = System.Text.Encoding.UTF8.GetString(content!);
        json.Should().Contain("\"truncated\":true");
        json.Should().Contain("\"pointCount\":256");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
        GC.SuppressFinalize(this);
    }
}
