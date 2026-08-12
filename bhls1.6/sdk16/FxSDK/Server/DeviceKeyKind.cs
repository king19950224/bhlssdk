namespace FxSDK.Server;

/// <summary>
/// Describes how a server-side TCP session is matched to a device.
/// </summary>
public enum DeviceKeyKind
{
    DeviceAddress,
    ListenerPort,
    RemoteEndpoint,
    DeviceCode
}
