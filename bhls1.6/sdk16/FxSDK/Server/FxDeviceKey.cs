namespace FxSDK.Server;

/// <summary>
/// Stable device key resolved from a server-side TCP session.
/// </summary>
public sealed class FxDeviceKey : IEquatable<FxDeviceKey>
{
    public DeviceKeyKind Kind { get; init; } = DeviceKeyKind.DeviceAddress;

    public string Value { get; init; } = string.Empty;

    public ushort? Address { get; init; }

    public ushort? DeviceModel { get; init; }

    public int? ListenPort { get; init; }

    public string? RemoteEndPoint { get; init; }

    public string ToStableKey()
    {
        return $"{ToKindText(Kind)}:{DeviceModel}:{ListenPort}:{Value}";
    }

    public bool Equals(FxDeviceKey? other)
    {
        return other != null && string.Equals(ToStableKey(), other.ToStableKey(), StringComparison.Ordinal);
    }

    public override bool Equals(object? obj)
    {
        return obj is FxDeviceKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(ToStableKey());
    }

    public override string ToString()
    {
        return ToStableKey();
    }

    public static string ToKindText(DeviceKeyKind kind)
    {
        return kind switch
        {
            DeviceKeyKind.DeviceAddress => "device_address",
            DeviceKeyKind.ListenerPort => "listener_port",
            DeviceKeyKind.RemoteEndpoint => "remote_endpoint",
            DeviceKeyKind.DeviceCode => "device_code",
            _ => kind.ToString().ToLowerInvariant()
        };
    }
}
