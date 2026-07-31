using BoltPreload.Api.Runtime;
using BoltPreload.Infrastructure.Ingestion;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
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
            "SELECT count(*) FROM audit_log WHERE target_id = @p0 AND action = 'create_bolt' AND note LIKE '%system:auto-enroll%'",
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
        var s = await SeedAsync(setDefaultGroup: false, hardwareState: "healthy", extraGroup: false);
        var enroller = MakeEnroller();

        var enrolled = await enroller.TryEnrollAsync(s.DeviceId, s.ChannelNo, CancellationToken.None);
        enrolled.Should().NotBeNull("同项目唯一活跃组兜底");
        enrolled!.Code.Should().Be("通道1-7号螺栓");
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

    private BoltAutoEnroller MakeEnroller()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = new CachedChannelResolver(_fx.DataSource, cache, NullLogger<CachedChannelResolver>.Instance);
        return new BoltAutoEnroller(_fx.DataSource, resolver, NullLogger<BoltAutoEnroller>.Instance);
    }

    private sealed record Seed(long GroupId, string GroupCode, long DeviceId, long ChannelId, ushort ChannelNo);

    private async Task<Seed> SeedAsync(
        int unboundRows = 0,
        bool setDefaultGroup = true,
        string hardwareState = "healthy",
        bool extraGroup = false,
        int extraGroupCount = 1)
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
            INSERT INTO monitor_groups (project_id, code, name, structure_type, expected_bolt_count, enabled)
            VALUES (@p, @c, '法兰', 'flange', 48, true) RETURNING id"))
        {
            cmd.Parameters.AddWithValue("p", NpgsqlDbType.Bigint, projectId);
            cmd.Parameters.AddWithValue("c", NpgsqlDbType.Varchar, groupCode);
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

        return new Seed(groupId, groupCode, deviceId, channelId, (ushort)channelNo);
    }

    private async Task ExecAsync(string sql, long p0)
    {
        await using var cmd = _fx.DataSource.CreateCommand(sql.Replace("@p0", "@arg"));
        cmd.Parameters.AddWithValue("arg", NpgsqlDbType.Bigint, p0);
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
