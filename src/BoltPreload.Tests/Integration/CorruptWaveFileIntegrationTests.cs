using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 波形文件损坏时的报法：必须和"没有这个文件"分开。
/// <para>
/// 这不是假想场景。波形落盘走"写 .tmp 再 rename"，rename 只保证读者看不到半截文件，
/// 而 rename 前没有 fsync（<c>LocalFileWaveStore</c> 里写明了这个取舍：波形是 30 天即删的
/// 辅助诊断数据，权威记录在 Postgres，不值得在采集路径上每条波形付一次刷盘）。
/// 于是断电后最终路径上完全可能是个截断的 JSON。
/// </para>
/// <para>
/// 这两种情况以前都报成"该通道暂无波形"。看到这句话的人会判断探头没采到数据、去现场查线，
/// 而真相是文件坏了——正在查信号质量的时候，这是最糟的一种指错方向。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class CorruptWaveFileIntegrationTests : IDisposable
{
    private readonly PostgresFixture _fx;
    private readonly string _waveRoot;

    public CorruptWaveFileIntegrationTests(PostgresFixture fx)
    {
        _fx = fx;
        _waveRoot = Path.Combine(Path.GetTempPath(), "bhls-wave-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_waveRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_waveRoot, recursive: true); } catch { /* 清理尽力 */ }
    }

    [PostgresFact]
    public async Task Compare_ReportsCorruptFile_DistinctlyFromAMissingOne()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("波形损坏");

        // 断电留下的典型残骸：JSON 写了一半就没了
        var relPath = WriteWaveFile(seed.DeviceId, channelNo: 5, "{\"echoStart\":23000,\"waveData\":[1,2,3");
        await InsertWaveFeatureAsync(seed.ChannelId, relPath);

        using var factory = CreateFactory();
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var body = await JsonOfAsync(client, $"/api/waves/compare/{seed.ChannelId}");

        body.GetProperty("hasCurrent").GetBoolean().Should().BeFalse("解析不出来就没有可比对的波形");
        body.TryGetProperty("waveError", out var err).Should().BeTrue("必须给出错误说明，不能只是 hasCurrent=false");
        err.GetString().Should().Contain("损坏")
            .And.Contain("不代表", "要明确否掉'该通道没采到波形'这个错误结论");
    }

    [PostgresFact]
    public async Task Compare_MissingFile_IsNotReportedAsCorrupt()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("波形缺失");

        // 指向一个压根不存在的文件：过了保留期被清理掉就是这样，属正常情况
        await InsertWaveFeatureAsync(seed.ChannelId, $"{seed.DeviceId}/5/20260101/nonexistent.json");

        using var factory = CreateFactory();
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var body = await JsonOfAsync(client, $"/api/waves/compare/{seed.ChannelId}");

        body.GetProperty("hasCurrent").GetBoolean().Should().BeFalse();
        // 文件不存在是正常态，不该报成损坏，否则又变成另一个方向的误导
        (body.TryGetProperty("waveError", out var err) && err.ValueKind != JsonValueKind.Null)
            .Should().BeFalse("文件不存在不是损坏，不该给出损坏提示");
    }

    // ---- helpers ----

    private WebApplicationFactory<Program> CreateFactory() =>
        TestApi.CreateFactory(_fx, b => b.UseSetting("WaveStore:RootPath", _waveRoot));

    /// <summary>按 LocalFileWaveStore 的路径规则写一个文件，返回它的相对路径。</summary>
    private string WriteWaveFile(long deviceId, ushort channelNo, string content)
    {
        var rel = $"{deviceId}/{channelNo}/20260101/1767225600000_3_deadbeef.json";
        var abs = Path.Combine(_waveRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content, Encoding.UTF8);
        return rel;
    }

    private async Task InsertWaveFeatureAsync(long channelId, string relPath)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO wave_features (channel_id, ts, wave_blob_url, point_count, echo_start_ns, clock_int_ns)
            VALUES (@cid, now(), @url, 3, 23000, 25)");
        cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
        cmd.Parameters.AddWithValue("url", NpgsqlDbType.Varchar, relPath);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<JsonElement> JsonOfAsync(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
