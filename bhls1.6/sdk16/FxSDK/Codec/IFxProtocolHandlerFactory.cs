namespace FxSDK.Codec;

/// <summary>
/// Creates protocol handlers for Fx device models.
/// </summary>
public interface IFxProtocolHandlerFactory
{
    FxProtocolHandler Create(ushort deviceModel);
}
