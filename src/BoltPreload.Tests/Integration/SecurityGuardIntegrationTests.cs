using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoltPreload.Domain.Entities;
using FluentAssertions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 安全守卫端点集成测试：
/// 1) 告警抑制：boltId 必填（禁止全局静默）、reason trim、shelve/cancel 写审计；
/// 2) 用户管理：不能把最后一名 enabled admin 降权 / 停用；
/// 3) 解绑通道：reviewer+（operator 403）；
/// 4) 建档绑定：通道设备与监测组必须同项目；
/// 5) commission 调试直发：随诊断组收紧为 reviewer+（operator 403）；
/// 6) 单螺栓查询 GET /api/bolts/{id}：详情页替代"全量列表再 find"；
/// 7) 标定模块"资源不存在"统一 404（原 400/404 混用）；
/// 8) 账号变更与阈值变更必须留痕，且被守卫拒绝时不得留痕（§0 责任链：审计与变更同事务）。
/// 测试替身见共享 harness <see cref="TestApi"/>。
/// </summary>
[Collection("postgres")]
public sealed class SecurityGuardIntegrationTests
{
    private readonly PostgresFixture _fx;
    public SecurityGuardIntegrationTests(PostgresFixture fx) => _fx = fx;

    // ===================== 告警抑制守卫 =====================

    [PostgresFact]
    public async Task Shelve_WithoutBoltId_Returns400()
    {
        _fx.EnsureAvailable();
        var userId = await SeedUserAsync("operator");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, userId, "operator");

        // bolt/type 双空 = 全局通配抑制，必须被拒
        var resp = await client.PostAsJsonAsync("/api/alarms/shelve", new { hours = 24, reason = "全站维护测试" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "缺 boltId 的全局抑制必须 400");

        // 纯空白凑长度的 reason 也要拒
        var s = await SeedAssetsAsync();
        var resp2 = await client.PostAsJsonAsync("/api/alarms/shelve", new { boltId = s.BoltId, hours = 24, reason = "a    " });
        resp2.StatusCode.Should().Be(HttpStatusCode.BadRequest, "trim 后不足 5 字的 reason 必须 400");
    }

    [PostgresFact]
    public async Task Shelve_AndCancel_WriteAudit()
    {
        _fx.EnsureAvailable();
        var s = await SeedAssetsAsync();
        var userId = await SeedUserAsync("operator");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, userId, "operator");

        var resp = await client.PostAsJsonAsync("/api/alarms/shelve",
            new { boltId = s.BoltId, alarmType = "preload_low", hours = 24, reason = "更换探头维护窗口" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var shelvingId = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("shelvingId").GetInt64();

        (await CountAsync("SELECT count(*) FROM audit_log WHERE action='alarm_shelve' AND target_id=@p0", shelvingId))
            .Should().Be(1, "抑制必须写审计");

        var cancel = await client.PostAsync($"/api/alarms/shelve/{shelvingId}/cancel", null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountAsync("SELECT count(*) FROM audit_log WHERE action='alarm_shelve_cancel' AND target_id=@p0", shelvingId))
            .Should().Be(1, "取消抑制必须写审计");
    }

    // ===================== 最后一名管理员保护 =====================

    [PostgresFact]
    public async Task CreateUser_DemoteLastAdmin_Returns400_ButAllowedWithSecondAdmin()
    {
        _fx.EnsureAvailable();

        // 场景固定：全库仅剩一名 enabled admin（其余全部停用；各测试自种用户，互不依赖）
        await DisableAllAdminsAsync();
        var lastAdminName = await SeedNamedUserAsync("admin");
        var actorId = await SeedUserAsync("operator"); // 认证走请求头角色，声明 admin 即可过策略

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, actorId, "admin");

        var demote = await client.PostAsJsonAsync("/api/auth/users",
            new { username = lastAdminName, password = "password123", role = "operator" });
        demote.StatusCode.Should().Be(HttpStatusCode.BadRequest, "唯一 enabled admin 不允许被降权");

        // 增加第二名 enabled admin 后，同样的降权应放行
        await SeedNamedUserAsync("admin");
        var demote2 = await client.PostAsJsonAsync("/api/auth/users",
            new { username = lastAdminName, password = "password123", role = "operator" });
        demote2.StatusCode.Should().Be(HttpStatusCode.OK, "还有其他 enabled admin 时允许降权");
    }

    [PostgresFact]
    public async Task ToggleUser_DisableLastAdmin_Returns400()
    {
        _fx.EnsureAvailable();

        await DisableAllAdminsAsync();
        var lastAdminName = await SeedNamedUserAsync("admin");
        var lastAdminId = await UserIdOfAsync(lastAdminName);
        var actorId = await SeedUserAsync("operator");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, actorId, "admin");

