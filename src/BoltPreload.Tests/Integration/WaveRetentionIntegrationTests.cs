using BoltPreload.Api.Runtime;
using BoltPreload.Fx.Models;
using BoltPreload.Infrastructure.Ingestion;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 波形保留清理（<see cref="WaveRetentionService"/>）。
/// <para>
/// 全系统唯一一处按时间批量删数据的后台任务，删掉就没了。要守的是那个例外：被
/// <c>reference_waves</c> 引用的参考波形不管多老都得留下，它是 <c>/compare</c> 的基线，
/// 清掉之后所有历史比对都失去参照，而页面只会显示"无参考波形"，看不出是被清理任务删的。
/// </para>
/// <para>
/// 保护靠字符串对得上：库里存的是 <c>SaveAsync</c> 返回的相对路径，清理时拿
/// <c>Path.GetRelativePath</c> 现算一个去比。两边任何一侧改了路径拼法，保护就静默失效
/// ——照样跑完、照样报删了多少个文件。<c>PurgeOlderThanAsync</c> 此前零覆盖。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class WaveRetentionIntegrationTests : IDisposable
{
    private readonly PostgresFixture _fx;
    private readonly string _root;

    public WaveRetentionIntegrationTests(PostgresFixture fx)
    {
        _fx = fx;
        _root = Path.Combine(Path.GetTempPath(), "waveret_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录，删不掉就留着 */ }
    }

    [PostgresFact]
    public async Task Sweep_KeepsReferenceWaves_AndDropsTheRestBeyondTheWindow()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("波形保留");
        var store = NewStore(retentionDays: 30);

        // 参考波形比保留窗口老得多，正是最容易被"按天删"顺手带走的那一档
        var reference = await SaveWaveAsync(store, seed.DeviceId, daysAgo: 200);
        var stale = await SaveWaveAsync(store, seed.DeviceId, daysAgo: 200);
        var fresh = await SaveWaveAsync(store, seed.DeviceId, daysAgo: 3);

        var refFeatureId = await InsertFeatureAsync(seed.ChannelId, reference, daysAgo: 200);
        var staleFeatureId = await InsertFeatureAsync(seed.ChannelId, stale, daysAgo: 200);
        var freshFeatureId = await InsertFeatureAsync(seed.ChannelId, fresh, daysAgo: 3);
        await MarkAsReferenceAsync(seed.ChannelId, refFeatureId);

        await NewService(store, retentionDays: 30).RunOnceAsync(CancellationToken.None);

        File.Exists(Abs(reference)).Should().BeTrue("参考波形是 /compare 的基线，比窗口老也得留");
        (await FeatureExistsAsync(refFeatureId)).Should().BeTrue("文件留了但特征行删了，比对一样做不了");

        File.Exists(Abs(stale)).Should().BeFalse("没人引用的超窗波形该删，不删就是无限膨胀");
        (await FeatureExistsAsync(staleFeatureId)).Should().BeFalse();

        File.Exists(Abs(fresh)).Should().BeTrue("窗口内的不该动");
        (await FeatureExistsAsync(freshFeatureId)).Should().BeTrue();
    }

    [PostgresFact]
    public async Task Sweep_KeepsTheDayFolderThatStillHoldsAProtectedWave()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("保护目录");
        var store = NewStore(retentionDays: 30);

        // 同一天目录里一个受保护、一个不受保护：清完目录非空，不能按"过期日目录"整个删掉
        var reference = await SaveWaveAsync(store, seed.DeviceId, daysAgo: 100);
        var sameDay = await SaveWaveAsync(store, seed.DeviceId, daysAgo: 100);
        await MarkAsReferenceAsync(seed.ChannelId, await InsertFeatureAsync(seed.ChannelId, reference, 100));

        await NewService(store, retentionDays: 30).RunOnceAsync(CancellationToken.None);

        File.Exists(Abs(reference)).Should().BeTrue();
        File.Exists(Abs(sameDay)).Should().BeFalse();
        Directory.Exists(Path.GetDirectoryName(Abs(reference))).Should().BeTrue(
            "目录还留着受保护的文件，删目录会连带把它删了");
    }

    [PostgresFact]
    public async Task Sweep_WithRetentionOff_DeletesNothingButStillClearsLeftoverTmp()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("关闭按天清理");
        var store = NewStore(retentionDays: 0);

        var old = await SaveWaveAsync(store, seed.DeviceId, daysAgo: 500);
        var featureId = await InsertFeatureAsync(seed.ChannelId, old, daysAgo: 500);
        var tmp = Abs(old) + ".abcdef.tmp";
        await File.WriteAllTextAsync(tmp, "{\"waveData\":[1,2");

        await NewService(store, retentionDays: 0).RunOnceAsync(CancellationToken.None);

        File.Exists(Abs(old)).Should().BeTrue("RetentionDays<=0 是关掉按天清理，不是关掉之后当成 cutoff=今天全删");
        (await FeatureExistsAsync(featureId)).Should().BeTrue("关掉清理时不该动数据库");
        File.Exists(tmp).Should().BeFalse(
            "残留 .tmp 是断电时 rename 没走完留下的半截文件，任何配置下都该清掉，不然只增不减");
    }

    // ---- helpers ----

    private LocalFileWaveStore NewStore(int retentionDays) => new(
        Options.Create(new LocalFileWaveStoreOptions
        {
            RootPath = _root, RetentionDays = retentionDays, MaxFileBytes = 1_000_000,
        }),
        NullLogger<LocalFileWaveStore>.Instance);

    private WaveRetentionService NewService(LocalFileWaveStore store, int retentionDays) => new(
        store, _fx.DataSource,
        Options.Create(new LocalFileWaveStoreOptions { RootPath = _root, RetentionDays = retentionDays }),
        NullLogger<WaveRetentionService>.Instance);

    private string Abs(string relativePath) => Path.Combine(_root, relativePath);

    /// <summary>落一条 <paramref name="daysAgo"/> 天前的波形，返回库里要存的相对路径。</summary>
    private static Task<string> SaveWaveAsync(LocalFileWaveStore store, long deviceId, int daysAgo) =>
        store.SaveAsync(deviceId, channelNo: 5, DateTimeOffset.UtcNow.AddDays(-daysAgo),
            new FxEchoWaveDto(Fc: 5, EchoStart: 23000, ClockInt: 25,
                WaveData: [1, 2, 3, 4], Threshold: -490));

    private async Task<long> InsertFeatureAsync(long channelId, string relativePath, int daysAgo)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO wave_features (channel_id, ts, wave_blob_url, point_count)
            VALUES (@ch, now() - make_interval(days => @days), @url, 4)
            RETURNING id");
        cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Bigint, channelId);
        cmd.Parameters.AddWithValue("days", NpgsqlDbType.Integer, daysAgo);
        cmd.Parameters.AddWithValue("url", NpgsqlDbType.Varchar, relativePath);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task MarkAsReferenceAsync(long channelId, long waveFeatureId)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO reference_waves (channel_id, wave_feature_id, reason)
            VALUES (@ch, @wf, 'manual')");
        cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Bigint, channelId);
        cmd.Parameters.AddWithValue("wf", NpgsqlDbType.Bigint, waveFeatureId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> FeatureExistsAsync(long id) =>
        await _fx.ScalarAsync<long>("SELECT count(*) FROM wave_features WHERE id=@arg", id) == 1;
}
