using System.Globalization;
using BoltPreload.Fx;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Api;

/// <summary>
/// 启动时加载 SDK 本地 SessionContext 所需的通道配置（返回 plain 的 <see cref="FxChannelInit"/>，
/// 由 <see cref="FxConnectorFactory"/> 在适配层映射回厂家 SDK 的 ChannelConfig——上层不直接 using FxSDK.*）。
/// <para>
/// 关键事实：厂家 SDK 的 <c>MultiChIotProtocolHandler.ProcessResult</c>
/// 只有在 SessionContext 里有 <c>ChannelConfig</c> 且 <c>ProbeFreq &gt; 0</c> 时，
/// 才会根据波形计算 <c>EchoTime</c>。这些配置用 <c>sendCommand=false</c> 写入 SDK 本地会话，
/// 不会写设备。
/// </para>
/// <para>优先级：设备参数历史（真实读回） &gt; appsettings 兜底 &gt; channels 表默认值。</para>
/// </summary>
public static class FxInitialChannelConfigLoader
{
    public static IReadOnlyList<FxChannelInit> Load(
        NpgsqlDataSource dataSource,
        IConfiguration configuration,
        ushort defaultAddress,
        long deviceId,
        ILogger logger,
        bool includeAppSettingsFallback = true)
    {
        var configs = new Dictionary<ushort, FxChannelInit>();

        foreach (var cfg in LoadFromDatabase(dataSource, deviceId, defaultAddress, logger))
            configs[cfg.Channel] = cfg;

        // appsettings 兜底仅适用于"单设备配置"路径（Fx:Real:Channels 是单设备约定）。
        // 多设备 DB 驱动加载时必须 includeAppSettingsFallback=false：各设备通道严格来自 channels 表，
        // 否则会把同一组兜底通道错误套到所有设备上。
        if (includeAppSettingsFallback)
        {
            foreach (var fallback in LoadFromAppSettings(configuration, defaultAddress))
            {
                if (!configs.TryGetValue(fallback.Channel, out var existing))
                {
                    configs[fallback.Channel] = fallback;
                    continue;
                }

                MergeMissing(existing, fallback);
            }
        }

        if (configs.Count == 0)
        {
            logger.LogWarning("未加载到任何 Fx ChannelConfig，SDK 将无法根据波形计算 EchoTime");
        }
        else
        {
            logger.LogInformation(
                "启动加载 Fx ChannelConfig {Count} 个：{Channels}",
                configs.Count,
                string.Join(", ", configs.Values.Select(c =>
                    $"ch={c.Channel}/probe={c.ProbeFreq}/mode={c.MeasMode}/thr={c.Threshold}/ref={c.RefTime}@{c.RefTemp}")));
        }

        return configs.Values.OrderBy(c => c.Channel).ToArray();
    }

