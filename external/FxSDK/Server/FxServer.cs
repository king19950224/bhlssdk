using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FxSDK.Codec;
using FxSDK.Core;
using FxSDK.Models;
using NLog;

namespace FxSDK.Server;

/// <summary>
/// TCP server entry for Fx devices that actively connect to the SDK.
/// </summary>
public sealed class FxServer : IAsyncDisposable
{
    private static readonly ILogger Logger = LogManager.GetLogger(nameof(FxServer));

    private readonly IFxProtocolHandlerFactory _protocolHandlerFactory;
    private readonly IFxDeviceKeyResolver _deviceKeyResolver;
    private readonly ConcurrentDictionary<int, ListenerState> _listeners = new();
    private readonly ConcurrentDictionary<string, FxServerSessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<IoSession, string> _connectionIdsBySession = new();
    private readonly ConcurrentDictionary<string, string> _connectionIdsByDeviceKey = new();
    private readonly CancellationTokenSource _serverCts = new();

    public event EventHandler<FxServerSessionAcceptedEventArgs>? OnSessionAccepted;
    public event EventHandler<FxServerDeviceIdentifiedEventArgs>? OnDeviceIdentified;
    public event EventHandler<FxServerDecodedResultsEventArgs>? OnResultReceived;
    public event EventHandler<FxServerSessionDisconnectedEventArgs>? OnSessionDisconnected;
    public event EventHandler<FxServerSessionRejectedEventArgs>? OnSessionRejected;
    public event EventHandler<FxServerTrafficObservedEventArgs>? OnTrafficObserved;

    public FxServer(
        IFxProtocolHandlerFactory? protocolHandlerFactory = null,
        IFxDeviceKeyResolver? deviceKeyResolver = null)
    {
        _protocolHandlerFactory = protocolHandlerFactory ?? FxProtocolHandlerFactory.Shared;
        _deviceKeyResolver = deviceKeyResolver ?? CompositeDeviceKeyResolver.CreateDefault();
    }

    public Task StartAsync(FxServerListenerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        if (_serverCts.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(FxServer));
        }

        var listenerOptions = options.Clone();
        if (_listeners.ContainsKey(listenerOptions.ListenPort))
        {
            return Task.CompletedTask;
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var listenerCts = CancellationTokenSource.CreateLinkedTokenSource(_serverCts.Token, cancellationToken);
        try
        {
            socket.Bind(new IPEndPoint(listenerOptions.ListenAddress, listenerOptions.ListenPort));
            socket.Listen(listenerOptions.Backlog);

            var listener = new ListenerState(listenerOptions, socket, listenerCts);
            if (!_listeners.TryAdd(listenerOptions.ListenPort, listener))
            {
                socket.Dispose();
                listenerCts.Dispose();
                return Task.CompletedTask;
            }

            listener.AcceptTask = Task.Run(() => AcceptLoopAsync(listener), CancellationToken.None);
            listener.HealthTask = Task.Run(() => HealthLoopAsync(listener), CancellationToken.None);
            Logger.Info("FxServer listener started. Port={Port}, Model={Model}, Name={Name}",
                listenerOptions.ListenPort, listenerOptions.DeviceModel, listenerOptions.Name);
        }
        catch
        {
            socket.Dispose();
            listenerCts.Dispose();
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var listeners = _listeners.Keys.ToList();
        foreach (var port in listeners)
        {
            await StopListenerAsync(port, cancellationToken).ConfigureAwait(false);
        }

        foreach (var session in _sessions.Values.ToList())
        {
            await CloseSessionInternalAsync(session, SessionCloseReason.Normal, "FxServer stopped")
                .ConfigureAwait(false);
        }
    }

    public async Task StopListenerAsync(int listenPort, CancellationToken cancellationToken = default)
    {
        if (!_listeners.TryRemove(listenPort, out var listener))
        {
            return;
        }

        try
        {
            listener.CancellationTokenSource.Cancel();
        }
        catch
        {
            // ignored
        }

        try
        {
            listener.Socket.Dispose();
        }
        catch
        {
            // ignored
        }

        var sessions = _sessions.Values.Where(s => s.ListenPort == listenPort).ToList();
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CloseSessionInternalAsync(session, SessionCloseReason.Normal, "Listener stopped")
                .ConfigureAwait(false);
        }

        await WaitListenerTasksAsync(listener).ConfigureAwait(false);
        listener.CancellationTokenSource.Dispose();
        Logger.Info("FxServer listener stopped. Port={Port}", listenPort);
    }

