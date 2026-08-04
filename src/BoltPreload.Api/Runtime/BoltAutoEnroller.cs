using System.Collections.Concurrent;
using BoltPreload.Api.Endpoints;
using BoltPreload.Application.Devices;
using BoltPreload.Application.Ingestion;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Runtime;

/// <summary>
/// 检测即螺栓：通道确认在位且无绑定螺栓时自动建档（现场零点击）。
/// <para>
/// 语义（用户拍板）：探头插上、回波过下限门+稳定性门确认在位 → 螺栓档案自动出现并显示；
/// 编号自动（中文“通道X-Y号螺栓”，见 <see cref="ChannelNaming"/>）、缺省规格
/// （42CrMo/M20/8.8，可后补位置/规格/目标值）。
/// 建档事务与点位表导入（WiringTableEndpoints）同构：INSERT bolts → 通道 business_state →
/// 暂存测量回填 → 波形特征归属 → 绑定历史 + 审计（操作者 system:auto-enroll，user_id 为空）。
/// </para>
/// <para>
/// 归属组解析：devices.default_group_id → 设备同项目唯一活跃组兜底 → 都没有则跳过
/// 并节流提示（识别页有横幅引导设置默认组；设置成功后 PATCH 端点会对在位未建档通道补建）。
/// </para>
/// <para>
/// 不自动复活：人工删除且探头仍在位时 channels.auto_enroll_hold=true，本服务跳过建档；
/// 真正离位（Present=false）后清 hold，再插入才重建。进程重启不清 hold——避免“删了重启又回来”。
/// </para>
/// </summary>
public sealed class BoltAutoEnroller
{
    /// <summary>与识别页 PRESENT_STATES 同一词表：这些 hardware_state 视为"探头在位"。</summary>
    public static readonly string[] PresentStates = ["healthy", "noisy", "suspect"];

    private readonly NpgsqlDataSource _ds;
    private readonly IChannelResolver _resolver;
    private readonly ILogger<BoltAutoEnroller> _logger;

    // 建档失败（DB 瞬断等）的通道 → ChannelPresenceService 的 sweep 周期驱动重试；
    // 成功或条件不再成立（已有螺栓/探头已拔/无归属组）即出队。
    private readonly ConcurrentDictionary<(long DeviceId, ushort ChannelNo), byte> _pending = new();

    // 无归属组跳过的节流日志：每设备最多 10 分钟提示一次（在位事件在启动确认时是一大批）
    private readonly ConcurrentDictionary<long, DateTimeOffset> _noGroupLoggedAt = new();
    private static readonly TimeSpan NoGroupLogInterval = TimeSpan.FromMinutes(10);

