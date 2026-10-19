using System.Text.Json;

namespace FxDevice;

/// <summary>一台采集设备：装在哪台机组、地址、巡检位，以及它各法兰的接线/标定/额定（CalPlan）。</summary>
public sealed class DevicePlan
{
    public int Turbine { get; init; }

    public string Endpoint { get; init; } = string.Empty;

    /// <summary>巡检位：只轮询、只判有没有探头，抓插到规划外通道的探头。</summary>
    public IReadOnlyList<ushort> Guard { get; init; } = Array.Empty<ushort>();

    public CalPlan Plan { get; init; } = null!;

    /// <summary>"F01（192.168.0.7:23）"。</summary>
    public string Label => $"F{Turbine:00}（{Endpoint}）";
}

/// <summary>
/// 整个风场的接入方案：一个大屏接很多台设备，每台设备装在一台机组上。
/// farm.json 里 flanges[] 是全场统一的法兰结构模板（名字、螺栓数、编号、规划位），
/// devices[] 每台写自己在哪台机组、IP、各法兰的 channels / channelBolts / ucf / ratedKn。
/// 老写法（顶层 device + bind + flanges[].channels）仍然认，当作只有一台设备。
/// </summary>
public sealed class FarmPlan
{
    /// <summary>没有设备时的空方案。读不到 farm.json 时用它，只提供网页。</summary>
    public static FarmPlan Empty { get; } = new();

    public IReadOnlyList<DevicePlan> Devices { get; private init; } = Array.Empty<DevicePlan>();

    public bool HasFarm { get; private init; }

    public int TurbineCount { get; private init; }

    public string FarmName { get; private init; } = string.Empty;

    /// <summary>按地址找设备：只比 IP，端口可以不写。找不到且只有一台设备时就当是它（桌面端连的就是它）。</summary>
    public DevicePlan? ForEndpoint(string? endpoint)
    {
        if (Devices.Count == 0) return null;
        var host = Host(endpoint);
        if (host.Length > 0)
        {
            var hit = Devices.FirstOrDefault(d => string.Equals(Host(d.Endpoint), host, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }

        return Devices.Count == 1 ? Devices[0] : null;

        static string Host(string? ep)
        {
            if (string.IsNullOrWhiteSpace(ep)) return string.Empty;
            var s = ep.Trim();
            var i = s.LastIndexOf(':');
            return i > 0 ? s[..i] : s;
        }
    }

    public DevicePlan? ForTurbine(int turbine) => Devices.FirstOrDefault(d => d.Turbine == turbine);

    public static FarmPlan Load(JsonElement? farm, string baseDir, Action<string> log)
    {
        var devices = new List<DevicePlan>();
        var turbineCount = 0;
        var farmName = string.Empty;

        if (farm is { } root)
        {
            if (root.TryGetProperty("turbineCount", out var tc) && tc.TryGetInt32(out var n)) turbineCount = n;
            if (root.TryGetProperty("farmName", out var fn) && fn.ValueKind == JsonValueKind.String) farmName = fn.GetString() ?? string.Empty;

            if (root.TryGetProperty("devices", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                // 写了 devices[]（哪怕是空数组）就按新写法：空 = 一台都不接，绝不退到虚构的默认 IP
                var seenTurbines = new HashSet<int>();
                foreach (var dev in arr.EnumerateArray())
                {
                    var turbine = dev.TryGetProperty("turbine", out var t) && t.TryGetInt32(out var tv) ? tv : 0;
                    var endpoint = dev.TryGetProperty("device", out var ep) && ep.ValueKind == JsonValueKind.String ? ep.GetString()?.Trim() ?? string.Empty : string.Empty;
                    if (turbine <= 0 || endpoint.Length == 0)
                    {
                        log($"devices[] 里有一项缺 turbine 或 device，跳过：{dev.GetRawText()}");
                        continue;
                    }

                    if (!seenTurbines.Add(turbine))
                    {
                        log($"F{turbine:00} 配了两台设备，一台机组只能接一台，后面那台（{endpoint}）跳过");
                        continue;
                    }

                    var label = $"F{turbine:00}（{endpoint}）";
                    devices.Add(new DevicePlan
                    {
                        Turbine = turbine,
                        Endpoint = endpoint,
                        Guard = CalPlan.ReadChannels(dev, "guardChannels"),
                        Plan = CalPlan.Load(root, dev, baseDir, s => log($"{label} {s}")),
                    });
                }
            }
            else
            {
                // 老写法：顶层 device / bind / flanges[].channels，只有一台设备。地址缺了就不连，不编造 IP
                var endpoint = root.TryGetProperty("device", out var ep) && ep.ValueKind == JsonValueKind.String ? ep.GetString()?.Trim() ?? string.Empty : string.Empty;
                var turbine = root.TryGetProperty("bind", out var bind) && bind.TryGetProperty("turbine", out var bt) && bt.TryGetInt32(out var btv) ? btv : 1;
                if (endpoint.Length == 0)
                {
                    log("老写法缺顶层 device 地址，不按默认 IP 去连");
                }
                else
                {
                    devices.Add(new DevicePlan
                    {
                        Turbine = turbine,
                        Endpoint = endpoint,
                        Guard = CalPlan.ReadChannels(root, "guardChannels"),
                        Plan = CalPlan.Load(root, null, baseDir, s => log($"F{turbine:00} {s}")),
                    });
                }
            }
        }

        return new FarmPlan
        {
            Devices = devices.OrderBy(d => d.Turbine).ToList(),
            HasFarm = farm is not null,
            TurbineCount = Math.Max(turbineCount, devices.Count == 0 ? 0 : devices.Max(d => d.Turbine)),
            FarmName = farmName,
        };
    }
}