    public async Task CloseSessionAsync(string connectionId, SessionCloseReason reason = SessionCloseReason.Normal)
    {
        if (_sessions.TryGetValue(connectionId, out var session))
        {
            await CloseSessionInternalAsync(session, reason, "Session closed by caller").ConfigureAwait(false);
        }
    }

    public async Task CloseDeviceSessionAsync(FxDeviceKey deviceKey, SessionCloseReason reason = SessionCloseReason.Normal)
    {
        var stableKey = deviceKey.ToStableKey();
        if (_connectionIdsByDeviceKey.TryGetValue(stableKey, out var connectionId))
        {
            await CloseSessionAsync(connectionId, reason).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<FxServerSessionInfo> GetSessions()
    {
        return _sessions.Values.Select(UpdateSessionActivity).ToList();
    }

    public FxServerSessionInfo? GetSession(string connectionId)
    {
        return _sessions.TryGetValue(connectionId, out var session)
            ? UpdateSessionActivity(session)
            : null;
    }

    public FxServerSessionInfo? GetSession(FxDeviceKey deviceKey)
    {
        return _connectionIdsByDeviceKey.TryGetValue(deviceKey.ToStableKey(), out var connectionId)
            ? GetSession(connectionId)
            : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_serverCts.IsCancellationRequested)
        {
            _serverCts.Cancel();
        }

        await StopAsync().ConfigureAwait(false);
        _serverCts.Dispose();
    }

    private async Task AcceptLoopAsync(ListenerState listener)
    {
        var token = listener.CancellationTokenSource.Token;
        while (!token.IsCancellationRequested)
        {
            Socket? acceptedSocket = null;
            try
            {
                acceptedSocket = await listener.Socket.AcceptAsync(token).ConfigureAwait(false);
                if (GetListenerSessionCount(listener.Options.ListenPort) >= listener.Options.MaxConnections)
                {
                    RejectSocket(acceptedSocket, listener.Options.ListenPort, "Max connections reached");
                    acceptedSocket = null;
                    continue;
                }

                ConfigureAcceptedSocket(acceptedSocket, listener.Options);
                var sessionSocket = acceptedSocket;
                acceptedSocket = null;
                _ = Task.Run(() => OpenAcceptedSessionAsync(sessionSocket, listener), CancellationToken.None);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "FxServer accept loop failed. Port={Port}", listener.Options.ListenPort);
                try
                {
                    acceptedSocket?.Dispose();
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private async Task OpenAcceptedSessionAsync(Socket socket, ListenerState listener)
    {
        var connectionId = $"tcp-server:{listener.Options.ListenPort}:{Guid.NewGuid():N}";
        TcpSession? tcpSession = null;

        try
        {
            var token = listener.CancellationTokenSource.Token;
            var remoteEndPoint = socket.RemoteEndPoint as IPEndPoint;
            var handler = _protocolHandlerFactory.Create(listener.Options.DeviceModel);
            tcpSession = new TcpSession(socket, handler, token);
            var now = DateTime.UtcNow;
            var sessionInfo = new FxServerSessionInfo
            {
                ConnectionId = connectionId,
                ListenPort = listener.Options.ListenPort,
                RemoteEndPoint = remoteEndPoint?.ToString() ?? string.Empty,
                RemoteAddress = remoteEndPoint?.Address.ToString() ?? string.Empty,
                RemotePort = remoteEndPoint?.Port ?? 0,
                DeviceModel = listener.Options.DeviceModel,
                RawSessionId = tcpSession.SessionId,
                ConnectedAtUtc = now,
                LastReceiveUtc = now,
                LastDecodedUtc = now,
                ListenerOptions = listener.Options,
                Session = tcpSession
            };

            _sessions[connectionId] = sessionInfo;
            _connectionIdsBySession[tcpSession] = connectionId;

            tcpSession.SetLifecycleHandler(OnTcpSessionConnected, OnTcpSessionDisconnected);
            tcpSession.SetDecodedResultHandler(OnDecodedResultReceived);
            tcpSession.SetTrafficObservedHandler(OnSessionTrafficObserved);
            await tcpSession.OpenAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "FxServer failed to open accepted session. ConnectionId={ConnectionId}", connectionId);
            RejectSocket(socket, listener.Options.ListenPort, "Session open failed", ex);
            if (tcpSession != null)
            {
                _connectionIdsBySession.TryRemove(tcpSession, out _);
            }

            _sessions.TryRemove(connectionId, out _);
        }
    }

    private void OnTcpSessionConnected(IoSession session)
    {
        if (!_connectionIdsBySession.TryGetValue(session, out var connectionId) ||
            !_sessions.TryGetValue(connectionId, out var serverSession))
        {
            return;
        }

        UpdateSessionActivity(serverSession);
        SafeInvoke(() => OnSessionAccepted?.Invoke(this,
            new FxServerSessionAcceptedEventArgs {Session = serverSession}));
    }

    private void OnTcpSessionDisconnected(IoSession session, SessionDisconnectedEventArgs e)
    {
        if (!_connectionIdsBySession.TryRemove(session, out var connectionId))
        {
            return;
        }

        if (!_sessions.TryRemove(connectionId, out var serverSession))
        {
            return;
        }

        if (serverSession.StableDeviceKey != null &&
            _connectionIdsByDeviceKey.TryGetValue(serverSession.StableDeviceKey, out var mappedConnectionId) &&
            mappedConnectionId == connectionId)
        {
            _connectionIdsByDeviceKey.TryRemove(serverSession.StableDeviceKey, out _);
        }

        session.SetLifecycleHandler(null, null);
        session.SetDecodedResultHandler(null);
        session.SetTrafficObservedHandler(null);

        SafeInvoke(() => OnSessionDisconnected?.Invoke(this,
            new FxServerSessionDisconnectedEventArgs
            {
                Session = serverSession,
                DeviceKey = serverSession.DeviceKey,
                Reason = e.Reason,
                Exception = e.Exception,
                Message = e.Exception?.Message
            }));
    }

    private void OnDecodedResultReceived(IoSession session, DecodedResultsReceivedEventArgs e)
    {
        if (!_connectionIdsBySession.TryGetValue(session, out var connectionId) ||
            !_sessions.TryGetValue(connectionId, out var serverSession))
        {
            return;
        }

        UpdateSessionActivity(serverSession);
        TryResolveDeviceKey(serverSession, e.Results);

        SafeInvoke(() => OnResultReceived?.Invoke(this,
            new FxServerDecodedResultsEventArgs
            {
                Session = serverSession,
                DeviceKey = serverSession.DeviceKey,
                Results = e.Results
            }));
    }

    private void OnSessionTrafficObserved(IoSession session, SessionTrafficObservedEventArgs e)
    {
        if (OnTrafficObserved is null ||
            !_connectionIdsBySession.TryGetValue(session, out var connectionId) ||
            !_sessions.TryGetValue(connectionId, out var serverSession))
        {
            return;
        }

        SafeInvoke(() => OnTrafficObserved.Invoke(this,
            new FxServerTrafficObservedEventArgs
            {
                Session = serverSession,
                Direction = e.Direction,
                Data = e.Data
            }));
    }

    private void TryResolveDeviceKey(FxServerSessionInfo serverSession, DecodedResult[] results)
    {
        if (serverSession.IsDeviceKeyResolved)
        {
            return;
        }

        if (!_deviceKeyResolver.TryResolve(serverSession, results, out var deviceKey))
        {
            return;
        }

        var stableKey = deviceKey.ToStableKey();
        if (serverSession.ListenerOptions.ReplaceExistingDeviceSession &&
            _connectionIdsByDeviceKey.TryGetValue(stableKey, out var oldConnectionId) &&
            oldConnectionId != serverSession.ConnectionId &&
            _sessions.TryGetValue(oldConnectionId, out var oldSession))
        {
            _ = CloseSessionInternalAsync(oldSession, SessionCloseReason.Normal,
                $"Replaced by new session {serverSession.ConnectionId}");
        }

        serverSession.DeviceKey = deviceKey;
        serverSession.StableDeviceKey = stableKey;
        serverSession.IsDeviceKeyResolved = true;
        serverSession.DeviceKeyResolvedAtUtc = DateTime.UtcNow;
        _connectionIdsByDeviceKey[stableKey] = serverSession.ConnectionId;

        SafeInvoke(() => OnDeviceIdentified?.Invoke(this,
            new FxServerDeviceIdentifiedEventArgs
            {
                Session = serverSession,
                DeviceKey = deviceKey
            }));
    }

    private async Task HealthLoopAsync(ListenerState listener)
    {
        var token = listener.CancellationTokenSource.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(listener.Options.HealthCheckInterval, token).ConfigureAwait(false);
                await CheckSessionHealthAsync(listener).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "FxServer health loop failed. Port={Port}", listener.Options.ListenPort);
            }
        }
    }

