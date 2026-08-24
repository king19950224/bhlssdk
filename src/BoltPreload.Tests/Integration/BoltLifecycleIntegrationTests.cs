using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoltPreload.Api.Runtime;
using BoltPreload.Application.Diagnostics;
using BoltPreload.Application.Ingestion;
using BoltPreload.Domain.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 螺栓档案两级模型（bolt-lifecycle-design.md）：占位档（confirmed_at IS NULL，自动建档且无人工痕迹）
/// 与资产档（被人确认过）。钉住两条对称规则：
/// R2 占位档探头离位满 N 天自动回收（资产档与新鲜占位档不动）；
/// R3 迁移遇到目标通道被占位档占用时同事务吸收（资产档占用维持 409）。
/// 这两条规则的反向保证同样重要——自动回收绝不能碰被人确认过的档案，
/// 吸收绝不能吞掉别人的资产声明。
/// </summary>
[Collection("postgres")]
public sealed class BoltLifecycleIntegrationTests
{
    private readonly PostgresFixture _fx;
    public BoltLifecycleIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task AutoRetire_RemovesStalePlaceholder_KeepsConfirmedAndFresh()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("占位档回收");

        // 三个角色：
        //  stale    占位档 + 探头离位 10 天  → 应被回收
        //  fresh    占位档 + 刚刚还在位      → 保留
        //  confirmed 资产档 + 同样离位 10 天 → 保留（资产语义：失联报警而非消失）
        var stale = await CreatePlaceholderAsync(seed, channelNo: 21, confirmed: false);
        var fresh = await CreatePlaceholderAsync(seed, channelNo: 22, confirmed: false);
        var confirmed = await CreatePlaceholderAsync(seed, channelNo: 23, confirmed: true);

        await SetChannelPresenceAsync(stale.ChannelId, hardwareState: "no_data", lastPresentDaysAgo: 10);
        await SetChannelPresenceAsync(fresh.ChannelId, hardwareState: "no_data", lastPresentDaysAgo: 0);
        await SetChannelPresenceAsync(confirmed.ChannelId, hardwareState: "no_data", lastPresentDaysAgo: 10);

        var retired = await NewService(autoRetireDays: 7).RetirePlaceholderBoltsAsync(CancellationToken.None);

        // 回收服务是全库扫描：全量套件运行时其它用例遗留的占位档可能被同一轮扫走，
        // 全局计数不可断言精确值——只钉本用例三颗螺栓的终态
        retired.Should().BeGreaterThanOrEqualTo(1, "至少回收了本用例的离位占位档");
        (await BoltDeletedAsync(stale.BoltId)).Should().BeTrue("离位 10 天的占位档应被软删");
        (await BoltDeletedAsync(fresh.BoltId)).Should().BeFalse("刚在位过的占位档不动");
        (await BoltDeletedAsync(confirmed.BoltId)).Should().BeFalse("资产档永不自动删——拔探头是失联告警不是档案消失");

        (await _fx.ScalarAsync<string>("SELECT business_state FROM channels WHERE id=@arg", stale.ChannelId))
            .Should().Be("unbound", "回收后通道回到未绑定，探头插回可重新自动建档");
        (await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM audit_log WHERE action='bolt_auto_retire' AND target_id=@arg", stale.BoltId))
            .Should().Be(1, "自动回收必须审计留痕");
    }

    [PostgresFact]
    public async Task AutoRetire_Disabled_TouchesNothing()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("回收开关");
        var stale = await CreatePlaceholderAsync(seed, channelNo: 24, confirmed: false);
        await SetChannelPresenceAsync(stale.ChannelId, hardwareState: "no_data", lastPresentDaysAgo: 30);

        var retired = await NewService(autoRetireDays: 0).RetirePlaceholderBoltsAsync(CancellationToken.None);

