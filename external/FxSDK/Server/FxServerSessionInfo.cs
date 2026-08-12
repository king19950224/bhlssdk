using FxSDK.Core;
using FxSDK.Models;
using FxSDK.Utility;

namespace FxSDK.Server;

/// <summary>
/// Runtime state for one TCP session accepted by <see cref="FxServer"/>.
/// </summary>
public sealed class FxServerSessionInfo
{
    public string ConnectionId { get; init; } = string.Empty;

    public int ListenPort { get; init; }

    public string RemoteEndPoint { get; init; } = string.Empty;

    public string RemoteAddress { get; init; } = string.Empty;

    public int RemotePort { get; init; }

    public ushort DeviceModel { get; init; }

    public bool IsDeviceKeyResolved { get; internal set; }

    public FxDeviceKey? DeviceKey { get; internal set; }

    public string RawSessionId { get; init; } = string.Empty;

    public DateTime ConnectedAtUtc { get; init; }

    public DateTime LastReceiveUtc { get; internal set; }

    public DateTime LastDecodedUtc { get; internal set; }

    public DateTime? DeviceKeyResolvedAtUtc { get; internal set; }

    internal FxServerListenerOptions ListenerOptions { get; init; } = new();

    internal IoSession Session { get; init; } = default!;

    internal string? StableDeviceKey { get; set; }

    public void StoreDeviceConfig(DeviceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        SessionContext.StoreToSession(Session, config);
    }

    public void StoreChannelConfig(ChannelConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        SessionContext.StoreToSession(Session, config);
    }

    public async Task ConfigureDeviceAsync(DeviceConfig config, string? propertyName = null, bool sendCommand = true)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            SessionContext.StoreToSession(Session, config);
            if (sendCommand)
            {
                await Session.SendMsgAsync(Session.ProtocolHandler.EncodeDevice(Session, config, propertyName))
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ex.ToCommunicationException(ex.Message);
        }
    }

    public async Task ConfigureChannelAsync(ChannelConfig config, string? propertyName = null, bool sendCommand = true)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            SessionContext.StoreToSession(Session, config);
            if (sendCommand)
            {
                await Session.SendMsgAsync(Session.ProtocolHandler.EncodeChannel(Session, config, propertyName))
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ex.ToCommunicationException(ex.Message);
        }
    }

    public async Task SetDeviceModeAsync(WorkMode mode, ushort channel = 0, ushort address = ChannelConfig.DefaultAddress)
    {
        try
        {
            if (mode == WorkMode.LWaveRead && channel > 0)
            {
                SessionContext.BeginEnvelopeScan(Session, address, channel);
            }
            SessionContext.StoreToSession(Session, mode, address);
            await Session.SendMsgAsync(Session.ProtocolHandler.EncodeWorkMode(Session, mode, channel, address))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ex.ToCommunicationException(ex.Message);
        }
    }

    public async Task ReadDeviceParameterAsync(string propertyName, ushort address = ChannelConfig.DefaultAddress)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            throw new ArgumentException("Property name is required.", nameof(propertyName));
        }

        try
        {
            SessionContext.StoreToSession(Session, propertyName);
            SessionContext.StoreToSession(Session, WorkMode.ReadParam, address);
            await Session.SendMsgAsync(Session.ProtocolHandler.EncodeReadDeviceParameter(Session, propertyName, address))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ex.ToCommunicationException(ex.Message);
        }
    }

    public async Task ReadChannelParameterAsync(
        string propertyName,
        ushort channel = 1,
        ushort address = ChannelConfig.DefaultAddress)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            throw new ArgumentException("Property name is required.", nameof(propertyName));
        }

        try
        {
            SessionContext.StoreToSession(Session, propertyName);
            await Session.SendMsgAsync(Session.ProtocolHandler.EncodeReadChannelParameter(Session, propertyName, channel, address))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ex.ToCommunicationException(ex.Message);
        }
    }

    public void ClearEnvelopeTracking(ushort? address = null, ushort? channel = null)
    {
        if (address.HasValue && channel.HasValue)
        {
            SessionContext.ClearEnvelopeTrackingContext(Session, address.Value, channel.Value);
            return;
        }

        SessionContext.ClearEnvelopeTrackingContexts(Session, address);
    }
}
