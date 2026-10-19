using System.Text.Json;
using System.Text.Json.Serialization;
using FxSDK.Models;

namespace FxReader;

sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public List<DeviceSettings> Devices { get; set; } = new();
    public int ReconnectSeconds { get; set; } = 10;
    public int StatusSeconds { get; set; } = 30;
    public double PrintIntervalSeconds { get; set; } = 1;

    // 空则不记
    public string RawDir { get; set; } = string.Empty;

    // 原始报文要开 SDK 开发者模式才解得出
    public bool RawHex { get; set; } = true;

    // 0 不显示。够大就整条按 hexdump 排
    public int HexPreviewBytes { get; set; } = 32;

    // 空则不存，启动也不回灌
    public string ZeroFile { get; set; } = "zero.json";

    public static AppSettings Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"找不到配置文件 {fullPath}");
        }

        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(fullPath), JsonOptions)
                       ?? throw new InvalidDataException("配置文件内容为空");

        foreach (var device in settings.Devices)
        {
            device.Validate();
        }

        return settings;
    }
}

sealed class DeviceSettings
{
    private const decimal DefaultSoundSpeed = 5890M;

    public string Name { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public ushort Address { get; set; } = 1;

    // 留空时只有 Address 一个终端
    public ushort[] TerminalAddresses { get; set; } = Array.Empty<ushort>();

    public ushort ChannelCount { get; set; } = 8;

    // false：只收数据，不下发、不切模式
    public bool ApplyConfigOnConnect { get; set; } = true;

    public double ConnectTimeout { get; set; } = 20;
    public ushort TransferMode { get; set; } = 1;
    public decimal Frequency { get; set; } = 10;
    public ushort SampleDepth { get; set; } = 4;
    public ushort DailyCount { get; set; }
    public ushort SingleCount { get; set; } = 30;
    public ChannelSettings Channel { get; set; } = new();
    public List<ChannelSettings> ChannelOverrides { get; set; } = new();

    [JsonIgnore]
    public string Title => string.IsNullOrWhiteSpace(Name) ? Endpoint : $"{Name}({Endpoint})";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            throw new InvalidDataException($"设备 {Name} 没有配置 endpoint");
        }

        if (!Endpoint.Contains(':'))
        {
            throw new InvalidDataException($"设备 {Title} 的 endpoint 要写成 IP:端口");
        }

        if (ChannelCount == 0)
        {
            throw new InvalidDataException($"设备 {Title} 的 channelCount 不能为 0");
        }

        if (TerminalAddresses.Length > 0 && !TerminalAddresses.Contains(Address))
        {
            Address = TerminalAddresses[0];
        }
    }

    public ushort[] Terminals()
    {
        return TerminalAddresses.Length > 0 ? TerminalAddresses : new[] { Address };
    }

    public DeviceConfig BuildDevice()
    {
        var terminals = Terminals();
        var mcr = new MCRConfig
        {
            DailyCount = DailyCount,
            SingleCount = SingleCount
        };

        var device = new DeviceConfig
        {
            Model = 3,
            CommunicationType = 1,
            CommunicationParameters = Endpoint,
            ConnTimeOut = ConnectTimeout,
            TransferMode = TransferMode,
            Address = Address,
            UsChannelCount = ChannelCount,
            MaxTerminal = (ushort)terminals.Length,
            TerminalAddresses = terminals,
            MeasConfig = new MeasConfig
            {
                Frequency = Frequency,
                SampleDepth = SampleDepth
            },
            MCRConfig = mcr
        };

        // 轮询用逻辑通道，地址不为 1 时不能直接填物理通道号
        mcr.EnabledChannels = terminals
            .SelectMany(address => Enumerable
                .Range(1, ChannelCount)
                .Select(channel => device.GetLogicChannel(address, (ushort)channel)))
            .ToList();

        return device;
    }

    public List<ChannelConfig> BuildChannels()
    {
        var soundSpeed = Channel.SoundSpeed ?? DefaultSoundSpeed;
        var channels = new List<ChannelConfig>();

        foreach (var address in Terminals())
        {
            for (ushort channel = 1; channel <= ChannelCount; channel++)
            {
                var config = new ChannelConfig { Address = address, Channel = channel };
                Channel.Apply(config, soundSpeed);

                var patch = ChannelOverrides.FirstOrDefault(
                    x => x.Channel == channel && (x.Address == 0 || x.Address == address));
                patch?.Apply(config, soundSpeed);

                channels.Add(config);
            }
        }

        return channels;
    }
}

sealed class ChannelSettings
{
    // 只在 channelOverrides 里用，0 表示不限定
    public ushort Address { get; set; }
    public ushort Channel { get; set; }

    public decimal? ProbeFreq { get; set; }
    public decimal? LengthMm { get; set; }
    public decimal? SoundSpeed { get; set; }
    public decimal? EchoWin { get; set; }
    public ushort? EchoWidth { get; set; }
    public decimal? Gain { get; set; }
    public ushort? MeasMode { get; set; }
    public short? Threshold { get; set; }
    public ushort? TcMode { get; set; }
    public decimal? TempCoefficient { get; set; }
    public decimal? RefTime { get; set; }
    public decimal? RefTemp { get; set; }
    public decimal? RefEnvTime { get; set; }
    public decimal? RefPeriodNs { get; set; }
    public decimal? TempTauSeconds { get; set; }
    public ushort? UseTempChannel { get; set; }
    public byte? PulseDuty { get; set; }
    public byte? PulseForward { get; set; }
    public byte? PulseReverse { get; set; }

    public void Apply(ChannelConfig config, decimal soundSpeed)
    {
        if (ProbeFreq.HasValue) config.ProbeFreq = ProbeFreq.Value;

        if (EchoWin.HasValue)
        {
            config.EchoWin = EchoWin.Value;
        }
        else if (LengthMm.HasValue)
        {
            config.EchoWin = LengthMm.Value * 2M * 1_000_000M / (SoundSpeed ?? soundSpeed);
        }

        if (EchoWidth.HasValue) config.EchoWidth = EchoWidth.Value;
        if (Gain.HasValue) config.Gain = Gain.Value;
        if (MeasMode.HasValue) config.MeasMode = MeasMode.Value;
        if (Threshold.HasValue) config.Threshold = Threshold.Value;
        if (TcMode.HasValue) config.TcMode = TcMode.Value;
        if (TempCoefficient.HasValue) config.TempCoefficient = TempCoefficient;
        if (RefTime.HasValue) config.RefTime = RefTime;
        if (RefTemp.HasValue) config.RefTemp = RefTemp;
        if (RefEnvTime.HasValue) config.RefEnvTime = RefEnvTime;
        if (RefPeriodNs.HasValue) config.RefPeriodNs = RefPeriodNs;
        if (TempTauSeconds.HasValue) config.TempTauSeconds = TempTauSeconds;
        if (UseTempChannel.HasValue) config.UseTempChannel = UseTempChannel.Value;
        if (PulseDuty.HasValue) config.PulseConfig.Duty = PulseDuty.Value;
        if (PulseForward.HasValue) config.PulseConfig.Forward = PulseForward.Value;
        if (PulseReverse.HasValue) config.PulseConfig.Reverse = PulseReverse.Value;
    }
}