    private async Task CheckSessionHealthAsync(ListenerState listener)
    {
        var now = DateTime.UtcNow;
        var sessions = _sessions.Values
            .Where(s => s.ListenPort == listener.Options.ListenPort)
            .Select(UpdateSessionActivity)
            .ToList();

        foreach (var session in sessions)
        {
            if (!session.IsDeviceKeyResolved)
            {
                if (now - session.ConnectedAtUtc > listener.Options.IdentifyTimeout)
                {
                    await RejectSessionAsync(session, "Identify timeout").ConfigureAwait(false);
                }

                continue;
            }

            if (now - session.LastReceiveUtc > listener.Options.ReceiveIdleTimeout)
            {
                await CloseUnhealthySessionAsync(session, SessionCloseReason.HeartbeatTimeout,
                    "Receive idle timeout").ConfigureAwait(false);
                continue;
            }

            if (now - session.LastDecodedUtc > listener.Options.DecodeIdleTimeout)
            {
                await CloseUnhealthySessionAsync(session, SessionCloseReason.ProtocolError,
                    "Decode idle timeout").ConfigureAwait(false);
            }
        }
    }

    private async Task RejectSessionAsync(FxServerSessionInfo session, string reason, Exception? exception = null)
    {
        SafeInvoke(() => OnSessionRejected?.Invoke(this,
            new FxServerSessionRejectedEventArgs
            {
                RemoteEndPoint = session.RemoteEndPoint,
                ListenPort = session.ListenPort,
                Reason = reason,
                Exception = exception
            }));

        await CloseSessionInternalAsync(session, SessionCloseReason.ProtocolError, reason, exception)
            .ConfigureAwait(false);
    }

