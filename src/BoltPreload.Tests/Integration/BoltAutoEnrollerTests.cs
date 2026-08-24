using BoltPreload.Api.Runtime;
using BoltPreload.Application.Ingestion;
using BoltPreload.Infrastructure.Ingestion;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 检测即螺栓：<see cref="BoltAutoEnroller"/> 集成测试（真实 Postgres）。
/// 锁定：在位未绑定 → 自动建档 + 暂存回填 + 幂等；无归属组跳过；探头不在位不建。
/// </summary>
[Collection("postgres")]
public sealed class BoltAutoEnrollerTests
{
    private readonly PostgresFixture _fx;
    public BoltAutoEnrollerTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task TryEnroll_CreatesBolt_Backfills_AndIsIdempotent()
    {
        _fx.EnsureAvailable();
        var s = await SeedAsync(unboundRows: 2, setDefaultGroup: true, hardwareState: "healthy");
        var enroller = MakeEnroller();

        var first = await enroller.TryEnrollAsync(s.DeviceId, s.ChannelNo, CancellationToken.None);
        first.Should().NotBeNull();
        first!.Code.Should().Be("通道1-7号螺栓", "缺省编号按 10 口一束的中文命名（CH7 → 通道1-7号）");

        // 暂存回填
        (await ScalarAsync<long>("SELECT count(*) FROM unbound_measurements WHERE channel_id = @p0", s.ChannelId))
            .Should().Be(0);
        (await ScalarAsync<long>(
            "SELECT count(*) FROM measurements WHERE bolt_id = @p0 AND algo_version = 'phase1:raw:backfilled'", first.BoltId))
            .Should().Be(2);
        (await ScalarAsync<string>("SELECT business_state FROM channels WHERE id = @p0", s.ChannelId))
            .Should().Be("bound");
        (await ScalarAsync<long>(
            "SELECT count(*) FROM channel_binding_history WHERE bolt_id = @p0 AND action = 'bind'", first.BoltId))
            .Should().Be(1);
        (await ScalarAsync<long>(
            "SELECT count(*) FROM audit_log WHERE target_id = @p0 AND action = 'create_bolt' AND note LIKE '%系统自动建档%'",
            first.BoltId))
            .Should().Be(1);

        // 幂等：已有螺栓再调 → null，不重复建档
        var second = await enroller.TryEnrollAsync(s.DeviceId, s.ChannelNo, CancellationToken.None);
        second.Should().BeNull();
        (await ScalarAsync<long>(
            "SELECT count(*) FROM bolts WHERE channel_id = @p0 AND deleted_at IS NULL", s.ChannelId))
            .Should().Be(1);
    }

    [PostgresFact]
    public async Task TryEnroll_SkipsWhenNoDefaultGroupAndMultipleGroups()
    {
        _fx.EnsureAvailable();
        // 同项目两个活跃组、设备未设默认组 → 不猜，跳过
        var s = await SeedAsync(setDefaultGroup: false, hardwareState: "healthy", extraGroup: true);
        var enroller = MakeEnroller();

        var enrolled = await enroller.TryEnrollAsync(s.DeviceId, s.ChannelNo, CancellationToken.None);
        enrolled.Should().BeNull();
        (await ScalarAsync<long>(
            "SELECT count(*) FROM bolts WHERE channel_id = @p0 AND deleted_at IS NULL", s.ChannelId))
            .Should().Be(0);
    }

