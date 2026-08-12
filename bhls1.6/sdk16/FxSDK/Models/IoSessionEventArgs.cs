namespace FxSDK.Models;

/// <summary>
/// 会话关闭原因
/// </summary>
public enum SessionCloseReason
{
    Normal,           // 用户主动关闭
    RemoteClosed,     // 对端优雅关闭（TCP FIN）
    ConnectionLost,   // 网络中断（RST/超时）
    HeartbeatTimeout, // 心跳失败
    ProtocolError,    //协议层致命错误
}
/// <summary>
/// 会话关闭事件参数
/// </summary>
public class SessionDisconnectedEventArgs : EventArgs
{
    public SessionCloseReason Reason { get; }
    public Exception? Exception { get; }
    
    public SessionDisconnectedEventArgs(SessionCloseReason reason, Exception? exception = null)
    {
        Reason = reason;
        Exception = exception;
    }
}
/// <summary>
/// 解码数据事件参数
/// </summary>
public class DecodedResultsReceivedEventArgs : EventArgs
{
    public DecodedResult[] Results { get; }
    public DecodedResultsReceivedEventArgs(DecodedResult[] results) => Results = results;
}