    private async Task CloseUnhealthySessionAsync(
        FxServerSessionInfo session,
        SessionCloseReason reason,
        string message)
    {
        if (!session.ListenerOptions.CloseUnhealthySession)
        {
            return;
        }

        await CloseSessionInternalAsync(session, reason, message, new TimeoutException(message))
            .ConfigureAwait(false);
    }

    private async Task CloseSessionInternalAsync(
        FxServerSessionInfo session,
        SessionCloseReason reason,
        string? message,
        Exception? exception = null)
    {
        try
        {
            await ((TcpSession)session.Session).CloseAsync(reason, exception).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "FxServer close session failed. ConnectionId={ConnectionId}, Message={Message}",
                session.ConnectionId, message);
        }
    }

    private FxServerSessionInfo UpdateSessionActivity(FxServerSessionInfo session)
    {
        session.LastReceiveUtc = session.Session.LastReceiveUtc;
        session.LastDecodedUtc = session.Session.LastDecodedUtc;
        return session;
    }

    private int GetListenerSessionCount(int listenPort)
    {
        return _sessions.Values.Count(s => s.ListenPort == listenPort);
    }

    private static void ValidateOptions(FxServerListenerOptions options)
    {
        if (options.ListenPort is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ListenPort), "ListenPort must be between 1 and 65535.");
        }

        if (options.MaxConnections <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxConnections), "MaxConnections must be positive.");
        }

        if (options.Backlog <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Backlog), "Backlog must be positive.");
        }
    }

    private static void ConfigureAcceptedSocket(Socket socket, FxServerListenerOptions options)
    {
        socket.NoDelay = options.NoDelay;
        socket.SendBufferSize = options.SendBufferSize;
        socket.ReceiveBufferSize = options.ReceiveBufferSize;
    }

    private void RejectSocket(Socket socket, int listenPort, string reason, Exception? exception = null)
    {
        var remoteEndPoint = socket.RemoteEndPoint?.ToString() ?? string.Empty;
        try
        {
            socket.Dispose();
        }
        catch
        {
            // ignored
        }

        SafeInvoke(() => OnSessionRejected?.Invoke(this,
            new FxServerSessionRejectedEventArgs
            {
                RemoteEndPoint = remoteEndPoint,
                ListenPort = listenPort,
                Reason = reason,
                Exception = exception
            }));
    }

    private static async Task WaitListenerTasksAsync(ListenerState listener)
    {
        var tasks = new[] {listener.AcceptTask, listener.HealthTask}
            .Where(t => t != null)
            .Cast<Task>()
            .ToArray();

        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch
        {
            // shutdown best effort
        }
    }

    private static void SafeInvoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "FxServer event handler failed.");
        }
    }

    private sealed class ListenerState
    {
        public ListenerState(FxServerListenerOptions options, Socket socket, CancellationTokenSource cancellationTokenSource)
        {
            Options = options;
            Socket = socket;
            CancellationTokenSource = cancellationTokenSource;
        }

        public FxServerListenerOptions Options { get; }

        public Socket Socket { get; }

        public CancellationTokenSource CancellationTokenSource { get; }

        public Task? AcceptTask { get; set; }

        public Task? HealthTask { get; set; }
    }
}
