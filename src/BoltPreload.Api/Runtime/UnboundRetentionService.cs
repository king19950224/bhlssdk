using BoltPreload.Application.Diagnostics;
using BoltPreload.Application.Ingestion;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Runtime;

/// <summary>未绑定测量数据保留期清理配置（appsettings 的 Ingestion:UnboundRetention 节）。</summary>
public sealed class UnboundRetentionOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>未绑定测量的保留天数。绑定建档时会回填该窗口内的暂存数据，超窗的丢弃。</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>清理周期（分钟）。</summary>
    public int SweepIntervalMinutes { get; set; } = 60;

    /// <summary>单批删除行数上限（避免大事务长锁）。</summary>
    public int BatchSize { get; set; } = 5000;
}

/// <summary>
/// unbound_measurements 保留期清理 + 占位档自动回收。
/// <para>
/// 背景：全通道轮询下，插着探头但一直未建档的通道会 7×24 往 unbound_measurements 落有回波数据
/// （无回波帧在 Sink 已挡，见 PostgresMeasurementSink），该表此前没有任何清理任务，属无限膨胀点。
/// 保留 N 天（默认 7）：远超"接探头→建档"的现场操作间隔，建档回填价值不受影响；
/// 超窗数据按批删除（LIMIT 子查询，避免一次大 DELETE 长时间锁表/膨胀 WAL）。
/// </para>
/// <para>
/// 占位档回收（两级模型 R2，见 bolt-lifecycle-design.md）：自动建档且无人工痕迹
/// （confirmed_at IS NULL）的螺栓档案，探头离位满 Fx:HotPlug:AutoRetireDays 天后软删 + 审计留痕——
/// auto-enroll 的对称逆操作，误插误建的 ghost 不再永存。资产档（被人确认过）永不自动删。
/// </para>
/// </summary>
public sealed class UnboundRetentionService : BackgroundService
{
    private readonly NpgsqlDataSource _ds;
    private readonly UnboundRetentionOptions _opt;
    private readonly ChannelPresenceOptions _presenceOpt;
    private readonly IChannelResolver _resolver;
    private readonly BackgroundServiceHealth _health;
    private readonly ILogger<UnboundRetentionService> _logger;

    public UnboundRetentionService(
        NpgsqlDataSource ds,
        IOptions<UnboundRetentionOptions> opt,
        IOptions<ChannelPresenceOptions> presenceOpt,
        IChannelResolver resolver,
        BackgroundServiceHealth health,
        ILogger<UnboundRetentionService> logger)
    {
        _ds = ds;
        _opt = opt.Value;
        _presenceOpt = presenceOpt.Value;
        _resolver = resolver;
        _health = health;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled)
        {
            _logger.LogInformation("未绑定测量保留清理已禁用（Ingestion:UnboundRetention:Enabled=false）");
            return;
        }
        var interval = TimeSpan.FromMinutes(Math.Max(5, _opt.SweepIntervalMinutes));
        _logger.LogInformation("未绑定测量保留清理启动：保留 {Days} 天，每 {Interval} 清一轮",
            _opt.RetentionDays, interval);

        using var timer = new PeriodicTimer(interval);
        while (true)
        {
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) { return; }

            _health.Beat("unbound-retention", interval);
            try
            {
                var deleted = await SweepAsync(stoppingToken);
                if (deleted > 0)
                    _logger.LogInformation("未绑定测量清理：删除 {Count} 行（保留 {Days} 天）", deleted, _opt.RetentionDays);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogError(ex, "未绑定测量清理异常（下轮重试）"); }

