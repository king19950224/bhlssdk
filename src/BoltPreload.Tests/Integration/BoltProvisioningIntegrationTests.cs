using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoltPreload.Domain.Entities;
using FluentAssertions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 螺栓建档闭环端点集成测试（现场"多接/新接探头"场景）：
/// 1) POST /api/monitor-groups/{gid}/bolts：建档 + 通道置 bound + 暂存回填 + 绑定历史/审计，一个事务；
/// 2) 通道已占用 / 组内编号重复 → 409；未知规格缺截面积 → 400；
/// 3) PATCH /api/monitor-groups/{gid}：expected_bolt_count 修改 + 审计 + 越界校验。
/// 测试替身见共享 harness <see cref="TestApi"/>。
/// </summary>
[Collection("postgres")]
public sealed class BoltProvisioningIntegrationTests
{
    private readonly PostgresFixture _fx;
    public BoltProvisioningIntegrationTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task CreateBolt_BindsChannel_BackfillsUnbound_AndAudits()
    {
        if (!_fx.Available) return;

        var s = await SeedAsync(unboundRows: 3);
        var userId = await SeedUserAsync("operator");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, userId, "operator");

        var resp = await client.PostAsJsonAsync($"/api/monitor-groups/{s.GroupId}/bolts", new
        {
            channelId = s.ChannelId,
            positionLabel = "5号",
            nominalDiam = "M16",
            grade = "8.8",
            targetPreloadN = 100000,
            minPreloadN = 80000,
            backfillUnbound = true,
            note = "集成测试建档",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var boltId = body.GetProperty("boltId").GetInt64();
        body.GetProperty("code").GetString().Should().Be($"{s.GroupCode}-CH{s.ChannelNo:D2}", "code 缺省按 组code-CH通道号 生成");
        body.GetProperty("backfilled").GetInt64().Should().Be(3, "绑定前暂存的 3 条测量应全部回填");

        // 螺栓档案落库：规格按 M16 预设（截面积 157），未标定状态
        await using (var cmd = _fx.DataSource.CreateCommand(@"
            SELECT group_id, channel_id, position_label, cross_section_mm2, state
            FROM bolts WHERE id = @id AND deleted_at IS NULL"))
        {
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, boltId);
            await using var r = await cmd.ExecuteReaderAsync();
            (await r.ReadAsync()).Should().BeTrue("bolts 应有新档案");
            r.GetInt64(0).Should().Be(s.GroupId);
            r.GetInt64(1).Should().Be(s.ChannelId);
            r.GetString(2).Should().Be("5号");
            r.GetDecimal(3).Should().Be(157m, "M16 预设应力截面积");
            r.GetString(4).Should().Be("uncalibrated");
        }

        (await ScalarAsync<string>(
            "SELECT business_state FROM channels WHERE id = @p0", s.ChannelId))
            .Should().Be("bound");

        (await ScalarAsync<long>(
            "SELECT count(*) FROM measurements WHERE bolt_id = @p0 AND algo_version = 'phase1:raw:backfilled'", boltId))
            .Should().Be(3, "暂存测量应迁入正式 measurements");
        (await ScalarAsync<long>(
            "SELECT count(*) FROM unbound_measurements WHERE channel_id = @p0", s.ChannelId))
            .Should().Be(0, "回填后暂存表应清空");

        (await ScalarAsync<long>(
            "SELECT count(*) FROM channel_binding_history WHERE bolt_id = @p0 AND action = 'bind'", boltId))
            .Should().Be(1, "建档绑定应写绑定历史");
        (await ScalarAsync<long>(
            "SELECT count(*) FROM audit_log WHERE action = 'create_bolt' AND target_id = @p0", boltId))
            .Should().Be(1, "建档应写审计");
    }

    [Fact]
    public async Task CreateBolt_ChannelAlreadyBound_Returns409()
    {
        if (!_fx.Available) return;

        var s = await SeedAsync();
        var userId = await SeedUserAsync("operator");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, userId, "operator");

        var first = await client.PostAsJsonAsync($"/api/monitor-groups/{s.GroupId}/bolts", new { channelId = s.ChannelId });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstCode = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

        var second = await client.PostAsJsonAsync($"/api/monitor-groups/{s.GroupId}/bolts", new { channelId = s.ChannelId });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict, "同通道不允许两颗活跃螺栓");
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("existingBoltCode").GetString().Should().Be(firstCode);
    }

    [Fact]
    public async Task CreateBolt_DuplicateCodeInGroup_Returns409()
    {
        if (!_fx.Available) return;

        var s = await SeedAsync(extraChannelNo: 9);
        var userId = await SeedUserAsync("operator");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, userId, "operator");

