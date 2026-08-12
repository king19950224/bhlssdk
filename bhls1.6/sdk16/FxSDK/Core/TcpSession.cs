using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using FxSDK.Codec;
using FxSDK.Models;
using FxSDK.Utility;
using NLog;

namespace FxSDK.Core;

public class TcpSession : IoSession
{
    private static readonly ILogger Logger = LogManager.GetLogger(nameof(TcpSession));
    
    private readonly Socket _socket;
    private readonly Pipe _pipe;
    private readonly CancellationTokenSource _sessionCts;
    private readonly CancellationTokenRegistration _externalCancellationRegistration;
    private readonly TcpSessionOptions _tcpSessionOptions;
    
    private Task _receiveTask = Task.CompletedTask;
    private Task _sendTask = Task.CompletedTask;
    private Task _heartbeatTask = Task.CompletedTask;
    private Task _decodeTask = Task.CompletedTask;
    private Task[] _backgroundTasks = Array.Empty<Task>();
    
    // 使用原子操作确保线程安全
    private int _isClosed = 0;

    public TcpSession(Socket socket, FxProtocolHandler fxProtocolHandler, CancellationToken externalToken,
        TcpSessionOptions? tcpSessionOptions = null)
        : base(fxProtocolHandler)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _tcpSessionOptions = tcpSessionOptions?.Clone() ?? new TcpSessionOptions();
        _sessionCts = externalToken.CanBeCanceled 
            ? CancellationTokenSource.CreateLinkedTokenSource(externalToken) 
            : new CancellationTokenSource();
        
        SessionToken = _sessionCts.Token;
        
        _pipe = new Pipe(new PipeOptions(
            pool: MemoryPool<byte>.Shared,
            readerScheduler: PipeScheduler.Inline,
            writerScheduler: PipeScheduler.ThreadPool,
            useSynchronizationContext: false,
            minimumSegmentSize: 4096,
            pauseWriterThreshold: 256 * 1024,
            resumeWriterThreshold: 128 * 1024
        ));
        
        var remoteEndPoint = _socket.RemoteEndPoint as IPEndPoint
                             ?? throw new InvalidOperationException("TCP remote endpoint is unavailable.");
        SessionId = GetSessionId(remoteEndPoint);
        