    [PostgresFact]
    public async Task TryEnroll_RejectsWhenSnrBelowGate_AllowsWhenGoodOrNoEvidence()
    {
        _fx.EnsureAvailable();

        // 低 SNR（6dB × 5 条，样本 ≥3）→ 拒绝建档：有信号 ≠ 有螺栓（08-24 CH7/CH10 串扰杂波实证）
        var low = await SeedAsync(setDefaultGroup: true, hardwareState: "healthy");
        await SeedWaveSnrAsync(low.ChannelId, snrDb: 6.0m, rows: 5);
        var enroller = MakeEnroller();
        (await enroller.TryEnrollAsync(low.DeviceId, low.ChannelNo, CancellationToken.None))
            .Should().BeNull("近 10 分钟 SNR 均值 6dB < 12dB 门，贴噪声的杂波不建档");
        (await ScalarAsync<long>(
            "SELECT count(*) FROM bolts WHERE channel_id = @p0 AND deleted_at IS NULL", low.ChannelId))
            .Should().Be(0);

        // SNR 达标 → 正常建档
        var good = await SeedAsync(setDefaultGroup: true, hardwareState: "healthy");
        await SeedWaveSnrAsync(good.ChannelId, snrDb: 30.0m, rows: 5);
        (await enroller.TryEnrollAsync(good.DeviceId, good.ChannelNo, CancellationToken.None))
            .Should().NotBeNull("SNR 30dB 达标，正常建档");

        // 查无 SNR 证据（无波形固件）→ 不拦（门只在有证据时说话）
        var noEvidence = await SeedAsync(setDefaultGroup: true, hardwareState: "healthy");
        (await enroller.TryEnrollAsync(noEvidence.DeviceId, noEvidence.ChannelNo, CancellationToken.None))
            .Should().NotBeNull("无 SNR 数据不拦，兼容无波形链路");

        // 样本不足（2 条 < 3）→ 不拦
        var few = await SeedAsync(setDefaultGroup: true, hardwareState: "healthy");
        await SeedWaveSnrAsync(few.ChannelId, snrDb: 5.0m, rows: 2);
        (await enroller.TryEnrollAsync(few.DeviceId, few.ChannelNo, CancellationToken.None))
            .Should().NotBeNull("样本不足不定罪");

        // 门关闭（≤0）→ 低 SNR 也放行
        var gateOff = await SeedAsync(setDefaultGroup: true, hardwareState: "healthy");
        await SeedWaveSnrAsync(gateOff.ChannelId, snrDb: 5.0m, rows: 5);
        var offEnroller = MakeEnroller(new ChannelPresenceOptions { AutoEnrollMinSnrDb = 0 });
        (await offEnroller.TryEnrollAsync(gateOff.DeviceId, gateOff.ChannelNo, CancellationToken.None))
            .Should().NotBeNull("门关闭时保持旧行为");

        // 耦合渐进改善：先 5 条低（按压前）再 5 条高（按压后）——证据取【最近 5 条】而非窗口全量均值，
        // 耦合达标后立即放行，不被历史低 SNR 样本拖累（"插了没反应"的根治点）
        var improved = await SeedAsync(setDefaultGroup: true, hardwareState: "healthy");
        await SeedWaveSnrAsync(improved.ChannelId, snrDb: 6.0m, rows: 5, ageOffsetSeconds: 120);
        await SeedWaveSnrAsync(improved.ChannelId, snrDb: 28.0m, rows: 5);
        (await enroller.TryEnrollAsync(improved.DeviceId, improved.ChannelNo, CancellationToken.None))
            .Should().NotBeNull("最近 5 条已达标（28dB），历史低 SNR 不再拖累");
    }

    [PostgresFact]
    public async Task TryEnroll_SkipsWhenChannelNotPresent()
    {
        _fx.EnsureAvailable();
        var s = await SeedAsync(setDefaultGroup: true, hardwareState: "no_data");
        var enroller = MakeEnroller();

        var enrolled = await enroller.TryEnrollAsync(s.DeviceId, s.ChannelNo, CancellationToken.None);
        enrolled.Should().BeNull("探头不在位不建档");
        (await ScalarAsync<long>(
            "SELECT count(*) FROM bolts WHERE channel_id = @p0 AND deleted_at IS NULL", s.ChannelId))
            .Should().Be(0);
    }

