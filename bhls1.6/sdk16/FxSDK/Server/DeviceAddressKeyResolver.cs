using FxSDK.Models;

namespace FxSDK.Server;

/// <summary>
/// Resolves device identity from DecodedResult.Address.
/// </summary>
public sealed class DeviceAddressKeyResolver : IFxDeviceKeyResolver
{
    public bool TryResolve(
        FxServerSessionInfo session,
        IReadOnlyList<DecodedResult> results,
        out FxDeviceKey deviceKey)
    {
        var address = results.Select(r => r.Address).FirstOrDefault(a => a > 0);
        if (address <= 0)
        {
            deviceKey = default!;
            return false;
        }

        deviceKey = new FxDeviceKey
        {
            Kind = DeviceKeyKind.DeviceAddress,
            Value = address.ToString(),
            Address = address,
            DeviceModel = session.DeviceModel,
            ListenPort = session.ListenPort,
            RemoteEndPoint = session.RemoteEndPoint
        };
        return true;
    }
}
