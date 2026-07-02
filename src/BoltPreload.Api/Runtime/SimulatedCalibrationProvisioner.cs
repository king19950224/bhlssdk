using BoltPreload.Application.Ingestion;
using BoltPreload.Fx.Simulator;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Runtime;

/// <summary>
/// 仿真模式专用：为模拟器驱动的螺栓通道写入一份<b>真实标定</b>(K_L/RefTime/RefTemp，与
/// <see cref="SimulatedBoltPhysics"/> 物理基线一致)，使真实采集链路能算出真实预紧力。
/// <para>
/// 这不是注入演示数据：测量值仍由模拟器→采集→GB 单波法算出；本服务只做"标定"这一真实运维步骤，
/// 等价于真机现场对传感器的标定。幂等：重复启动只更新已有 sim_baseline 标定。
/// </para>
/// <para>仅在 <c>Fx:Mode=Simulated</c> 注册（见 Program.cs）。生产真机环境不参与。</para>
/// </summary>
public sealed class SimulatedCalibrationProvisioner : IHostedService
{
    private readonly NpgsqlDataSource _ds;
    private readonly IChannelResolver _resolver;
    private readonly IConfiguration _cfg;
    private readonly ILogger<SimulatedCalibrationProvisioner> _logger;

    public SimulatedCalibrationProvisioner(
        NpgsqlDataSource ds, IChannelResolver resolver,
        IConfiguration cfg, ILogger<SimulatedCalibrationProvisioner> logger)
    {
        _ds = ds;
        _resolver = resolver;
        _cfg = cfg;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ProvisionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "仿真基线标定供给失败（不影响启动）");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ProvisionAsync(CancellationToken ct)
    {
        // 多模拟器：DeviceConnectorManager 会建 deviceId = DeviceId .. DeviceId+DeviceCount-1 个模拟器，
        // 标定供给必须覆盖全部，否则除首台外的模拟设备通道无 sim 标定、预紧力走 raw/null（仿真链路不完整）。
        var baseDeviceId = _cfg.GetValue<long>("Fx:Simulator:DeviceId", 1);
        var deviceCount = Math.Max(1, _cfg.GetValue<int>("Fx:Simulator:DeviceCount", 1));

        long? userId = null;
        await using (var u = _ds.CreateCommand(
            "SELECT id FROM users WHERE role='admin' OR username='admin' ORDER BY id LIMIT 1"))
        {
            if (await u.ExecuteScalarAsync(ct) is long uid) userId = uid;
        }
        if (userId is null)
        {
            _logger.LogWarning("仿真基线标定供给跳过：未找到 admin 用户（FK 需要）");
            return;
        }

        var provisioned = 0;
        for (var deviceId = baseDeviceId; deviceId < baseDeviceId + deviceCount; deviceId++)
            provisioned += await ProvisionDeviceAsync(deviceId, userId.Value, ct);

        _resolver.InvalidateAll(); // 让采集热路径立刻读到新标定
        _logger.LogInformation("仿真基线标定供给完成：{N} 个螺栓通道已标定（devices={Base}..{End}），真实预紧力将由采集链路算出",
            provisioned, baseDeviceId, baseDeviceId + deviceCount - 1);
    }