        var resp = await client.PostAsync($"/api/auth/users/{lastAdminId}/toggle", null);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "唯一 enabled admin 不允许被停用");

        // 被守卫拒绝 = 事务整体回滚，审计也不该留下半条（审计与变更同事务的反向验证）
        (await CountAsync("SELECT count(*) FROM audit_log WHERE action='user_toggle' AND target_id=@p0", lastAdminId))
            .Should().Be(0, "变更被拒就不该有审计");

        // 有第二名 enabled admin 后可以停用
        await SeedNamedUserAsync("admin");
        var resp2 = await client.PostAsync($"/api/auth/users/{lastAdminId}/toggle", null);
        resp2.StatusCode.Should().Be(HttpStatusCode.OK);

        (await CountAsync("SELECT count(*) FROM audit_log WHERE action='user_toggle' AND target_id=@p0", lastAdminId))
            .Should().Be(1, "停用成功必须留痕");
    }

    // ===================== 变更留痕（§0 责任链：审计与变更同事务） =====================

    [PostgresFact]
    public async Task AccountChanges_WriteAudit_CreateChangePasswordToggle()
    {
        _fx.EnsureAvailable();
        var adminId = await SeedUserAsync("admin");

        using var factory = TestApi.CreateFactory(_fx);
        using var admin = TestApi.AuthedClient(factory, adminId, "admin");

        var username = "u_" + Guid.NewGuid().ToString("N")[..12];
        var created = await admin.PostAsJsonAsync("/api/auth/users",
            new { username, password = "password123", role = "operator" });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var newUserId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        (await CountAsync(
            "SELECT count(*) FROM audit_log WHERE action='user_create' AND target_type='user' AND target_id=@p0", newUserId))
            .Should().Be(1, "建号必须留痕");

        // 改密以本人身份走：建号时写的是真实 hash，旧密码校验能过（harness 只替换认证，不替换 hasher）
        using (var self = TestApi.AuthedClient(factory, newUserId, "operator"))
        {
            var changed = await self.PostAsJsonAsync("/api/auth/change-password",
                new { oldPassword = "password123", newPassword = "password456" });
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        (await CountAsync(
            "SELECT count(*) FROM audit_log WHERE action='user_change_password' AND target_type='user' AND target_id=@p0", newUserId))
            .Should().Be(1, "改密必须留痕");

        var toggled = await admin.PostAsync($"/api/auth/users/{newUserId}/toggle", null);
        toggled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountAsync(
            "SELECT count(*) FROM audit_log WHERE action='user_toggle' AND target_type='user' AND target_id=@p0", newUserId))
            .Should().Be(1, "启停用必须留痕");
    }

    [PostgresFact]
    public async Task UpdateBoltSettings_WritesAudit_AndNothingWhenBoltMissing()
    {
        _fx.EnsureAvailable();
        var s = await SeedAssetsAsync();
        var userId = await SeedUserAsync("operator");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, userId, "operator");

        // 报警下限决定何时报警，改了必须说得清是谁改的
        var resp = await client.PostAsJsonAsync($"/api/bolts/{s.BoltId}/settings",
            new { targetPreloadN = 120000m, minPreloadN = 90000m });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountAsync(
            "SELECT count(*) FROM audit_log WHERE action='update_bolt_spec' AND target_type='bolt' AND target_id=@p0", s.BoltId))
            .Should().Be(1, "阈值变更必须留痕");

        const long missingBoltId = 999999999L;
        var missing = await client.PostAsJsonAsync($"/api/bolts/{missingBoltId}/settings", new { targetPreloadN = 1m });
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CountAsync("SELECT count(*) FROM audit_log WHERE action='update_bolt_spec' AND target_id=@p0", missingBoltId))
            .Should().Be(0, "螺栓不存在、变更未生效，就不该有审计");
    }

    // ===================== 解绑权限收紧 =====================

    [PostgresFact]
    public async Task UnbindBolt_AsOperator_Returns403()
    {
        _fx.EnsureAvailable();
        var s = await SeedAssetsAsync();
        var operatorId = await SeedUserAsync("operator");
        var reviewerId = await SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);

        using (var opClient = TestApi.AuthedClient(factory, operatorId, "operator"))
        {
            var resp = await opClient.PostAsJsonAsync($"/api/channels/{s.ChannelId}/unbind-bolt", new { note = "op 越权尝试" });
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "解绑是破坏性操作，operator 必须被拒");
        }

        using (var revClient = TestApi.AuthedClient(factory, reviewerId, "reviewer"))
        {
            var resp = await revClient.PostAsJsonAsync($"/api/channels/{s.ChannelId}/unbind-bolt", new { note = "reviewer 正常解绑" });
            resp.StatusCode.Should().Be(HttpStatusCode.OK, "reviewer 解绑应放行");
        }
    }

    // ===================== 建档跨项目校验 =====================

    [PostgresFact]
    public async Task CreateBolt_CrossProject_Returns400()
    {
        _fx.EnsureAvailable();

        var a = await SeedAssetsAsync();          // 项目 A：设备 + 未绑定通道（ExtraChannelId）
        var b = await SeedAssetsAsync();          // 项目 B：独立的组
        var userId = await SeedUserAsync("operator");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, userId, "operator");

        // 用 A 项目的空闲通道绑到 B 项目的监测组 → 400
        var resp = await client.PostAsJsonAsync($"/api/monitor-groups/{b.GroupId}/bolts",
            new { channelId = a.ExtraChannelId });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "跨项目绑定必须被拒");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("跨项目");

        // 同项目（A 的组 + A 的空闲通道）应放行
        var ok = await client.PostAsJsonAsync($"/api/monitor-groups/{a.GroupId}/bolts",
            new { channelId = a.ExtraChannelId });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===================== commission 调试直发收紧 reviewer+ =====================

    [PostgresFact]
    public async Task Commission_AsOperator_Returns403_ReviewerPassesPolicy()
    {
        _fx.EnsureAvailable();
        var s = await SeedAssetsAsync();
        var operatorId = await SeedUserAsync("operator");
        var reviewerId = await SeedUserAsync("reviewer");

        // 设备刻意留在运行时之外：本用例要断言的是"过了策略、到达业务分支"，
        // 用 404（设备未加载）当到达业务层的证据，比构造一台连得上的设备便宜且稳定
        using var factory = await TestApi.CreateFactoryWithDeviceOutOfRuntimeAsync(_fx, s.DeviceId);

        using (var op = TestApi.AuthedClient(factory, operatorId, "operator"))
        {
            var resp = await op.PostAsJsonAsync(
                $"/api/devices/{s.DeviceId}/diagnostic/configure-channel/9/commission", new { gain = 20 });
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "调试直发本质绕过异人审批，operator 必须被拒");
        }

        using (var rev = TestApi.AuthedClient(factory, reviewerId, "reviewer"))
        {
            // reviewer 应通过鉴权、走到业务分支（设备未加载 404），而不是被策略挡在门外（403）。
            var resp = await rev.PostAsJsonAsync(
                $"/api/devices/{s.DeviceId}/diagnostic/configure-channel/9/commission", new { gain = 20 });
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "reviewer 过策略后应到达业务 404（设备未加载运行时）");
        }
    }

    // ===================== 单螺栓查询 =====================

    [PostgresFact]
    public async Task GetBolt_ById_Returns200_And404WhenMissing()
    {
        _fx.EnsureAvailable();
        var s = await SeedAssetsAsync();
        var userId = await SeedUserAsync("operator");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, userId, "operator");

        var ok = await client.GetAsync($"/api/bolts/{s.BoltId}");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetInt64().Should().Be(s.BoltId);
        body.GetProperty("channelId").GetInt64().Should().Be(s.ChannelId);

        var missing = await client.GetAsync("/api/bolts/999999999");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound, "不存在的螺栓必须 404");
    }

    // ===================== 标定"资源不存在"统一 404 =====================

    [PostgresFact]
    public async Task ApplyTorqueFit_MissingChannel_Returns404()
    {
        _fx.EnsureAvailable();
        var reviewerId = await SeedUserAsync("reviewer");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync("/api/channels/999999999/apply-torque-fit", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "通道不存在统一 404（原 400/404 混用）");
    }

    // ===================== 用户管理端点的越权防线 =====================

    [PostgresFact]
    public async Task UserManagementEndpoints_RejectNonAdmin()
    {
        _fx.EnsureAvailable();
        var actorId = await SeedUserAsync("operator");

        using var factory = TestApi.CreateFactory(_fx);

        // 建号改角色、列账号名单、启停用——这三个直接决定谁能进系统、以什么角色进。
        // 任何一个漏挂 AdminAccess 就是一条提权通道，而此前只有 admin 的正向路径被走过。
        var escalated = "x_" + Guid.NewGuid().ToString("N")[..10];
        foreach (var role in new[] { "operator", "reviewer" })
        {
            using var client = TestApi.AuthedClient(factory, actorId, role);

            (await client.PostAsJsonAsync("/api/auth/users",
                    new { username = escalated, password = "password123", role = "admin" }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} 不得建号或改角色");

            (await client.GetAsync("/api/auth/users"))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} 不得查看账号名单");

            (await client.PostAsync($"/api/auth/users/{actorId}/toggle", null))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} 不得启停账号");
        }

        (await CountUsersNamedAsync(escalated)).Should().Be(0, "越权建号必须连记录都没落下");

        // 确认 403 来自策略而不是端点本身坏了：同样的请求换 admin 应当走得通
        var adminId = await SeedUserAsync("admin");
        using var admin = TestApi.AuthedClient(factory, adminId, "admin");
        (await admin.GetAsync("/api/auth/users")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===================== seed =====================

    private sealed record Seed(long ProjectId, long GroupId, long DeviceId, long ChannelId, long ExtraChannelId, long BoltId);

    /// <summary>项目 + 设备 + 两路通道（CH5 已绑螺栓 / CH9 空闲）+ 监测组 + 螺栓。</summary>
    private async Task<Seed> SeedAssetsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..10];
        await using var db = _fx.NewDbContext();

        var project = new Project { Code = $"P-{tag}", Name = "守卫集成", Industry = "风电", SlaLevel = "standard", CreatedAt = now, UpdatedAt = now };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var device = new Device
        {
            ProjectId = project.Id, Code = $"D-{tag}", Model = 3, CommType = 1, CommParams = "in-process",
            Address = 1, TransferMode = 1, ChannelCount = 40, State = "online", CreatedAt = now, UpdatedAt = now,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        var bound = NewChannel(device.Id, 5, now);
        var free = NewChannel(device.Id, 9, now);
        db.Channels.AddRange(bound, free);

        var group = new MonitorGroup
        {
            ProjectId = project.Id, Code = $"G-{tag}", Name = "守卫测试法兰",
            StructureType = "flange", ExpectedBoltCount = 8, TargetPreloadN = 100000, Enabled = true,
            CreatedAt = now, UpdatedAt = now,
        };
        db.MonitorGroups.Add(group);
        await db.SaveChangesAsync();

        var bolt = new Bolt
        {
            GroupId = group.Id, ChannelId = bound.Id, Code = $"B-{tag}",
            NominalDiam = "M16", Grade = "8.8", Rp02Mpa = 640, Material = "42CrMo",
            ElasticModulusGpa = 206, EffectiveLengthMm = 200, TotalLengthMm = 220,
            CrossSectionMm2 = 157, SoundVelocityMs = 5890,
            State = "uncalibrated", Enabled = true, GbCompliant = true,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Bolts.Add(bolt);
        await db.SaveChangesAsync();

        return new Seed(project.Id, group.Id, device.Id, bound.Id, free.Id, bolt.Id);
    }

    private static Channel NewChannel(long deviceId, short channelNo, DateTimeOffset now) => new()
    {
        DeviceId = deviceId, ChannelNo = channelNo, ProbeFreqMhz = 2.0m, Gain = 16, MeasMode = 2, TcMode = 1,
        TofAlgorithm = "ThrSignal", HardwareState = "healthy", BusinessState = "unbound", ConfigSource = "database",
        State = "manual", DiscoveredAt = now, CreatedAt = now, UpdatedAt = now,
    };

    private async Task<long> SeedUserAsync(string role)
    {
        var name = "u_" + Guid.NewGuid().ToString("N")[..12];
        await SeedNamedUserAsync(role, name);
        return await UserIdOfAsync(name);
    }

    private async Task<string> SeedNamedUserAsync(string role, string? name = null)
    {
        name ??= "u_" + Guid.NewGuid().ToString("N")[..12];
        await using var cmd = _fx.DataSource.CreateCommand(
            "INSERT INTO users (username, password_hash, role, enabled) VALUES (@u, 'seed', @r, true)");
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, name);
        cmd.Parameters.AddWithValue("r", NpgsqlDbType.Varchar, role);
        await cmd.ExecuteNonQueryAsync();
        return name;
    }

    private async Task<long> UserIdOfAsync(string username)
    {
        await using var cmd = _fx.DataSource.CreateCommand("SELECT id FROM users WHERE username=@u");
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, username);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>把库里所有 enabled admin 停用，为"最后一名管理员"场景清场（各测试自种用户，互不依赖既有账号）。</summary>
    private async Task DisableAllAdminsAsync()
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "UPDATE users SET enabled=false WHERE role='admin' AND enabled=true");
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string sql, long p0)
    {
        await using var cmd = _fx.DataSource.CreateCommand(sql.Replace("@p0", "@arg"));
        cmd.Parameters.AddWithValue("arg", NpgsqlDbType.Bigint, p0);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> CountUsersNamedAsync(string username)
    {
        await using var cmd = _fx.DataSource.CreateCommand("SELECT count(*) FROM users WHERE username=@u");
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, username);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