    [PostgresFact]
    public async Task TryEnroll_FallsBackToSoleActiveGroup()
    {
        _fx.EnsureAvailable();
        // 未设默认组，但设备同项目恰好一个活跃组 → 兜底建档（其它项目的组不影响）
        var s = await SeedAsync(setDefaultGroup: false, hardwareState: "healthy", extraGroup: false,
            groupTargetPreloadN: 200_000m);
        var enroller = MakeEnroller();

        var enrolled = await enroller.TryEnrollAsync(s.DeviceId, s.ChannelNo, CancellationToken.None);
        enrolled.Should().NotBeNull("同项目唯一活跃组兜底");
        enrolled!.Code.Should().Be("通道1-7号螺栓");
        (await ScalarAsync<decimal?>("SELECT target_preload_n FROM bolts WHERE id = @p0", enrolled.BoltId))
            .Should().Be(200_000m, "兜底组同样要把组目标继承下来");
    }

    [PostgresFact]
    public async Task TryEnroll_InheritsGroupTargetPreload()
    {
        _fx.EnsureAvailable();
        // 组配了设计目标就带上：留空的话首次扭矩法标定会拿"当次扭矩的估算值"兜底填成目标
        // （CalibrationEndpoints 的 COALESCE），各通道扭矩不同就得到一组互不相同的假目标。
        var s = await SeedAsync(setDefaultGroup: true, hardwareState: "healthy", groupTargetPreloadN: 200_000m);
        var enrolled = await MakeEnroller().TryEnrollAsync(s.DeviceId, s.ChannelNo, CancellationToken.None);

        enrolled.Should().NotBeNull();
        (await ScalarAsync<decimal?>("SELECT target_preload_n FROM bolts WHERE id = @p0", enrolled!.BoltId))
            .Should().Be(200_000m);
        (await ScalarAsync<long>(
            "SELECT count(*) FROM audit_log WHERE target_id = @p0 AND action = 'create_bolt' AND note LIKE '%继承监测组 200 kN%'",
            enrolled.BoltId))
            .Should().Be(1, "继承来源要能从审计里查到");
    }

    [PostgresFact]
    public async Task TryEnroll_LeavesTargetNullWhenGroupHasNoTarget()
    {
        _fx.EnsureAvailable();
        // 组没配目标仍留空（沿用旧行为：等现场补规格），不能拿 0 之类的默认值糊上去
        var s = await SeedAsync(setDefaultGroup: true, hardwareState: "healthy");
        var enrolled = await MakeEnroller().TryEnrollAsync(s.DeviceId, s.ChannelNo, CancellationToken.None);

        enrolled.Should().NotBeNull();
        (await ScalarAsync<decimal?>("SELECT target_preload_n FROM bolts WHERE id = @p0", enrolled!.BoltId))
            .Should().BeNull();
    }

    [PostgresFact]
    public async Task TryEnroll_SkipsWhenHold_ClearsOnAbsent_ThenReEnrolls()
    {
        _fx.EnsureAvailable();
        var s = await SeedAsync(setDefaultGroup: true, hardwareState: "healthy");
        var enroller = MakeEnroller();

        // 模拟人工删除且仍在位：置 hold（重启也不清，故再次 TryEnroll 仍跳过）
        await ExecAsync("UPDATE channels SET auto_enroll_hold = true WHERE id = @p0", s.ChannelId);
        (await enroller.TryEnrollAsync(s.DeviceId, s.ChannelNo, CancellationToken.None))
            .Should().BeNull("hold 时跳过建档");
        (await enroller.TryEnrollAsync(s.DeviceId, s.ChannelNo, CancellationToken.None))
            .Should().BeNull("重启场景：hold 仍在则继续跳过");

        // Present=false 清 hold（与 ChannelPresenceService.PersistAsync 同式）
        await using (var cmd = _fx.DataSource.CreateCommand(@"
            UPDATE channels
               SET auto_enroll_hold = CASE WHEN @present THEN auto_enroll_hold ELSE false END,
                   hardware_state = CASE WHEN @present THEN hardware_state ELSE 'no_data' END
             WHERE id = @id"))
        {
            cmd.Parameters.AddWithValue("present", NpgsqlDbType.Boolean, false);
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, s.ChannelId);
            await cmd.ExecuteNonQueryAsync();
        }
        (await ScalarAsync<bool>("SELECT auto_enroll_hold FROM channels WHERE id = @p0", s.ChannelId))
            .Should().BeFalse();

        // 再插入：恢复在位后可建档
        await using (var cmd = _fx.DataSource.CreateCommand(
            "UPDATE channels SET hardware_state = 'healthy' WHERE id = @id"))
        {
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, s.ChannelId);
            await cmd.ExecuteNonQueryAsync();
        }
        var enrolled = await enroller.TryEnrollAsync(s.DeviceId, s.ChannelNo, CancellationToken.None);
        enrolled.Should().NotBeNull("离位清 hold 后再在位应建档");
        enrolled!.Code.Should().Be("通道1-7号螺栓");
    }

