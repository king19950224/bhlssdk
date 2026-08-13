using BoltPreload.Application.Devices;
using BoltPreload.Application.Ingestion;
using BoltPreload.Fx;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BoltPreload.Api.Runtime;

/// <summary>
/// 设备运行时管理器：统一构造/持有所有设备的连接器、运行状态。
/// <para>
/// 从 <c>devices</c> 表加载所有启用设备，逐个建运行时（多设备）；
/// 表为空或查询失败时回退到 <c>Fx:Real:*</c> 单设备配置（向后兼容）。
/// </para>
/// <para>
/// 所有业务/诊断服务必须经本管理器按 deviceId 解析连接器，禁止直接注入单例连接器，
/// 否则多设备会静默路由到主设备。
/// </para>
/// </summary>
public sealed class DeviceConnectorManager : IDeviceConnectorManager, IAsyncDisposable
{
    // TryUpdateChannelCount / 热增删 会整体替换或增删字典条目（Options 是不可变 record），
    // 与后台服务的 Runtimes/Find 读取并发，用 _gate 串行化字典读写。
    private readonly object _gate = new();
    private readonly Dictionary<long, DeviceRuntimeContext> _runtimes;
    private long _primaryId;
    private readonly IConfiguration _configuration;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IngestionTaskRegistry _registry;
    private readonly ILogger<DeviceConnectorManager> _log;

    public DeviceConnectorManager(
        IConfiguration configuration,
        NpgsqlDataSource dataSource,
        ILoggerFactory loggerFactory,
        IngestionTaskRegistry registry)
    {
        var log = loggerFactory.CreateLogger<DeviceConnectorManager>();
        _log = log;
        _configuration = configuration;
        _dataSource = dataSource;
        _loggerFactory = loggerFactory;
        _registry = registry;

        var runtimes = BuildRealRuntimes(configuration, dataSource, loggerFactory, log);

        if (runtimes.Count == 0)
            throw new InvalidOperationException("未构造出任何设备运行时：检查 devices 表或 Fx 配置。");

        // 恰好一个 primary：若没有任何标记，第一个兜底为 primary
        if (!runtimes.Any(r => r.IsPrimary))
            runtimes[0] = runtimes[0] with { IsPrimary = true };

        _runtimes = runtimes.ToDictionary(r => r.DeviceId);
        _primaryId = runtimes.First(r => r.IsPrimary).DeviceId;

        log.LogInformation("DeviceConnectorManager 载入 {Count} 个设备运行时：{List}",
            runtimes.Count,
            string.Join(", ", runtimes.Select(r =>
                $"{r.DeviceId}/{r.Name}/{r.Options.CommunicationParameters}{(r.IsPrimary ? "(primary)" : "")}")));
    }

    public IReadOnlyCollection<DeviceRuntimeContext> Runtimes
    {
        get { lock (_gate) return _runtimes.Values.ToArray(); }
    }

    public DeviceRuntimeContext Primary
    {
        get
        {
            lock (_gate)
            {
                // RestartAsync 的摘除-重建窗口里 _primaryId 可能短暂不在字典（重建后恢复标记），
                // 退回任一在跑设备，别让并发的诊断请求在这个窗口 500
                if (_runtimes.TryGetValue(_primaryId, out var p)) return p;
                return _runtimes.Values.FirstOrDefault()
                    ?? throw new InvalidOperationException("设备运行时为空（热重启窗口），稍后重试");
            }
        }
    }

    public DeviceRuntimeContext? Find(long deviceId)
    {
        lock (_gate) return _runtimes.TryGetValue(deviceId, out var runtime) ? runtime : null;
    }

