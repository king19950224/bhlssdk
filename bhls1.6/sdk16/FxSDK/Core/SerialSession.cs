using System.IO.Ports;
using FxSDK.Codec;
using FxSDK.Models;
using FxSDK.Utility;
using NLog;

namespace FxSDK.Core;

public class SerialSession : IoSession
{
    private static readonly ILogger Logger = LogManager.GetLogger(nameof(SerialSession));

    private readonly SerialPort _serialPort;
    private readonly CancellationTokenSource _sessionCts;
    private readonly CancellationTokenRegistration _externalCancellationRegistration;

    // 使用原子操作确保线程安全
    private int _isClosed = 0; 
    private Task _decodeTask = Task.CompletedTask;
    private Task[] _backgroundTasks = Array.Empty<Task>(); // 追踪后台任务

    public SerialSession(SerialPort serialPort, FxProtocolHandler fxProtocolHandler, CancellationToken externalToken)
        : base(fxProtocolHandler)
    {
        _serialPort = serialPort ?? throw new ArgumentNullException(nameof(serialPort));
        // 外部提供了可取消的 token：链接它；否则 创建内部的
        _sessionCts = externalToken.CanBeCanceled 
            ? CancellationTokenSource.CreateLinkedTokenSource(externalToken) 
            : new CancellationTokenSource();
        
        SessionToken = _sessionCts.Token;
        SessionId = _serialPort.PortName;
        // 注册外部取消回调
        if (externalToken.CanBeCanceled)
        {
            _externalCancellationRegistration = externalToken.Register(() =>
            {
                Logger.Debug($"External cancellation triggered for session {SessionId}");
                _ = CloseAsync(SessionCloseReason.Normal); // 外部取消时主动关闭会话
            });
        }
    }

    protected sealed override CancellationToken SessionToken { get; init; }

    protected override void RequestClose(SessionCloseReason reason, Exception? exception)
    {
        _ = CloseAsync(reason, exception);
    }

    internal override async Task OpenAsync()
    {
        if (_serialPort is not { IsOpen: true })
            throw new InvalidOperationException("The SerialPort is not open.");

        // 清理旧事件
        _serialPort.DataReceived -= SerialPort_DataReceived;
        _serialPort.DataReceived += SerialPort_DataReceived;
        
        if (!SessionToken.IsCancellationRequested)
        {
            await base.OpenAsync();
            // 启动解码任务
            _decodeTask = DecodingLoop();
        }
        _backgroundTasks = new[] { _decodeTask };
    }

    private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        // 快速退出：已关闭 或 端口异常
        if (_isClosed == 1 || !_serialPort.IsOpen)
            return;

