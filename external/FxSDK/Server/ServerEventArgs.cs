using FxSDK.Models;

namespace FxSDK.Server;

public sealed class FxServerSessionAcceptedEventArgs : EventArgs
{
    public FxServerSessionInfo Session { get; init; } = default!;
}

public sealed class FxServerDeviceIdentifiedEventArgs : EventArgs
{
    public FxServerSessionInfo Session { get; init; } = default!;

    public FxDeviceKey DeviceKey { get; init; } = default!;
}

public sealed class FxServerDecodedResultsEventArgs : EventArgs
{
    public FxServerSessionInfo Session { get; init; } = default!;

    public FxDeviceKey? DeviceKey { get; init; }

    public DecodedResult[] Results { get; init; } = Array.Empty<DecodedResult>();
}

public sealed class FxServerSessionDisconnectedEventArgs : EventArgs
{
    public FxServerSessionInfo Session { get; init; } = default!;

    public FxDeviceKey? DeviceKey { get; init; }

    public SessionCloseReason Reason { get; init; }

    public string? Message { get; init; }

    public Exception? Exception { get; init; }
}

public sealed class FxServerSessionRejectedEventArgs : EventArgs
{
    public string RemoteEndPoint { get; init; } = string.Empty;

    public int ListenPort { get; init; }

    public string Reason { get; init; } = string.Empty;

    public Exception? Exception { get; init; }
}

public sealed class FxServerTrafficObservedEventArgs : EventArgs
{
    public FxServerSessionInfo Session { get; init; } = default!;

    public SessionTrafficDirection Direction { get; init; }

    public ReadOnlyMemory<byte> Data { get; init; }
}
