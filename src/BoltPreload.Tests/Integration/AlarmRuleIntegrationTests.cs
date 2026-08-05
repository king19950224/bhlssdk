using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoltPreload.Application.Alarm;
using BoltPreload.Domain.Entities;
using BoltPreload.Infrastructure.Alarm;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 告警阈值进库（alarm_rules）的集成测试。锁定的行为：
/// 1) 三级解析顺序 bolt &gt; group &gt; global &gt; appsettings 兜底（PostgresAlarmRuleProvider）；
/// 2) 评估器按 boltId 取阈值：bolt 级规则改 TriggerConsecutive 后触发节奏跟着变；
/// 3) 两步异人复核：提案人自批被后端拒（不只前端禁按钮）、跨人批准即生效、旧 active 被 supersede；
/// 4) 参数链校验：回差配反 API 层 400，绕过 API 直插被 DB CHECK 拦；
/// 5) 提案人自驳 = 撤回，允许；同目标重复 pending 提案 409。
/// 每个用例开头清空 alarm_rules，保证用例间无顺序依赖（夹具库整个集合共享）。
/// </summary>
[Collection("postgres")]
public sealed class AlarmRuleIntegrationTests
{
    private readonly PostgresFixture _fx;
    public AlarmRuleIntegrationTests(PostgresFixture fx) => _fx = fx;

    // ===================== 三级解析（provider 直测） =====================

    [PostgresFact]
    public async Task Provider_ResolvesBoltOverGroupOverGlobal_FactoryDefaultWhenEmpty()
    {
        _fx.EnsureAvailable();
        await ClearRulesAsync();

        var userId = await SeedUserAsync();
        var (projectId, deviceId) = await SeedProjectDeviceAsync();
        var g1 = await SeedGroupAsync(projectId);
        var g2 = await SeedGroupAsync(projectId);
        var b1 = await SeedBoltAsync(deviceId, g1, channelNo: 1);
        var b2 = await SeedBoltAsync(deviceId, g1, channelNo: 2);
        var b3 = await SeedBoltAsync(deviceId, g2, channelNo: 3);

        var provider = NewProvider();

        // 空表：全部落 appsettings 出厂兜底（0.70）
        await provider.RefreshAsync();
        provider.Resolve(b1).TriggerLowRatio.Should().Be(0.70m, "无任何规则时行为与阈值进库前相同");

        await InsertActiveRuleAsync("global", null, null, triggerLow: 0.60m, userId);
        await InsertActiveRuleAsync("group", g1, null, triggerLow: 0.65m, userId);
        await InsertActiveRuleAsync("bolt", null, b1, triggerLow: 0.68m, userId);
        await provider.RefreshAsync();

        provider.Resolve(b1).TriggerLowRatio.Should().Be(0.68m, "bolt 级覆盖压过 group 级");
        provider.Resolve(b2).TriggerLowRatio.Should().Be(0.65m, "同组无 bolt 级覆盖 → group 级");
        provider.Resolve(b3).TriggerLowRatio.Should().Be(0.60m, "另一组无覆盖 → global");
        provider.Resolve(999_999).TriggerLowRatio.Should().Be(0.60m, "查不到 bolt→group 映射（如刚建档）→ global");
    }

