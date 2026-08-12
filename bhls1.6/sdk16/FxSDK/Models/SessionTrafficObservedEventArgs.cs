namespace FxSDK.Models;

public enum SessionTrafficDirection
{
    Received = 0,
    Sent = 1
}

public sealed class SessionTrafficObservedEventArgs : EventArgs
{
    public SessionTrafficObservedEventArgs(SessionTrafficDirection direction, ReadOnlyMemory<byte> data)
    {
        Direction = direction;
        Data = data;
    }

    public SessionTrafficDirection Direction { get; }

    public ReadOnlyMemory<byte> Data { get; }
}
