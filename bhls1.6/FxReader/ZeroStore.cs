using System.Text.Json;
using FxSDK.Models;

namespace FxReader;

// 置零结果必须应用自己存着，重建会话后要原样灌回 ChannelConfig，否则自动法会漂
sealed class ZeroStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly Dictionary<string, ZeroRecord> _records;

    private ZeroStore(string path, Dictionary<string, ZeroRecord> records)
    {
        _path = path;
        _records = records;
    }

    public static ZeroStore Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return new ZeroStore(fullPath, new Dictionary<string, ZeroRecord>());
        }

        try
        {
            var records = JsonSerializer.Deserialize<Dictionary<string, ZeroRecord>>(
                File.ReadAllText(fullPath), JsonOptions);
            return new ZeroStore(fullPath, records ?? new Dictionary<string, ZeroRecord>());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"置零记录读取失败，当作没有：{ex.Message}");
            return new ZeroStore(fullPath, new Dictionary<string, ZeroRecord>());
        }
    }

    public int Apply(string endpoint, IEnumerable<ChannelConfig> channels)
    {
        var count = 0;

        foreach (var channel in channels)
        {
            if (!_records.TryGetValue(Key(endpoint, channel.Address, channel.Channel), out var record))
            {
                continue;
            }

            channel.RefTime = record.RefTime;
            channel.RefTemp = record.RefTemp;
            channel.RefEnvTime = record.RefEnvTime;
            channel.RefPeriodNs = record.RefPeriodNs;

            if (record.Wave is { Length: > 0 })
            {
                channel.LEchoWave = new EchoWave
                {
                    EchoStart = record.WaveStart,
                    ClockInt = record.WaveClock,
                    WaveData = record.Wave.ToList()
                };
            }

            count++;
        }

        return count;
    }

    public void Save(string endpoint, IEnumerable<ChannelConfig> channels)
    {
        foreach (var channel in channels)
        {
            if (!channel.RefTime.HasValue)
            {
                continue;
            }

            _records[Key(endpoint, channel.Address, channel.Channel)] = new ZeroRecord
            {
                SavedAt = DateTime.Now,
                RefTime = channel.RefTime.Value,
                RefTemp = channel.RefTemp ?? 22.22M,
                RefEnvTime = channel.RefEnvTime,
                RefPeriodNs = channel.RefPeriodNs,
                WaveStart = channel.LEchoWave?.EchoStart ?? 0,
                WaveClock = channel.LEchoWave?.ClockInt ?? 0,
                Wave = channel.LEchoWave?.WaveData?.ToArray() ?? Array.Empty<short>()
            };
        }

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(_records, JsonOptions));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"置零记录写入失败：{ex.Message}");
        }
    }

    private static string Key(string endpoint, ushort address, ushort channel)
    {
        return $"{endpoint}|{address}|{channel}";
    }
}

sealed class ZeroRecord
{
    public DateTime SavedAt { get; set; }
    public decimal RefTime { get; set; }
    public decimal RefTemp { get; set; }
    public decimal? RefEnvTime { get; set; }
    public decimal? RefPeriodNs { get; set; }
    public uint WaveStart { get; set; }
    public ushort WaveClock { get; set; }
    public short[] Wave { get; set; } = Array.Empty<short>();
}