    [PostgresFact]
    public async Task TryEnroll_SkipsWhenDefaultGroupSoftDeleted_HardDeleteClearsFk()
    {
        _fx.EnsureAvailable();
        // 两组额外活跃组：硬删默认组后仍不唯一，禁止兜底乱猜
        var s = await SeedAsync(setDefaultGroup: true, hardwareState: "healthy", extraGroup: true, extraGroupCount: 2);
        var enroller = MakeEnroller();

        await ExecAsync("UPDATE monitor_groups SET deleted_at = now() WHERE id = @p0", s.GroupId);
        (await enroller.TryEnrollAsync(s.DeviceId, s.ChannelNo, CancellationToken.None))
            .Should().BeNull("默认组软删后跳过建档");
        (await ScalarAsync<long?>("SELECT default_group_id FROM devices WHERE id = @p0", s.DeviceId))
            .Should().Be(s.GroupId, "软删不触发 FK；脏引用由 enroller 拒绝");

        // 硬删组 → ON DELETE SET NULL（组上无活跃螺栓时可行）
        await ExecAsync("DELETE FROM monitor_groups WHERE id = @p0", s.GroupId);
        (await ScalarAsync<long?>("SELECT default_group_id FROM devices WHERE id = @p0", s.DeviceId))
            .Should().BeNull("硬删监测组后 default_group_id 置 NULL");
        (await enroller.TryEnrollAsync(s.DeviceId, s.ChannelNo, CancellationToken.None))
            .Should().BeNull("无默认组且多组时仍不猜");
    }

    // ===================== helpers =====================

