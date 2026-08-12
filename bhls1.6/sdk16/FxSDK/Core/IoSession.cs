using System.Threading.Channels;
using FxSDK.Codec;
using FxSDK.Models;
using FxSDK.Utility;
using NLog;

namespace FxSDK.Core;

/// <summary>
/// SDK和设备会话
/// </summary>
public abstract class IoSession : IDisposable
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    internal readonly FxProtocolHandler ProtocolHandler;
    private Action<IoSession, DecodedResultsReceivedEventArgs>? _decodedResultHandler;
    private Action<IoSession, SessionTrafficObservedEventArgs>? _trafficObservedHandler;
    private Action<IoSession>? _sessionConnectedHandler;
    private Action<IoSession, SessionDisconnectedEventArgs>? _sessionDisconnectedHandler;
    private bool _disposed = false; // 添加一个标志
    private int _resourcesCleared;
    private long _lastReceiveUtcTicks;
    private long _lastDecodedUtcTicks;
    private long _lastSendUtcTicks;
    private readonly Channel<DecodedResult[]> _decodedResultQueue = Channel.CreateUnbounded<DecodedResult[]>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private int _queuedResultBatchCount;
    private Task _decodedResultDispatchTask = Task.CompletedTask;
    private DateTime _lastResultQueueLogTime = DateTime.MinValue;
    protected IoSession(FxProtocolHandler protocolHandler)
    {
        ProtocolHandler = protocolHandler ?? throw new ArgumentNullException(nameof(protocolHandler));
        var nowTicks = DateTime.UtcNow.Ticks;
        _lastReceiveUtcTicks = nowTicks;
        _lastDecodedUtcTicks = nowTicks;
        _lastSendUtcTicks = nowTicks;
    }

    /// <summary>
    /// 会话取消令牌（由派生类提供）
    /// </summary>
    protected abstract CancellationToken SessionToken { get; init; }

    /// <summary>
    /// 解码器发生无法恢复的异常时，请求派生会话异步关闭。
    /// 调用方不能等待关闭完成，因为关闭流程会等待当前解码任务退出。
    /// </summary>
    protected abstract void RequestClose(SessionCloseReason reason, Exception? exception);

    /// <summary>
    /// 会话标识
    /// </summary>
    public string SessionId { get; protected set; } = string.Empty;

    public virtual bool IsConnected => true;
    public DateTime LastReceiveUtc => new(Interlocked.Read(ref _lastReceiveUtcTicks), DateTimeKind.Utc);
    public DateTime LastDecodedUtc => new(Interlocked.Read(ref _lastDecodedUtcTicks), DateTimeKind.Utc);
    public DateTime LastSendUtc => new(Interlocked.Read(ref _lastSendUtcTicks), DateTimeKind.Utc);

    /// <summary>
    /// 心跳间隔（秒）
    /// </summary>
    protected static readonly double HeartbeatSeconds = 20;

    #region 触发事件
    protected void FireSessionConnected()
    {
        _sessionConnectedHandler?.Invoke(this);
    }

    protected void FireSessionDisconnected(SessionCloseReason reason, Exception? ex = null)
    {
        _sessionDisconnectedHandler?.Invoke(this, new SessionDisconnectedEventArgs(reason, ex));
    }

    internal void EnqueueDecodedResults(DecodedResult[] batchResults)
    {
        if (batchResults.Length <= 0 || _disposed)
        {
            return;
        }

        Interlocked.Increment(ref _queuedResultBatchCount);
        if (!_decodedResultQueue.Writer.TryWrite(batchResults))
        {
            Interlocked.Decrement(ref _queuedResultBatchCount);
            Logger.Warn("Decoded result queue write failed. SessionId={SessionId}, BatchSize={BatchSize}", SessionId,
                batchResults.Length);
        }
        LogResultQueuePressureIfNeeded();
    }

    internal void SetDecodedResultHandler(Action<IoSession, DecodedResultsReceivedEventArgs>? handler)
    {
        _decodedResultHandler = handler;
    }

    internal void SetTrafficObservedHandler(Action<IoSession, SessionTrafficObservedEventArgs>? handler)
    {
        _trafficObservedHandler = handler;
    }

    internal void SetLifecycleHandler(
        Action<IoSession>? connectedHandler,
        Action<IoSession, SessionDisconnectedEventArgs>? disconnectedHandler)
    {
        _sessionConnectedHandler = connectedHandler;
        _sessionDisconnectedHandler = disconnectedHandler;
    }
    #endregion

    /// <summary>
    /// 子类可重写以调整解码轮询间隔（默认 10ms）
    /// </summary>
    protected virtual int GetDecodeDelayMilliseconds() => 10;
    
    protected async Task DecodingLoop()
    {
        while (!SessionToken.IsCancellationRequested)
        {
            try
            {
                await ProtocolHandler.WaitForDataAsync(SessionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SessionToken.IsCancellationRequested)
            {
                return;
            }
            
            while (!SessionToken.IsCancellationRequested)
            {
                SessionToken.ThrowIfCancellationRequested();
                List<DecodedResult>? results = null;

                try
                {
                    results = ProtocolHandler.TryDecode(this, SessionToken);
                }
                catch (OperationCanceledException) when (SessionToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    Logger.Error(e,
                        "TryDecode threw an unrecoverable exception. Closing session. SessionId={SessionId}",
                        SessionId);
                    ProtocolHandler.ClearContext();
                    RequestClose(SessionCloseReason.ProtocolError, e);
                    return;
                }

                if (results is not {Count: > 0})
                {
                    break;
                }

                MarkDecoded();
                EnqueueDecodedResults(results.ToArray());
            }
        }
    }

    protected void MarkReceived()
    {
        Interlocked.Exchange(ref _lastReceiveUtcTicks, DateTime.UtcNow.Ticks);
    }

    protected void MarkSent()
    {
        Interlocked.Exchange(ref _lastSendUtcTicks, DateTime.UtcNow.Ticks);
    }

    protected void FireTrafficObserved(SessionTrafficDirection direction, ReadOnlyMemory<byte> data)
    {
        var handler = _trafficObservedHandler;
        if (handler is null || data.IsEmpty)
        {
            return;
        }

        handler(this, new SessionTrafficObservedEventArgs(direction, data));
    }

    private void MarkDecoded()
    {
        Interlocked.Exchange(ref _lastDecodedUtcTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// 所有派生类必须调用
    /// </summary>
    internal virtual Task OpenAsync()
    {
        StartDecodedResultDispatchLoop();
        FireSessionConnected();
        return Task.CompletedTask;
    }

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    internal async Task SendMsgAsync(IReadOnlyList<object> msgList)
    {
        var lockTaken = false;
        try
        {
            await _sendLock.WaitAsync(SessionToken);
            lockTaken = true;
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
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"SendMsgAsync failed: {ex.Message}");
        }
        finally
        {
            if (lockTaken)
            {
                try
                {
                    _sendLock.Release();
                }
                catch (ObjectDisposedException)
                {
                    // 会话关闭和发送命令并发时，发送锁可能已经被释放。
                }
            }
        }
    }

    protected abstract Task SendDataAsync(byte[] data);

    private void StartDecodedResultDispatchLoop()
    {
        if (!_decodedResultDispatchTask.IsCompleted)
        {
            return;
        }

        _decodedResultDispatchTask = Task.Run(DispatchDecodedResultsAsync);
    }

    private async Task DispatchDecodedResultsAsync()
    {
        try
        {
            await foreach (var batchResults in _decodedResultQueue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queuedResultBatchCount);
                try
                {
                    _decodedResultHandler?.Invoke(this, new DecodedResultsReceivedEventArgs(batchResults));
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Decoded result dispatch failed. SessionId={SessionId}", SessionId);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Decoded result dispatch loop failed. SessionId={SessionId}", SessionId);
        }
    }

    private void LogResultQueuePressureIfNeeded()
    {
        var queued = Volatile.Read(ref _queuedResultBatchCount);
        if (queued < 1024)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastResultQueueLogTime < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastResultQueueLogTime = now;
        Logger.Warn("Decoded result channel backlog. SessionId={SessionId}, QueuedBatches={QueuedBatches}", SessionId,
            queued);
    }

    public virtual async Task CloseAsync()
    {
        await CloseAsync(drainDecodedResults: true).ConfigureAwait(false);
    }

    public virtual async Task CloseFastAsync()
    {
        await CloseAsync(drainDecodedResults: false).ConfigureAwait(false);
    }

    private async Task CloseAsync(bool drainDecodedResults)
    {
        try
        {
            _disposed = true;
            if (!drainDecodedResults)
            {
                _decodedResultHandler = null;
            }

            _decodedResultQueue.Writer.TryComplete();
            try
            {
                var timeout = drainDecodedResults ? TimeSpan.FromSeconds(3) : TimeSpan.FromMilliseconds(200);
                await _decodedResultDispatchTask.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Logger.Warn("Decoded result dispatch loop did not complete within timeout. SessionId={SessionId}, Drain={Drain}",
                    SessionId, drainDecodedResults);
            }
            ProtocolHandler.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"IoSession.CloseAsync failed: {ex.Message}");
        }
        finally
        {
            ClearSessionResources();
        }
    }

    private void ClearSessionResources()
    {
        if (Interlocked.Exchange(ref _resourcesCleared, 1) != 0)
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(SessionId))
            {
                SessionContext.ClearContext(this);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Session context cleanup failed. SessionId={SessionId}", SessionId);
        }

        _sessionConnectedHandler = null;
        _sessionDisconnectedHandler = null;
        _decodedResultHandler = null;
        _trafficObservedHandler = null;

        try
        {
            _sendLock.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Send lock dispose failed. SessionId={SessionId}", SessionId);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _decodedResultQueue.Writer.TryComplete();
        }
        catch
        {
            // ignore dispose path cleanup errors
        }

        try
        {
            ProtocolHandler.Dispose();
        }
        catch
        {
            // ignore dispose path cleanup errors
        }

        ClearSessionResources();
        GC.SuppressFinalize(this);
    }
}