        // 注册外部取消回调
        if (externalToken.CanBeCanceled)
        {
            _externalCancellationRegistration = externalToken.Register(() =>
            {
                Logger.Debug($"External cancellation triggered for session {SessionId}");
                _= CloseAsync(SessionCloseReason.Normal);// 外部取消时主动关闭会话
            });
        }
    }

    protected sealed override CancellationToken SessionToken { get; init; }

    protected override void RequestClose(SessionCloseReason reason, Exception? exception)
    {
        _ = CloseAsync(reason, exception);
    }

    public override bool IsConnected => Volatile.Read(ref _isClosed) == 0;

    internal override async Task OpenAsync()
    {
        if (_socket.RemoteEndPoint == null || _socket.LocalEndPoint == null)
            throw new InvalidOperationException("Socket is not connected.");
        
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(true, 0));
        
        if (!SessionToken.IsCancellationRequested)
        {
            await base.OpenAsync();
            // 启动 I/O 循环任务
            _receiveTask = ReceiveLoop();
            _sendTask = SendLoop();
            _heartbeatTask = HeartbeatLoop();
            // 启动解码任务
            _decodeTask = DecodingLoop();
        }
        _backgroundTasks = new[] { _receiveTask, _sendTask, _heartbeatTask,_decodeTask };
    }

    // ====== 接收循环 ======
    private async Task ReceiveLoop()
    {
        if (SessionToken.IsCancellationRequested)
        {
            Logger.Debug("ReceiveLoop: Start cancelled immediately.");
            return;
        }
        var buffer = new byte[65536];
        try
        {
            while (!SessionToken.IsCancellationRequested)
            {
                try
                {
                    var bytesRead = await _socket.ReceiveAsync(buffer, SocketFlags.None, SessionToken).ConfigureAwait(false);

                    if (bytesRead == 0)//对端取消的 要主动close掉
                    {
                        Logger.Info("Remote side closed the connection gracefully.");
                        _ = CloseAsync(SessionCloseReason.RemoteClosed).ConfigureAwait(false);
                        return;
                    }
                    
                    var data = buffer.AsMemory(0, bytesRead);
                    MarkReceived();
                    FireTrafficObserved(SessionTrafficDirection.Received, data);
                    ProtocolHandler.Enqueue(data.Span);
                    
                    if (FxUtility.DeveloperMode)
                    {
                        Logger.Debug($"receive: {ByteUtility.BytesToHexStr(data.ToArray())}");
                    }
                }
                catch (OperationCanceledException) when (SessionToken.IsCancellationRequested)
                {
                    // 正常取消，直接退出
                    Logger.Debug("ReceiveLoop: Cancelled by token");
                    return;
                }
                catch (ObjectDisposedException)
                {
                    // Socket 已释放，正常退出
                    Logger.Debug("ReceiveLoop: Socket disposed");
                    return;
                }
                catch (Exception ex) when (IsConnectionError(ex))
                {
                    Logger.Warn($"Receive error (connection lost): {ex.Message}");
                    _ = CloseAsync(SessionCloseReason.ConnectionLost, ex).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Unexpected error in ReceiveLoop");
                    return;
                }
            }
            Logger.Debug("ReceiveLoop exited due to cancellation");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Fatal error in ReceiveLoop");
        }
    }

    // ====== 发送循环 ======
    private async Task SendLoop()
    {
        try
        {
            while (!SessionToken.IsCancellationRequested)
            {
                try
                {
                    var result = await _pipe.Reader.ReadAsync(SessionToken).ConfigureAwait(false);
                    var buffer = result.Buffer;
                    
                    try
                    {
                        if (buffer.Length > 0)
                        {
                            foreach (var segment in buffer)
                            {
                                if (SessionToken.IsCancellationRequested || _socket == null)
                                    break;

                                var remaining = segment;
                                while (!remaining.IsEmpty)
                                {
                                    var bytesSent = await _socket.SendAsync(remaining, SocketFlags.None, SessionToken)
                                        .ConfigureAwait(false);
                                    if (bytesSent <= 0)
                                    {
                                        break;
                                    }
                                    remaining = remaining[bytesSent..];
                                }
                                MarkSent();
                                if (FxUtility.DeveloperMode)
                                {
                                    Logger.Debug($"send: {ByteUtility.BytesToHexStr(segment.ToArray())}");
                                }
                            }
                        }
                    }
                    finally
                    {
                        _pipe.Reader.AdvanceTo(buffer.End);
                    }
                    
                    if (result.IsCompleted)
                    {
                        Logger.Debug("SendLoop: Pipe completed");
                        break;
                    }
                }
                catch (OperationCanceledException) when (SessionToken.IsCancellationRequested)
                {
                    // 正常取消
                    Logger.Debug("SendLoop: Cancelled by token");
                    return;
                }
                catch (ObjectDisposedException)
                {
                    // Socket 或 Pipe 已释放
                    Logger.Debug("SendLoop: Object disposed");
                    return;
                }
                catch (Exception ex) when (IsConnectionError(ex))
                {
                    Logger.Warn($"Send error (connection lost): {ex.Message}");
                    _ = CloseAsync(SessionCloseReason.ConnectionLost, ex).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Unexpected error in SendLoop");
                    return;
                }
            }
            
            Logger.Debug("SendLoop exited normally");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Fatal error in SendLoop");
        }
    }

    // ====== 心跳循环 ======
    private const int HeartbeatMaxMissed = 3;
    
    private async Task HeartbeatLoop()
    {
        var heartbeatSeconds = Math.Max(1d, _tcpSessionOptions.HeartbeatSeconds);
        if (!_tcpSessionOptions.EnableHeartbeat && !_tcpSessionOptions.EnableReceiveActivityTimeout)
        {
            Logger.Debug("TCP heartbeat and receive activity timeout disabled");
            return;
        }
        
        var missed = 0;
        try
        {
            while (!SessionToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(heartbeatSeconds), SessionToken)
                        .ConfigureAwait(false);
                    
                    if (_tcpSessionOptions.EnableHeartbeat)
                    {
                        await SendDataAsync(new byte[] { 0x00 }).ConfigureAwait(false);
                        missed = 0;
                    }

                    if (_tcpSessionOptions.EnableReceiveActivityTimeout &&
                        IsReceiveActivityTimedOut(out var timeout, out var silence))
                    {
                        var ex = new TimeoutException(
                            $"No TCP data received for {silence.TotalSeconds:F0}s, timeout is {timeout.TotalSeconds:F0}s.");
                        Logger.Warn(
                            "TCP session {SessionId} receive activity timed out. Silence={SilenceSeconds:F0}s, Timeout={TimeoutSeconds:F0}s, LastReceiveUtc={LastReceiveUtc:o}, LastDecodedUtc={LastDecodedUtc:o}",
                            SessionId,
                            silence.TotalSeconds,
                            timeout.TotalSeconds,
                            LastReceiveUtc,
                            LastDecodedUtc);
                        _ = CloseAsync(SessionCloseReason.HeartbeatTimeout, ex).ConfigureAwait(false);
                        return;
                    }
                }
                catch (OperationCanceledException) when (SessionToken.IsCancellationRequested)
                {
                    // 正常取消
                    Logger.Debug("HeartbeatLoop: Cancelled by token");
                    return;
                }
                catch (Exception ex) when (IsConnectionError(ex))
                {
                    Logger.Warn(ex, "Heartbeat failed due to connection loss");
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Heartbeat send failed");
                    missed++;
                    if (missed >= HeartbeatMaxMissed)
                    {
                        Logger.Warn($"Heartbeat failed {HeartbeatMaxMissed} times, closing session.");
                        _ = CloseAsync(SessionCloseReason.ConnectionLost, ex).ConfigureAwait(false);
                        return;
                    }
                }
            }
            Logger.Debug("HeartbeatLoop exited normally");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Fatal error in HeartbeatLoop");
        }
    }

    // ====== 优化的关闭逻辑 ======
    public override async Task CloseAsync()
    {
        await CloseAsync(
            SessionCloseReason.Normal,
            exception: null,
            waitForBackgroundTasks: TimeSpan.FromSeconds(10),
            fastBaseClose: false).ConfigureAwait(false);
    }

    public override async Task CloseFastAsync()
    {
        await CloseAsync(
            SessionCloseReason.Normal,
            exception: null,
            waitForBackgroundTasks: TimeSpan.FromMilliseconds(200),
            fastBaseClose: true).ConfigureAwait(false);
    }

    internal async Task CloseAsync(SessionCloseReason reason, Exception? exception = null)
    {
        await CloseAsync(
            reason,
            exception,
            waitForBackgroundTasks: TimeSpan.FromSeconds(10),
            fastBaseClose: false).ConfigureAwait(false);
    }

    private async Task CloseAsync(
        SessionCloseReason reason,
        Exception? exception,
        TimeSpan waitForBackgroundTasks,
        bool fastBaseClose)
    {
        if (Interlocked.CompareExchange(ref _isClosed, 1, 0) != 0)
            return;

        Logger.Debug($"Closing session {SessionId}, reason: {reason}");

        // Step 1: 触发取消（让任务主动退出
        try { _pipe.Writer.Complete(); } catch { } // 先 Complete Writer
        try { _pipe.Reader.Complete(); } catch { } // 再 Complete Reader
        try { _sessionCts?.Cancel(); } catch { }   // 触发所有 CancellationToken）

        // Step 2: 给任务时间自然退出
        if (_backgroundTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(_backgroundTasks).WaitAsync(waitForBackgroundTasks).ConfigureAwait(false);
                Logger.Debug($"Session {SessionId} all tasks completed successfully");
            }
            catch (TimeoutException)
            {
                Logger.Warn($"Session {SessionId} tasks did not exit cleanly within close timeout.");
            }
        }

        // Step 3: 强制释放底层资源（最后一步！）
        try { _socket?.Shutdown(SocketShutdown.Both); } catch { }
        try { _socket?.Close(0); } catch { }
        try { _socket?.Dispose(); } catch { } //

        // Step 4: 清理其他资源
        try { _sessionCts?.Dispose(); } catch { }
        try { _externalCancellationRegistration.Dispose(); } catch { }
        
        // Step 5: 生命周期回调 & 基类
        FireSessionDisconnected(reason, exception);
        if (fastBaseClose)
        {
            await base.CloseFastAsync().ConfigureAwait(false);
        }
        else
        {
            await base.CloseAsync().ConfigureAwait(false);
        }
    }

    protected override async Task SendDataAsync(byte[] data)
    {
        if (_isClosed == 1 || data.Length == 0 || SessionToken.IsCancellationRequested)
            return;
        
        try
        {
            await _pipe.Writer.WriteAsync(data, SessionToken).ConfigureAwait(false);
            FireTrafficObserved(SessionTrafficDirection.Sent, data);
        }
        catch (OperationCanceledException) when (SessionToken.IsCancellationRequested)
        {
            // 正常取消，不记录
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            Logger.Warn(ex, "SendDataAsync failed due to connection loss");
            _ = CloseAsync(SessionCloseReason.ConnectionLost, ex).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unexpected error in SendDataAsync");
        }
    }

    // ====== 工具方法 ======
    private static bool IsConnectionError(Exception ex)
    {
        return ex switch
        {
            null => false,
            OperationCanceledException => false, // 不再视为连接错误
            ObjectDisposedException => false,     // 不再视为连接错误
            SocketException se => se.SocketErrorCode is
                SocketError.ConnectionAborted or
                SocketError.ConnectionReset or
                SocketError.Shutdown or
                SocketError.TimedOut or
                SocketError.OperationAborted or
                SocketError.NotConnected or
                SocketError.HostUnreachable or
                SocketError.NetworkUnreachable,
            IOException ioe => ioe.InnerException != null && IsConnectionError(ioe.InnerException),
            _ => false
        };
    }

    private bool IsReceiveActivityTimedOut(out TimeSpan timeout, out TimeSpan silence)
    {
        timeout = TimeSpan.Zero;
        silence = DateTime.UtcNow - LastReceiveUtc;
        var mode = SessionContext.GetDeviceMode(this);
        if (!RequiresReceiveActivity(mode))
        {
            return false;
        }

        timeout = ResolveReceiveActivityTimeout(mode);
        return timeout > TimeSpan.Zero && silence > timeout;
    }

    private TimeSpan ResolveReceiveActivityTimeout(WorkMode mode)
    {
        var heartbeatSeconds = Math.Max(1d, _tcpSessionOptions.HeartbeatSeconds);
        var minTimeout = TimeSpan.FromSeconds(heartbeatSeconds * HeartbeatMaxMissed);
        var config = SessionContext.GetDeviceConfig(this);
        if (mode == WorkMode.MultiMeas && config?.MCRConfig?.DailyCount > 0)
        {
            var roundInterval = TimeSpan.FromSeconds(24d * 60d * 60d / config.MCRConfig.DailyCount);
            return Max(roundInterval + minTimeout, roundInterval * 1.5);
        }

        if (config?.SampleInterval > 0)
        {
            var expectedInterval = TimeSpan.FromSeconds(Math.Max(1d, (double)config.SampleInterval * 10d));
            return Max(minTimeout, expectedInterval);
        }

        return minTimeout;
    }

    private static bool RequiresReceiveActivity(WorkMode mode)
    {
        return mode is WorkMode.LWaveRead
            or WorkMode.TWaveRead
            or WorkMode.SingleMeas
            or WorkMode.MultiMeas
            or WorkMode.DualMeas
            or WorkMode.FullWaveRead
            or WorkMode.ReadParam;
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right)
    {
        return left >= right ? left : right;
    }

    private string GetSessionId(IPEndPoint remoteEndPoint)
    {
        var address = remoteEndPoint.Address;
        var ipString = address.AddressFamily switch
        {
            AddressFamily.InterNetworkV6 when address.IsIPv4MappedToIPv6 => address.MapToIPv4().ToString(),
            AddressFamily.InterNetwork => address.ToString(),
            _ => address.ToString()
        };
        return $"{ipString}:{remoteEndPoint.Port}";
    }
}
