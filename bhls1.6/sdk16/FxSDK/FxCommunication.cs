using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FxSDK.Codec;
using FxSDK.Core;
using FxSDK.Models;
using FxSDK.Utility;
using NLog;

namespace FxSDK;

/// <summary>
/// FxClient会话相关
/// </summary>
partial class FxClient
{
    #region 会话管理相关
    private readonly ConcurrentDictionary<string, IoSession> _sessions = new();
    private void SubscribeSessionEvents(IoSession session)
    {
        try
        {
            session.SetLifecycleHandler(OnSessionConnected, OnSessionDisconnected);
            session.SetDecodedResultHandler(OnDecodedResultReceived);
        }
        catch (Exception ex)
        {
            Logger.Error($"FxClient SubscribeSessionEvents failed: {ex.Message}");
        }
    }
    private void UnSubscribeSessionEvents(IoSession session)
    {
        try
        {
            session.SetLifecycleHandler(null, null);
            session.SetDecodedResultHandler(null);
        }
        catch (Exception ex)
        {
            Logger.Error($"FxClient UnSubscribeSessionEvents failed: {ex.Message}");
        }
    }
    private void OnSessionConnected(IoSession newSession)
    {
        try
        {
            var replaced = _sessions.AddOrUpdate(
                newSession.SessionId,
                newSession,
                (sessionId, oldSession) =>
                {
                    if (!ReferenceEquals(oldSession, newSession))
                    {
                        _ = CloseReplacedSessionAsync(oldSession, sessionId);
                    }

                    return newSession;
                });

            if (!ReferenceEquals(replaced, newSession))
            {
                Logger.Warn("Session replacement did not store the new session immediately. SessionId={SessionId}",
                    newSession.SessionId);
            }

            OnConnected?.Invoke(newSession, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error in OnSessionConnected for session {SessionId}", newSession.SessionId);
            _ = newSession.CloseAsync();
        }
    }
    private void OnSessionDisconnected(IoSession session, SessionDisconnectedEventArgs e)
    {
        UnSubscribeSessionEvents(session);
        // 1：查看当前字典中是谁
        if (_sessions.TryGetValue(session.SessionId, out var current))
        {
            // 2：如果是自己，才移除
            if (ReferenceEquals(current, session))
            {
                _sessions.TryRemove(session.SessionId, out _);
                OnDisconnected?.Invoke(session, e);
            }
            else
            {
                Logger.Debug("Old session {Id} disconnected after replacement.", session.SessionId);
            }
        }
    }
    private void OnDecodedResultReceived(IoSession session, DecodedResultsReceivedEventArgs e)
    {
        try
        {
            OnResultReceived?.Invoke(session, e);
            //处理自动增益控制
            if (_sessionAgcContext.TryGetValue(session.SessionId, out var agcContext))
            {
                if (e.Results.Length <= 0) return;
                var decodeResult = e.Results[^1];
                if (decodeResult is ChannelResult {EchoWave.WaveData.Count: > 0} chResult)
                {
                    agcContext.AddWaveData(chResult.EchoWave.WaveData);   
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "OnSessionExceptionCaught 处理过程中发生意外异常 - 会话 {SessionId}", session.SessionId);
        }
    }
    /// <summary>
    /// 尝试关闭同个sessionId 的会话
    /// </summary>
    private async Task CloseSessionAsync(string sessionId, bool fastClose = false, bool delayAfterClose = true)
    {
        if (_sessions.TryRemove(sessionId, out var oldSession))
        {
            await CloseSessionInstanceAsync(oldSession, sessionId, fastClose, delayAfterClose).ConfigureAwait(false);
        }
    }

    private async Task CloseReplacedSessionAsync(IoSession oldSession, string sessionId)
    {
        await CloseSessionInstanceAsync(oldSession, sessionId, fastClose: true, delayAfterClose: false)
            .ConfigureAwait(false);
    }

    private async Task CloseSessionInstanceAsync(
        IoSession session,
        string sessionId,
        bool fastClose,
        bool delayAfterClose)
    {
        try
        {
            UnSubscribeSessionEvents(session);
            if (fastClose)
            {
                await session.CloseFastAsync().ConfigureAwait(false);
            }
            else
            {
                await session.CloseAsync().ConfigureAwait(false);
            }

            if (delayAfterClose)
            {
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, $"Error closing old session {sessionId}");
        }
    }
    #endregion
    /// <summary>
    /// 根据会话标识获取会话
    /// </summary>
    private IoSession? GetSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return null;
        }
        return _sessions.GetValueOrDefault(sessionId);
    }
    #region 通讯连接相关
    private async Task OpenConnectionAsync(DeviceConfig deviceConfig, CancellationToken cancellationToken)
    {
        // 根据设备模型选择协议处理器
        var fxProtocolHandler = FxProtocolHandlerFactory.Shared.Create(deviceConfig.Model);
        switch (deviceConfig.CommunicationType)
        {
            case 0: //串口
                await OpenSerialPortAsync(deviceConfig, fxProtocolHandler, cancellationToken);
                break;
            case 1: //TCP客户端
                await ConnectTcpClientAsync(deviceConfig, fxProtocolHandler, cancellationToken);
                break;
            case 2: //TCP服务器
                throw new NotSupportedException(
                    "TCP Server mode has moved to FxServer in FxSDK 2.0. " +
                    "Use FxSDK.Server.FxServer.StartAsync(FxServerListenerOptions) for devices that actively connect to the SDK.");
            default:
                throw new ArgumentException($"Unsupported communication type-{deviceConfig.CommunicationType}.",
                    nameof(deviceConfig.CommunicationType));
        }
    }
    private async Task OpenSerialPortAsync(DeviceConfig config, FxProtocolHandler fxProtocolHandler, CancellationToken cancellationToken)
    {
        // 解析通信参数
        var serialPort = config.CommunicationParameters.ToSerialPort();
        serialPort.ReadBufferSize = 81920;
        serialPort.WriteBufferSize = 40960;
        //打开串口
        serialPort.Open();
        // 创建 SerialSession 实例
        var serialSession = new SerialSession(serialPort, fxProtocolHandler, cancellationToken);
        SubscribeSessionEvents(serialSession);
        // 打开会话
        await serialSession.OpenAsync();
    }
    /// <summary>
    /// 开启 TCP Client 会话
    /// </summary>
    private async Task ConnectTcpClientAsync(DeviceConfig config, FxProtocolHandler fxProtocolHandler, CancellationToken cancellationToken)
    {
        var (host, port) = config.CommunicationParameters.ToIpEndPoint();
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            SendBufferSize = 64 * 1024,
            ReceiveBufferSize = 64 * 1024
        };

