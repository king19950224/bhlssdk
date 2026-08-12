using FxSDK.Models;

namespace FxSDK.Server;

/// <summary>
/// Resolves device keys by trying multiple resolvers in order.
/// </summary>
public sealed class CompositeDeviceKeyResolver : IFxDeviceKeyResolver
{
    private readonly IReadOnlyList<IFxDeviceKeyResolver> _resolvers;

    public CompositeDeviceKeyResolver(IEnumerable<IFxDeviceKeyResolver> resolvers)
    {
        _resolvers = resolvers.ToList();
    }

    public static CompositeDeviceKeyResolver CreateDefault()
    {
        return new CompositeDeviceKeyResolver(new IFxDeviceKeyResolver[]
        {
            new DeviceAddressKeyResolver()
        });
    }

    public static CompositeDeviceKeyResolver CreateWithFallbacks()
    {
        return new CompositeDeviceKeyResolver(new IFxDeviceKeyResolver[]
        {
            new DeviceAddressKeyResolver(),
            new ListenerPortKeyResolver(),
            new RemoteEndpointKeyResolver()
        });
    }

    public bool TryResolve(
        FxServerSessionInfo session,
        IReadOnlyList<DecodedResult> results,
        out FxDeviceKey deviceKey)
    {
        foreach (var resolver in _resolvers)
        {
            if (resolver.TryResolve(session, results, out deviceKey))
            {
                return true;
            }
        }

        deviceKey = default!;
        return false;
    }
}
