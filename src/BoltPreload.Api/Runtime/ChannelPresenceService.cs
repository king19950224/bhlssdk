using BoltPreload.Application.Devices;
using BoltPreload.Application.Devices.Acceptance;
using BoltPreload.Application.Devices.ProbeScan;
using BoltPreload.Application.Diagnostics;
using BoltPreload.Application.Ingestion;
using BoltPreload.Application.Realtime;
using BoltPreload.Fx;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api.Runtime;

/// <summary>
/// 探头热插拔宿主服务（设计见 资料/工程文档/plan.md 中“探头热插拔识别设计”段落）。
/// <para>
/// 职责一：让设备常态轮询【全部物理通道】（1..channel_count），这是热插拔识别的物理前提——
/// 厂家设备只测 MCR 轮询集内的通道，探头插在轮询集外的口上一条数据都不会上来（probe-scan 设计 F1）。
/// 代价是单通道刷新率被稀释（8→40 通道约 5 倍），预紧力为缓变量可接受；可用 Fx:HotPlug:Enabled 关闭回旧行为。
/// </para>
/// <para>
/// 职责二：消费 <see cref="ChannelPresenceTracker"/> 的在位变化事件——
/// 更新 channels.hardware_state / last_seen_at / last_present_at（重启后状态可恢复），
/// 在位且未绑定螺栓时触发 <see cref="BoltAutoEnroller"/> 自动建档（检测即螺栓，Fx:HotPlug:AutoEnroll 可关），
/// SignalR 推 OnChannelPresence 给前端（事件带上新建螺栓编号）。
/// </para>
/// <para>
/// 职责三：周期调用 <see cref="ChannelPresenceTracker.Sweep"/> 做拔出静默判定；
/// 设备离线 / probe-scan 扫描期间该设备判定挂起（假静默不误报拔出），与 Watchdog/SignalHealth 同一不变量。
/// </para>
/// <para>
/// 注意分层：这里的"在位/断开"是 UI 秒级显示；安全告警 signal_lost（120s，有国标依据）仍由
/// <see cref="SignalHealthService"/> 独立判定，两者不混。
/// </para>
/// </summary>
public sealed class ChannelPresenceService : BackgroundService
{
    private readonly IDeviceConnectorManager _deviceManager;
    private readonly ChannelPresenceTracker _tracker;
    private readonly IChannelPresencePublisher _publisher;
    private readonly ProbeScanState _probeScan;
    private readonly AcceptanceSessionState _acceptance;
    private readonly NpgsqlDataSource _ds;
    private readonly BoltAutoEnroller _enroller;
    private readonly ChannelPresenceOptions _opt;
    private readonly BackgroundServiceHealth _health;
    private readonly ILogger<ChannelPresenceService> _logger;

    // 已确认下发过全通道 MCR 的设备 → 当时的目标通道数（本进程内幂等；设备端 MCR 是持久配置，
    // 重启进程会话快照为空时会再下发一次收敛）。记录通道数是为了在通道目录扩容
    //（TryUpdateChannelCount，如 40→48）后检测到目标变化并重新收敛，而不是永久跳过。
    // 已摘出运行时的设备（热禁用/删除）每轮末尾清掉，重新启用时按新会话重新收敛。
    private readonly Dictionary<long, ushort> _mcrConfigured = [];

    public ChannelPresenceService(
        IDeviceConnectorManager deviceManager,
        ChannelPresenceTracker tracker,
        IChannelPresencePublisher publisher,
        ProbeScanState probeScan,
        AcceptanceSessionState acceptance,
        NpgsqlDataSource ds,
        BoltAutoEnroller enroller,
        BackgroundServiceHealth health,
        IOptions<ChannelPresenceOptions> opt,
        ILogger<ChannelPresenceService> logger)
    {
        _deviceManager = deviceManager;
        _tracker = tracker;
        _publisher = publisher;
        _probeScan = probeScan;
        _acceptance = acceptance;
        _ds = ds;
        _enroller = enroller;
        _health = health;
        _opt = opt.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled)
        {
            _logger.LogInformation("探头热插拔识别已禁用（Fx:HotPlug:Enabled=false），设备保持业务通道轮询");
            return;
        }
        _logger.LogInformation(
            "探头热插拔识别启动：插入判定连续 {Plug} 条回波，拔出判定静默 {Silence}s，扫描周期 {Sweep}s",
            _opt.PlugConsecutiveEchoes, _opt.UnplugSilenceSeconds, _opt.SweepIntervalSeconds);

