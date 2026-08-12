using System.Net;

namespace FxSDK.Server;

/// <summary>
/// Options for one TCP server listener.
/// </summary>
public sealed class FxServerListenerOptions
{
    public string Name { get; set; } = string.Empty;

    public IPAddress ListenAddress { get; set; } = IPAddress.Any;

    public int ListenPort { get; set; }

    /// <summary>
    /// 0 standard, 1 expert, 2 IOT R24, 3 multi-channel IOT.
    /// </summary>
    public ushort DeviceModel { get; set; } = 3;

    public TimeSpan IdentifyTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ReceiveIdleTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public TimeSpan DecodeIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(10);

    public bool CloseUnhealthySession { get; set; } = true;

    public bool ReplaceExistingDeviceSession { get; set; } = true;

    public int MaxConnections { get; set; } = 100;

    public int Backlog { get; set; } = 100;

    public bool NoDelay { get; set; } = true;

    public int SendBufferSize { get; set; } = 64 * 1024;

    public int ReceiveBufferSize { get; set; } = 64 * 1024;

    internal FxServerListenerOptions Clone()
    {
        return new FxServerListenerOptions
        {
            Name = Name,
            ListenAddress = ListenAddress,
            ListenPort = ListenPort,
            DeviceModel = DeviceModel,
            IdentifyTimeout = IdentifyTimeout,
            ReceiveIdleTimeout = ReceiveIdleTimeout,
            DecodeIdleTimeout = DecodeIdleTimeout,
            HealthCheckInterval = HealthCheckInterval,
            CloseUnhealthySession = CloseUnhealthySession,
            ReplaceExistingDeviceSession = ReplaceExistingDeviceSession,
            MaxConnections = MaxConnections,
            Backlog = Backlog,
            NoDelay = NoDelay,
            SendBufferSize = SendBufferSize,
            ReceiveBufferSize = ReceiveBufferSize
        };
    }
}
