using BoltPreload.Api.Runtime;
using BoltPreload.Application.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 未绑定测量的保留期清理（<see cref="UnboundRetentionService"/>）。
/// <para>
/// 插着探头但还没建档的通道会一直往 <c>unbound_measurements</c> 落数据，这个表没有别的清理入口。
/// 保留窗口同时是建档回填的取数范围：删早了，现场接完探头再回来建档就补不齐历史；
/// 不删则表无限涨。所以这里钉的是"到底按哪条线删"，而不是"删了多少行"。
/// </para>
/// <para>
/// 尤其是 <c>Math.Max(1, RetentionDays)</c>：配置里写 0 的自然理解是"不清理"，
/// 但那样 cutoff 就是此刻，一轮下来整张表清空，回填再也没有数据可取。这条兜底之前没人测。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class UnboundRetentionIntegrationTests
{
    private readonly PostgresFixture _fx;
    public UnboundRetentionIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task Sweep_DropsOnlyWhatIsOlderThanTheWindow()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("未绑定清理");

        var expired = await InsertAsync(seed, daysAgo: 8);
        var justInside = await InsertAsync(seed, daysAgo: 6);

        var deleted = await NewService(retentionDays: 7).SweepAsync(CancellationToken.None);

        deleted.Should().Be(1);
        (await ExistsAsync(expired)).Should().BeFalse();
        (await ExistsAsync(justInside)).Should().BeTrue("还在窗口里，建档回填要用");
    }

    [PostgresFact]
    public async Task Sweep_WithRetentionDaysZero_FallsBackToOneDay_NotToWipingTheTable()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("保留天数为零");

        var justNow = await InsertAsync(seed, daysAgo: 0);
        var twoDaysOld = await InsertAsync(seed, daysAgo: 2);

        await NewService(retentionDays: 0).SweepAsync(CancellationToken.None);

        (await ExistsAsync(justNow)).Should().BeTrue(
            "配 0 不能理解成 cutoff=此刻，那是一轮清空整张表");
        (await ExistsAsync(twoDaysOld)).Should().BeFalse("兜底到 1 天，两天前的照删");
    }

    [PostgresFact]
    public async Task Sweep_KeepsBatchingUntilNothingIsLeftBeyondTheWindow()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("分批清理");

        // 单批上限被 Math.Clamp 抬到 100，所以要超过一批得插 100 行以上。
        // 分批是为了不长时间锁表，但循环得一直转到删完为止，只删头一批的话表还是只增不减。
        for (var i = 0; i < 150; i++) await InsertAsync(seed, daysAgo: 30);

        var deleted = await NewService(retentionDays: 7, batchSize: 1).SweepAsync(CancellationToken.None);

        deleted.Should().Be(150);
        (await RemainingAsync(seed.ChannelId)).Should().Be(0);
    }

    // ---- helpers ----

    private UnboundRetentionService NewService(int retentionDays, int batchSize = 5000) => new(
        _fx.DataSource,
        Options.Create(new UnboundRetentionOptions
        {
            RetentionDays = retentionDays, BatchSize = batchSize,
        }),
        new BackgroundServiceHealth(),
        NullLogger<UnboundRetentionService>.Instance);

    private async Task<long> InsertAsync(IntegrationSeed.CalibratableChannel seed, int daysAgo)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO unbound_measurements (device_id, channel_id, channel_no, ts, echo_time_ns, status_code)
            VALUES (@dev, @ch, 5, now() - make_interval(days => @days), 27200, 0)
            RETURNING id");
        cmd.Parameters.AddWithValue("dev", NpgsqlDbType.Bigint, seed.DeviceId);
        cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Bigint, seed.ChannelId);
        cmd.Parameters.AddWithValue("days", NpgsqlDbType.Integer, daysAgo);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<bool> ExistsAsync(long id) =>
        await _fx.ScalarAsync<long>("SELECT count(*) FROM unbound_measurements WHERE id=@arg", id) == 1;

    private Task<long> RemainingAsync(long channelId) =>
        _fx.ScalarAsync<long>("SELECT count(*) FROM unbound_measurements WHERE channel_id=@arg", channelId);
}