    public BoltAutoEnroller(NpgsqlDataSource ds, IChannelResolver resolver, ILogger<BoltAutoEnroller> logger)
    {
        _ds = ds;
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>自动建档成功的结果：新建螺栓 id 与编号。</summary>
    public sealed record EnrolledBolt(long BoltId, string Code);

    /// <summary>
    /// 对"在位且未绑定"的通道自动建档。返回 null 表示本次没有建档（已有螺栓/探头不在位/
    /// 无归属组/编号冲突），瞬时异常会进入重试队列由 <see cref="RetryPendingAsync"/> 驱动。
    /// </summary>
    public async Task<EnrolledBolt?> TryEnrollAsync(long deviceId, ushort channelNo, CancellationToken ct)
    {
        try
        {
            var enrolled = await EnrollCoreAsync(deviceId, channelNo, ct);
            _pending.TryRemove((deviceId, channelNo), out _);
            return enrolled;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (_pending.TryAdd((deviceId, channelNo), 0))
                _logger.LogWarning(ex, "自动建档失败 device={DeviceId} ch={Ch}，已入队随 sweep 重试", deviceId, channelNo);
            else
                _logger.LogDebug(ex, "自动建档重试仍失败 device={DeviceId} ch={Ch}", deviceId, channelNo);
            return null;
        }
    }

    /// <summary>重试队列（由 ChannelPresenceService 的 sweep 周期调用）。</summary>
    public async Task RetryPendingAsync(CancellationToken ct)
    {
        foreach (var key in _pending.Keys)
        {
            ct.ThrowIfCancellationRequested();
            var enrolled = await TryEnrollAsync(key.DeviceId, key.ChannelNo, ct);
            if (enrolled is not null)
                _logger.LogInformation("自动建档重试成功 device={DeviceId} ch={Ch} bolt={Code}",
                    key.DeviceId, key.ChannelNo, enrolled.Code);
        }
    }

    private async Task<EnrolledBolt?> EnrollCoreAsync(long deviceId, ushort channelNo, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // ---------- 归属组解析 ----------
        long deviceProjectId;
        long? defaultGroupId;
        await using (var cmd = new NpgsqlCommand(
            "SELECT project_id, default_group_id FROM devices WHERE id = @id AND deleted_at IS NULL", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, deviceId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            deviceProjectId = r.GetInt64(0);
            defaultGroupId = r.IsDBNull(1) ? null : r.GetInt64(1);
        }

        long groupId;
        string groupCode;
        if (defaultGroupId is long gid)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT code, project_id FROM monitor_groups WHERE id = @gid AND deleted_at IS NULL", conn, tx);
            cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, gid);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct) || r.GetInt64(1) != deviceProjectId)
            {
                LogNoGroupThrottled(deviceId,
                    $"默认监测组 {gid} 不存在/已删除/与设备不同项目，自动建档跳过——请到设备页重新设置");
                return null;
            }
            groupId = gid;
            groupCode = r.GetString(0);
        }
        else
        {
            // 兜底：设备同项目恰好一个活跃组（单法兰部署的常态），多组/零组都不猜
            var candidates = new List<(long Id, string Code)>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT id, code FROM monitor_groups WHERE project_id = @p AND deleted_at IS NULL LIMIT 2", conn, tx))
            {
                cmd.Parameters.AddWithValue("p", NpgsqlDbType.Bigint, deviceProjectId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) candidates.Add((r.GetInt64(0), r.GetString(1)));
            }
            if (candidates.Count != 1)
            {
                LogNoGroupThrottled(deviceId,
                    "设备未设置默认监测组且无法兜底（同项目活跃组不唯一），在位通道不会自动建档——请到设备页设置默认监测组");
                return null;
            }
            (groupId, groupCode) = candidates[0];
        }

        // ---------- 通道行锁 + 前置复核（与建档/换绑同一把锁语义） ----------
        long channelId;
        string? positionLabel;
        await using (var cmd = new NpgsqlCommand(@"
            SELECT c.id, c.name, c.hardware_state, b.id, c.auto_enroll_hold
            FROM channels c
            LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
            WHERE c.device_id = @d AND c.channel_no = @ch AND c.deleted_at IS NULL
            FOR UPDATE OF c", conn, tx))
        {
            cmd.Parameters.AddWithValue("d", NpgsqlDbType.Bigint, deviceId);
            cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Smallint, (short)channelNo);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;              // 目录里还没有这行，等自动发现建行后的下次事件
            if (!r.IsDBNull(3)) return null;                       // 已有活跃螺栓（并发建档/换绑赢了），幂等退出
            if (r.GetBoolean(4)) return null;                      // 人工删除抑制：须离位后再插入才重建
            var hw = r.IsDBNull(2) ? "unknown" : r.GetString(2);
            if (!PresentStates.Contains(hw)) return null;          // 事件到建档之间探头又被拔了，不建
            channelId = r.GetInt64(0);
            positionLabel = r.IsDBNull(1) ? null : r.GetString(1);
        }

        // ---------- 编号：中文“通道X-Y号螺栓”，与既有（含软删）冲突时追加 -2/-3… ----------
        var baseCode = ChannelNaming.DefaultBoltCode(channelNo);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new NpgsqlCommand(
            "SELECT code FROM bolts WHERE group_id = @gid AND (code = @base OR code LIKE @base || '-%')", conn, tx))
        {
            cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, groupId);
            cmd.Parameters.AddWithValue("base", NpgsqlDbType.Varchar, baseCode);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) taken.Add(r.GetString(0));
        }
        var code = baseCode;
        for (var n = 2; taken.Contains(code); n++) code = $"{baseCode}-{n}";

        // ---------- 建档（缺省规格取 BoltSpecPresets，与单颗建档、点位表导入同一份定义） ----------
        var csa = BoltSpecPresets.StressAreaMm2[BoltSpecPresets.DefaultNominalDiam];
        var rp02 = BoltSpecPresets.GradeRp02Mpa[BoltSpecPresets.DefaultGrade];

        long boltId;
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO bolts (
                group_id, channel_id, code, nominal_diam, grade, rp02_mpa, material,
                elastic_modulus_gpa, effective_length_mm, total_length_mm, cross_section_mm2,
                sound_velocity_ms, target_preload_n, min_preload_n, position_label,
                install_date, gb_compliant, state, enabled)
            VALUES (@gid, @cid, @code, @nd, @grade, @rp02, @material,
                    @e, @eff_len, @total_len, @csa, @v, NULL, NULL, @pos,
                    CURRENT_DATE, true, 'uncalibrated', true)
            ON CONFLICT (group_id, code) DO NOTHING
            RETURNING id", conn, tx))
        {
            cmd.Parameters.AddWithValue("gid", NpgsqlDbType.Bigint, groupId);
            cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
            cmd.Parameters.AddWithValue("code", NpgsqlDbType.Varchar, code);
            cmd.Parameters.AddWithValue("nd", NpgsqlDbType.Varchar, BoltSpecPresets.DefaultNominalDiam);
            cmd.Parameters.AddWithValue("grade", NpgsqlDbType.Varchar, BoltSpecPresets.DefaultGrade);
            cmd.Parameters.AddWithValue("material", NpgsqlDbType.Varchar, BoltSpecPresets.DefaultMaterial);
            cmd.Parameters.AddWithValue("e", NpgsqlDbType.Numeric, BoltSpecPresets.DefaultElasticModulusGpa);
            cmd.Parameters.AddWithValue("eff_len", NpgsqlDbType.Numeric, BoltSpecPresets.DefaultEffectiveLengthMm);
            cmd.Parameters.AddWithValue("total_len", NpgsqlDbType.Numeric, BoltSpecPresets.DefaultTotalLengthMm);
            cmd.Parameters.AddWithValue("rp02", NpgsqlDbType.Numeric, rp02);
            cmd.Parameters.AddWithValue("csa", NpgsqlDbType.Numeric, csa);
            cmd.Parameters.AddWithValue("v", NpgsqlDbType.Numeric, BoltSpecPresets.DefaultSoundVelocityMs);
            cmd.Parameters.AddWithValue("pos", NpgsqlDbType.Varchar, (object?)positionLabel ?? DBNull.Value);
            var scalar = await cmd.ExecuteScalarAsync(ct);
            if (scalar is not long id)
            {
                _logger.LogWarning("自动建档编号冲突 device={DeviceId} ch={Ch} code={Code}（并发导入？下次在位事件再试）",
                    deviceId, channelNo, code);
                return null;
            }
            boltId = id;
        }

        await using (var cmd = new NpgsqlCommand(
            "UPDATE channels SET business_state = 'bound', updated_at = now() WHERE id = @cid", conn, tx))
        {
            cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, channelId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ---------- 暂存回填（与点位表导入同构：测量迁入 + 波形特征归属 + 清空暂存） ----------
        long backfilled;
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO measurements (
                bolt_id, channel_id, ts, ingested_at,
                echo_time_ns, delta_tof_ns, sp_temp_c, gain, status_code,
                wave_blob_url, algo_version)
            SELECT @bolt_id, channel_id, ts, ingested_at,
                   echo_time_ns, delta_tof_ns, sp_temp_c, COALESCE(gain, 0), status_code,
                   wave_blob_url, 'phase1:raw:backfilled'
            FROM unbound_measurements
            WHERE channel_id = @channel_id
            ON CONFLICT (channel_id, ts) DO NOTHING", conn, tx))
        {
            cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
            cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
            backfilled = await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand(
            "UPDATE wave_features SET bolt_id = @bolt_id WHERE channel_id = @channel_id AND bolt_id IS NULL", conn, tx))
        {
            cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
            cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM unbound_measurements WHERE channel_id = @channel_id", conn, tx))
        {
            cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ---------- 绑定历史 + 审计（系统动作：moved_by/user_id 为空，note 标明 system:auto-enroll） ----------
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO channel_binding_history (
                bolt_id, from_channel_id, to_channel_id, action, calibration_action,
                moved_by, before_jsonb, after_jsonb, note)
            SELECT @bolt_id, NULL, @channel_id, 'bind', 'keep_uncomputed',
                   NULL, NULL, to_jsonb(b), @note
            FROM bolts b WHERE b.id = @bolt_id", conn, tx))
        {
            cmd.Parameters.AddWithValue("bolt_id", NpgsqlDbType.Bigint, boltId);
            cmd.Parameters.AddWithValue("channel_id", NpgsqlDbType.Bigint, channelId);
            cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                $"[系统自动建档] 探头在位自动建档 CH{channelNo}（检测即螺栓）");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var audit = new NpgsqlCommand(@"
            INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
            SELECT NULL, 'create_bolt', 'bolt', @id, NULL, to_jsonb(b), @note, now()
            FROM bolts b WHERE b.id = @id", conn, tx))
        {
            audit.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, boltId);
            audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                $"[系统自动建档] 探头在位自动建档：{code} 绑定 CH{channelNo}（设备 {deviceId}，监测组 {groupCode}），缺省规格 M20/8.8，待补位置与目标值");
            await audit.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        // 路由缓存失效：新 bolt 立刻参与数据路由（后续数据直接进 measurements）
        _resolver.Invalidate(deviceId, channelNo);

        _logger.LogInformation("自动建档完成 device={DeviceId} ch={Ch} bolt={Code}(id={Id}) 回填 {Backfilled} 条暂存测量",
            deviceId, channelNo, code, boltId, backfilled);
        return new EnrolledBolt(boltId, code);
    }

    private void LogNoGroupThrottled(long deviceId, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        var last = _noGroupLoggedAt.GetValueOrDefault(deviceId);
        if (now - last < NoGroupLogInterval) return;
        _noGroupLoggedAt[deviceId] = now;
        _logger.LogWarning("自动建档跳过 device={DeviceId}：{Reason}", deviceId, reason);
    }
}