    private BoltAutoEnroller MakeEnroller(ChannelPresenceOptions? options = null)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CachedChannelResolver(_fx.DataSource, cache, NullLogger<CachedChannelResolver>.Instance);
        return new BoltAutoEnroller(_fx.DataSource, resolver,
            Options.Create(options ?? new ChannelPresenceOptions()), NullLogger<BoltAutoEnroller>.Instance);
    }

    private sealed record Seed(long GroupId, long DeviceId, long ChannelId, ushort ChannelNo);

    private async Task<Seed> SeedAsync(
        int unboundRows = 0,
        bool setDefaultGroup = true,
        string hardwareState = "healthy",
        bool extraGroup = false,
        int extraGroupCount = 1,
        decimal? groupTargetPreloadN = null)
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        long projectId, deviceId, groupId, channelId;
        const short channelNo = 7;
        var groupCode = $"G-{tag}";

        await using (var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO projects (code, name, industry, sla_level) VALUES (@c, 'auto-enroll', '风电', 'standard') RETURNING id"))
        {
            cmd.Parameters.AddWithValue("c", NpgsqlDbType.Varchar, $"P-{tag}");
            projectId = (long)(await cmd.ExecuteScalarAsync())!;
        }
        await using (var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO devices (project_id, code, model, comm_type, comm_params, address, transfer_mode, channel_count, state)
            VALUES (@p, @c, 3, 1, 'in-process', 1, 1, 48, 'online') RETURNING id"))
        {
            cmd.Parameters.AddWithValue("p", NpgsqlDbType.Bigint, projectId);
            cmd.Parameters.AddWithValue("c", NpgsqlDbType.Varchar, $"D-{tag}");
            deviceId = (long)(await cmd.ExecuteScalarAsync())!;
        }
        await using (var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO monitor_groups (project_id, code, name, structure_type, expected_bolt_count, target_preload_n, enabled)
            VALUES (@p, @c, '法兰', 'flange', 48, @target, true) RETURNING id"))
        {
            cmd.Parameters.AddWithValue("p", NpgsqlDbType.Bigint, projectId);
            cmd.Parameters.AddWithValue("c", NpgsqlDbType.Varchar, groupCode);
            cmd.Parameters.AddWithValue("target", NpgsqlDbType.Numeric, (object?)groupTargetPreloadN ?? DBNull.Value);
            groupId = (long)(await cmd.ExecuteScalarAsync())!;
        }
        if (extraGroup)
        {
            for (var i = 2; i < 2 + Math.Max(1, extraGroupCount); i++)
            {
                await using var cmd = _fx.DataSource.CreateCommand(@"
                    INSERT INTO monitor_groups (project_id, code, name, structure_type, enabled)
                    VALUES (@p, @c, '另一组', 'flange', true)");
                cmd.Parameters.AddWithValue("p", NpgsqlDbType.Bigint, projectId);
                cmd.Parameters.AddWithValue("c", NpgsqlDbType.Varchar, $"G{i}-{tag}");
                await cmd.ExecuteNonQueryAsync();
            }
        }
        if (setDefaultGroup)
        {
            await using var cmd = _fx.DataSource.CreateCommand(
                "UPDATE devices SET default_group_id = @g WHERE id = @d");
            cmd.Parameters.AddWithValue("g", NpgsqlDbType.Bigint, groupId);
            cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO channels (device_id, channel_no, state, hardware_state, business_state, probe_freq_mhz, gain, meas_mode, tc_mode)
            VALUES (@d, @ch, 'auto_discovered', @hw, 'unbound', 2.0, 16, 2, 1) RETURNING id"))
        {
            cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
            cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Smallint, channelNo);
            cmd.Parameters.AddWithValue("hw", NpgsqlDbType.Varchar, hardwareState);
            channelId = (long)(await cmd.ExecuteScalarAsync())!;
        }
        if (unboundRows > 0)
        {
            await using var cmd = _fx.DataSource.CreateCommand(@"
                INSERT INTO unbound_measurements (device_id, channel_id, channel_no, ts, echo_time_ns, sp_temp_c, gain, status_code)
                SELECT @d, @c, @no, now() - (i || ' seconds')::interval, 32000 + i, 20.5, 16, 0
                FROM generate_series(1, @n) i");
            cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
            cmd.Parameters.AddWithValue("c", NpgsqlDbType.Bigint, channelId);
            cmd.Parameters.AddWithValue("no", NpgsqlDbType.Smallint, channelNo);
            cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, unboundRows);
            await cmd.ExecuteNonQueryAsync();
        }

        return new Seed(groupId, deviceId, channelId, (ushort)channelNo);
    }

    private async Task ExecAsync(string sql, long p0)
    {
        await using var cmd = _fx.DataSource.CreateCommand(sql.Replace("@p0", "@arg"));
        cmd.Parameters.AddWithValue("arg", NpgsqlDbType.Bigint, p0);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>铺近期波形 SNR 证据（SNR 门用：门只看近 10 分钟内【最近 5 条】的 snr_db 均值）。</summary>
    private async Task SeedWaveSnrAsync(long channelId, decimal snrDb, int rows, int ageOffsetSeconds = 0)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO wave_features (channel_id, ts, wave_blob_url, point_count, snr_db)
            SELECT @c, now() - ((@off + i) || ' seconds')::interval, 'test://wave', 500, @snr
            FROM generate_series(1, @n) i");
        cmd.Parameters.AddWithValue("c", NpgsqlDbType.Bigint, channelId);
        cmd.Parameters.AddWithValue("off", NpgsqlDbType.Integer, ageOffsetSeconds);
        cmd.Parameters.AddWithValue("snr", NpgsqlDbType.Numeric, snrDb);
        cmd.Parameters.AddWithValue("n", NpgsqlDbType.Integer, rows);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T?> ScalarAsync<T>(string sql, long p0)
    {
        await using var cmd = _fx.DataSource.CreateCommand(sql.Replace("@p0", "@arg"));
        cmd.Parameters.AddWithValue("arg", NpgsqlDbType.Bigint, p0);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }
}
