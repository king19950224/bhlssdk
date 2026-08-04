using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoltPreload.Domain.Entities;
using FluentAssertions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 标定 R² 门槛三段判定（apply-torque-fit，门槛值见 <c>CalibrationThresholds</c>）。
/// <list type="bullet">
/// <item>R² ≥ 0.98：通过，激活并顶替旧标定</item>
/// <item>0.95 ≤ R² &lt; 0.98：留档 passed=false，但<b>不激活</b>，通道继续沿用原标定</item>
/// <item>R² &lt; 0.95：拒绝，400，什么都不写</item>
/// </list>
/// <para>
/// 中间那档是最凶险的：判错方向就会把在产的好标定 supersede 掉，而 sink 对 passed=false
/// 又不出预紧力，通道于是从“能出数”变成“无数据”——现场看到的是一个安静的空白，不是报错。
/// 这三段此前一条测试都没有。
/// </para>
/// </summary>
[Collection("postgres")]
public sealed class CalibrationThresholdIntegrationTests
{
    private readonly PostgresFixture _fx;
    public CalibrationThresholdIntegrationTests(PostgresFixture fx) => _fx = fx;

    // 拟合在 SQL 里做：regr_slope(delta_tof_ns, estimated_preload_n)，即斜率就是 K_L = Δt/F。
    // 取 K_L = 5e-4 ns/N（量级 ~1e-4，过得了 ValidateKLMagnitude 的上下界护栏）。
    private const decimal Slope = 0.0005m;
    private static readonly decimal[] Forces = [10_000m, 20_000m, 30_000m, 40_000m];

    /// <summary>
    /// 按对称残差 (+d, −d, −d, +d) 造点：sum(e)=0 且 sum(x·e)=0，所以最小二乘直线原样不变，
    /// 只有 R² 随 d 变化，且可以手算 —— R² = 1 − 4d² / (a²·Sxx + 4d²)，其中 a²·Sxx = 5e-4² × 5e8 = 125。
    /// d=0.5 → 0.9921（通过）；d=1.0 → 0.9690（警告层）；d=1.5 → 0.9328（拒绝）。
    /// </summary>
    private static decimal[] DeltaTofs(decimal d) =>
    [
        Forces[0] * Slope + d,
        Forces[1] * Slope - d,
        Forces[2] * Slope - d,
        Forces[3] * Slope + d,
    ];

    [PostgresFact]
    public async Task ApplyTorqueFit_R2AbovePassLine_ActivatesOnChannel()
    {
        _fx.EnsureAvailable();
        var s = await SeedCalibratableChannelAsync();
        await InsertPointsAsync(s, d: 0.5m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, s.ReviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/apply-torque-fit", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("rSquared").GetDecimal().Should().BeApproximately(0.9921m, 0.001m);
        body.GetProperty("passed").GetBoolean().Should().BeTrue("R² 已过 0.98 合格线");

        var calibId = body.GetProperty("calibrationId").GetGuid();
        (await ActiveCalibrationOfAsync(s.ChannelId)).Should().Be(calibId, "达标的拟合必须激活到通道");
        (await ScalarAsync<bool>("SELECT passed FROM calibration_records WHERE id = @arg", calibId))
            .Should().BeTrue();
    }

    [PostgresFact]
    public async Task ApplyTorqueFit_R2InWarnBand_KeepsRecordButDoesNotSupersedeActive()
    {
        _fx.EnsureAvailable();
        var s = await SeedCalibratableChannelAsync();

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, s.ReviewerId, "reviewer");

        // 先用一组达标数据建立"在产的好标定"
        await InsertPointsAsync(s, d: 0.5m);
        var good = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/apply-torque-fit", new { });
        good.StatusCode.Should().Be(HttpStatusCode.OK);
        var goodId = (await good.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("calibrationId").GetGuid();
        (await ActiveCalibrationOfAsync(s.ChannelId)).Should().Be(goodId);

        // 再补一组落在 [0.95, 0.98) 的数据重拟合
        await ExcludeExistingPointsAsync(s.ChannelId);
        await InsertPointsAsync(s, d: 1.0m);
        var warn = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/apply-torque-fit", new { });

        warn.StatusCode.Should().Be(HttpStatusCode.OK, "警告层是留档，不是报错");
        var body = await warn.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("rSquared").GetDecimal().Should().BeApproximately(0.9690m, 0.001m);
        body.GetProperty("passed").GetBoolean().Should().BeFalse("未达 0.98 合格线");

        var warnId = body.GetProperty("calibrationId").GetGuid();
        warnId.Should().NotBe(goodId);
        (await ScalarAsync<bool>("SELECT passed FROM calibration_records WHERE id = @arg", warnId))
            .Should().BeFalse("留档记录本身要如实标记不合格");

        // 这一条是本测试的要害：劣质拟合绝不能顶替在产的好标定
        (await ActiveCalibrationOfAsync(s.ChannelId)).Should().Be(goodId,
            "未达合格线的拟合只留档不激活，通道必须仍指向原来那份好标定");
        (await ScalarAsync<long>(
            "SELECT count(*) FROM calibration_records WHERE id = @arg AND superseded_by IS NOT NULL", goodId))
            .Should().Be(0, "好标定不该被劣质拟合 supersede");
    }

    [PostgresFact]
    public async Task ApplyTorqueFit_R2BelowRejectLine_Returns400AndWritesNothing()
    {
        _fx.EnsureAvailable();
        var s = await SeedCalibratableChannelAsync();
        await InsertPointsAsync(s, d: 1.5m);

        using var factory = TestApi.CreateFactory(_fx);
        using var client = TestApi.AuthedClient(factory, s.ReviewerId, "reviewer");

        var resp = await client.PostAsJsonAsync($"/api/channels/{s.ChannelId}/apply-torque-fit", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "R² 低于 0.95 拒绝下限");
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("拒绝应用为标定");

        // 拒绝路径整体回滚：既不留档，也不动通道
        (await ScalarAsync<long>(
            "SELECT count(*) FROM calibration_records WHERE bolt_id = @arg", s.BoltId))
            .Should().Be(0, "被拒的拟合不该留下标定档案");
        (await ActiveCalibrationOfAsync(s.ChannelId)).Should().BeNull();
    }

    // ===================== seed =====================

    private sealed record Seed(long ChannelId, long BoltId, long ReviewerId);

    /// <summary>项目 + 设备 + 一路带基线（ref_time/ref_temp）的通道 + 绑定螺栓 + 一名 reviewer。</summary>
    private async Task<Seed> SeedCalibratableChannelAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..10];
        await using var db = _fx.NewDbContext();

