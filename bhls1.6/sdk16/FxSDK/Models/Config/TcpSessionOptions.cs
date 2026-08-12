namespace FxSDK.Models;

/// <summary>
/// TCP 会话运行时选项。默认值保持 SDK 既有行为。
/// </summary>
public sealed class TcpSessionOptions
{
    /// <summary>
    /// 是否定时发送 TCP 心跳字节。
    /// </summary>
    public bool EnableHeartbeat { get; set; } = true;

    /// <summary>
    /// 是否在流式工作模式下按接收活动超时主动断开。
    /// </summary>
    public bool EnableReceiveActivityTimeout { get; set; } = true;

    /// <summary>
    /// 心跳和接收活动检测周期，单位秒。
    /// </summary>
    public double HeartbeatSeconds { get; set; } = 20d;

    internal TcpSessionOptions Clone()
    {
        return new TcpSessionOptions
        {
            EnableHeartbeat = EnableHeartbeat,
            EnableReceiveActivityTimeout = EnableReceiveActivityTimeout,
            HeartbeatSeconds = HeartbeatSeconds
        };
    }
}
