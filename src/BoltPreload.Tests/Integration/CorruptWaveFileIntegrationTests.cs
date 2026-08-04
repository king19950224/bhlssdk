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
/// 波形文件损坏要和"没有这个文件"分开报。
/// 波形落盘是写 .tmp 再 rename，rename 前没有 fsync（<c>LocalFileWaveStore</c> 里写了为什么不补），
/// 所以断电后最终路径上可能是个截断的 JSON。这两种情况以前都显示成"该通道暂无波形"，
/// 看的人会以为探头没采到数据、跑去现场查线。
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
        body.TryGetProperty("waveError", out var err).Should().BeTrue("光给 hasCurrent=false 说明不了原因");
        err.GetString().Should().Contain("损坏")
            .And.Contain("不代表", "得把'该通道没采到波形'这个结论明确否掉");
    }

    [PostgresFact]
    public async Task Compare_MissingFile_IsNotReportedAsCorrupt()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("波形缺失");

        // 指向一个不存在的文件：过了保留期被清理掉就是这样，属正常情况
        await InsertWaveFeatureAsync(seed.ChannelId, $"{seed.DeviceId}/5/20260101/nonexistent.json");

        using var factory = CreateFactory();
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var body = await JsonOfAsync(client, $"/api/waves/compare/{seed.ChannelId}");

        body.GetProperty("hasCurrent").GetBoolean().Should().BeFalse();
        // 反过来也别错：文件不存在是正常的，报成损坏又成了另一头的误导
        (body.TryGetProperty("waveError", out var err) && err.ValueKind != JsonValueKind.Null)
            .Should().BeFalse("文件不存在不是损坏");
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
