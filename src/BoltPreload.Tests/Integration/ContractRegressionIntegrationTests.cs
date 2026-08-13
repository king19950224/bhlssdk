using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 2026-08-13 契约修复批次的回归锁。这三条都是"改回去页面照常渲染、只是数据悄悄变错"的行为，
/// 不钉测试的话没有任何东西拦得住回退：
/// <list type="number">
/// <item>/api/bolts 的 calibrated 口径 = K_L + 生效标定 + 未过期（与 bolt_status CTE / 采集热路径同源）。
/// 旧口径只看 k_l 非空——标定过期不清 k_l，详情页/实时页会继续拿过期 K_L 出数并判"正常"，
/// 与总览"未标定/过期"口径分裂（08-12 事故同链的假安全窗口）。</item>
/// <item>/api/alarms 的 boltId 服务端过滤（螺栓详情履历的数据源）。退回"全站拉 N 条客户端筛"后，
/// 全站告警超窗口时本螺栓早期告警从履历静默消失。</item>
/// <item>总览 preload-health 对未挂机组（monitor_groups.turbine_id IS NULL）的螺栓：
/// 计数与清单必须同现。byGroup/topLoose 用 INNER JOIN turbines 时，这类螺栓计入 global
/// 却从清单消失——结论条报"N 颗松动"、排行找不到那颗。</item>
/// </list>
/// </summary>
[Collection("postgres")]
public class ContractRegressionIntegrationTests
{
    private readonly PostgresFixture _fx;
    public ContractRegressionIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task Bolts_ExpiredActiveCalibration_ReportsUncalibratedWithExpiryFlag()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("标定过期口径");
        var uid = await _fx.SeedUserAsync("reviewer");
        // 生效标定昨天到期：k_l / active_calibration_id 仍挂在通道上（过期不清列是设计内行为）
        await SeedActiveCalibrationAsync(s, uid, expiresOn: DateTime.UtcNow.Date.AddDays(-1));

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, uid, "reviewer");

        var bolt = await client.GetFromJsonAsync<JsonElement>($"/api/bolts/{s.BoltId}");
        bolt.GetProperty("calibrated").GetBoolean().Should().BeFalse(
            "标定已过期就是未标定——只看 k_l 非空的旧口径会让详情页拿过期 K_L 出数并判'正常'");
        bolt.GetProperty("calibrationExpired").GetBoolean().Should().BeTrue(
            "前端要把'过期'与'从未标定'分开显示（横幅引导重新标定）");
        bolt.GetProperty("calibrationExpiresAt").GetString().Should().NotBeNullOrEmpty("过期日期要能显示在横幅里");
        bolt.GetProperty("kL").ValueKind.Should().Be(JsonValueKind.Number,
            "K_L 本身保留（客户端不得用它换算，但重标定参照需要旧值）");

        // 列表端与单查同投影，同一口径必须两处一致
        var list = await client.GetFromJsonAsync<JsonElement>("/api/bolts");
        var row = list.GetProperty("items").EnumerateArray()
            .Single(b => b.GetProperty("id").GetInt64() == s.BoltId);
        row.GetProperty("calibrated").GetBoolean().Should().BeFalse();
        row.GetProperty("calibrationExpired").GetBoolean().Should().BeTrue();
    }

    [PostgresFact]
    public async Task Bolts_UnexpiredActiveCalibration_ReportsCalibrated()
    {
        _fx.EnsureAvailable();
        var s = await _fx.SeedCalibratableChannelAsync("标定有效口径");
        var uid = await _fx.SeedUserAsync("reviewer");
        await SeedActiveCalibrationAsync(s, uid, expiresOn: DateTime.UtcNow.Date.AddDays(30));

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, uid, "reviewer");

        var bolt = await client.GetFromJsonAsync<JsonElement>($"/api/bolts/{s.BoltId}");
        bolt.GetProperty("calibrated").GetBoolean().Should().BeTrue("有生效且未过期的标定");
        bolt.GetProperty("calibrationExpired").GetBoolean().Should().BeFalse();
        // 客户端实时换算与热路径同口径所需的字段必须随行下发
        bolt.GetProperty("deviceId").GetInt64().Should().Be(s.DeviceId, "多设备页面按 (deviceId, channelNo) 隔离通道");
        bolt.TryGetProperty("kLInterceptNs", out _).Should().BeTrue();
        bolt.GetProperty("tempCoefficientPerC").ValueKind.Should().Be(JsonValueKind.Number,
            "温补系数缺省时后端要回退 SDK 默认值，不能发 null 让前端各自兜底");
    }

    [PostgresFact]
    public async Task Alarms_BoltIdFilter_ReturnsOnlyThatBolt()
    {
        _fx.EnsureAvailable();
        var a = await _fx.SeedCalibratableChannelAsync("告警过滤-A");
        var b = await _fx.SeedCalibratableChannelAsync("告警过滤-B");
        await SeedAlarmAsync(a, "preload_low");
        await SeedAlarmAsync(b, "preload_low");

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, await _fx.SeedUserAsync("operator"), "operator");

        var res = await client.GetFromJsonAsync<JsonElement>(
            $"/api/alarms/?includeCleared=true&boltId={a.BoltId}&limit=200");
        var items = res.GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty("刚插的告警必须能按螺栓查回来");
        items.Should().OnlyContain(x => x.GetProperty("boltId").GetInt64() == a.BoltId,
            "boltId 过滤是履历页数据源：混入别的螺栓（或共享库里其他用例的告警）就退化回全站窗口");
    }

    [PostgresFact]
    public async Task PreloadHealth_GroupWithoutTurbine_AppearsInGroupAndLooseLists()
    {
        _fx.EnsureAvailable();
        var groupName = "未挂机组-" + Guid.NewGuid().ToString("N")[..6];
        // SeedCalibratableChannelAsync 的监测组不挂 turbine（turbine_id IS NULL）——正是被测形态
        var s = await _fx.SeedCalibratableChannelAsync(groupName);
        var uid = await _fx.SeedUserAsync("operator");
        await SeedActiveCalibrationAsync(s, uid, expiresOn: DateTime.UtcNow.Date.AddDays(30));
        // 压到远低于下限：ratio 0.011 让它按 preload_ratio ASC 排进 topLoose 前列（LIMIT 8）
        await _fx.ScalarAsync<long>("UPDATE bolts SET min_preload_n = 80000 WHERE id = @arg RETURNING id", s.BoltId);
        await SeedMeasurementAsync(s, preloadN: 1100m, preloadRatio: 0.011m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, uid, "operator");

        var res = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/preload-health");

        var byGroup = res.GetProperty("byGroup").EnumerateArray()
            .SingleOrDefault(g => g.GetProperty("group").GetString() == groupName);
        byGroup.ValueKind.Should().Be(JsonValueKind.Object,
            "未挂机组的组必须出现在 byGroup——INNER JOIN turbines 会让它整组消失（计数有、清单无）");
        byGroup.GetProperty("turbine").GetString().Should().Be("—", "无机组时占位显示，不能因 NULL 丢行");
        byGroup.GetProperty("health").GetProperty("loose").GetInt64().Should().BeGreaterThanOrEqualTo(1);

        var bolt = await client.GetFromJsonAsync<JsonElement>($"/api/bolts/{s.BoltId}");
        var code = bolt.GetProperty("code").GetString();
        res.GetProperty("topLoose").EnumerateArray()
            .Should().Contain(x => x.GetProperty("boltCode").GetString() == code,
                "松动排行必须能定位到这颗螺栓，否则结论条报'N 颗松动'值班却找不到是哪颗");
    }

    // ---- helpers ----

    /// <summary>直插一条生效标定并挂到通道（列清单与 20260813 运维清理脚本同源，NOT NULL 列全覆盖）。</summary>
    private async Task SeedActiveCalibrationAsync(
        IntegrationSeed.CalibratableChannel s, long userId, DateTime expiresOn)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            WITH cal AS (
                INSERT INTO calibration_records (
                    bolt_id, method, operator_user_id, reviewer_user_id,
                    points_jsonb, k_l, k_l_uncertainty, r_squared, outliers_removed,
                    fitter_version, ref_time_ns, ref_temp_c, passed, review_state,
                    operator_signature_at, reviewer_signature_at, expires_at
                ) VALUES (
                    @bolt, 'quick', @uid, @uid,
                    '[]'::jsonb, 0.0005, 0.00015, 0, 0,
                    'test:v1', 27200, 22, true, 'self_signed',
                    now(), now(), @expires
                ) RETURNING id
            )
            UPDATE channels c SET k_l = 0.0005, active_calibration_id = cal.id, updated_at = now()
            FROM cal WHERE c.id = @ch");
        cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, s.BoltId);
        cmd.Parameters.AddWithValue("uid", NpgsqlDbType.Bigint, userId);
        cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Bigint, s.ChannelId);
        cmd.Parameters.AddWithValue("expires", NpgsqlDbType.Date, expiresOn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedAlarmAsync(IntegrationSeed.CalibratableChannel s, string type)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO alarms (bolt_id, channel_id, type, severity, triggered_at, message)
            VALUES (@bolt, @ch, @type, 'warn', now(), '回归用例')");
        cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, s.BoltId);
        cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Bigint, s.ChannelId);
        cmd.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, type);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedMeasurementAsync(
        IntegrationSeed.CalibratableChannel s, decimal preloadN, decimal preloadRatio)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO measurements (bolt_id, channel_id, ts, ingested_at, echo_time_ns, sp_temp_c,
                                      gain, status_code, preload_n, preload_ratio, algo_version)
            VALUES (@bolt, @ch, now(), now(), 27201, 22, 16, 0, @n, @r, 'test')");
        cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, s.BoltId);
        cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Bigint, s.ChannelId);
        cmd.Parameters.AddWithValue("n", NpgsqlDbType.Numeric, preloadN);
        cmd.Parameters.AddWithValue("r", NpgsqlDbType.Numeric, preloadRatio);
        await cmd.ExecuteNonQueryAsync();
    }
}
