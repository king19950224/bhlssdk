using FxSDK.Models;

namespace FxSDK.Server;

/// <summary>
/// Resolves device identity by remote endpoint. Prefer fixed device IP deployments.
/// </summary>
public sealed class RemoteEndpointKeyResolver : IFxDeviceKeyResolver
{
    public bool UsePort { get; init; }

    public bool TryResolve(
        FxServerSessionInfo session,
        IReadOnlyList<DecodedResult> results,
        out FxDeviceKey deviceKey)
    {
        var value = UsePort ? session.RemoteEndPoint : session.RemoteAddress;
        if (string.IsNullOrWhiteSpace(value))
        {
            deviceKey = default!;
            return false;
        }

        deviceKey = new FxDeviceKey
        {
            Kind = DeviceKeyKind.RemoteEndpoint,
            Value = value,
            DeviceModel = session.DeviceModel,
            ListenPort = session.ListenPort,
            RemoteEndPoint = session.RemoteEndPoint
        };
        return true;
    }
}