        var first = await client.PostAsJsonAsync(
            $"/api/monitor-groups/{s.GroupId}/bolts", new { channelId = s.ChannelId, code = "DUP-01" });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.PostAsJsonAsync(
            $"/api/monitor-groups/{s.GroupId}/bolts", new { channelId = s.ExtraChannelId, code = "DUP-01" });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict, "UNIQUE(group_id, code) 冲突应报 409 而非 500");
    }

    [Fact]
    public async Task CreateBolt_UnknownSpec_WithoutCrossSection_Returns400()
    {
        if (!_fx.Available) return;

        var s = await SeedAsync();
        var userId = await SeedUserAsync("operator");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, userId, "operator");

        var resp = await client.PostAsJsonAsync(
            $"/api/monitor-groups/{s.GroupId}/bolts", new { channelId = s.ChannelId, nominalDiam = "M99" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "预设表没有 M99，必须显式给 crossSectionMm2");
    }

    [Fact]
    public async Task PatchGroup_UpdatesExpectedBoltCount_AndAudits()
    {
        if (!_fx.Available) return;

        var s = await SeedAsync();
        var userId = await SeedUserAsync("operator");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, userId, "operator");

        var resp = await client.PatchAsJsonAsync($"/api/monitor-groups/{s.GroupId}", new { expectedBoltCount = 24 });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("expectedBoltCount").GetInt32().Should().Be(24);
        body.GetProperty("previous").GetInt32().Should().Be(8, "种子组配置孔位为 8");

        (await ScalarAsync<short>(
            "SELECT expected_bolt_count FROM monitor_groups WHERE id = @p0", s.GroupId))
            .Should().Be((short)24);
        (await ScalarAsync<long>(
            "SELECT count(*) FROM audit_log WHERE action = 'update_group_config' AND target_id = @p0", s.GroupId))
            .Should().Be(1, "孔位数变更应写审计");
    }

    [Fact]
    public async Task PatchGroup_InvalidCount_Returns400_MissingGroup_Returns404()
    {
        if (!_fx.Available) return;

        var s = await SeedAsync();
        var userId = await SeedUserAsync("operator");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, userId, "operator");

        (await client.PatchAsJsonAsync($"/api/monitor-groups/{s.GroupId}", new { expectedBoltCount = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PatchAsJsonAsync($"/api/monitor-groups/{s.GroupId}", new { expectedBoltCount = 300 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PatchAsJsonAsync($"/api/monitor-groups/{s.GroupId}", new { }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PatchAsJsonAsync("/api/monitor-groups/999999999", new { expectedBoltCount = 12 }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ===================== seed =====================

    private sealed record Seed(
        long GroupId, string GroupCode, long DeviceId,
        long ChannelId, short ChannelNo, long ExtraChannelId);

    /// <summary>
    /// 种子：项目 + 设备 + 未绑定通道（CH5，模拟 auto_discovered）+ 监测组（配置孔位 8）。
    /// unboundRows &gt; 0 时给通道堆 N 条暂存测量（现场先接探头后建档的典型状态）。
    /// </summary>
    private async Task<Seed> SeedAsync(int unboundRows = 0, short? extraChannelNo = null)
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..10];
        await using var db = _fx.NewDbContext();

        var project = new Project { Code = $"P-{tag}", Name = "建档集成", Industry = "风电", SlaLevel = "standard", CreatedAt = now, UpdatedAt = now };
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
        var channel = NewChannel(device.Id, channelNo, now);
        db.Channels.Add(channel);
        Channel? extra = null;
        if (extraChannelNo is { } no)
        {
            extra = NewChannel(device.Id, no, now);
            db.Channels.Add(extra);
        }

        var group = new MonitorGroup
        {
            ProjectId = project.Id, Code = $"G-{tag}", Name = "测试法兰",
            StructureType = "flange", ExpectedBoltCount = 8, TargetPreloadN = 100000, Enabled = true,
            CreatedAt = now, UpdatedAt = now,
        };
        db.MonitorGroups.Add(group);
        await db.SaveChangesAsync();

        if (unboundRows > 0)
        {
            await using var cmd = _fx.DataSource.CreateCommand(@"
                INSERT INTO unbound_measurements (device_id, channel_id, channel_no, ts, echo_time_ns, sp_temp_c, gain, status_code)
                SELECT @d, @c, @no, now() - (i || ' seconds')::interval, 32000 + i, 20.5, 16, 0
                FROM generate_series(1, @n) i");
            cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, device.Id);
            cmd.Parameters.AddWithValue("c", NpgsqlDbType.Bigint, channel.Id);
            cmd.Parameters.AddWithValue("no", NpgsqlDbType.Smallint, channelNo);
            cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, unboundRows);
            await cmd.ExecuteNonQueryAsync();
        }

        return new Seed(group.Id, group.Code, device.Id, channel.Id, channelNo, extra?.Id ?? 0);
    }

    private static Channel NewChannel(long deviceId, short channelNo, DateTimeOffset now) => new()
    {
        DeviceId = deviceId, ChannelNo = channelNo, ProbeFreqMhz = 2.0m, Gain = 16, MeasMode = 2, TcMode = 1,
        TofAlgorithm = "ThrSignal", HardwareState = "healthy", BusinessState = "unbound", ConfigSource = "database",
        State = "auto_discovered", DiscoveredAt = now, CreatedAt = now, UpdatedAt = now,
    };

    private async Task<long> SeedUserAsync(string role)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "INSERT INTO users (username, password_hash, role) VALUES (@u, 'seed', @r) RETURNING id");
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, "u_" + Guid.NewGuid().ToString("N")[..12]);
        cmd.Parameters.AddWithValue("r", NpgsqlDbType.Varchar, role);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<T?> ScalarAsync<T>(string sql, long p0)
    {
        await using var cmd = _fx.DataSource.CreateCommand(sql.Replace("@p0", "@arg"));
        cmd.Parameters.AddWithValue("arg", NpgsqlDbType.Bigint, p0);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }
}
