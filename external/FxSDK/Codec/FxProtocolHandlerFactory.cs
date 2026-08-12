namespace FxSDK.Codec;

/// <summary>
/// Shared protocol handler factory used by both client and server connection modes.
/// </summary>
public sealed class FxProtocolHandlerFactory : IFxProtocolHandlerFactory
{
    public static FxProtocolHandlerFactory Shared { get; } = new();

    private FxProtocolHandlerFactory()
    {
    }

    public FxProtocolHandler Create(ushort deviceModel)
    {
        return deviceModel switch
        {
            0 => new StandardProtocolHandler(),
            1 => new ExpertProtocolHandler(),
            2 => new IotR24ProtocolHandler(),
            3 => new MultiChIotProtocolHandler(),
            5 => new UltraHighRate28ProtocolHandler(),
            _ => new MultiChIotProtocolHandler()
        };
    }
}