        try
        {
            var bytesToRead = _serialPort.BytesToRead;
            if (bytesToRead <= 0) return;

            var buffer = new byte[bytesToRead];
            var read = _serialPort.Read(buffer, 0, bytesToRead);

            if (read > 0)
            {
                var receivedData = read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
                if (FxUtility.DeveloperMode)
                {
                    Logger.Debug($"receive: {ByteUtility.BytesToHexStr(receivedData)}");
                }
                ProtocolHandler.Enqueue(receivedData);
            }
            else
            {
                Task.Delay(10, SessionToken);
            }
        }
        catch (ObjectDisposedException ex)
        {
            // SerialPort 已被 Dispose
            Logger.Warn("SerialPort already disposed during DataReceived.");
            _ = CloseAsync(SessionCloseReason.ConnectionLost, ex);
        }
        catch (InvalidOperationException ex)
        {
            // 端口未打开等状态错误
            Logger.Warn($"SerialPort state error during DataReceived: {ex.Message}");
            _ = CloseAsync(SessionCloseReason.ConnectionLost, ex);
        }
        catch (IOException ex)
        {
            // 设备断开连接 (e.g., USB unplugged)
            Logger.Warn(ex, "Serial port device disconnected (e.g., USB unplugged)");
            _ = CloseAsync(SessionCloseReason.ConnectionLost, ex);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unexpected error in SerialPort_DataReceived");
            _ = CloseAsync(SessionCloseReason.ProtocolError, ex);
        }
    }
    
    // ====== 发送数据 ======
    protected override Task SendDataAsync(byte[] data)
    {
        if (data is null || data.Length == 0 || _isClosed == 1 || SessionToken.IsCancellationRequested)
            return Task.CompletedTask;

        if (!_serialPort.IsOpen)
        {
            Logger.Warn("Attempted to send on a closed SerialPort.");
            _ = CloseAsync(SessionCloseReason.ConnectionLost, new InvalidOperationException("SerialPort is not open during send"));
            return Task.CompletedTask;
        }

        try
        {
            _serialPort.Write(data, 0, data.Length);

            if (FxUtility.DeveloperMode)
            {
                Logger.Debug($"send: {ByteUtility.BytesToHexStr(data)}");
            }
        }
        catch (ObjectDisposedException ex)
        {
            Logger.Warn("SerialPort already disposed during send");
            _ = CloseAsync(SessionCloseReason.ConnectionLost, ex);
        }
        catch (InvalidOperationException ex)
        {
            Logger.Warn($"SerialPort not open during send: {ex.Message}");
            _ = CloseAsync(SessionCloseReason.ConnectionLost, ex);
        }
        catch (IOException ex)
        {
            Logger.Warn(ex, "SerialPort I/O error during send");
            _ = CloseAsync(SessionCloseReason.ConnectionLost, ex);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unexpected error in SendDataAsync");
            _ = CloseAsync(SessionCloseReason.ProtocolError, ex);
        }

        return Task.CompletedTask;
    }

    // ====== 关闭逻辑 ======
    public override async Task CloseAsync()
    {
        await CloseAsync(SessionCloseReason.Normal, waitForBackgroundTasks: TimeSpan.FromSeconds(3), fastBaseClose: false).ConfigureAwait(false);
    }

    public override async Task CloseFastAsync()
    {
        await CloseAsync(SessionCloseReason.Normal, waitForBackgroundTasks: TimeSpan.FromMilliseconds(200), fastBaseClose: true).ConfigureAwait(false);
    }

    private async Task CloseAsync(
        SessionCloseReason reason,
        Exception? exception = null,
        TimeSpan? waitForBackgroundTasks = null,
        bool fastBaseClose = false)
    {
        // 使用 Interlocked.CompareExchange 确保只有一个线程能执行核心关闭逻辑
        if (Interlocked.CompareExchange(ref _isClosed, 1, 0) != 0)
            return;

        Logger.Debug($"Closing serial session {SessionId}, reason: {reason}");

        try
        {
            // 1. 取消所有基于 SessionToken 的操作
            try
            {
                _sessionCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // CTS 已被释放，忽略
            }

            // 2. 关闭 SerialPort，中断所有阻塞的 I/O 操作
            try
            {
                _serialPort.DataReceived -= SerialPort_DataReceived;
                if (_serialPort is { IsOpen: true })
                {
                    _serialPort.Close(); // 这会中断 DataReceived 回调的读取操作
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Error while closing serial port during CloseAsync");
            }

            // 3. 等待后台任务完成
            if (_backgroundTasks.Length > 0)
            {
                try
                {
                    var timeout = waitForBackgroundTasks ?? TimeSpan.FromSeconds(3);
                    await Task.WhenAll(_backgroundTasks).WaitAsync(timeout).ConfigureAwait(false);
                    Logger.Debug($"Session {SessionId} all background tasks completed successfully");
                }
                catch (TimeoutException)
                {
                    Logger.Warn($"Session {SessionId} background tasks did not complete within close timeout. ");
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"Error waiting for background tasks in session {SessionId}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Error during core close logic for session {SessionId}");
        }
        finally
        {
            // 4. 释放资源
            try { _serialPort.DataReceived -= SerialPort_DataReceived; } catch { /* ignore */ }
            try { _serialPort.Dispose(); } catch { /* ignore */ }
            try { _sessionCts?.Dispose(); } catch { /* ignore */ }
            try { _externalCancellationRegistration.Dispose(); } catch { /* ignore */ }
            
            // 5. 通知生命周期回调
            try
            {
                FireSessionDisconnected(reason, exception);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Exception in session disconnected lifecycle handler");
            }
            
            // 6. 调用基类清理
            if (fastBaseClose)
            {
                await base.CloseFastAsync().ConfigureAwait(false);
            }
            else
            {
                await base.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