    [PostgresFact]
    public async Task Evaluator_UsesBoltRule_TriggerConsecutiveFromDb()
    {
        _fx.EnsureAvailable();
        await ClearRulesAsync();

        var userId = await SeedUserAsync();
        var (projectId, deviceId) = await SeedProjectDeviceAsync();
        var g = await SeedGroupAsync(projectId);
        var ruled = await SeedBoltAsync(deviceId, g, channelNo: 4);
        var control = await SeedBoltAsync(deviceId, g, channelNo: 5);

        // ruled 螺栓连击门槛降到 2；control 无规则走出厂默认 5
        await InsertActiveRuleAsync("bolt", null, ruled, triggerLow: 0.70m, userId, triggerConsecutive: 2);
        var provider = NewProvider();
        await provider.RefreshAsync();
        var evaluator = new StaticThresholdAlarmEvaluator(provider, NullLogger<StaticThresholdAlarmEvaluator>.Instance);

        AlarmInput Low(long boltId) => new(boltId, ChannelId: 1, Ts: DateTimeOffset.UtcNow,
            PreloadN: 40_000m, PreloadRatio: 0.50m, TargetPreloadN: 80_000m, SdkStatusCode: 0);

        evaluator.Evaluate(Low(ruled)).Should().BeEmpty("第 1 条未到 bolt 级门槛 2");
        evaluator.Evaluate(Low(ruled)).Should().ContainSingle(e => e.Type == "preload_low")
            .Which.ThresholdLowRatio.Should().Be(0.70m, "第 2 条即触发：连击门槛来自库内 bolt 级规则");

        for (var i = 0; i < 4; i++)
            evaluator.Evaluate(Low(control)).Should().BeEmpty($"第 {i + 1} 条未到出厂门槛 5");
        evaluator.Evaluate(Low(control)).Should().ContainSingle("无规则螺栓维持出厂 TriggerConsecutive=5");
    }

    // ===================== 提案 → 异人批准 → 生效 → 替代 =====================

