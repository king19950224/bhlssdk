using FxSDK.Codec;
using FxSDK.Models;
using FxSDK.Utility;
using NLog;

namespace FxSDK.Core;

/// <summary>
/// SDK和设备会话
/// </summary>
public abstract class IoSession
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    public event EventHandler<EventArgs>? SessionConnected;
    public event EventHandler<SessionDisconnectedEventArgs>? SessionDisconnected;
    public event EventHandler<DecodedResultsReceivedEventArgs>? SessionDecResultsReceived;
    internal readonly FxProtocolHandler ProtocolHandler;
    private bool _disposed = false; // 添加一个标志
    protected IoSession(FxProtocolHandler protocolHandler)
    {
        ProtocolHandler = protocolHandler ?? throw new ArgumentNullException(nameof(protocolHandler));
    }

    /// <summary>
    /// 会话取消令牌（由派生类提供）
    /// </summary>
    protected abstract CancellationToken SessionToken { get; init; }

    /// <summary>
    /// 会话标识
    /// </summary>
    public string SessionId { get; protected set; }

    /// <summary>
    /// 心跳间隔（秒）。
    /// 厂家原值 20s。实测 DEV_LAB_001 的 TCP 链路（192.168.0.7:23 串口服务器）约 5s 无流量即被对端断开：
    /// 设备不上行数据时（未在测量），20s 心跳保不住连接，导致 MultiMeas 启动/轮询配置命令
    /// 只能在原厂软件扫波"喂活"链路时才有机会生效。缩短到 2s 使空闲连接常驻。
    /// 心跳内容是单字节 0x00 填充，协议解码按帧头(DE DE DE DE)同步，会自动忽略。
    /// </summary>
    protected static readonly double HeartbeatSeconds = 2;

    #region 触发事件
    protected void FireSessionConnected()
    {
        SessionConnected?.Invoke(this, EventArgs.Empty);
    }

    protected void FireSessionDisconnected(SessionCloseReason reason, Exception? ex = null)
    {
        SessionDisconnected?.Invoke(this, new SessionDisconnectedEventArgs(reason, ex));
    }

    internal void FireSessionDecodedResultReceived(DecodedResult[] batchResults)
    {
        SessionDecResultsReceived?.Invoke(this, new DecodedResultsReceivedEventArgs(batchResults));
    }
    #endregion

    /// <summary>
    /// 子类可重写以调整解码轮询间隔（默认 10ms）
    /// </summary>
    protected virtual int GetDecodeDelayMilliseconds() => 10;
    
    protected async Task DecodingLoop()
    {
        var delayMs = GetDecodeDelayMilliseconds();
    
        while (!SessionToken.IsCancellationRequested)
        {
            SessionToken.ThrowIfCancellationRequested();
            List<DecodedResult>? results = null;

            try
            {
                // 在独立 Task 中运行 TryDecode，带超时
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(SessionToken);
                cts.CancelAfter(TimeSpan.FromMilliseconds(200)); // 最多 200ms
                var token = cts.Token;
                var decodeTask = Task.Run(() => ProtocolHandler.TryDecode(this,token), token);
                results = await decodeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SessionToken.IsCancellationRequested)
            {
                break; //会话正常关闭
            }
            catch (OperationCanceledException)
            {
                // 解码超时（200ms 没返回）：多为坏数据卡住解码状态机。
                // 旧实现在这里 break，解码循环退出但 TCP 和心跳还在，只能重启进程恢复。
                // 改为清掉积压、复位解码状态继续跑，代价是丢掉缓冲里这一段数据。
                Logger.Warn("TryDecode 超时（疑似坏数据卡住解码状态机），复位解码缓冲后继续。");
                ProtocolHandler.ResetDecodeState();
            }
            catch (Exception e)
            {
                Logger.Error(e, "TryDecode 抛出异常，复位解码缓冲后继续。");
                ProtocolHandler.ResetDecodeState();
            }

            if (results is {Count: > 0})
            {
                FireSessionDecodedResultReceived(results.ToArray());
            }

            try
            {
                await Task.Delay(delayMs, SessionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 所有派生类必须调用
    /// </summary>
    internal virtual async Task OpenAsync()
    {
        FireSessionConnected();
    }

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    internal async Task SendMsgAsync(IReadOnlyList<object> msgList)
    {
        await _sendLock.WaitAsync(SessionToken);
        try
        {
            foreach (var msg in msgList)
            {
                if (_disposed) return; // 检查是否已释放
                if (msg.Equals(FxUtility.Delay500Ms))
                    await Task.Delay(500, SessionToken);
                else if (msg.Equals(FxUtility.Delay100Ms))
                    await Task.Delay(100, SessionToken);
                else if (msg.Equals(FxUtility.Delay50Ms))
                    await Task.Delay(50, SessionToken);
                else if (msg.Equals(FxUtility.Delay10Ms))
                    await Task.Delay(10, SessionToken);
                else if (msg is byte[] bytes)
                    await SendDataAsync(bytes);
                else if (msg is List<byte> byteList)
                    await SendDataAsync(byteList.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            // 会话正在关闭，指令没发出去。上抛让调用方感知，否则会白等设备回包到超时。
            Logger.Warn("SendMsgAsync 取消：会话已关闭，指令未发出。");
            throw;
        }
        catch (Exception ex)
        {
            // 旧实现吞掉异常只记 Warn，调用方以为指令已发出。上抛由上层决定重试或报错。
            Logger.Warn(ex, $"SendMsgAsync failed: {ex.Message}");
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    protected abstract Task SendDataAsync(byte[] data);

    public virtual Task CloseAsync()
    {
        try
        {
            ProtocolHandler.Dispose();
            // 清理 session 上下文
            SessionContext.ClearContext(this);
            // 解绑事件
            SessionConnected = null;
            SessionDisconnected = null;
            SessionDecResultsReceived = null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"IoSession.CloseAsync failed: {ex.Message}");
        }
        return Task.CompletedTask;
    }
}