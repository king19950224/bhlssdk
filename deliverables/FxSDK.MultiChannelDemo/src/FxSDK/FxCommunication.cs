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
    private readonly ConcurrentBag<int> _usedPorts = new();//已经监听的端口
    private readonly Dictionary<int, ushort> _serverModels = new(); // 端口号-设备模型
    private void SubscribeSessionEvents(IoSession session)
    {
        try
        {
            session.SessionConnected += OnSessionConnected;
            session.SessionDisconnected += OnSessionDisconnected;
            session.SessionDecResultsReceived += OnDecodedResultReceived;
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
            session.SessionConnected -= OnSessionConnected;
            session.SessionDisconnected -= OnSessionDisconnected;
            session.SessionDecResultsReceived -= OnDecodedResultReceived;
        }
        catch (Exception ex)
        {
            Logger.Error($"FxClient UnSubscribeSessionEvents failed: {ex.Message}");
        }
    }
    private async void OnSessionConnected(object? sender, EventArgs e)
    {
        if (sender is not IoSession newSession)
            return;

        try
        {
            // 等待旧会话完全关闭
            await CloseSessionAsync(newSession.SessionId).ConfigureAwait(false);
            // 注册新会话
            _sessions[newSession.SessionId] = newSession;
            OnConnected?.Invoke(sender, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error in OnSessionConnected for session {SessionId}", newSession.SessionId);
            _ = newSession.CloseAsync();
        }
    }
    private void OnSessionDisconnected(object? sender, SessionDisconnectedEventArgs e)
    {
        if (sender is not IoSession session) return;
        UnSubscribeSessionEvents(session);
        // 1：查看当前字典中是谁
        if (_sessions.TryGetValue(session.SessionId, out var current))
        {
            // 2：如果是自己，才移除
            if (ReferenceEquals(current, session))
            {
                _sessions.TryRemove(session.SessionId, out _);
                OnDisconnected?.Invoke(sender, e);
            }
            else
            {
                Logger.Debug("Old session {Id} disconnected after replacement.", session.SessionId);
            }
        }
    }
    private void OnDecodedResultReceived(object? sender, DecodedResultsReceivedEventArgs e)
    {
        if (sender is not IoSession session) return;
        try
        {
            OnResultReceived?.Invoke(session, new DecodedResultsReceivedEventArgs(e.Results));
            //处理自动增益控制
            if (_sessionAgcContext.TryGetValue(session.SessionId, out var agcContext) && !agcContext.Cts.IsCancellationRequested)
            {
                if (e.Results.Length <= 0) return;
                var decodeResult = e.Results.ToList().Last();
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
    private async Task CloseSessionAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var oldSession))
        {
            try
            {
                UnSubscribeSessionEvents(oldSession);
                await oldSession.CloseAsync();
                // 额外等待一小段时间,确保资源释放
                await Task.Delay(200); 
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, $"Error closing old session {sessionId}");
            }
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
    /// <summary>
    /// 创建协议编解码器
    /// 0 标准版协议
    /// 1 专家版协议
    /// 2 实时24通道版协议
    /// 3 或默认 多通道监测版协议
    /// </summary>
    private FxProtocolHandler CreateProtocolHandler(ushort model)
    {
        return model switch
        {
            0 => new StandardProtocolHandler(),
            1 => new ExpertProtocolHandler(),
            2 => new IotR24ProtocolHandler(),
            3 => new MultiChIotProtocolHandler(),
            _ => new MultiChIotProtocolHandler()
        };
    }
    private async Task OpenConnectionAsync(DeviceConfig deviceConfig, CancellationToken cancellationToken)
    {
        // 根据设备模型选择协议处理器
        var fxProtocolHandler = CreateProtocolHandler(deviceConfig.Model);
        switch (deviceConfig.CommunicationType)
        {
            case 0: //串口
                await OpenSerialPortAsync(deviceConfig, fxProtocolHandler, cancellationToken);
                break;
            case 1: //TCP客户端
                await ConnectTcpClientAsync(deviceConfig, fxProtocolHandler, cancellationToken);
                break;
            case 2: //TCP服务器
                var remotePort = GetLocalPort(deviceConfig.CommunicationParameters);
                _serverModels[remotePort] = deviceConfig.Model;
                await StartTcpServerAsync(deviceConfig, cancellationToken);
                break;
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
        
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(config.ConnTimeOut));
        
            if (!IPAddress.TryParse(host, out var address))
            {
                var ipAddresses = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
                if (ipAddresses.Length == 0)
                    throw new SocketException((int)SocketError.HostNotFound);
                address = ipAddresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork) ?? ipAddresses[0];
            }

            var endPoint = new IPEndPoint(address, port);
            await socket.ConnectAsync(endPoint, cts.Token).ConfigureAwait(false);
        
            tcpSession = new TcpSession(socket, fxProtocolHandler, cancellationToken);
            SubscribeSessionEvents(tcpSession);
            await tcpSession.OpenAsync().ConfigureAwait(false);
        }
        catch
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
            throw; // 重新抛出异常
        }
    }
    private readonly Dictionary<int, Socket> _serverSockets = new(); // 监听 Socket
    private async Task StartTcpServerAsync(DeviceConfig config, CancellationToken cancellationToken)
    {
        var localPort = GetLocalPort(config.CommunicationParameters);
        lock (_serverSockets)
        {
            if (_serverSockets.ContainsKey(localPort))
                return;
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                listener.Bind(new IPEndPoint(IPAddress.Any, localPort));
                listener.Listen(100);
                _serverSockets[localPort] = listener;
                // 启动接受循环，传入 listener 引用
                _ = Task.Run(() => TcpServerAcceptLoop(listener, localPort, cancellationToken), cancellationToken);
            }
            catch
            {
                listener.Dispose();
            }
        }
    }
    private async Task TcpServerAcceptLoop(Socket listener, int port, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var clientSocket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var localEndpoint = (IPEndPoint)clientSocket.LocalEndPoint!;
                        if (!_serverModels.TryGetValue(localEndpoint.Port, out var deviceModel))
                        {
                            clientSocket.Dispose();
                            return;
                        }
                        var handler = CreateProtocolHandler(deviceModel);
                        var session = new TcpSession(clientSocket, handler, cancellationToken);
                        SubscribeSessionEvents(session);
                        await session.OpenAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        clientSocket?.Dispose();
                    }
                }, cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
            {
                break;
            }
        }
    }
    /// <summary>
    /// 获取连接的本地端口
    /// </summary>
    private int GetLocalPort(string commParam)
    {
        var paramArray = commParam.Split(new[] { ':', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (paramArray.Length < 3)
        {
            throw new ArgumentException("Invalid communication parameter for TCP server.", nameof(commParam));
        }
        return int.Parse(paramArray[2]);
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