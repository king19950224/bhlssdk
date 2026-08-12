using FxSDK.Models;

namespace FxSDK.Server;

/// <summary>
/// Resolves device identity by listener port. Use for one-device-per-port deployments.
/// </summary>
public sealed class ListenerPortKeyResolver : IFxDeviceKeyResolver
{
    public bool TryResolve(
        FxServerSessionInfo session,
        IReadOnlyList<DecodedResult> results,
        out FxDeviceKey deviceKey)
    {
        deviceKey = new FxDeviceKey
        {
            Kind = DeviceKeyKind.ListenerPort,
            Value = session.ListenPort.ToString(),
            DeviceModel = session.DeviceModel,
            ListenPort = session.ListenPort,
            RemoteEndPoint = session.RemoteEndPoint
        };
        return true;
    }
}