        retired.Should().Be(0);
        (await BoltDeletedAsync(stale.BoltId)).Should().BeFalse("AutoRetireDays<=0 表示关闭回收");
    }

    [PostgresFact]
    public async Task MoveChannel_AbsorbsPlaceholder_And409OnConfirmedTarget()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("迁移吸收");

        // 场景：seed 的资产档螺栓要迁到 ch25——但 ch25 已被 auto-enroll 抢建了占位档
        //（探头挪线的典型现场时序）。迁移应一步吸收占位档，免去"先手工删 ghost"。
        var placeholder = await CreatePlaceholderAsync(seed, channelNo: 25, confirmed: false);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var resp = await client.PostAsJsonAsync($"/api/bolts/{seed.BoltId}/move-channel",
            new { toChannelId = placeholder.ChannelId });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("absorbedBoltCode").GetString().Should().NotBeNullOrEmpty("响应要明示吸收了哪个占位档");

        (await BoltDeletedAsync(placeholder.BoltId)).Should().BeTrue("占位档被吸收（软删 + 审计）");
        (await _fx.ScalarAsync<long>("SELECT channel_id FROM bolts WHERE id=@arg", seed.BoltId))
            .Should().Be(placeholder.ChannelId, "资产档连历史一起迁到新通道");
        (await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM audit_log WHERE action='bolt_absorb_placeholder' AND target_id=@arg", placeholder.BoltId))
            .Should().Be(1, "吸收必须审计留痕");

        // 反向保证：目标通道上是资产档（被人确认过）时绝不吸收——覆盖别人的声明必须显式解绑
        var confirmedTarget = await CreatePlaceholderAsync(seed, channelNo: 26, confirmed: true);
        var conflict = await client.PostAsJsonAsync($"/api/bolts/{seed.BoltId}/move-channel",
            new { toChannelId = confirmedTarget.ChannelId });
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await BoltDeletedAsync(confirmedTarget.BoltId)).Should().BeFalse("资产档不可被吸收");
    }

    [PostgresFact]
    public async Task ManualTouchPoints_StampConfirmedAt()
    {
        _fx.EnsureAvailable();
        var seed = await _fx.SeedCalibratableChannelAsync("盖章点");
        var placeholder = await CreatePlaceholderAsync(seed, channelNo: 27, confirmed: false);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        // 人工编辑规格 = 认领 → 占位档升级为资产档
        var resp = await client.PostAsJsonAsync($"/api/bolts/{placeholder.BoltId}/settings",
            new { targetPreloadN = 120000 });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        (await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM bolts WHERE id=@arg AND confirmed_at IS NOT NULL", placeholder.BoltId))
            .Should().Be(1, "人工改规格是两级模型的盖章点之一");
    }

    // ---- helpers ----

    private sealed record PlaceholderBolt(long BoltId, long ChannelId);

    /// <summary>在 seed 设备上建一条新通道 + 螺栓档案。confirmed=false 模拟 auto-enroll 的占位档。</summary>
    private async Task<PlaceholderBolt> CreatePlaceholderAsync(
        IntegrationSeed.CalibratableChannel seed, short channelNo, bool confirmed)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = _fx.NewDbContext();
        var channel = new Channel
        {
            DeviceId = seed.DeviceId, ChannelNo = channelNo, ProbeFreqMhz = 2.0m, Gain = 16,
            MeasMode = 2, TcMode = 1, TofAlgorithm = "ThrSignal",
            HardwareState = "healthy", BusinessState = "bound", ConfigSource = "database",
            State = "auto_discovered", DiscoveredAt = now, CreatedAt = now, UpdatedAt = now,
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var bolt = new Bolt
        {
            GroupId = seed.GroupId, ChannelId = channel.Id, Code = $"占位-{channelNo}-{Guid.NewGuid().ToString("N")[..6]}",
            NominalDiam = "M20", Grade = "8.8", Rp02Mpa = 640, Material = "42CrMo",
            ElasticModulusGpa = 206, EffectiveLengthMm = 200, TotalLengthMm = 220,
            CrossSectionMm2 = 245, SoundVelocityMs = 5890,
            State = "uncalibrated", Enabled = true, GbCompliant = true,
            ConfirmedAt = confirmed ? now : null,
            CreatedAt = now.AddDays(-30), UpdatedAt = now,
        };
        db.Bolts.Add(bolt);
        await db.SaveChangesAsync();
        return new PlaceholderBolt(bolt.Id, channel.Id);
    }

    private async Task SetChannelPresenceAsync(long channelId, string hardwareState, int lastPresentDaysAgo)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            UPDATE channels
               SET hardware_state = @hw,
                   last_present_at = now() - make_interval(days => @days)
             WHERE id = @id");
        cmd.Parameters.AddWithValue("hw", NpgsqlDbType.Varchar, hardwareState);
        cmd.Parameters.AddWithValue("days", NpgsqlDbType.Integer, lastPresentDaysAgo);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> BoltDeletedAsync(long boltId) =>
        await _fx.ScalarAsync<long>(
            "SELECT count(*) FROM bolts WHERE id=@arg AND deleted_at IS NOT NULL", boltId) == 1;

    private UnboundRetentionService NewService(int autoRetireDays) => new(
        _fx.DataSource,
        Options.Create(new UnboundRetentionOptions()),
        Options.Create(new ChannelPresenceOptions { AutoRetireDays = autoRetireDays }),
        new NoopResolver(),
        new BackgroundServiceHealth(),
        NullLogger<UnboundRetentionService>.Instance);

    private sealed class NoopResolver : IChannelResolver
    {
        public Task<ChannelRoute> ResolveAsync(long deviceId, ushort channelNo, DateTimeOffset firstSeenAt, CancellationToken ct = default)
            => Task.FromResult(new ChannelRoute(0, 0));
        public void Invalidate(long deviceId, ushort channelNo) { }
        public void InvalidateDevice(long deviceId) { }
        public void InvalidateAll() { }
    }
}
