using FxSDK.Models;

namespace FxSDK.Server;

/// <summary>
/// Resolves a stable device key from decoded results.
/// </summary>
public interface IFxDeviceKeyResolver
{
    bool TryResolve(
        FxServerSessionInfo session,
        IReadOnlyList<DecodedResult> results,
        out FxDeviceKey deviceKey);
}