        TcpSession? tcpSession = null;
        try
        {
            // 等待旧会话完全关闭
            await CloseSessionAsync(config.GetSessionId()).ConfigureAwait(false);
        
            var connectTimeout = TimeSpan.FromSeconds(Math.Max(1d, config.ConnTimeOut));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(connectTimeout);
        
            if (!IPAddress.TryParse(host, out var address))
            {
                var ipAddresses = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
                if (ipAddresses.Length == 0)
                    throw new SocketException((int)SocketError.HostNotFound);
                address = ipAddresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork) ?? ipAddresses[0];
            }

            var endPoint = new IPEndPoint(address, port);
            await socket.ConnectAsync(endPoint, cts.Token).ConfigureAwait(false);
        
            tcpSession = new TcpSession(socket, fxProtocolHandler, cancellationToken, config.TcpSessionOptions);
            SubscribeSessionEvents(tcpSession);
            await tcpSession.OpenAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (tcpSession != null)
            {
                // 确保会话完全关闭
                await tcpSession.CloseAsync(SessionCloseReason.ProtocolError).ConfigureAwait(false);
            }
            else
            {
                // 确保 Socket 释放
                try { socket.Dispose(); } catch { /*ignore*/ }
            }

            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Connection timed out after {Math.Max(1d, config.ConnTimeOut):0.#}s.", ex);
            }

            throw; // 重新抛出异常
        }
    }
    #endregion
}
/// <summary>
/// 通讯异常
/// </summary>
public class CommunicationException : Exception
{
    public CommunicationException(string message, Exception innerException) : base(message, innerException) { }
}
public static class CommunicationExceptionMapper
{
    public static Exception ToCommunicationException(this Exception ex, string? context = null)
    {
        return ex switch
        {
            CommunicationException => ex,
            
            OperationCanceledException => new OperationCanceledException(context),

            SocketException { SocketErrorCode: var code } =>
                new CommunicationException($"Socket error ({code}): {ex.Message}", ex),

            TimeoutException =>
                new CommunicationException("Connection timed out", ex),

            ArgumentException =>
                new CommunicationException($"Invalid connection parameter: {ex.Message}", ex),

            UnauthorizedAccessException =>
                new CommunicationException($"Access denied: {ex.Message}", ex),

            IOException =>
                new CommunicationException("Device disconnected or unavailable", ex),
            
            _ => ex // 非通信异常不包装，直接抛出！
        };
    }
}