            try
            {
                await RetirePlaceholderBoltsAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogError(ex, "占位档回收异常（下轮重试）"); }
        }
    }

    /// <summary>
    /// 占位档自动回收（两级模型 R2）：confirmed_at IS NULL 且探头离位满 N 天的自动建档螺栓，
    /// 软删 + 通道回 unbound + 绑定轨迹与审计留痕（system:auto-retire），并失效路由缓存。
    /// 判据用 COALESCE(last_present_at, created_at)：老数据无在位时间戳时按档案年龄兜底。
    /// </summary>
    // internal 供测试直调单轮回收
    internal async Task<int> RetirePlaceholderBoltsAsync(CancellationToken ct)
    {
        var days = _presenceOpt.AutoRetireDays;
        if (days <= 0) return 0;

        var candidates = new List<(long BoltId, string Code, long ChannelId, long DeviceId, short ChannelNo)>();
        await using (var cmd = _ds.CreateCommand(@"
            SELECT b.id, b.code, b.channel_id, c.device_id, c.channel_no
            FROM bolts b
            JOIN channels c ON c.id = b.channel_id AND c.deleted_at IS NULL
            WHERE b.deleted_at IS NULL
              AND b.confirmed_at IS NULL
              AND b.state = 'uncalibrated'
              AND c.hardware_state NOT IN ('healthy','noisy','suspect')
              AND COALESCE(c.last_present_at, b.created_at) < now() - make_interval(days => @days)
            LIMIT 100"))
        {
            cmd.Parameters.AddWithValue("days", NpgsqlDbType.Integer, days);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                candidates.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt16(4)));
        }
        if (candidates.Count == 0) return 0;

        var retired = 0;
        foreach (var b in candidates)
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                // 复核 + 行锁：离位判据在拿锁后重查，防止与"探头刚插回、presence 正在确认"竞态
                var stillEligible = false;
                await using (var cmd = new NpgsqlCommand(@"
                    SELECT 1 FROM bolts b
                    JOIN channels c ON c.id = b.channel_id
                    WHERE b.id = @id AND b.deleted_at IS NULL AND b.confirmed_at IS NULL
                      AND b.state = 'uncalibrated'
                      AND c.hardware_state NOT IN ('healthy','noisy','suspect')
                      AND COALESCE(c.last_present_at, b.created_at) < now() - make_interval(days => @days)
                    FOR UPDATE OF b", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, b.BoltId);
                    cmd.Parameters.AddWithValue("days", NpgsqlDbType.Integer, days);
                    stillEligible = await cmd.ExecuteScalarAsync(ct) is not null;
                }
                if (!stillEligible) { await tx.RollbackAsync(ct); continue; }

                await using (var cmd = new NpgsqlCommand(
                    "UPDATE bolts SET deleted_at = now(), enabled = false, updated_at = now() WHERE id = @id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, b.BoltId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await using (var cmd = new NpgsqlCommand(
                    "UPDATE channels SET business_state = 'unbound', updated_at = now() WHERE id = @cid", conn, tx))
                {
                    cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, b.ChannelId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await using (var cmd = new NpgsqlCommand(@"
                    INSERT INTO channel_binding_history (
                        bolt_id, from_channel_id, to_channel_id, action, calibration_action,
                        moved_by, before_jsonb, note)
                    VALUES (@id, @cid, NULL, 'unbind', 'keep_uncomputed', NULL, NULL, @note)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, b.BoltId);
                    cmd.Parameters.AddWithValue("cid", NpgsqlDbType.Bigint, b.ChannelId);
                    cmd.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                        $"[系统自动回收] 占位档 {b.Code}（自动建档、无人工痕迹）探头离位超过 {days} 天，自动软删。恢复接入后会重新自动建档");
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await using (var audit = new NpgsqlCommand(@"
                    INSERT INTO audit_log (user_id, action, target_type, target_id, before_jsonb, after_jsonb, note, at)
                    VALUES (NULL, 'bolt_auto_retire', 'bolt', @id, NULL, NULL, @note, now())", conn, tx))
                {
                    audit.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, b.BoltId);
                    audit.Parameters.AddWithValue("note", NpgsqlDbType.Text,
                        $"[系统自动回收] 占位档 {b.Code} 探头离位超过 {days} 天（设备 {b.DeviceId} CH{b.ChannelNo}），自动软删（两级模型 R2，auto-enroll 的对称逆操作）");
                    await audit.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                _resolver.Invalidate(b.DeviceId, (ushort)b.ChannelNo);
                retired++;
                _logger.LogInformation("占位档自动回收：{Code}（设备 {DeviceId} CH{Ch}，离位超 {Days} 天）",
                    b.Code, b.DeviceId, b.ChannelNo, days);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                try { await tx.RollbackAsync(CancellationToken.None); } catch { /* 连接已坏 */ }
                _logger.LogWarning(ex, "占位档回收失败 bolt={Code}（下轮重试）", b.Code);
            }
        }
        return retired;
    }

    // internal 供测试直调单轮清理
    internal async Task<long> SweepAsync(CancellationToken ct)
    {
        var days = Math.Max(1, _opt.RetentionDays);
        var batch = Math.Clamp(_opt.BatchSize, 100, 50_000);
        long total = 0;
        while (!ct.IsCancellationRequested)
        {
            await using var cmd = _ds.CreateCommand(@"
                DELETE FROM unbound_measurements
                WHERE id IN (
                    SELECT id FROM unbound_measurements
                    WHERE ts < now() - make_interval(days => @days)
                    LIMIT @batch)");
            cmd.Parameters.AddWithValue("days", NpgsqlDbType.Integer, days);
            cmd.Parameters.AddWithValue("batch", NpgsqlDbType.Integer, batch);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            total += n;
            if (n < batch) break;
        }
        return total;
    }
}
