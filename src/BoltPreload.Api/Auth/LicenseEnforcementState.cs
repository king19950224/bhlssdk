namespace BoltPreload.Api.Auth;

/// <summary>
/// License 容量限额（MaxDevices / MaxChannels）的运行时强制状态。
/// <para>
/// 启动时按【实际加载的设备 / 通道数】评估一次，<see cref="LicenseGateMiddleware"/> 据此与签名有效性
/// 一起决定写操作是否放行。设计原则与「无效 license 仍继续监测、只拦写操作」一致：
/// 超授权容量【不停采集】（绝不因授权问题让安全监测静默失效），只拦截写操作并显著告警，
/// 逼业主补足正确授权后才能继续配置/标定/下发。
/// </para>
/// </summary>
public sealed class LicenseEnforcementState
{
    private readonly object _lock = new();

    /// <summary>当前部署是否在授权容量内。未评估前默认 true（不误伤）。</summary>
    public bool WithinLimits { get; private set; } = true;

    /// <summary>是否已在启动时完成评估。</summary>
    public bool Evaluated { get; private set; }

    public int CurrentDevices { get; private set; }
    public int CurrentChannels { get; private set; }
    public int? MaxDevices { get; private set; }
    public int? MaxChannels { get; private set; }

    /// <summary>超限说明（WithinLimits=false 时非空），用于审计与 403 响应。</summary>
    public string? Violation { get; private set; }

    public void Set(int currentDevices, int currentChannels, int? maxDevices, int? maxChannels)
    {
        lock (_lock)
        {
            CurrentDevices = currentDevices;
            CurrentChannels = currentChannels;
            MaxDevices = maxDevices;
            MaxChannels = maxChannels;

            var violations = new List<string>(2);
            if (maxDevices is int md && currentDevices > md)
                violations.Add($"设备数 {currentDevices} 超过授权上限 {md}");
            if (maxChannels is int mc && currentChannels > mc)
                violations.Add($"通道数 {currentChannels} 超过授权上限 {mc}");

            Violation = violations.Count == 0 ? null : string.Join("；", violations);
            WithinLimits = Violation is null;
            Evaluated = true;
        }
    }
}
