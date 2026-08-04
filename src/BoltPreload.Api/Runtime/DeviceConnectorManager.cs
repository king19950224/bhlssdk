using BoltPreload.Application.Devices;
using BoltPreload.Application.Ingestion;
using BoltPreload.Fx;
using BoltPreload.Fx.Simulator;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BoltPreload.Api.Runtime;

/// <summary>
/// 设备运行时管理器：统一构造/持有所有设备的连接器、运行状态。
/// <para>
/// Real 模式从 <c>devices</c> 表加载所有启用设备，逐个建运行时（多设备）；
/// 表为空或查询失败时回退到 <c>Fx:Real:*</c> 单设备配置（向后兼容）。
/// Simulated 模式按 <c>Fx:Simulator:DeviceCount</c> 建 1..N 个进程内模拟设备（无硬件多设备测试用）。
/// </para>
/// <para>
/// 所有业务/诊断服务必须经本管理器按 deviceId 解析连接器，禁止直接注入单例连接器，
/// 否则多设备会静默路由到主设备。
/// </para>
/// </summary>
public sealed class DeviceConnectorManager : IDeviceConnectorManager, IAsyncDisposable
{
    // TryUpdateChannelCount 会整体替换字典里的 record（Options 是不可变 record），
    // 与后台服务的 Runtimes/Find 读取并发，用 _gate 串行化字典读写。
    private readonly object _gate = new();
    private readonly Dictionary<long, DeviceRuntimeContext> _runtimes;
    private readonly long _primaryId;
    private readonly ILogger<DeviceConnectorManager> _log;

    public DeviceConnectorManager(
        IConfiguration configuration,
        NpgsqlDataSource dataSource,
        ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger<DeviceConnectorManager>();
        _log = log;

        // 缺 Fx:Mode 时按 Real 兜底，不是 Simulated：模拟器会产出物理上自洽、看起来完全正常的预紧力，
        // 现场若因配置丢失而静默跑成仿真，值班人员盯着的是假数据 —— 对安全监测系统这是最坏的失效方向。
        // 退到 Real 时若没有可用设备配置，表现为"连不上/无数据"，是能被看见的失败。
        var fxMode = configuration["Fx:Mode"];
        if (string.IsNullOrWhiteSpace(fxMode))
        {
            fxMode = "Real";
            log.LogWarning("未配置 Fx:Mode，按 Real（真机）兜底。若本机是开发/演示环境需显式设置 Fx:Mode=Simulated。");
        }

        var runtimes = string.Equals(fxMode, "Real", StringComparison.OrdinalIgnoreCase)
            ? BuildRealRuntimes(configuration, dataSource, loggerFactory, log)
            : BuildSimulatorRuntimes(configuration, loggerFactory, log);

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
                $"{r.DeviceId}/{r.Name}/{r.Options.Mode}{(r.IsPrimary ? "(primary)" : "")}")));
    }

    public IReadOnlyCollection<DeviceRuntimeContext> Runtimes
    {
        get { lock (_gate) return _runtimes.Values.ToArray(); }
    }

    public DeviceRuntimeContext Primary
    {
        get { lock (_gate) return _runtimes[_primaryId]; }
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
        var logger = loggerFactory.GetRequiredLogger<FxSdkConnector>();
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
                Mode: "Real",
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
        var logger = loggerFactory.GetRequiredLogger<FxSdkConnector>();
        var deviceId = configuration.GetValue<long>("Fx:Real:DeviceId", 1);
        var spec = new FxRealDeviceSpec(
            DeviceId: deviceId,
            Model: configuration.GetValue<ushort>("Fx:Real:Model", 3),
            CommType: configuration.GetValue<byte>("Fx:Real:CommType", 1),
            CommParams: configuration["Fx:Real:CommParams"]
                ?? throw new InvalidOperationException("缺少 Fx:Real:CommParams 配置"),
            Address: configuration.GetValue<ushort>("Fx:Real:Address", 1),
            TransferMode: configuration.GetValue<ushort>("Fx:Real:TransferMode", 1),
            ChannelCount: configuration.GetValue<ushort>(
                "Fx:Real:ChannelCount",
                LoadDeviceChannelCount(dataSource, deviceId, fallback: DeviceLimits.DefaultChannelCount)));

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
                Mode: "Real",
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

    private static ushort LoadDeviceChannelCount(NpgsqlDataSource dataSource, long deviceId, ushort fallback)
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
            return scalar is null || scalar is DBNull
                ? fallback
                : (ushort)Math.Clamp(Convert.ToInt32(scalar), 1, DeviceLimits.MaxChannelsPerDevice);
        }
        catch
        {
            return fallback;
        }
    }

    // ===================== Simulated（进程内模拟，可多设备） =====================

    private static List<DeviceRuntimeContext> BuildSimulatorRuntimes(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        ILogger log)
    {
        var logger = loggerFactory.GetRequiredLogger<SimulatedFxConnector>();
        var baseDeviceId = configuration.GetValue<long>("Fx:Simulator:DeviceId", 1);
        var address = configuration.GetValue<ushort>("Fx:Simulator:Address", 1);
        var channelCount = configuration.GetValue<ushort>("Fx:Simulator:ChannelCount", 3);
        var rate = configuration.GetValue<double>("Fx:Simulator:SamplingRateHz", 5);
        var count = Math.Clamp(configuration.GetValue<int>("Fx:Simulator:DeviceCount", 1), 1, 50);

        var list = new List<DeviceRuntimeContext>(count);
        for (var i = 0; i < count; i++)
        {
            var deviceId = baseDeviceId + i;
            var options = new SimulatorOptions(
                DeviceId: deviceId,
                Address: address,
                ChannelCount: channelCount,
                SamplingRateHz: rate);
            var connector = new SimulatedFxConnector(options, logger);
            list.Add(new DeviceRuntimeContext(
                DeviceId: deviceId,
                Name: $"Simulator-{deviceId}",
                IsPrimary: i == 0,
                Connector: connector,
                RuntimeStatus: new IngestionRuntimeStatus(),
                Options: new DeviceRuntimeOptions(
                    Mode: "Simulated",
                    CommunicationParameters: "in-process",
                    Address: address,
                    TransferMode: 1,
                    Model: 3,
                    ChannelCount: channelCount)));
        }

        if (count > 1)
            log.LogInformation("多模拟器模式：{Count} 个模拟设备 deviceId={Base}..{Max}",
                count, baseDeviceId, baseDeviceId + count - 1);
        return list;
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

internal static class LoggerFactoryExtensions
{
    public static ILogger<T> GetRequiredLogger<T>(this ILoggerFactory loggerFactory)
        => loggerFactory.CreateLogger<T>();
}