    private static List<FxChannelInit> LoadFromDatabase(
        NpgsqlDataSource dataSource,
        long deviceId,
        ushort defaultAddress,
        ILogger logger)
    {
        var configs = new Dictionary<ushort, FxChannelInit>();

        try
        {
            using var cmd = dataSource.CreateCommand(@"
                SELECT channel_no, probe_freq_mhz, gain, threshold, echo_win_ns,
                       echo_width_us, meas_mode, tc_mode, temp_coefficient,
                       ref_time_ns, ref_temp_c
                FROM channels
                WHERE device_id = @device_id AND deleted_at IS NULL
                ORDER BY channel_no");
            cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var channelNo = (ushort)reader.GetInt16(0);
                configs[channelNo] = new FxChannelInit
                {
                    Address = defaultAddress,
                    Channel = channelNo,
                    ProbeFreq = reader.GetDecimal(1),
                    Gain = Convert.ToDecimal(reader.GetFloat(2), CultureInfo.InvariantCulture),
                    Threshold = reader.GetInt16(3),
                    EchoWin = reader.IsDBNull(4) ? 0m : reader.GetDecimal(4),
                    EchoWidth = (ushort)reader.GetInt16(5),
                    MeasMode = (ushort)reader.GetInt16(6),
                    TcMode = (ushort)reader.GetInt16(7),
                    TempCoefficient = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                    RefTime = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                    RefTemp = reader.IsDBNull(10) ? null : reader.GetDecimal(10)
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "从 channels 表加载 Fx ChannelConfig 失败，改用 appsettings 兜底");
        }

        OverlayLatestDeviceParameters(dataSource, deviceId, configs, logger);

        return configs.Values.ToList();
    }

    private static void OverlayLatestDeviceParameters(
        NpgsqlDataSource dataSource,
        long deviceId,
        Dictionary<ushort, FxChannelInit> configs,
        ILogger logger)
    {
        try
        {
            using var cmd = dataSource.CreateCommand(@"
                SELECT DISTINCT ON (channel_no, param_name)
                       channel_no, param_name, param_value_text
                FROM device_parameter_history
                WHERE device_id = @device_id
                  AND channel_no IS NOT NULL
                  AND param_value_text IS NOT NULL
                  AND param_name IN (
                    'ProbeFreq','Gain','Threshold','MeasMode','RefTime','RefTemp',
                    'EchoWin','EchoWidth','TcMode','TempCoefficient'
                  )
                ORDER BY channel_no, param_name, received_at DESC");
            cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var channelNo = (ushort)reader.GetInt16(0);
                var param = reader.GetString(1);
                var value = reader.GetString(2);

                // 只覆盖 channels 表里仍存在(未软删)的通道。
                // 不能在这里"复活"已删除通道：参数历史是永久留痕，但 MCRConfig 轮询清单
                // 必须严格来自 channels 表（见类注释），否则删掉的空通道会被重新轮询，
                // 持续产生"信号过小"异常,且 autodiscover 会把删除的通道行再建回来。
                if (!configs.TryGetValue(channelNo, out var cfg))
                    continue;

                ApplyParam(cfg, param, value);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "用 device_parameter_history 覆盖 Fx ChannelConfig 失败");
        }
    }

    private static IEnumerable<FxChannelInit> LoadFromAppSettings(IConfiguration configuration, ushort defaultAddress)
    {
        return configuration
            .GetSection("Fx:Real:Channels")
            .GetChildren()
            .Select(section => new FxChannelInit
            {
                Address = section.GetValue<ushort>("Address", defaultAddress),
                Channel = section.GetValue<ushort>("Channel", 1),
                ProbeFreq = section.GetValue<decimal>("ProbeFreqMhz", 2m),
                Gain = section.GetValue<decimal>("Gain", 10m),
                MeasMode = section.GetValue<ushort>("MeasMode", 2),
                Threshold = section.GetValue<short>("Threshold", 0),
                RefTime = section.GetValue<decimal?>("RefTimeNs"),
                RefTemp = section.GetValue<decimal?>("RefTempC"),
                TcMode = section.GetValue<ushort>("TcMode", 1),
                TempCoefficient = section.GetValue<decimal?>("TempCoefficient")
            });
    }

    private static void MergeMissing(FxChannelInit target, FxChannelInit fallback)
    {
        if (target.ProbeFreq <= 0m) target.ProbeFreq = fallback.ProbeFreq;
        if (target.Gain <= 0m) target.Gain = fallback.Gain;
        if (target.Threshold == 0 && fallback.Threshold != 0) target.Threshold = fallback.Threshold;
        if (target.MeasMode == 2 && fallback.MeasMode != 2) target.MeasMode = fallback.MeasMode;
        if (target.RefTime is null) target.RefTime = fallback.RefTime;
        if (target.RefTemp is null) target.RefTemp = fallback.RefTemp;
        if (target.TempCoefficient is null) target.TempCoefficient = fallback.TempCoefficient;
    }

    private static void ApplyParam(FxChannelInit cfg, string param, string value)
    {
        switch (param)
        {
            case "ProbeFreq":
                if (TryDecimal(value, out var probe)) cfg.ProbeFreq = probe;
                break;
            case "Gain":
                if (TryDecimal(value, out var gain)) cfg.Gain = gain;
                break;
            case "Threshold":
                if (short.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var threshold))
                    cfg.Threshold = threshold;
                break;
            case "MeasMode":
                if (ushort.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var mode))
                    cfg.MeasMode = mode;
                break;
            case "RefTime":
                if (TryDecimal(value, out var refTime)) cfg.RefTime = refTime;
                break;
            case "RefTemp":
                if (TryDecimal(value, out var refTemp)) cfg.RefTemp = refTemp;
                break;
            case "EchoWin":
                if (TryDecimal(value, out var echoWin)) cfg.EchoWin = echoWin;
                break;
            case "EchoWidth":
                if (ushort.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var echoWidth))
                    cfg.EchoWidth = echoWidth;
                break;
            case "TcMode":
                if (ushort.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var tcMode))
                    cfg.TcMode = tcMode;
                break;
            case "TempCoefficient":
                if (TryDecimal(value, out var tc)) cfg.TempCoefficient = tc;
                break;
        }
    }

    private static bool TryDecimal(string value, out decimal result)
        => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
}