    public bool TryUpdateChannelCount(long deviceId, ushort channelCount)
    {
        var clamped = (ushort)Math.Clamp((int)channelCount, 1, DeviceLimits.MaxChannelsPerDevice);
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(deviceId, out var rt)) return false;
            if (rt.Options.ChannelCount == clamped) return true;
            _runtimes[deviceId] = rt with { Options = rt.Options with { ChannelCount = clamped } };
        }
        _log.LogInformation("运行时通道数已更新 device={DeviceId} channelCount={Count}", deviceId, clamped);
        return true;
    }

    // ===================== 热增删（IngestionService 对账拉起/收尾） =====================

    public async Task<bool> AddAsync(long deviceId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_runtimes.ContainsKey(deviceId))
                return true;
        }

        var row = await LoadDeviceRowAsync(deviceId, ct);
        if (row is null)
        {
            _log.LogWarning("热加设备失败：devices 表无 id={DeviceId} 的启用设备", deviceId);
            return false;
        }
        if (row.CommType == 2)
        {
            // SDK 1.6 移除了 TCP Server：comm_type=2 在 ConnectAsync 时直接抛 NotSupportedException。
            // 新建/改参入口已在 DeviceEndpoints 拒掉 2，这里挡的是存量脏数据
            _log.LogError("热加设备拒绝：device={DeviceId} comm_type=2(TCPServer) 已随 SDK 1.6 移除，设备主动连入需另立 FxServer 方案", deviceId);
            return false;
        }

        // 只建不连：连接、下发工作模式由 IngestionService 对账（2 秒内）拉起，与启动路径同一分工
        var runtime = BuildRealRuntimeFromDb(row, _configuration, _dataSource, _loggerFactory, isPrimary: false);
        lock (_gate)
        {
            if (!_runtimes.TryAdd(deviceId, runtime))
                return true;   // 并发加同一台：先到的赢，本次构建的运行时没连接过，丢弃无残留
        }
        _log.LogInformation("热加设备运行时 device={DeviceId} name={Name} comm={Comm}", deviceId, runtime.Name, row.CommParams);
        return true;
    }

    public Task<bool> RemoveAndStopAsync(long deviceId, TimeSpan stopTimeout, CancellationToken ct = default)
        => RemoveAndStopCoreAsync(deviceId, stopTimeout, allowPrimary: false, ct);

    public async Task<bool> RestartAsync(long deviceId, TimeSpan stopTimeout, CancellationToken ct = default)
    {
        bool wasPrimary;
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(deviceId, out var rt)) return false;
            wasPrimary = rt.IsPrimary;
        }
        if (!await RemoveAndStopCoreAsync(deviceId, stopTimeout, allowPrimary: true, ct))
            return false;
        if (!await AddAsync(deviceId, ct))
            return false;   // 行在摘除后被禁用/删除：按新事实办，不复活
        if (wasPrimary)
            TransferPrimary(deviceId);
        _log.LogInformation("设备已热重启 device={DeviceId}", deviceId);
        return true;
    }

    public bool TransferPrimary(long deviceId)
    {
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(deviceId, out var target)) return false;
            if (_runtimes.TryGetValue(_primaryId, out var old) && old.DeviceId != deviceId)
                _runtimes[old.DeviceId] = old with { IsPrimary = false };
            _runtimes[deviceId] = target with { IsPrimary = true };
            _primaryId = deviceId;
        }
        _log.LogInformation("主设备已转移 primary={DeviceId}", deviceId);
        return true;
    }

    private async Task<bool> RemoveAndStopCoreAsync(long deviceId, TimeSpan stopTimeout, bool allowPrimary, CancellationToken ct)
    {
        bool wasRunning;
        DeviceRuntimeContext? removed;
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(deviceId, out removed))
                return false;
            if (removed.IsPrimary && !allowPrimary)
                throw new InvalidOperationException("primary 设备不许摘除：先把主设备转移给另一台启用设备");
            wasRunning = _registry.IsRunning(deviceId);
            _runtimes.Remove(deviceId);
        }

        if (wasRunning)
        {
            // 消费任务在跑：supervisor 对账发现孤儿后取消任务、走 finally 清理、释放连接器并登记停止。
            // 超时不视为失败——supervisor 到点会强制释放，这里没必要吊死调用方。
            if (!await _registry.WaitStoppedAsync(deviceId, stopTimeout, ct))
                _log.LogWarning("设备 {DeviceId} 的消费任务未在 {Timeout}s 内确认停止（supervisor 会强制释放连接器）",
                    deviceId, stopTimeout.TotalSeconds);
        }
        else
        {
            // 采集服务根本没在跑（对账没登记过它）：没人会来释放这个连接器，得自己收
            try
            {
                await removed.Connector.DisposeAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "释放未在采集中的设备连接器失败 device={DeviceId}", deviceId);
            }
        }
        _log.LogInformation("设备已摘出运行时 device={DeviceId}（任务此前在跑：{WasRunning}）", deviceId, wasRunning);
        return true;
    }

    private async Task<DeviceRow?> LoadDeviceRowAsync(long deviceId, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand(@"
            SELECT id, code, model, comm_type, comm_params, address, transfer_mode, channel_count
            FROM devices
            WHERE id = @id AND enabled = true AND deleted_at IS NULL");
        cmd.Parameters.AddWithValue("id", NpgsqlTypes.NpgsqlDbType.Bigint, deviceId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new DeviceRow(
            Id: reader.GetInt64(0),
            Code: reader.GetString(1),
            Model: reader.GetInt16(2),
            CommType: reader.GetInt16(3),
            CommParams: reader.GetString(4),
            Address: reader.GetInt16(5),
            TransferMode: reader.GetInt16(6),
            ChannelCount: reader.GetInt16(7));
    }

    /// <summary>
    /// 停机时释放所有设备连接器（DI 容器对单例 IAsyncDisposable 自动调用）。
    /// 不释放会让 SDK 的 TCP 会话/解码循环残留到进程强杀,设备端留下半开连接,
    /// 重启后的新连接可能被设备拒绝或踢线。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        DeviceRuntimeContext[] snapshot;
        lock (_gate) snapshot = _runtimes.Values.ToArray();
        foreach (var rt in snapshot)
        {
            try
            {
                await rt.Connector.DisposeAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "停机释放设备连接器失败 device={DeviceId}", rt.DeviceId);
            }
        }
        _log.LogInformation("已释放全部设备连接器（{Count} 个）", _runtimes.Count);
    }

    // ===================== Real（真机，多设备 DB 驱动） =====================

    private static List<DeviceRuntimeContext> BuildRealRuntimes(
        IConfiguration configuration,
        NpgsqlDataSource dataSource,
        ILoggerFactory loggerFactory,
        ILogger log)
    {
        var rows = LoadEnabledDevices(dataSource, log);

        // SDK 1.6 移除了 TCP Server（comm_type=2 连接时抛 NotSupportedException）。
        // 存量脏行不能把整个启动构建炸穿：挑出来记 ERROR 跳过，其余设备照常起
        foreach (var bad in rows.Where(r => r.CommType == 2))
            log.LogError("跳过设备 device={DeviceId} code={Code}：comm_type=2(TCPServer) 已随 SDK 1.6 移除，设备主动连入需另立 FxServer 方案", bad.Id, bad.Code);
        rows = rows.Where(r => r.CommType != 2).ToList();

        if (rows.Count == 0)
        {
            log.LogWarning("devices 表无启用设备（或查询失败），回退到 Fx:Real:* 单设备配置");
            return [BuildRealRuntimeFromConfig(configuration, dataSource, loggerFactory)];
        }

        // primary：Fx:Real:DeviceId 指定且存在则取之，否则第一个（id 最小）
        var preferredPrimary = configuration.GetValue<long?>("Fx:Real:DeviceId");
        var primaryIndex = preferredPrimary.HasValue ? rows.FindIndex(r => r.Id == preferredPrimary.Value) : 0;
        if (primaryIndex < 0) primaryIndex = 0;

        var list = new List<DeviceRuntimeContext>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
            list.Add(BuildRealRuntimeFromDb(rows[i], configuration, dataSource, loggerFactory, isPrimary: i == primaryIndex));
        return list;
    }

    private static DeviceRuntimeContext BuildRealRuntimeFromDb(
        DeviceRow row,
        IConfiguration configuration,
        NpgsqlDataSource dataSource,
        ILoggerFactory loggerFactory,
        bool isPrimary)
    {
        var logger = loggerFactory.CreateLogger<FxSdkConnector>();
        var spec = new FxRealDeviceSpec(
            DeviceId: row.Id,
            Model: (ushort)row.Model,
            CommType: (byte)row.CommType,
            CommParams: row.CommParams,
            Address: (ushort)row.Address,
            TransferMode: (ushort)row.TransferMode,
            ChannelCount: (ushort)Math.Clamp((int)row.ChannelCount, 1, DeviceLimits.MaxChannelsPerDevice));

        // 多设备严格按 channels 表加载每设备通道，不用单设备 appsettings 兜底
        var initialChannels = FxInitialChannelConfigLoader.Load(
            dataSource, configuration, spec.Address, row.Id, logger, includeAppSettingsFallback: false);
        var connector = FxConnectorFactory.CreateReal(spec, initialChannels, logger);

        return new DeviceRuntimeContext(
            DeviceId: row.Id,
            Name: string.IsNullOrWhiteSpace(row.Code) ? $"Device-{row.Id}" : row.Code,
            IsPrimary: isPrimary,
            Connector: connector,
            RuntimeStatus: new IngestionRuntimeStatus(),
            Options: new DeviceRuntimeOptions(
                CommunicationParameters: spec.CommParams,
                Address: spec.Address,
                TransferMode: spec.TransferMode,
                Model: spec.Model,
                ChannelCount: spec.ChannelCount));
    }

    private static DeviceRuntimeContext BuildRealRuntimeFromConfig(
        IConfiguration configuration,
        NpgsqlDataSource dataSource,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<FxSdkConnector>();
        var deviceId = configuration.GetValue<long>("Fx:Real:DeviceId", 1);
        // 配置优先，缺了才读库：GetValue(key, default) 的默认值是先求值再传进去的，
        // 写成默认参数会让配置已给出通道数时也白查一次库，还可能打出一条与实际取值无关的回退警告
        var configuredChannelCount = configuration.GetValue<ushort?>("Fx:Real:ChannelCount");
        var spec = new FxRealDeviceSpec(
            DeviceId: deviceId,
            Model: configuration.GetValue<ushort>("Fx:Real:Model", 3),
            CommType: configuration.GetValue<byte>("Fx:Real:CommType", 1),
            CommParams: configuration["Fx:Real:CommParams"]
                ?? throw new InvalidOperationException("缺少 Fx:Real:CommParams 配置"),
            Address: configuration.GetValue<ushort>("Fx:Real:Address", 1),
            TransferMode: configuration.GetValue<ushort>("Fx:Real:TransferMode", 1),
            ChannelCount: configuredChannelCount
                ?? LoadDeviceChannelCount(dataSource, deviceId, DeviceLimits.DefaultChannelCount, logger));

        var initialChannels = FxInitialChannelConfigLoader.Load(
            dataSource, configuration, spec.Address, deviceId, logger);
        var connector = FxConnectorFactory.CreateReal(spec, initialChannels, logger);

        return new DeviceRuntimeContext(
            DeviceId: deviceId,
            Name: configuration["Fx:Real:Name"] ?? $"Device-{deviceId}",
            IsPrimary: true,
            Connector: connector,
            RuntimeStatus: new IngestionRuntimeStatus(),
            Options: new DeviceRuntimeOptions(
                CommunicationParameters: spec.CommParams,
                Address: spec.Address,
                TransferMode: spec.TransferMode,
                Model: spec.Model,
                ChannelCount: spec.ChannelCount));
    }

    private static List<DeviceRow> LoadEnabledDevices(NpgsqlDataSource dataSource, ILogger log)
    {
        var rows = new List<DeviceRow>();
        try
        {
            using var cmd = dataSource.CreateCommand(@"
                SELECT id, code, model, comm_type, comm_params, address, transfer_mode, channel_count
                FROM devices
                WHERE enabled = true AND deleted_at IS NULL
                ORDER BY id");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new DeviceRow(
                    Id: reader.GetInt64(0),
                    Code: reader.GetString(1),
                    Model: reader.GetInt16(2),
                    CommType: reader.GetInt16(3),
                    CommParams: reader.GetString(4),
                    Address: reader.GetInt16(5),
                    TransferMode: reader.GetInt16(6),
                    ChannelCount: reader.GetInt16(7)));
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "从 devices 表加载启用设备失败，将回退单设备配置");
            return [];
        }
        return rows;
    }

    /// <summary>
    /// 从 devices 表读该设备的通道数，读不到时回退。
    /// <para>
    /// 回退必须留痕：采集按这个数轮询，真机通道比它多的话，多出来的探头就永远不会被采样。
    /// 那些通道在界面上表现为"暂无数据"，和探头没插、线没通完全分不出来，
    /// 排查的人会去现场查线，而根因在这条读库上。
    /// </para>
    /// </summary>
    private static ushort LoadDeviceChannelCount(
        NpgsqlDataSource dataSource, long deviceId, ushort fallback, ILogger log)
    {
        try
        {
            using var cmd = dataSource.CreateCommand(@"
                SELECT COALESCE(channel_count, @fallback)
                FROM devices
                WHERE id = @device_id AND deleted_at IS NULL");
            cmd.Parameters.AddWithValue("device_id", NpgsqlTypes.NpgsqlDbType.Bigint, deviceId);
            cmd.Parameters.AddWithValue("fallback", NpgsqlTypes.NpgsqlDbType.Smallint, (short)fallback);
            var scalar = cmd.ExecuteScalar();
            // COALESCE 保证行存在就有值，所以取不到值只有一种含义：这个 device_id 在表里没有（或已软删）
            if (scalar is null or DBNull)
            {
                log.LogWarning(
                    "devices 表里没有 id={DeviceId} 的启用设备，通道数回退为 {Fallback}；" +
                    "真机通道多于此数时，多出的探头不会被采样（表现为通道无数据）",
                    deviceId, fallback);
                return fallback;
            }
            return (ushort)Math.Clamp(Convert.ToInt32(scalar), 1, DeviceLimits.MaxChannelsPerDevice);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "读取 device={DeviceId} 的通道数失败，回退为 {Fallback}；" +
                "真机通道多于此数时，多出的探头不会被采样（表现为通道无数据）",
                deviceId, fallback);
            return fallback;
        }
    }

    private sealed record DeviceRow(
        long Id,
        string Code,
        short Model,
        short CommType,
        string CommParams,
        short Address,
        short TransferMode,
        short ChannelCount);
}
