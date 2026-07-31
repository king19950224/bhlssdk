using BoltPreload.Domain.Entities;
using FluentAssertions;
using NpgsqlTypes;
using Xunit;

namespace BoltPreload.Tests.Integration;

/// <summary>
/// 沉降期窗口起点选取（预测/即将到限的 cal CTE）对真库的集成测试（无 DB 时自动 Skip）。
/// 锁定本会话修复：cal 只取“当前生效”标定（superseded_by IS NULL 且 review_state IN ('approved','self_signed')），
/// 否则被顶替/待复核的记录会把 max(created_at) 错误前移、饿死回归历史。
/// </summary>
[Collection("postgres")]
public sealed class PredictionWindowIntegrationTests
{
    private readonly PostgresFixture _fx;
    public PredictionWindowIntegrationTests(PostgresFixture fx) => _fx = fx;

    [PostgresFact]
    public async Task CalibrationWindow_IgnoresSupersededAndPending()
    {
        _fx.EnsureAvailable();

        var boltId = await SeedBoltAsync();
        var userId = await SeedUserAsync();

        // active（当前生效）：approved + 未被 superseded，created_at = 5 天前
        var activeId = await InsertCalibAsync(boltId, userId, "approved", superseded: null, createdDaysAgo: 5);
        // superseded（被顶替旧档）：approved，但 superseded_by 指向 active，created_at = 20 天前
        await InsertCalibAsync(boltId, userId, "approved", superseded: activeId, createdDaysAgo: 20);
        // pending（待复核，最新）：review_state=pending 且未被 superseded，created_at = 1 天前
        await InsertCalibAsync(boltId, userId, "pending", superseded: null, createdDaysAgo: 1);

        var unfiltered = await MaxCreatedAtAsync(boltId, filtered: false); // 会错误取到 1 天前的 pending
        var filtered = await MaxCreatedAtAsync(boltId, filtered: true);    // 端点口径：取到 5 天前的 active

        // 过滤后的窗口起点应显著早于未过滤（约相差 4 天：active 5 天前 vs pending 1 天前）
        (unfiltered - filtered).Should().BeGreaterThan(TimeSpan.FromDays(3),
            "未过滤会取到最新的 pending，过滤后应回到生效的 active");
    }

    // ===================== helpers =====================

    private async Task<DateTime> MaxCreatedAtAsync(long boltId, bool filtered)
    {
        var where = filtered
            ? "WHERE bolt_id=@b AND superseded_by IS NULL AND review_state IN ('approved','self_signed')"
            : "WHERE bolt_id=@b";
        await using var cmd = _fx.DataSource.CreateCommand($"SELECT max(created_at) FROM calibration_records {where}");
        cmd.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
        var val = await cmd.ExecuteScalarAsync();
        return val is DateTime dt
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            : ((DateTimeOffset)val!).UtcDateTime;
    }

    private async Task<Guid> InsertCalibAsync(long boltId, long userId, string reviewState, Guid? superseded, int createdDaysAgo)
    {
        await using var cmd = _fx.DataSource.CreateCommand(@"
            INSERT INTO calibration_records
              (bolt_id, method, operator_user_id, reviewer_user_id, points_jsonb,
               k_l, k_l_uncertainty, r_squared, outliers_removed, fitter_version,
               ref_time_ns, ref_temp_c, passed, operator_signature_at, reviewer_signature_at,
               expires_at, review_state, superseded_by, created_at)
            VALUES (@b,'quick',@u,@u,'[]'::jsonb,
               0.001,0.00001,0.999,0,'test:1.0',
               30000,20,true,now(),now(),
               (now()+interval '365 days')::date, @rs, @sup, now() - (@days * interval '1 day'))
            RETURNING id");
        cmd.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Bigint, userId);
        cmd.Parameters.AddWithValue("rs", NpgsqlDbType.Varchar, reviewState);
        cmd.Parameters.AddWithValue("sup", NpgsqlDbType.Uuid, (object?)superseded ?? DBNull.Value);
        cmd.Parameters.AddWithValue("days", NpgsqlDbType.Integer, createdDaysAgo);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> SeedUserAsync()
    {
        await using var cmd = _fx.DataSource.CreateCommand(
            "INSERT INTO users (username, password_hash, role) VALUES (@u, 'seed', 'admin') RETURNING id");
        cmd.Parameters.AddWithValue("u", NpgsqlDbType.Varchar, "u_" + Guid.NewGuid().ToString("N")[..12]);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> SeedBoltAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..10];
        await using var db = _fx.NewDbContext();

        var project = new Project { Code = $"P-{tag}", Name = "窗口测试", Industry = "风电", SlaLevel = "standard", CreatedAt = now, UpdatedAt = now };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var device = new Device
        {
            ProjectId = project.Id, Code = $"D-{tag}", Model = 3, CommType = 1, CommParams = "in-process",
            Address = 1, TransferMode = 1, ChannelCount = 40, State = "online", CreatedAt = now, UpdatedAt = now,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        var group = new MonitorGroup { ProjectId = project.Id, Code = $"G-{tag}", Name = "测试组", CreatedAt = now, UpdatedAt = now };
        db.MonitorGroups.Add(group);
        await db.SaveChangesAsync();

        var channel = new Channel
        {
            DeviceId = device.Id, ChannelNo = 1, ProbeFreqMhz = 2.0m, Gain = 16, MeasMode = 2, TcMode = 1,
            TofAlgorithm = "ThrSignal", HardwareState = "healthy", BusinessState = "bound", ConfigSource = "database",
            State = "manual", DiscoveredAt = now, CreatedAt = now, UpdatedAt = now,
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var bolt = new Bolt
        {
            GroupId = group.Id, ChannelId = channel.Id, Code = $"B-{tag}", NominalDiam = "M20", Grade = "10.9",
            Rp02Mpa = 940m, Material = "42CrMo", ElasticModulusGpa = 206m, EffectiveLengthMm = 100m, TotalLengthMm = 120m,
            CrossSectionMm2 = 245m, SoundVelocityMs = 5890m, TargetPreloadN = 172_000m, MinPreloadN = 120_000m,
            State = "calibrated", CreatedAt = now, UpdatedAt = now,
        };
        db.Bolts.Add(bolt);
        await db.SaveChangesAsync();
        return bolt.Id;
    }
}