        var project = new Project { Code = $"P-{tag}", Name = "标定门槛", Industry = "风电", SlaLevel = "standard", CreatedAt = now, UpdatedAt = now };
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
            // 拟合应用需要基线，缺 ref_time/ref_temp 会在门槛判定之前就被 400 挡掉
            RefTimeNs = 27_200m, RefTempC = 22m,
        };
        db.Channels.Add(channel);

        var group = new MonitorGroup
        {
            ProjectId = project.Id, Code = $"G-{tag}", Name = "标定测试法兰",
            StructureType = "flange", ExpectedBoltCount = 8, TargetPreloadN = 100000, Enabled = true,
            CreatedAt = now, UpdatedAt = now,
        };
        db.MonitorGroups.Add(group);
        await db.SaveChangesAsync();

        var bolt = new Bolt
        {
            GroupId = group.Id, ChannelId = channel.Id, Code = $"B-{tag}",
            NominalDiam = "M16", Grade = "8.8", Rp02Mpa = 640, Material = "42CrMo",
            ElasticModulusGpa = 206, EffectiveLengthMm = 200, TotalLengthMm = 220,
            CrossSectionMm2 = 157, SoundVelocityMs = 5890,
            State = "uncalibrated", Enabled = true, GbCompliant = true,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Bolts.Add(bolt);
        await db.SaveChangesAsync();

        return new Seed(channel.Id, bolt.Id, await SeedReviewerAsync());
    }

    private async Task<long> SeedReviewerAsync()
    {
        var name = "u_" + Guid.NewGuid().ToString("N")[..12];
        await using var cmd = _fx.DataSource.CreateCommand(
            "INSERT INTO users (username, password_hash, role, enabled) VALUES (@u, 'seed', 'reviewer', true) RETURNING id");
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, name);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>按残差幅度 d 插一组四点扭矩阶梯数据。</summary>
    private async Task InsertPointsAsync(Seed s, decimal d)
    {
        var deltas = DeltaTofs(d);
        for (var i = 0; i < Forces.Length; i++)
        {
            await using var cmd = _fx.DataSource.CreateCommand(@"
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
            cmd.Parameters.AddWithValue("torque", NpgsqlDbType.Numeric, Forces[i] / 1000m);
            cmd.Parameters.AddWithValue("force", NpgsqlDbType.Numeric, Forces[i]);
            cmd.Parameters.AddWithValue("echo", NpgsqlDbType.Numeric, 27_200m + deltas[i]);
            cmd.Parameters.AddWithValue("delta", NpgsqlDbType.Numeric, deltas[i]);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>把已有点标成 excluded，让下一次拟合只看新插的那组（拟合只取 excluded=false）。</summary>
    private async Task ExcludeExistingPointsAsync(long channelId)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "UPDATE torque_test_points SET excluded = true WHERE channel_id = @cid");
        cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid?> ActiveCalibrationOfAsync(long channelId)
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "SELECT active_calibration_id FROM channels WHERE id = @cid");
        cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
        var v = await cmd.ExecuteScalarAsync();
        return v is Guid g ? g : null;
    }

    private async Task<T> ScalarAsync<T>(string sql, object arg)
    {
        await using var cmd = _fx.DataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("arg", arg);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