    [PostgresFact]
    public async Task ProposeApprove_CrossUserEnforced_TakesEffectAndSupersedes()
    {
        _fx.EnsureAvailable();
        await ClearRulesAsync();

        var reviewerA = await SeedUserAsync();
        var reviewerB = await SeedUserAsync();
        var operatorId = await SeedUserAsync();
        var (projectId, deviceId) = await SeedProjectDeviceAsync();
        var g = await SeedGroupAsync(projectId);
        var boltId = await SeedBoltAsync(deviceId, g, channelNo: 6);

        using var factory = TestApi.CreateFactory(_fx);
        using var clientA = TestApi.AuthedClient(factory, reviewerA, "reviewer");
        using var clientB = TestApi.AuthedClient(factory, reviewerB, "reviewer");
        using var clientOp = TestApi.AuthedClient(factory, operatorId, "operator");

        // 无规则时生效链给出厂兜底
        var eff0 = await GetJsonAsync(clientA, $"/api/bolts/{boltId}/effective-alarm-rule");
        eff0.GetProperty("source").GetString().Should().Be("default");

        // operator 无提案权限
        (await clientOp.PostAsJsonAsync("/api/alarm-rules", GlobalRuleBody(0.60m)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "提案是 reviewer+ 权限");

        // A 提案 → A 自批被拒 → B 批准生效
        var propose = await clientA.PostAsJsonAsync("/api/alarm-rules", GlobalRuleBody(0.60m));
        propose.StatusCode.Should().Be(HttpStatusCode.OK);
        var ruleId = (await propose.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        (await clientA.PostAsync($"/api/alarm-rules/{ruleId}/approve", null))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "提案人自批必须被后端拒绝");
        (await clientB.PostAsync($"/api/alarm-rules/{ruleId}/approve", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var eff1 = await GetJsonAsync(clientA, $"/api/bolts/{boltId}/effective-alarm-rule");
        eff1.GetProperty("source").GetString().Should().Be("global");
        eff1.GetProperty("triggerLowRatio").GetDecimal().Should().Be(0.60m);

        // 第二版获批后旧 active 置 superseded，同一目标只留一条生效
        var propose2 = await clientA.PostAsJsonAsync("/api/alarm-rules", GlobalRuleBody(0.58m));
        var ruleId2 = (await propose2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        (await clientB.PostAsync($"/api/alarm-rules/{ruleId2}/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await RuleStateAsync(ruleId)).Should().Be("superseded");
        (await RuleStateAsync(ruleId2)).Should().Be("active");

        // 同目标已有 pending 时再提 → 409
        (await clientA.PostAsJsonAsync("/api/alarm-rules", GlobalRuleBody(0.55m))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await clientA.PostAsJsonAsync("/api/alarm-rules", GlobalRuleBody(0.54m)))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "同目标同时只允许一条待审提案");

        // 全程留审计
        (await AuditCountAsync("alarm_rule_propose")).Should().BeGreaterThanOrEqualTo(3);
        (await AuditCountAsync("alarm_rule_approve")).Should().Be(2);
    }

    [PostgresFact]
    public async Task Reject_SelfWithdrawAllowed_CanProposeAgain()
    {
        _fx.EnsureAvailable();
        await ClearRulesAsync();

        var reviewerA = await SeedUserAsync();
        using var factory = TestApi.CreateFactory(_fx);
        using var clientA = TestApi.AuthedClient(factory, reviewerA, "reviewer");

        var propose = await clientA.PostAsJsonAsync("/api/alarm-rules", GlobalRuleBody(0.62m));
        var ruleId = (await propose.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        // 自驳 = 撤回，允许（与"自批必须异人"不同：撤回不产生生效规则）
        (await clientA.PostAsJsonAsync($"/api/alarm-rules/{ruleId}/reject", new { note = "参数填错，撤回重提" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await RuleStateAsync(ruleId)).Should().Be("rejected");

        // 撤回后同目标可再提案（pending 唯一索引不再挡）
        (await clientA.PostAsJsonAsync("/api/alarm-rules", GlobalRuleBody(0.63m))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===================== 参数链校验：API 层 + DB CHECK 双防线 =====================

    [PostgresFact]
    public async Task Validation_HysteresisDirection_ApiRejects_DbCheckBacksUp()
    {
        _fx.EnsureAvailable();
        await ClearRulesAsync();

        var reviewerA = await SeedUserAsync();
        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, reviewerA, "reviewer");

        // 回差配反：恢复下限 ≤ 触发下限
        var bad = GlobalRuleBody(0.70m) with { ClearLowRatio = 0.70m };
        (await client.PostAsJsonAsync("/api/alarm-rules", bad)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // scope 与目标不成套
        var mismatched = GlobalRuleBody(0.60m) with { Scope = "bolt" };
        (await client.PostAsJsonAsync("/api/alarm-rules", mismatched)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 依据不足 5 字
        var noReason = GlobalRuleBody(0.60m) with { Reason = "改" };
        (await client.PostAsJsonAsync("/api/alarm-rules", noReason)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 绕过 API 直插回差配反的行 → DB CHECK 拦（最后防线）
        await using var insert = _fx.DataSource.CreateCommand(@"
            INSERT INTO alarm_rules
                (scope, trigger_low_ratio, clear_low_ratio, trigger_high_ratio, clear_high_ratio,
                 trigger_consecutive, clear_consecutive, critical_deviation, preload_loss_ratio, reason, created_by)
            VALUES ('global', 0.70, 0.70, 1.10, 1.08, 5, 10, 0.30, 0.05, '回差配反必须被 CHECK 拦', @u)");
        insert.Parameters.AddWithValue("u", NpgsqlDbType.Bigint, reviewerA);
        var act = async () => await insert.ExecuteNonQueryAsync();
        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    // ===================== helpers =====================

    private PostgresAlarmRuleProvider NewProvider() =>
        new(_fx.DataSource,
            Options.Create(new StaticThresholdAlarmOptions()),
            NullLogger<PostgresAlarmRuleProvider>.Instance);

    /// <summary>提案请求体（record 便于 with 改单项造坏参数）。默认一套合法的全站规则。</summary>
    private sealed record RuleBody(
        string Scope, long? GroupId, long? BoltId,
        decimal TriggerLowRatio, decimal ClearLowRatio, decimal TriggerHighRatio, decimal ClearHighRatio,
        int TriggerConsecutive, int ClearConsecutive, decimal CriticalDeviation, decimal PreloadLossRatio,
        string Reason);

    private static RuleBody GlobalRuleBody(decimal triggerLow) => new(
        Scope: "global", GroupId: null, BoltId: null,
        TriggerLowRatio: triggerLow, ClearLowRatio: triggerLow + 0.02m,
        TriggerHighRatio: 1.10m, ClearHighRatio: 1.08m,
        TriggerConsecutive: 5, ClearConsecutive: 10,
        CriticalDeviation: 0.30m, PreloadLossRatio: 0.05m,
        Reason: "集成测试：合同约定阈值调整");

    private async Task ClearRulesAsync()
    {
        await using var cmd = _fx.DataSource.CreateCommand("DELETE FROM alarm_rules");
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertActiveRuleAsync(
        string scope, long? groupId, long? boltId, decimal triggerLow, long userId, int triggerConsecutive = 5)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO alarm_rules
                (scope, group_id, bolt_id, trigger_low_ratio, clear_low_ratio, trigger_high_ratio, clear_high_ratio,
                 trigger_consecutive, clear_consecutive, critical_deviation, preload_loss_ratio,
                 reason, review_state, created_by, approved_by, approved_at)
            VALUES (@scope, @gid, @bid, @tl, @tl + 0.02, 1.10, 1.08, @tc, 10, 0.30, 0.05,
                    '集成测试直插生效规则', 'active', @u, @u, now())");
        cmd.Parameters.AddWithValue("scope", NpgsqlDbType.Varchar, scope);
        cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, (object?)groupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("bid", NpgsqlDbType.Bigint, (object?)boltId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tl", NpgsqlDbType.Numeric, triggerLow);
        cmd.Parameters.AddWithValue("tc", NpgsqlDbType.Integer, triggerConsecutive);
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Bigint, userId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string> RuleStateAsync(long ruleId)
    {
        await using var cmd = _fx.DataSource.CreateCommand("SELECT review_state FROM alarm_rules WHERE id = @id");
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, ruleId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> AuditCountAsync(string action)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "SELECT count(*) FROM audit_log WHERE action = @a AND target_type = 'alarm_rule'");
        cmd.Parameters.AddWithValue("a", NpgsqlDbType.Varchar, action);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} 应成功");
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<long> SeedUserAsync()
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "INSERT INTO users (username, password_hash, role) VALUES (@u, 'seed', 'reviewer') RETURNING id");
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, "u_" + Guid.NewGuid().ToString("N")[..12]);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(long projectId, long deviceId)> SeedProjectDeviceAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..10];
        await using var db = _fx.NewDbContext();

        var project = new Project { Code = $"P-{tag}", Name = "规则集成", Industry = "风电", SlaLevel = "standard", CreatedAt = now, UpdatedAt = now };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var device = new Device
        {
            ProjectId = project.Id, Code = $"D-{tag}", Model = 3, CommType = 1, CommParams = "in-process",
            Address = 1, TransferMode = 1, ChannelCount = 40, State = "online", CreatedAt = now, UpdatedAt = now,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return (project.Id, device.Id);
    }

    private async Task<long> SeedGroupAsync(long projectId)
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..10];
        await using var db = _fx.NewDbContext();
        var group = new MonitorGroup { ProjectId = projectId, Code = $"G-{tag}", Name = "规则测试组", CreatedAt = now, UpdatedAt = now };
        db.MonitorGroups.Add(group);
        await db.SaveChangesAsync();
        return group.Id;
    }

    private async Task<long> SeedBoltAsync(long deviceId, long groupId, short channelNo)
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..10];
        await using var db = _fx.NewDbContext();

        var channel = new Channel
        {
            DeviceId = deviceId, ChannelNo = channelNo, ProbeFreqMhz = 2.0m, Gain = 16, MeasMode = 2, TcMode = 1,
            TofAlgorithm = "ThrSignal", HardwareState = "healthy", BusinessState = "bound", ConfigSource = "database",
            State = "manual", DiscoveredAt = now, CreatedAt = now, UpdatedAt = now,
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var bolt = new Bolt
        {
            GroupId = groupId, ChannelId = channel.Id, Code = $"B-{tag}", NominalDiam = "M20", Grade = "10.9",
            Rp02Mpa = 940m, Material = "42CrMo", ElasticModulusGpa = 206m, EffectiveLengthMm = 100m, TotalLengthMm = 120m,
            CrossSectionMm2 = 245m, SoundVelocityMs = 5890m, TargetPreloadN = 172_000m, MinPreloadN = 120_000m,
            State = "calibrated", CreatedAt = now, UpdatedAt = now,
        };
        db.Bolts.Add(bolt);
        await db.SaveChangesAsync();
        return bolt.Id;
    }
}
