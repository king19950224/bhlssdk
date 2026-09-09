namespace FxDevice;

public enum ParamKind
{
    ReadWrite,
    Observe,
}

public enum ParamGroup
{
    Register,
    Pulse,
    Storage,
}

public sealed record ParamSpec(string Name, string Cn, bool IsChannel, ParamGroup Group, ParamKind Kind);

public static class ParamCatalog
{
    public static readonly ParamSpec[] All =
    {
        new("ProbeFreq", "探头频率MHz", true, ParamGroup.Register, ParamKind.ReadWrite),
        new("EchoWin", "回波窗ns", true, ParamGroup.Register, ParamKind.ReadWrite),
        new("EchoWidth", "回波宽度us", true, ParamGroup.Register, ParamKind.ReadWrite),
        new("Duty", "激励占空比%", true, ParamGroup.Pulse, ParamKind.ReadWrite),
        new("Forward", "正向脉冲数", true, ParamGroup.Pulse, ParamKind.ReadWrite),
        new("Reverse", "反向脉冲数", true, ParamGroup.Pulse, ParamKind.ReadWrite),
        new("Gain", "增益档", true, ParamGroup.Register, ParamKind.ReadWrite),
        new("MeasMode", "测量模式", true, ParamGroup.Storage, ParamKind.ReadWrite),
        new("Threshold", "检波阈值", true, ParamGroup.Storage, ParamKind.ReadWrite),
        new("RefTime", "参考声时ns", true, ParamGroup.Storage, ParamKind.ReadWrite),
        new("RefTemp", "参考温度C", true, ParamGroup.Storage, ParamKind.ReadWrite),
        new("LEchoWave", "参考波形", true, ParamGroup.Storage, ParamKind.Observe),
    };

    public static ParamSpec? Find(string name) => All.FirstOrDefault(p => p.Name == name);

    public static string CnOf(string name) => Find(name)?.Cn ?? name;

    public static readonly Dictionary<string, string> Sentinels = new()
    {
        ["RefTime"] = "4294967295",
        ["RefTemp"] = "655.35",
        ["MeasMode"] = "255",
        ["Threshold"] = "-1",
        ["WaveStart"] = "65535000",
    };
}
