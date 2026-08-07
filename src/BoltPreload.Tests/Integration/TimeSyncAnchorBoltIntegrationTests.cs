using BoltPreload.Api.Runtime;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 校时服务的锚定螺栓查询（<see cref="TimeSyncService.FindAnchorBoltAsync"/>）。
/// <para>
/// clock_drift 是设备级故障，但告警模型挂在螺栓上，靠这条查询取设备下
/// channel_no 最小的绑定螺栓当代表。真机泡机撞过一次：JOIN 方向写成了
/// c.bolt_id（channels 表没有这列，绑定方向是 bolts.channel_id → channels.id），
/// 42703 每轮校时必抛，clock_drift 的触发与清除整条链路自服务上线起没工作过，
/// 而日志之外没有任何可见症状。此前这条查询零测试覆盖——总览页同病灶
/// （EndpointIntegrationTests 里钉过的那次）修完没有排查同类写法，这里漏网。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class TimeSyncAnchorBoltIntegrationTests
{
    private readonly PostgresFixture _fx;

    public TimeSyncAnchorBoltIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task FindAnchorBolt_ReturnsBoundBolt_AndNullAfterSoftDelete()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("校时锚定");
        var svc = NewService();

        var anchor = await svc.FindAnchorBoltAsync(seed.DeviceId, CancellationToken.None);
        anchor.Should().NotBeNull("设备下有绑定螺栓，锚定查询必须找得到代表");
        anchor!.Value.BoltId.Should().Be(seed.BoltId);
        anchor.Value.ChannelId.Should().Be(seed.ChannelId);

        // 螺栓软删后要返回 null（校时告警静默跳过），而不是把软删行当代表
        await using (var cmd = _fx.DataSource.CreateCommand(
            "UPDATE bolts SET deleted_at = now() WHERE id = @b"))
        {
            cmd.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, seed.BoltId);
            await cmd.ExecuteNonQueryAsync();
        }

        (await svc.FindAnchorBoltAsync(seed.DeviceId, CancellationToken.None))
            .Should().BeNull("软删的螺栓不能再当 clock_drift 的落靠对象");
    }

    // 锚定查询只走 NpgsqlDataSource；其余依赖不参与，这里不装假实现
    private TimeSyncService NewService() => new(
        manager: null!,
        ds: _fx.DataSource,
        sink: null!,
        publisher: null!,
        probeScan: null!,
        opt: Options.Create(new TimeSyncOptions()),
        health: null!,
        logger: NullLogger<TimeSyncService>.Instance);
}
