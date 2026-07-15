using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoltPreload.Domain.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// HTTP 层端点集成测试（WebApplicationFactory；无 DB 时自动 Skip）。锁定本会话修复：
/// 1) 管理员免审批：admin 提交 configure-channel 直接落 approved；reviewer 仍 requested；
/// 2) 入参校验：preload-trend 非法 bucket → 400。
/// 测试替身（认证/license/去后台服务）见共享 harness <see cref="TestApi"/>。
/// </summary>
[Collection("postgres")]
public sealed class EndpointIntegrationTests
{
    private readonly PostgresFixture _fx;
    public EndpointIntegrationTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task ConfigureChannel_AsAdmin_AutoApproves()
    {
        if (!_fx.Available) return;

        var (deviceId, channelNo) = await SeedDeviceChannelAsync();
        var adminId = await SeedUserAsync("admin");

        using var factory = CreateFactory();
        using var client = AuthedClient(factory, adminId, "admin");

        var resp = await client.PostAsJsonAsync(
            $"/api/devices/{deviceId}/diagnostic/configure-channel/{channelNo}", new { gain = 20 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("approved", "管理员提交应免审批直接批准");
        (await LatestRequestStatusAsync(deviceId, channelNo)).Should().Be("approved");
    }

    [Fact]
    public async Task ConfigureChannel_AsReviewer_StaysRequested()
    {
        if (!_fx.Available) return;

        var (deviceId, channelNo) = await SeedDeviceChannelAsync();
        var reviewerId = await SeedUserAsync("reviewer");

        using var factory = CreateFactory();
        using var client = AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync(
            $"/api/devices/{deviceId}/diagnostic/configure-channel/{channelNo}", new { gain = 20 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("requested", "非管理员仍需异人审批");
        (await LatestRequestStatusAsync(deviceId, channelNo)).Should().Be("requested");
    }

    [Fact]
    public async Task PreloadTrend_InvalidBucket_Returns400()
    {
        if (!_fx.Available) return;

        var userId = await SeedUserAsync("operator");
        using var factory = CreateFactory();
        using var client = AuthedClient(factory, userId, "operator");

        // 非法 bucket（不在白名单）→ 400，在 bolt 查询之前拦下（故 boltId 是否存在无关）
        var resp = await client.GetAsync("/api/bolts/1/preload-trend?bucket=bogus");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ===================== harness =====================

    private WebApplicationFactory<Program> CreateFactory() => TestApi.CreateFactory(_fx);

    private static HttpClient AuthedClient(WebApplicationFactory<Program> factory, long userId, string role) =>
        TestApi.AuthedClient(factory, userId, role);

    private async Task<long> SeedUserAsync(string role)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "INSERT INTO users (username, password_hash, role) VALUES (@u, 'seed', @r) RETURNING id");
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, "u_" + Guid.NewGuid().ToString("N")[..12]);
        cmd.Parameters.AddWithValue("r", NpgsqlDbType.Varchar, role);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(long deviceId, short channelNo)> SeedDeviceChannelAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..10];
        await using var db = _fx.NewDbContext();

        var project = new Project { Code = $"P-{tag}", Name = "端点集成", Industry = "风电", SlaLevel = "standard", CreatedAt = now, UpdatedAt = now };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var device = new Device
        {
            ProjectId = project.Id, Code = $"D-{tag}", Model = 3, CommType = 1, CommParams = "in-process",
            Address = 1, TransferMode = 1, ChannelCount = 40, State = "online", CreatedAt = now, UpdatedAt = now,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        const short channelNo = 5;
        var channel = new Channel
        {
            DeviceId = device.Id, ChannelNo = channelNo, ProbeFreqMhz = 2.0m, Gain = 16, MeasMode = 2, TcMode = 1,
            TofAlgorithm = "ThrSignal", HardwareState = "healthy", BusinessState = "unbound", ConfigSource = "database",
            State = "manual", DiscoveredAt = now, CreatedAt = now, UpdatedAt = now,
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        return (device.Id, channelNo);
    }

    private async Task<string?> LatestRequestStatusAsync(long deviceId, short channelNo)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            SELECT status FROM channel_config_change_requests
            WHERE device_id=@d AND channel_no=@c ORDER BY requested_at DESC LIMIT 1");
        cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
        cmd.Parameters.AddWithValue("c", NpgsqlDbType.Smallint, channelNo);
        return await cmd.ExecuteScalarAsync() as string;
    }
}
