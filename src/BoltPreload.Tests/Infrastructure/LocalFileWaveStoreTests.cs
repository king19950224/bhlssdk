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