        // 事件消费与静默扫描并行；任一结束（进程停机）即收尾
        var consume = ConsumeEventsAsync(stoppingToken);
        var sweep = SweepLoopAsync(stoppingToken);
        await Task.WhenAll(consume, sweep);
    }

    // ===================== 全通道轮询下发 =====================

    /// <summary>
    /// 对已连接且未确认过的设备下发全通道 MCR（1..channel_count）。
    /// 若会话快照显示已是全通道则只标记不下发。
    /// 扫描期间跳过且【清除已确认标记】：扫描恢复的 MCR 快照可能是扫描前的旧集
    /// （扫描恰在本服务首次下发前快照的竞态），扫完后下一轮重新校验收敛。
    /// </summary>
    // internal 供集成测试直调单轮收敛（BoltPreload.Tests，见 csproj InternalsVisibleTo）
    internal async Task EnsureFullChannelPollingAsync(CancellationToken ct)
    {
        foreach (var rt in _deviceManager.Runtimes)
        {
            if (_probeScan.IsRunning(rt.DeviceId))
            {
                _mcrConfigured.Remove(rt.DeviceId);
                continue;
            }
            if (_acceptance.IsActive(rt.DeviceId))
            {
                // 接入验收会话把 MCR 临时缩到待验收通道集，本服务不抢：清确认标记，
                // 会话结束/过期后下一轮自动收敛回全通道（会话的恢复路径就押在这里）
                _mcrConfigured.Remove(rt.DeviceId);
                continue;
            }
            if (rt.Connector.State != FxConnectorState.Connected)
            {
                // 断开期间清"已下发"标记（必须在下面的标记短路判断之前，否则已确认的设备走不到这里）：
                // 重连是新会话，设备断电重启可能丢轮询集。连接器侧在新会话 provisioning 时会把
                // 会话 MCR 视图一并清空（FxSdkConnector.OnSdkConnectedAsync），两边都清，
                // 恢复 Connected 后才会真正重新校验下发，而不是拿旧快照判"一致"跳过。
                _mcrConfigured.Remove(rt.DeviceId);
                continue;
            }
            var channelCount = rt.Options.ChannelCount;
            // 已确认且目标通道数没变才跳过；通道目录扩容（40→48）会改变目标，需重新收敛
            if (_mcrConfigured.TryGetValue(rt.DeviceId, out var configuredCount) && configuredCount == channelCount)
                continue;

            if (rt.Options.Model != 3)
            {
                // MCR 多通道轮询仅 model=3（多通道监测型）支持，其它型号不下发
                _mcrConfigured[rt.DeviceId] = channelCount;
                _logger.LogInformation("设备 {DeviceId} model={Model} 非多通道监测型，热插拔识别仅覆盖现有轮询集", rt.DeviceId, rt.Options.Model);
                continue;
            }

            if (channelCount == 0)
            {
                // 无通道数信息（老配置路径）：不猜，跳过并只提示一次
                _mcrConfigured[rt.DeviceId] = channelCount;
                _logger.LogWarning("设备 {DeviceId} 无 channel_count 信息，跳过全通道轮询下发（热插拔识别仅覆盖现有轮询集）", rt.DeviceId);
                continue;
            }

            var target = Enumerable.Range(1, channelCount).Select(i => (ushort)i).ToArray();
            var session = rt.Connector.GetSessionMcrChannels();
            if (session.Count == target.Length && !target.Except(session).Any())
            {
                _mcrConfigured[rt.DeviceId] = channelCount;
                continue;
            }

            try
            {
                // 竞态收窄：从循环开头的检查走到这里之间，probe-scan 可能刚占位成功——
                // 全通道下发若落在扫描器的单通道 MCR 之后，会把它的采样窗口稀释成全通道轮询。
                // 下发前紧邻处再确认一次；残余的极窄窗口由扫描器侧的 MCR 核对重发兜底（双保险）。
                if (_probeScan.IsRunning(rt.DeviceId))
                {
                    _mcrConfigured.Remove(rt.DeviceId);
                    continue;
                }
                // SingleCount=1：每通道单次采样后轮转，让全通道遍历周期最短（与 probe-scan 恢复用参数一致）
                await rt.Connector.ConfigureMultiChannelPollingAsync(target, singleCount: 1, dailyCount: 0, cancellationToken: ct);
                await rt.Connector.SetWorkModeAsync(FxWorkMode.MultiMeas, channel: 0, address: rt.Options.Address, ct);
                _mcrConfigured[rt.DeviceId] = channelCount;
                _logger.LogInformation("已下发全通道轮询（热插拔识别）device={DeviceId} channels=1..{Count}", rt.DeviceId, channelCount);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 不标记，下一轮 sweep tick 重试
                _logger.LogWarning(ex, "全通道轮询下发失败 device={DeviceId}，下轮重试", rt.DeviceId);
            }
        }

        // 已摘出运行时的设备（热禁用/删除）清掉确认标记：重新启用是新连接器新会话，必须重新收敛，
        // 残留的旧标记会让"目标通道数没变"的短路判断直接跳过下发
        var alive = new HashSet<long>(_deviceManager.Runtimes.Select(r => r.DeviceId));
        foreach (var staleId in _mcrConfigured.Keys.Where(id => !alive.Contains(id)).ToList())
            _mcrConfigured.Remove(staleId);
    }

    // ===================== 静默扫描 =====================

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _opt.SweepIntervalSeconds)));
        while (true)
        {
            try { if (!await timer.WaitForNextTickAsync(ct)) return; }
            catch (OperationCanceledException) { return; }

            // 心跳：本服务停摆则探头插拔不再被识别，UI 在位状态会一直停在最后一次的样子
            _health.Beat("channel-presence", TimeSpan.FromSeconds(Math.Max(1, _opt.SweepIntervalSeconds)));

            try
            {
                // 过期的验收会话先清掉，下面的收敛马上把 MCR 拉回全通道（会话恢复路径的兜底）
                foreach (var expired in _acceptance.ExpireStale(DateTimeOffset.UtcNow))
                    _logger.LogInformation("接入验收会话过期自动结束 device={DeviceId} channels={Count}（全通道轮询即将收敛恢复）",
                        expired.DeviceId, expired.Channels.Count);

                await EnsureFullChannelPollingAsync(ct);

                // 每轮预取一次各设备的会话 MCR 快照（避免每通道重复取），空快照 = 无 MCR 概念，全部可观测
                var mcrByDevice = _deviceManager.Runtimes.ToDictionary(
                    rt => rt.DeviceId,
                    rt => rt.Connector.GetSessionMcrChannels());
                _tracker.Sweep(
                    DateTimeOffset.UtcNow,
                    IsDeviceSuspended,
                    (deviceId, channelNo) =>
                        !mcrByDevice.TryGetValue(deviceId, out var mcr) || mcr.Count == 0 || mcr.Contains(channelNo),
                    // 拔出静默阈值随通道数自适应：48 通道全轮询下每通道 ~4.8s 才被测一次，
                    // 固定 20s 余量为零会导致在位灯抖动（详见 ChannelPresenceOptions 注释）。
                    // 验收会话期间轮询集缩小、刷新变快，仍按全通道口径取阈值——只会更稳，不会误报。
                    deviceId => _opt.EffectiveUnplugSilenceSeconds(_deviceManager.Find(deviceId)?.Options.ChannelCount ?? 0));

                // 自动建档的瞬时失败（DB 抖动等）随 sweep 重试，成功或条件不再成立即出队
                if (_opt.AutoEnroll)
                    await _enroller.RetryPendingAsync(ct);

                // 周期对账：事件丢失（消费失败/建行竞态）的终态兜底，约每分钟一次
                if (_opt.ReconcileEverySweeps > 0 && ++_sweepsSinceReconcile >= _opt.ReconcileEverySweeps)
                {
                    _sweepsSinceReconcile = 0;
                    await ReconcilePresenceAsync(ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogError(ex, "在位静默扫描异常"); }
        }
    }

    private int _sweepsSinceReconcile;

    /// <summary>
    /// 设备级假静默：挂起该设备的拔出判定并抬高静默地板。
    /// 覆盖三种情况——离线 / probe-scan 扫描中 / 设备整体无上报（待机、链路抖动等：
    /// 全设备静默是设备级条件而非探头拔出，交给 Watchdog/SignalHealth 处理，不在这里刷一屏"断开"）。
    /// </summary>
    private bool IsDeviceSuspended(long deviceId)
    {
        if (_probeScan.IsRunning(deviceId)) return true;
        var rt = _deviceManager.Find(deviceId);
        if (rt is null || rt.Connector.State != FxConnectorState.Connected) return true;

        // 扫描后宽限：mcr_single 扫完恢复业务轮询集后，设备重新跑起全通道轮转需要时间，
        // 最后被轮到的通道可能超过拔出静默阈值仍没有新回波（真机实测 ch3 扫后 36s 才回首条回波，
        // 阈值 24s → 误判拔出 → 扫描刚写的 noisy 被冲成 no_data，回波回来又翻成 healthy）。
        // 宽限期按"仍挂起"处理：静默地板继续抬，宽限结束后每通道还有完整的一个拔出窗口。
        // 取 2× 自适应窗口：1× 时总容忍 = 2×24s=48s，对实测 36s 的恢复只剩 12s 余量——
        // 设备恢复轮转受 CRC 重传/固件状态影响有波动，薄余量迟早再触发假拔出（08-18 审查确认）。
        // 2× 后总容忍 72s（余量 2 倍）；代价只是扫描期间真拔掉的探头晚 ~24s 显示离位，
        // UI 在位灯而已，signal_lost 安全告警（120s）不受影响。
        if (_probeScan.LastEndedAt(deviceId) is { } endedAt)
        {
            var grace = 2 * _opt.EffectiveUnplugSilenceSeconds((int)rt.Options.ChannelCount);
            if ((DateTimeOffset.UtcNow - endedAt).TotalSeconds < grace) return true;
        }

        // 设备级静默窗：超过 max(10s, 2×扫描周期) 整机无任何 ChannelResult → 挂起单通道判定
        var deviceSilenceWindow = Math.Max(10, _opt.SweepIntervalSeconds * 2);
        var last = rt.RuntimeStatus.LastChannelResultAt;
        return last is null || (DateTimeOffset.UtcNow - last.Value).TotalSeconds > deviceSilenceWindow;
    }

    // ===================== 事件消费（落库 + 推送） =====================

    private async Task ConsumeEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var change in _tracker.Events.ReadAllAsync(ct))
            {
                try
                {
                    await ApplyChangeAsync(change, reconciled: false, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 事件一次性消费，这里失败就丢了（状态机不会为同一状态再发一次）——
                    // 终态一致由 ReconcilePresenceAsync 的周期对账兜底，此处只记账不重投
                    _logger.LogError(ex, "在位事件处理失败 device={DeviceId} ch={Ch}（等待周期对账修复）", change.DeviceId, change.ChannelNo);
                }
            }
        }
        catch (OperationCanceledException) { /* 停机 */ }
    }

    /// <summary>
    /// 在位变化的统一应用路径：落库 + 自动建档 + 推送 + 日志。
    /// 事件消费与周期对账共用（对账 reconciled=true：日志降级、事件带 Initial 语义让前端只刷状态不弹通知）。
    /// 每一步都幂等：PersistAsync 是条件 UPDATE，enroller 事务内有在位/未绑定/hold 三重复核。
    /// </summary>
    private async Task ApplyChangeAsync(ChannelPresenceChange change, bool reconciled, CancellationToken ct)
    {
        var (channelId, boltId, boltCode) = await PersistAsync(change, ct);

        // 检测即螺栓：在位确认且通道无绑定螺栓 → 自动建档（编号自动、缺省规格）。
        // PersistAsync 已先把 hardware_state 置为在位词表态，enroller 事务内会复核在位+未绑定。
        if (change.Present && boltId is null && channelId is not null && _opt.AutoEnroll)
        {
            var enrolled = await _enroller.TryEnrollAsync(change.DeviceId, change.ChannelNo, ct);
            if (enrolled is not null) (boltId, boltCode) = (enrolled.BoltId, enrolled.Code);
        }

        try { await _publisher.PublishAsync(change, channelId, boltId, boltCode, ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "在位事件推送失败 device={DeviceId} ch={Ch}", change.DeviceId, change.ChannelNo); }

        if (reconciled)
            _logger.LogInformation("在位状态对账修复 device={DeviceId} ch={Ch} present={Present} bolt={Bolt}",
                change.DeviceId, change.ChannelNo, change.Present, boltCode ?? "未绑定");
        else if (change.Present)
            _logger.LogInformation("探头插入 device={DeviceId} ch={Ch} bolt={Bolt}{Initial}",
                change.DeviceId, change.ChannelNo, boltCode ?? "未绑定", change.Initial ? "（启动确认）" : "");
        else if (change.Initial)
            // 启动后的首次"无探头"确认：空口的常态 + 陈旧 hardware_state 自愈，不是告警事件
            _logger.LogDebug("通道无探头（启动确认）device={DeviceId} ch={Ch} bolt={Bolt}",
                change.DeviceId, change.ChannelNo, boltCode ?? "未绑定");
        else
            _logger.LogWarning("探头断开 device={DeviceId} ch={Ch} bolt={Bolt}（UI 在位显示；signal_lost 告警由 SignalHealth 独立判定）",
                change.DeviceId, change.ChannelNo, boltCode ?? "未绑定");
    }

    // ===================== 周期对账（事件丢失的兜底） =====================

    /// <summary>
    /// "内存在位状态 vs 库"对账：事件消费失败（DB 抖动/WAL 降级）、通道行未建（首测建行竞态）
    /// 都会让一次性消费的事件白丢——状态机不会为同一状态再发事件，不对账的话
    /// 不一致会一直挂到下次插拔。规则见 probe-presence-hardening-design.md §2.3。
    /// </summary>
    // internal 供集成测试直调单轮对账
    internal async Task ReconcilePresenceAsync(CancellationToken ct)
    {
        var snapshot = _tracker.Snapshot();
        if (snapshot.Count == 0) return;

        // 挂起设备（离线/扫描/整机静默）的内存状态是冻结的旧值，扫描期间库可能被 probe-scan 合法改写，
        // 拿冻结值去覆盖必错——跳过，恢复后凭新回波证据再对
        var eligible = snapshot.Select(s => s.DeviceId).Distinct()
            .Where(id => !IsDeviceSuspended(id))
            .ToHashSet();
        if (eligible.Count == 0) return;

        // 一次批量取全部候选通道的库侧状态
        var db = new Dictionary<(long DeviceId, ushort ChannelNo), (string Hw, bool Hold, bool HasBolt)>();
        await using (var cmd = _ds.CreateCommand(@"
            SELECT c.device_id, c.channel_no, c.hardware_state, c.auto_enroll_hold, (b.id IS NOT NULL)
            FROM channels c
            LEFT JOIN bolts b ON b.channel_id = c.id AND b.deleted_at IS NULL
            WHERE c.device_id = ANY(@ids) AND c.deleted_at IS NULL"))
        {
            cmd.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, eligible.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                db[(r.GetInt64(0), (ushort)r.GetInt16(1))] =
                    (r.IsDBNull(2) ? "unknown" : r.GetString(2), r.GetBoolean(3), r.GetBoolean(4));
        }

        foreach (var s in snapshot)
        {
            if (!eligible.Contains(s.DeviceId)) continue;

            bool mismatch;
            if (s.Present)
            {
                // 冻结的旧"在位"不外推；行还没被首测自动发现建出来的这轮修不了，行出现后下轮自然补上
                if (!s.EvidenceFresh) continue;
                if (!db.TryGetValue((s.DeviceId, s.ChannelNo), out var row)) continue;
                // 在位但库不在在位词表 → 补状态；状态对但没建档（channelId=null 竞态的后遗症）→ 补建档。
                // hold 挂着的不算 mismatch：人工删除抑制优先于对账
                mismatch = !BoltAutoEnroller.PresentStates.Contains(row.Hw)
                           || (_opt.AutoEnroll && !row.HasBolt && !row.Hold);
            }
            else
            {
                mismatch = db.TryGetValue((s.DeviceId, s.ChannelNo), out var row)
                           && BoltAutoEnroller.PresentStates.Contains(row.Hw);
            }
            if (!mismatch) continue;

            try
            {
                // Initial=true：对账是修复不是新事件，前端只刷状态不弹通知（与启动确认同一通道语义）
                var change = new ChannelPresenceChange(
                    s.DeviceId, s.ChannelNo, s.Present,
                    s.LastEchoAt == default ? DateTimeOffset.UtcNow : s.LastEchoAt,
                    Initial: true);
                await ApplyChangeAsync(change, reconciled: true, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 单通道修复失败不挡其他通道，下轮对账再来
                _logger.LogWarning(ex, "在位状态对账修复失败 device={DeviceId} ch={Ch}（下轮重试）", s.DeviceId, s.ChannelNo);
            }
        }
    }

    /// <summary>
    /// 更新 channels.hardware_state / last_seen_at / last_present_at 并带回绑定信息。
    /// 插入 → healthy，但已是在位词表态（healthy/noisy/suspect）时保留原值——
    /// noisy/suspect 是 probe-scan 的细分判定，在位确认不该把它抹成 healthy（重启确认/对账修复尤其如此）。
    /// 拔出 → no_data（与 probe-scan verdict→hardware_state 映射同一词表）。
    /// last_present_at 是“曾在位”的专用标记（识别页据此区分"失联"与"待接入"）——
    /// 只在 Present=true 时写，别的写入方没有（last_seen_at 会被任意上报刷新，语义不同）。
    /// Present=false 时清 auto_enroll_hold（人工删除抑制：真正离位后才允许再自动建档）。
    /// 通道行可能尚未由自动发现创建（首条测量入库前）——0 行受影响时不报错，绑定信息返回空。
    /// </summary>
    private async Task<(long? ChannelId, long? BoltId, string? BoltCode)> PersistAsync(ChannelPresenceChange change, CancellationToken ct)
    {
        const string sql = @"
            WITH updated AS (
                UPDATE channels
                   SET hardware_state = CASE
                           WHEN @present AND hardware_state IN ('healthy','noisy','suspect') THEN hardware_state
                           WHEN @present THEN 'healthy'
                           ELSE 'no_data' END,
                       last_seen_at = CASE WHEN @present THEN @at ELSE last_seen_at END,
                       last_present_at = CASE WHEN @present THEN @at ELSE last_present_at END,
                       auto_enroll_hold = CASE WHEN @present THEN auto_enroll_hold ELSE false END,
                       updated_at = now()
                 WHERE device_id = @device_id AND channel_no = @ch AND deleted_at IS NULL
                 RETURNING id
            )
            SELECT u.id, b.id, b.code
            FROM updated u
            LEFT JOIN bolts b ON b.channel_id = u.id AND b.deleted_at IS NULL";

        await using var cmd = _ds.CreateCommand(sql);
        cmd.Parameters.AddWithValue("present", NpgsqlDbType.Boolean, change.Present);
        cmd.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, change.At);
        cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, change.DeviceId);
        cmd.Parameters.AddWithValue("ch", NpgsqlDbType.Smallint, (short)change.ChannelNo);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (null, null, null);
        return (
            r.GetInt64(0),
            r.IsDBNull(1) ? null : r.GetInt64(1),
            r.IsDBNull(2) ? null : r.GetString(2));
    }
}
