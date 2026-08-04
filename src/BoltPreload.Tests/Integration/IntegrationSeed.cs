using BoltPreload.Domain.Entities;
using NpgsqlTypes;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 标定/报告类集成测试共用的铺数据与读库辅助。
/// <para>
/// "项目 → 设备 → 通道 → 螺栓"这条链是所有标定用例的前置，各测试类各抄一份的话，
/// schema 一动就要改好几处，且各份的物性参数容易漂移到过不了量级护栏。集中在这里一处维护。
/// </para>
/// </summary>
internal static class IntegrationSeed
{
    /// <summary>一条铺好的可标定链路。</summary>
    internal sealed record CalibratableChannel(long ProjectId, long DeviceId, long GroupId, long ChannelId, long BoltId);

    /// <summary>建一个指定角色的启用账号，返回 user_id。</summary>
    public static async Task<long> SeedUserAsync(this PostgresFixture fx, string role)
    {
        await using var cmd = fx.DataSource.CreateCommand(
            "INSERT INTO users (username, password_hash, role, enabled) VALUES (@u, 'seed', @r, true) RETURNING id");
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, "u_" + Guid.NewGuid().ToString("N")[..12]);
        cmd.Parameters.AddWithValue("r", NpgsqlDbType.Varchar, role);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// 项目 + 设备 + 一路带基线（ref_time/ref_temp）的通道 + 绑定螺栓。
    /// <para>
    /// 螺栓取 M16/8.8（Rp0.2=640 MPa，A_e=157 mm² → 屈服力 100 480 N，目标预紧力 100 kN），
    /// 配 t₀=27 200 ns。这组数使 K_L≈5e-4 ns/N 落在 <c>ValidateKLMagnitude</c> 上下界之间：
    /// 屈服力下 Δt≈50 ns ≪ t₀（不触上界），目标力下 Δt≈50 ns ≫ 1 ns（不触下界）。
    /// 改这里的物性前先算一遍这两个乘积，否则用例会以 400 失败而不是走到被测逻辑。
    /// </para>
    /// </summary>
    public static async Task<CalibratableChannel> SeedCalibratableChannelAsync(this PostgresFixture fx, string groupName)
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..10];
        await using var db = fx.NewDbContext();

        var project = new Project
        {
            Code = $"P-{tag}", Name = groupName, Industry = "风电", SlaLevel = "standard",
            CreatedAt = now, UpdatedAt = now,
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var device = new Device
        {
            ProjectId = project.Id, Code = $"D-{tag}", Model = 3, CommType = 1, CommParams = "in-process",
            Address = 1, TransferMode = 1, ChannelCount = 40, State = "online", CreatedAt = now, UpdatedAt = now,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        var channel = new Channel
        {
            DeviceId = device.Id, ChannelNo = 5, ProbeFreqMhz = 2.0m, Gain = 16, MeasMode = 2, TcMode = 1,
            TofAlgorithm = "ThrSignal", HardwareState = "healthy", BusinessState = "bound", ConfigSource = "database",
            State = "manual", DiscoveredAt = now, CreatedAt = now, UpdatedAt = now,
            // 扭矩阶梯拟合要用通道上的基线；缺 ref_time/ref_temp 会在门槛判定之前就被 400 挡掉
            RefTimeNs = 27_200m, RefTempC = 22m,
        };
        db.Channels.Add(channel);

        var group = new MonitorGroup
        {
            ProjectId = project.Id, Code = $"G-{tag}", Name = groupName,
            StructureType = "flange", ExpectedBoltCount = 8, TargetPreloadN = 100_000, Enabled = true,
            CreatedAt = now, UpdatedAt = now,
        };
        db.MonitorGroups.Add(group);
        await db.SaveChangesAsync();

        var bolt = new Bolt
        {
            GroupId = group.Id, ChannelId = channel.Id, Code = $"B-{tag}",
            NominalDiam = "M16", Grade = "8.8", Rp02Mpa = 640, Material = "42CrMo",
            ElasticModulusGpa = 206, EffectiveLengthMm = 200, TotalLengthMm = 220,
            CrossSectionMm2 = 157, SoundVelocityMs = 5890, TargetPreloadN = 100_000,
            State = "uncalibrated", Enabled = true, GbCompliant = true,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Bolts.Add(bolt);
        await db.SaveChangesAsync();

        return new CalibratableChannel(project.Id, device.Id, group.Id, channel.Id, bolt.Id);
    }

    /// <summary>
    /// 给通道插一组四点扭矩阶梯数据，残差幅度 d 决定拟合的 R²（见 <see cref="CalibrationFitData"/>）。
    /// 扭矩阶梯拟合与扭矩阶梯报告都从这张表取数。
    /// </summary>
    public static async Task SeedTorquePointsAsync(this PostgresFixture fx, CalibratableChannel s, decimal d)
    {
        var deltas = CalibrationFitData.DeltaTofs(d);
        for (var i = 0; i < CalibrationFitData.Forces.Length; i++)
        {
            await using var cmd = fx.DataSource.CreateCommand(@"
                INSERT INTO torque_test_points (
                    bolt_id, channel_id, torque_nm, nut_factor, nominal_diameter_mm,
                    estimated_preload_n, sample_window_seconds, sample_count,
                    window_start, window_end, avg_echo_time_ns, avg_temp_c,
                    ref_time_ns, ref_temp_c, normalized_echo_time_ns, delta_tof_ns
                ) VALUES (
                    @bolt, @ch, @torque, 0.20, 16,
                    @force, 60, 100,
                    now() - interval '1 minute', now(), @echo, 22,
                    27200, 22, @echo, @delta
                )");
            cmd.Parameters.AddWithValue("bolt", NpgsqlDbType.Bigint, s.BoltId);
            cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Bigint, s.ChannelId);
            cmd.Parameters.AddWithValue("torque", NpgsqlDbType.Numeric, CalibrationFitData.Forces[i] / 1000m);
            cmd.Parameters.AddWithValue("force", NpgsqlDbType.Numeric, CalibrationFitData.Forces[i]);
            cmd.Parameters.AddWithValue("echo", NpgsqlDbType.Numeric, 27_200m + deltas[i]);
            cmd.Parameters.AddWithValue("delta", NpgsqlDbType.Numeric, deltas[i]);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>通道当前生效的标定 id（未标定为 null）。</summary>
    public static async Task<Guid?> ActiveCalibrationAsync(this PostgresFixture fx, long channelId)
    {
        await using var cmd = fx.DataSource.CreateCommand(
            "SELECT active_calibration_id FROM channels WHERE id = @cid");
        cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
        return await cmd.ExecuteScalarAsync() is Guid g ? g : null;
    }

    /// <summary>取单值；SQL 里用 <c>@arg</c> 占位。</summary>
    public static async Task<T> ScalarAsync<T>(this PostgresFixture fx, string sql, object arg)
    {
        await using var cmd = fx.DataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("arg", arg);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