    /// <summary>为单个仿真设备的已绑螺栓通道供给 sim_baseline 标定，返回本设备处理的通道数。</summary>
    private async Task<int> ProvisionDeviceAsync(long deviceId, long userId, CancellationToken ct)
    {
        var provisioned = 0;
        foreach (var ch in SimulatedBoltPhysics.BoltedChannels)
        {
            var phys = SimulatedBoltPhysics.For(ch)!;

            long channelId, boltId;
            await using (var q = _ds.CreateCommand(@"
                SELECT c.id, b.id FROM channels c
                JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
                WHERE c.device_id = @d AND c.channel_no = @ch AND c.deleted_at IS NULL
                LIMIT 1"))
            {
                q.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
                q.Parameters.AddWithValue("ch", NpgsqlDbType.Smallint, (short)ch);
                await using var r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) continue; // 该通道未绑螺栓，跳过
                channelId = r.GetInt64(0);
                boltId = r.GetInt64(1);
            }

            // 取/建 sim_baseline 标定档案（幂等）
            Guid calibId = Guid.Empty;
            await using (var existing = _ds.CreateCommand(
                "SELECT id FROM calibration_records WHERE bolt_id=@b AND method='sim_baseline' ORDER BY created_at DESC LIMIT 1"))
            {
                existing.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
                if (await existing.ExecuteScalarAsync(ct) is Guid g) calibId = g;
            }

            if (calibId == Guid.Empty)
            {
                await using var ins = _ds.CreateCommand(@"
                    INSERT INTO calibration_records
                      (bolt_id, method, operator_user_id, reviewer_user_id, points_jsonb,
                       k_l, k_l_uncertainty, r_squared, outliers_removed, fitter_version,
                       ref_time_ns, ref_temp_c, passed, operator_signature_at, reviewer_signature_at, expires_at, notes)
                    VALUES (@b,'sim_baseline',@u,@u,'[]'::jsonb,
                       @kl,@klu,0.999,0,'sim:1.0',
                       @rt,@rtemp,true,now(),now(),(now()+interval '365 days')::date,'仿真基线标定（开发环境，模拟器物理基线）')
                    RETURNING id");
                ins.Parameters.AddWithValue("b", NpgsqlDbType.Bigint, boltId);
                ins.Parameters.AddWithValue("u", NpgsqlDbType.Bigint, userId);
                ins.Parameters.AddWithValue("kl", NpgsqlDbType.Numeric, phys.Kl);
                ins.Parameters.AddWithValue("klu", NpgsqlDbType.Numeric, SimulatedBoltPhysics.KlUncertainty);
                ins.Parameters.AddWithValue("rt", NpgsqlDbType.Numeric, phys.RefTofNs);
                ins.Parameters.AddWithValue("rtemp", NpgsqlDbType.Numeric, phys.RefTempC);
                calibId = (Guid)(await ins.ExecuteScalarAsync(ct))!;
            }
            else
            {
                await using var upd = _ds.CreateCommand(@"
                    UPDATE calibration_records SET k_l=@kl, k_l_uncertainty=@klu, ref_time_ns=@rt, ref_temp_c=@rtemp,
                        passed=true, expires_at=(now()+interval '365 days')::date WHERE id=@id");
                upd.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, calibId);
                upd.Parameters.AddWithValue("kl", NpgsqlDbType.Numeric, phys.Kl);
                upd.Parameters.AddWithValue("klu", NpgsqlDbType.Numeric, SimulatedBoltPhysics.KlUncertainty);
                upd.Parameters.AddWithValue("rt", NpgsqlDbType.Numeric, phys.RefTofNs);
                upd.Parameters.AddWithValue("rtemp", NpgsqlDbType.Numeric, phys.RefTempC);
                await upd.ExecuteNonQueryAsync(ct);
            }

            // 把通道指向该标定 + 写冗余基线字段（intercept 置空 → 走 GB 单波法）；螺栓置为已标定
            await using (var uc = _ds.CreateCommand(@"
                UPDATE channels SET k_l=@kl, k_l_uncertainty=@klu, ref_time_ns=@rt, ref_temp_c=@rtemp,
                    k_l_intercept_ns=NULL, active_calibration_id=@cid WHERE id=@id"))
            {
                uc.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
                uc.Parameters.AddWithValue("kl", NpgsqlDbType.Numeric, phys.Kl);
                uc.Parameters.AddWithValue("klu", NpgsqlDbType.Numeric, SimulatedBoltPhysics.KlUncertainty);
                uc.Parameters.AddWithValue("rt", NpgsqlDbType.Numeric, phys.RefTofNs);
                uc.Parameters.AddWithValue("rtemp", NpgsqlDbType.Numeric, phys.RefTempC);
                uc.Parameters.AddWithValue("cid", NpgsqlDbType.Uuid, calibId);
                await uc.ExecuteNonQueryAsync(ct);
            }
            await using (var ub = _ds.CreateCommand("UPDATE bolts SET state='calibrated', updated_at=now() WHERE channel_id=@id"))
            {
                ub.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
                await ub.ExecuteNonQueryAsync(ct);
            }
            provisioned++;
        }

        return provisioned;
    }
}
