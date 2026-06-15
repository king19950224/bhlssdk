using BoltPreload.Application.Devices;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace BoltPreload.Infrastructure.Devices;

public sealed class PostgresChannelConfigService : IChannelConfigService
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresChannelConfigService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<ChannelConfigView?> GetAsync(long channelId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT id, device_id, channel_no, state, sampling_enabled, hardware_state, business_state,
                   config_source, config_synced_at,
                   probe_freq_mhz, gain, threshold, meas_mode,
                   ref_time_ns, ref_temp_c, tc_mode, temp_coefficient, echo_win_ns, echo_width_us,
                   k_l, k_l_intercept_ns, active_calibration_id, last_seen_at
            FROM channels
            WHERE id = @id AND deleted_at IS NULL";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, channelId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;

        var deviceId = r.GetInt64(1);
        var channelNo = (ushort)r.GetInt16(2);
        var desired = new ChannelConfigValues(
            ProbeFreqMhz: r.GetDecimal(9),
            Gain: Convert.ToDecimal(r.GetFloat(10), CultureInfo.InvariantCulture),
            Threshold: r.GetInt16(11),
            MeasMode: (ushort)r.GetInt16(12),
            RefTimeNs: r.IsDBNull(13) ? null : r.GetDecimal(13),
            RefTempC: r.IsDBNull(14) ? null : r.GetDecimal(14),
            TcMode: (ushort)r.GetInt16(15),
            TempCoefficient: r.IsDBNull(16) ? null : r.GetDecimal(16),
            EchoWinNs: r.IsDBNull(17) ? null : r.GetDecimal(17),
            EchoWidthUs: r.IsDBNull(18) ? null : (ushort)r.GetInt16(18),
            Source: r.GetString(7));
        var actual = await LoadActualAsync(deviceId, channelNo, ct);
        var effective = MergeEffective(desired, actual);

        return new ChannelConfigView(
            ChannelId: r.GetInt64(0),
            DeviceId: deviceId,
            ChannelNo: channelNo,
            State: r.GetString(3),
            SamplingEnabled: r.GetBoolean(4),
            HardwareState: r.GetString(5),
            BusinessState: r.GetString(6),
            ConfigSource: r.GetString(7),
            ConfigSyncedAt: r.IsDBNull(8) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(8), DateTimeKind.Utc)),
            ProbeFreqMhz: desired.ProbeFreqMhz!.Value,
            Gain: r.GetFloat(10),
            Threshold: desired.Threshold!.Value,
            MeasMode: desired.MeasMode!.Value,
            RefTimeNs: desired.RefTimeNs,
            RefTempC: desired.RefTempC,
            TcMode: desired.TcMode!.Value,
            TempCoefficient: desired.TempCoefficient,
            KL: r.IsDBNull(19) ? null : r.GetDecimal(19),
            KLInterceptNs: r.IsDBNull(20) ? null : r.GetDecimal(20),
            ActiveCalibrationId: r.IsDBNull(21) ? null : r.GetGuid(21),
            LastSeenAt: r.IsDBNull(22) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(22), DateTimeKind.Utc)),
            Desired: desired,
            Actual: actual,
            Effective: effective);
    }

    public async Task<IReadOnlyList<ChannelConfigDifference>> CompareAsync(long deviceId, ushort channelNo, CancellationToken ct = default)
    {
        const string sql = @"
            WITH dbv AS (
                SELECT probe_freq_mhz::text AS ProbeFreq,
                       gain::text AS Gain,
                       threshold::text AS Threshold,
                       meas_mode::text AS MeasMode,
                       ref_time_ns::text AS RefTime,
                       ref_temp_c::text AS RefTemp,
                       tc_mode::text AS TcMode,
                       temp_coefficient::text AS TempCoefficient
                FROM channels
                WHERE device_id = @device_id AND channel_no = @channel_no AND deleted_at IS NULL
                LIMIT 1
            ),
            dev AS (
                SELECT DISTINCT ON (param_name)
                       param_name, param_value_text
                FROM device_parameter_history
                WHERE device_id = @device_id
                  AND channel_no = @channel_no
                  AND param_name IN ('ProbeFreq','Gain','Threshold','MeasMode','RefTime','RefTemp','TcMode','TempCoefficient')
                  AND param_value_text IS NOT NULL
                ORDER BY param_name, received_at DESC
            )
            SELECT field, db_value, device_value
            FROM (
                SELECT 'ProbeFreq' AS field, dbv.ProbeFreq AS db_value, (SELECT param_value_text FROM dev WHERE param_name='ProbeFreq') AS device_value FROM dbv
                UNION ALL SELECT 'Gain', dbv.Gain, (SELECT param_value_text FROM dev WHERE param_name='Gain') FROM dbv
                UNION ALL SELECT 'Threshold', dbv.Threshold, (SELECT param_value_text FROM dev WHERE param_name='Threshold') FROM dbv
                UNION ALL SELECT 'MeasMode', dbv.MeasMode, (SELECT param_value_text FROM dev WHERE param_name='MeasMode') FROM dbv
                UNION ALL SELECT 'RefTime', dbv.RefTime, (SELECT param_value_text FROM dev WHERE param_name='RefTime') FROM dbv
                UNION ALL SELECT 'RefTemp', dbv.RefTemp, (SELECT param_value_text FROM dev WHERE param_name='RefTemp') FROM dbv
                UNION ALL SELECT 'TcMode', dbv.TcMode, (SELECT param_value_text FROM dev WHERE param_name='TcMode') FROM dbv
                UNION ALL SELECT 'TempCoefficient', dbv.TempCoefficient, (SELECT param_value_text FROM dev WHERE param_name='TempCoefficient') FROM dbv
            ) x";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
        cmd.Parameters.AddWithValue("channel_no", NpgsqlDbType.Smallint, (short)channelNo);

        var items = new List<ChannelConfigDifference>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var field = r.GetString(0);
            var dbValue = r.IsDBNull(1) ? null : Normalize(r.GetString(1));
            var deviceValue = r.IsDBNull(2) ? null : Normalize(r.GetString(2));
            var different = deviceValue is not null && dbValue != deviceValue;
            items.Add(new ChannelConfigDifference(
                Field: field,
                DatabaseValue: dbValue,
                DeviceValue: deviceValue,
                IsDifferent: different,
                Severity: different ? (field is "RefTime" or "RefTemp" or "MeasMode" ? "warn" : "info") : "ok"));
        }

        return items;
    }

    private static string Normalize(string value)
    {
        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
        {
            return d.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        return value;
    }

    private async Task<ChannelConfigValues?> LoadActualAsync(long deviceId, ushort channelNo, CancellationToken ct)
    {
        const string sql = @"
            SELECT DISTINCT ON (param_name) param_name, param_value_text
            FROM device_parameter_history
            WHERE device_id = @device_id
              AND channel_no = @channel_no
              AND param_value_text IS NOT NULL
              AND param_name IN ('ProbeFreq','Gain','Threshold','MeasMode','RefTime','RefTemp','TcMode','TempCoefficient','EchoWin','EchoWidth')
            ORDER BY param_name, received_at DESC";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("device_id", NpgsqlDbType.Bigint, deviceId);
        cmd.Parameters.AddWithValue("channel_no", NpgsqlDbType.Smallint, (short)channelNo);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            values[r.GetString(0)] = r.GetString(1);
        }

        if (values.Count == 0) return null;

        return new ChannelConfigValues(
            ProbeFreqMhz: TryDecimal(values, "ProbeFreq"),
            Gain: TryDecimal(values, "Gain"),
            Threshold: TryShort(values, "Threshold"),
            MeasMode: TryUshort(values, "MeasMode"),
            RefTimeNs: TryDecimal(values, "RefTime"),
            RefTempC: TryDecimal(values, "RefTemp"),
            TcMode: TryUshort(values, "TcMode"),
            TempCoefficient: TryDecimal(values, "TempCoefficient"),
            EchoWinNs: TryDecimal(values, "EchoWin"),
            EchoWidthUs: TryUshort(values, "EchoWidth"),
            Source: "device_readback");
    }

    private static ChannelConfigValues MergeEffective(ChannelConfigValues desired, ChannelConfigValues? actual)
    {
        if (actual is null) return desired with { Source = "database" };

        return new ChannelConfigValues(
            ProbeFreqMhz: actual.ProbeFreqMhz ?? desired.ProbeFreqMhz,
            Gain: actual.Gain ?? desired.Gain,
            Threshold: actual.Threshold ?? desired.Threshold,
            MeasMode: actual.MeasMode ?? desired.MeasMode,
            RefTimeNs: actual.RefTimeNs ?? desired.RefTimeNs,
            RefTempC: actual.RefTempC ?? desired.RefTempC,
            TcMode: actual.TcMode ?? desired.TcMode,
            TempCoefficient: actual.TempCoefficient ?? desired.TempCoefficient,
            EchoWinNs: actual.EchoWinNs ?? desired.EchoWinNs,
            EchoWidthUs: actual.EchoWidthUs ?? desired.EchoWidthUs,
            Source: "device_readback_over_database");
    }

    private static decimal? TryDecimal(Dictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value)
           && decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static short? TryShort(Dictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value)
           && short.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static ushort? TryUshort(Dictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value)
           && ushort.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
}
