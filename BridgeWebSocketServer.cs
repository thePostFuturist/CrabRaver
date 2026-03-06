using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace DigitRaverHelperMCP;

/// <summary>
/// WebSocket SERVER that listens for a single Unity Bridge connection.
/// Replaces BridgeWebSocketClient — same public API for BridgeToolRegistry.
/// Uses TcpListener + manual WebSocket upgrade (no HttpListener/Kestrel needed).
/// </summary>
public class BridgeWebSocketServer : IDisposable
{
    private readonly string _bind;
    private readonly int _port;
    private readonly int _timeoutMs;
    private readonly ILogger<BridgeWebSocketServer> _logger;

    private TcpListener? _listener;
    private TcpListener? _relayListener;
    private WebSocket? _ws;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private Task? _acceptTask;
    private Task? _keepaliveTask;
    private Task? _relayAcceptTask;
    private readonly ConcurrentDictionary<Guid, RelayClientConnection> _relayClients = new();

    public const int RelayPortOffset = 1; // Relay port = main port + 1

    private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageEnvelope>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Event buffering
    private readonly ConcurrentQueue<MessageEnvelope> _eventBuffer = new();
    private readonly HashSet<string> _activeSubscriptions = new();
    private readonly object _subscriptionLock = new();
    private const int MaxEventBufferSize = 200;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        Converters = { new StringEnumConverter() }
    };

    private const int ReceiveBufferSize = 524288;
    private const int KeepaliveIntervalSeconds = 15;
    private const int KeepaliveTimeoutSeconds = 30;

    private DateTime _connectedAt;
    private int _reconnectCount;
    private string? _lastError;
    private bool _disposed;

    private DateTime? _lastKeepaliveAt;
    private DateTime? _lastKeepaliveResponseAt;
    internal long _lastCallDurationMs;

    // WebSocket handshake constants
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public event Func<Task>? OnReconnected;

    // Delegate for handling relay tool calls (set by Program.cs after tool registry is ready)
    public Func<string, Dictionary<string, object?>?, Task<(bool success, string result)>>? RelayToolDispatcher { get; set; }

    // Delegate for getting tool list for relay clients
    public Func<List<object>>? RelayToolListProvider { get; set; }

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public bool HasRelayClients => !_relayClients.IsEmpty;
    public bool KeepAliveAfterHostShutdown { get; set; }
    public string Bind => _bind;
    public int Port => _port;
    public int ReconnectCount => _reconnectCount;
    public string? LastError => _lastError;
    public TimeSpan Uptime => IsConnected ? DateTime.UtcNow - _connectedAt : TimeSpan.Zero;
    public DateTime? LastKeepaliveAt => _lastKeepaliveAt;
    public DateTime? LastKeepaliveResponseAt => _lastKeepaliveResponseAt;
    public long LastCallDurationMs => _lastCallDurationMs;

    public BridgeWebSocketServer(string bind, int port, int timeoutMs, ILogger<BridgeWebSocketServer> logger)
    {
        _bind = bind;
        _port = port;
        _timeoutMs = timeoutMs;
        _logger = logger;
    }

    /// <summary>
    /// Start listening for WebSocket connections from Unity and relay clients.
    /// Returns immediately — accepts connections in the background.
    /// </summary>
    public Task StartListeningAsync()
    {
        // Use dual-stack sockets to accept both IPv4 and IPv6 connections.
        // On Windows, "localhost" resolves to ::1 (IPv6) first, so binding only
        // to 0.0.0.0 (IPv4) causes Unity's ClientWebSocket to hang at SYN_SENT.
        var useDualStack = _bind == "0.0.0.0" || _bind == "::";

        // Main listener for Unity Bridge connections
        if (useDualStack)
        {
            _listener = new TcpListener(IPAddress.IPv6Any, _port);
            _listener.Server.DualMode = true;
        }
        else
        {
            _listener = new TcpListener(IPAddress.Parse(_bind), _port);
        }
        _listener.Start();
        _logger.LogInformation("Listening for Unity Bridge on {Bind}:{Port} (dual-stack: {DualStack})", _bind, _port, useDualStack);

        // Relay listener for secondary MCP instances
        var relayPort = _port + RelayPortOffset;
        if (useDualStack)
        {
            _relayListener = new TcpListener(IPAddress.IPv6Any, relayPort);
            _relayListener.Server.DualMode = true;
        }
        else
        {
            _relayListener = new TcpListener(IPAddress.Parse(_bind), relayPort);
        }
        _relayListener.Start();
        _logger.LogInformation("Listening for relay clients on {Bind}:{RelayPort} (dual-stack: {DualStack})", _bind, relayPort, useDualStack);

        _acceptTask = Task.Run(AcceptLoopAsync);
        _relayAcceptTask = Task.Run(RelayAcceptLoopAsync);
        return Task.CompletedTask;
    }

    public int RelayPort => _port + RelayPortOffset;

    /// <summary>
    /// Wait for the first Unity connection with a timeout.
    /// Returns true if Unity connected, false if timed out.
    /// </summary>
    public async Task<bool> WaitForConnectionAsync(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (IsConnected) return true;
            await Task.Delay(100);
        }
        return IsConnected;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_disposed && _listener != null)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync();
                _logger.LogInformation("TCP connection accepted from {Remote}", tcp.Client.RemoteEndPoint);

                // Enable TCP keepalive — OS-level safety net for dead connections
                tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
                tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
                tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);

                // If we already have a connection, close it (only 1 Unity instance at a time)
                if (_ws != null)
                {
                    _logger.LogInformation("Replacing existing Unity connection");
                    _receiveCts?.Cancel();
                    try { await (_receiveTask ?? Task.CompletedTask); } catch { }
                    try { await (_keepaliveTask ?? Task.CompletedTask); } catch { }
                    try { _ws.Dispose(); } catch { }
                    _ws = null;
                }

                var stream = tcp.GetStream();
                using var handshakeCts = new CancellationTokenSource(10_000);
                var ws = await PerformWebSocketHandshakeAsync(stream, handshakeCts.Token);
                if (ws == null)
                {
                    _logger.LogWarning("WebSocket handshake failed");
                    tcp.Close();
                    continue;
                }

                _ws = ws;
                _connectedAt = DateTime.UtcNow;
                _lastError = null;
                _lastKeepaliveAt = null;
                _lastKeepaliveResponseAt = null;

                _logger.LogInformation("State: connected — Unity Bridge connected");

                _receiveCts = new CancellationTokenSource();
                _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
                _keepaliveTask = Task.Run(() => KeepaliveLoopAsync(_receiveCts.Token));

                await ResubscribeAllAsync();

                // Notify listeners that Unity reconnected (e.g., to reload tools)
                // Fire-and-forget so we don't block the accept loop
                if (OnReconnected != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await OnReconnected.Invoke(); }
                        catch (Exception ex) { _logger.LogWarning("OnReconnected handler failed: {Error}", ex.Message); }
                    });
                }

                // Wait for the receive loop to finish (Unity disconnected)
                await _receiveTask;

                // Belt-and-suspenders: fail any pending requests that survived the receive loop
                FailAllPending("Unity disconnected");

                _logger.LogInformation("State: disconnected — Unity Bridge disconnected, waiting for reconnection...");
                _reconnectCount++;
            }
            catch (ObjectDisposedException)
            {
                break; // Listener was stopped
            }
            catch (SocketException) when (_disposed)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Accept error: {Error}", ex.Message);
                tcp?.Close();
                await Task.Delay(1000); // Brief pause before accepting again
            }
        }
    }

    /// <summary>
    /// Accept loop for relay clients (secondary MCP instances).
    /// </summary>
    private async Task RelayAcceptLoopAsync()
    {
        while (!_disposed && _relayListener != null)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = await _relayListener.AcceptTcpClientAsync();
                var clientId = Guid.NewGuid();
                _logger.LogInformation("Relay client connected from {Remote} (id={ClientId})",
                    tcp.Client.RemoteEndPoint, clientId);

                var stream = tcp.GetStream();
                var ws = await PerformWebSocketHandshakeAsync(stream);
                if (ws == null)
                {
                    _logger.LogWarning("Relay client WebSocket handshake failed");
                    tcp.Close();
                    continue;
                }

                // Create and track the relay client connection
                var relayClient = new RelayClientConnection(clientId, ws, tcp, this, _logger);
                _relayClients[clientId] = relayClient;

                // Start handling the relay client in the background
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await relayClient.RunAsync();
                    }
                    finally
                    {
                        _relayClients.TryRemove(clientId, out _);
                        relayClient.Dispose();
                        _logger.LogInformation("Relay client disconnected (id={ClientId})", clientId);
                    }
                });
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (_disposed)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Relay accept error: {Error}", ex.Message);
                tcp?.Close();
                await Task.Delay(1000);
            }
        }
    }

    private async Task<WebSocket?> PerformWebSocketHandshakeAsync(NetworkStream stream, CancellationToken ct = default)
    {
        try
        {
            // Read HTTP request headers
            var headerBuilder = new StringBuilder();
            var buffer = new byte[4096];
            var headerComplete = false;

            while (!headerComplete)
            {
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (bytesRead == 0) return null;

                headerBuilder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                if (headerBuilder.ToString().Contains("\r\n\r\n"))
                    headerComplete = true;
            }

            var request = headerBuilder.ToString();

            // Extract Sec-WebSocket-Key
            string? wsKey = null;
            foreach (var line in request.Split("\r\n"))
            {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                {
                    wsKey = line.Substring("Sec-WebSocket-Key:".Length).Trim();
                    break;
                }
            }

            if (wsKey == null)
            {
                _logger.LogWarning("No Sec-WebSocket-Key in request");
                _logger.LogWarning("Full request:\n{Request}", request);
                return null;
            }

            // Compute accept key
            var concatenated = wsKey + WsGuid;
            var hashBytes = SHA1.HashData(Encoding.UTF8.GetBytes(concatenated));
            var acceptKey = Convert.ToBase64String(hashBytes);

            _logger.LogDebug("WS handshake: key={Key} accept={Accept}", wsKey, acceptKey);

            // Send HTTP 101 response
            var response = "HTTP/1.1 101 Switching Protocols\r\n" +
                           "Upgrade: websocket\r\n" +
                           "Connection: Upgrade\r\n" +
                           $"Sec-WebSocket-Accept: {acceptKey}\r\n" +
                           "\r\n";

            var responseBytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(responseBytes, 0, responseBytes.Length, ct);
            await stream.FlushAsync(ct);

            // Create WebSocket from the upgraded stream
            return WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null,
                keepAliveInterval: TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("WebSocket handshake timed out — client connected TCP but didn't send upgrade headers");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("WebSocket handshake error: {Error}", ex.Message);
            return null;
        }
    }

    public async Task<MessageEnvelope> SendCommandAsync(string domain, string action, JObject? payload = null, int? timeoutMs = null)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Unity Bridge not connected");

        var envelope = new MessageEnvelope
        {
            Id = Guid.NewGuid().ToString(),
            Type = MessageType.command,
            Domain = domain,
            Action = action,
            Payload = payload
        };

        var tcs = new TaskCompletionSource<MessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[envelope.Id] = tcs;

        try
        {
            var json = JsonConvert.SerializeObject(envelope, JsonSettings);
            var buffer = Encoding.UTF8.GetBytes(json);

            var effectiveTimeout = timeoutMs ?? _timeoutMs;
            using var cts = new CancellationTokenSource(effectiveTimeout);
            cts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);

            await _sendLock.WaitAsync(cts.Token);
            try
            {
                await _ws!.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cts.Token);
            }
            finally
            {
                _sendLock.Release();
            }

            _logger.LogDebug("Sent: {Domain}.{Action} (id={Id})", domain, action, envelope.Id);

            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Bridge command {domain}.{action} timed out after {(timeoutMs ?? _timeoutMs)}ms");
        }
        finally
        {
            _pending.TryRemove(envelope.Id, out _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];

        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                var result = await ReceiveFullMessageAsync(buffer, ct);
                if (result.messageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Unity Bridge closed connection");
                    break;
                }

                var json = Encoding.UTF8.GetString(buffer, 0, result.count);
                var envelope = JsonConvert.DeserializeObject<MessageEnvelope>(json, JsonSettings);

                if (envelope == null) continue;

                _logger.LogDebug("Received: {Type} {Domain}.{Action} (id={Id})", envelope.Type, envelope.Domain, envelope.Action, envelope.Id);

                switch (envelope.Type)
                {
                    case MessageType.result:
                    case MessageType.error:
                        if (_pending.TryRemove(envelope.Id, out var tcs))
                        {
                            tcs.TrySetResult(envelope);
                        }
                        break;

                    case MessageType.@event:
                        EnqueueEvent(envelope);
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                _lastError = ex.Message;
                _logger.LogWarning("WebSocket receive error: {Error}", ex.Message);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in receive loop");
            }
        }

        FailAllPending("Connection lost");

        _logger.LogDebug("Receive loop ended");
    }

    private async Task<(WebSocketMessageType messageType, int count)> ReceiveFullMessageAsync(byte[] buffer, CancellationToken ct)
    {
        int totalBytes = 0;

        while (true)
        {
            var segment = new ArraySegment<byte>(buffer, totalBytes, buffer.Length - totalBytes);
            var result = await _ws!.ReceiveAsync(segment, ct);

            totalBytes += result.Count;

            if (result.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, 0);

            if (result.EndOfMessage)
                return (result.MessageType, totalBytes);

            if (totalBytes >= buffer.Length)
            {
                var newBuffer = new byte[buffer.Length * 2];
                Buffer.BlockCopy(buffer, 0, newBuffer, 0, totalBytes);
                buffer = newBuffer;
            }
        }
    }

    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(KeepaliveIntervalSeconds), ct);

                if (!IsConnected) break;

                _lastKeepaliveAt = DateTime.UtcNow;
                var sw = Stopwatch.StartNew();

                try
                {
                    await SendCommandAsync("bridge", "ping", timeoutMs: KeepaliveTimeoutSeconds * 1000);
                    sw.Stop();
                    _lastKeepaliveResponseAt = DateTime.UtcNow;
                    _logger.LogDebug("Keepalive OK ({Elapsed}ms)", sw.ElapsedMilliseconds);
                }
                catch (TimeoutException)
                {
                    sw.Stop();
                    _logger.LogWarning("Keepalive timeout after {Elapsed}ms — closing connection", sw.ElapsedMilliseconds);
                    await _sendLock.WaitAsync();
                    try
                    {
                        try { await _ws!.CloseAsync(WebSocketCloseStatus.NormalClosure, "Keepalive timeout", CancellationToken.None); }
                        catch { }
                    }
                    finally { _sendLock.Release(); }
                    break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Keepalive failed: {Error}", ex.Message);
            }
        }
    }

    private void FailAllPending(string reason)
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetException(new WebSocketException(reason));
        }
    }

    public async Task<MessageEnvelope> SubscribeAsync(string domain, string action)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Unity Bridge not connected");

        var key = $"{domain}.{action}";
        var envelope = new MessageEnvelope
        {
            Id = Guid.NewGuid().ToString(),
            Type = MessageType.subscribe,
            Domain = domain,
            Action = action,
            Payload = null
        };

        var tcs = new TaskCompletionSource<MessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[envelope.Id] = tcs;

        try
        {
            var json = JsonConvert.SerializeObject(envelope, JsonSettings);
            var buffer = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync();
            try
            {
                await _ws!.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }

            _logger.LogInformation("Subscribing to {Key}", key);

            using var cts = new CancellationTokenSource(_timeoutMs);
            cts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);

            var response = await tcs.Task;

            lock (_subscriptionLock)
            {
                _activeSubscriptions.Add(key);
            }

            _logger.LogInformation("Subscribed to {Key}", key);
            return response;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Subscribe to {key} timed out after {_timeoutMs}ms");
        }
        finally
        {
            _pending.TryRemove(envelope.Id, out _);
        }
    }

    public async Task<MessageEnvelope> UnsubscribeAsync(string domain, string action)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Unity Bridge not connected");

        var key = $"{domain}.{action}";
        var envelope = new MessageEnvelope
        {
            Id = Guid.NewGuid().ToString(),
            Type = MessageType.unsubscribe,
            Domain = domain,
            Action = action,
            Payload = null
        };

        var tcs = new TaskCompletionSource<MessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[envelope.Id] = tcs;

        try
        {
            var json = JsonConvert.SerializeObject(envelope, JsonSettings);
            var buffer = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync();
            try
            {
                await _ws!.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }

            _logger.LogInformation("Unsubscribing from {Key}", key);

            using var cts = new CancellationTokenSource(_timeoutMs);
            cts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);

            var response = await tcs.Task;

            lock (_subscriptionLock)
            {
                _activeSubscriptions.Remove(key);
            }

            _logger.LogInformation("Unsubscribed from {Key}", key);
            return response;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Unsubscribe from {key} timed out after {_timeoutMs}ms");
        }
        finally
        {
            _pending.TryRemove(envelope.Id, out _);
        }
    }

    public List<MessageEnvelope> DrainEvents(int maxEvents = 50)
    {
        var result = new List<MessageEnvelope>();
        while (result.Count < maxEvents && _eventBuffer.TryDequeue(out var evt))
        {
            result.Add(evt);
        }
        return result;
    }

    public List<MessageEnvelope> DrainEventsFiltered(string? domain, string? action, int maxEvents = 50)
    {
        var matched = new List<MessageEnvelope>();
        var requeue = new List<MessageEnvelope>();

        while (_eventBuffer.TryDequeue(out var evt))
        {
            bool domainMatch = string.IsNullOrEmpty(domain) || string.Equals(evt.Domain, domain, StringComparison.OrdinalIgnoreCase);
            bool actionMatch = string.IsNullOrEmpty(action) || string.Equals(evt.Action, action, StringComparison.OrdinalIgnoreCase);

            if (domainMatch && actionMatch && matched.Count < maxEvents)
            {
                matched.Add(evt);
            }
            else
            {
                requeue.Add(evt);
            }
        }

        foreach (var evt in requeue)
        {
            _eventBuffer.Enqueue(evt);
        }

        return matched;
    }

    public List<string> GetActiveSubscriptions()
    {
        lock (_subscriptionLock)
        {
            return new List<string>(_activeSubscriptions);
        }
    }

    private void EnqueueEvent(MessageEnvelope envelope)
    {
        _eventBuffer.Enqueue(envelope);
        _logger.LogDebug("Event buffered: {Domain}.{Action} (buffer size: {Size})", envelope.Domain, envelope.Action, _eventBuffer.Count);

        while (_eventBuffer.Count > MaxEventBufferSize)
        {
            if (_eventBuffer.TryDequeue(out _))
            {
                _logger.LogDebug("Event buffer overflow — dropped oldest event");
            }
        }
    }

    private async Task ResubscribeAllAsync()
    {
        List<string> subscriptions;
        lock (_subscriptionLock)
        {
            subscriptions = new List<string>(_activeSubscriptions);
        }

        if (subscriptions.Count == 0) return;

        _logger.LogInformation("Re-registering {Count} event subscriptions after Unity reconnect", subscriptions.Count);

        var failed = new List<string>();
        foreach (var key in subscriptions)
        {
            var parts = key.Split('.', 2);
            if (parts.Length != 2) continue;

            try
            {
                var envelope = new MessageEnvelope
                {
                    Id = Guid.NewGuid().ToString(),
                    Type = MessageType.subscribe,
                    Domain = parts[0],
                    Action = parts[1],
                    Payload = null
                };

                var tcs = new TaskCompletionSource<MessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[envelope.Id] = tcs;

                var json = JsonConvert.SerializeObject(envelope, JsonSettings);
                var buffer = Encoding.UTF8.GetBytes(json);

                await _sendLock.WaitAsync();
                try
                {
                    await _ws!.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                finally
                {
                    _sendLock.Release();
                }

                using var cts = new CancellationTokenSource(5000);
                cts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);

                await tcs.Task;
                _logger.LogDebug("Re-subscribed to {Key}", key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to re-subscribe to {Key}: {Error}", key, ex.Message);
                failed.Add(key);
            }
        }

        if (failed.Count > 0)
        {
            lock (_subscriptionLock)
            {
                foreach (var key in failed)
                {
                    _activeSubscriptions.Remove(key);
                }
            }
            _logger.LogWarning("Removed {Count} failed subscriptions", failed.Count);
        }
    }

    public void Dispose()
    {
        if (KeepAliveAfterHostShutdown)
        {
            KeepAliveAfterHostShutdown = false; // Only skip once
            return;
        }
        ForceDispose();
    }

    public void ForceDispose()
    {
        if (_disposed) return;
        _disposed = true;

        _receiveCts?.Cancel();
        _receiveCts?.Dispose();

        try { _listener?.Stop(); } catch { }
        try { _relayListener?.Stop(); } catch { }

        // Close all relay clients
        foreach (var client in _relayClients.Values)
        {
            try { client.Dispose(); } catch { }
        }
        _relayClients.Clear();

        try { _ws?.CloseAsync(WebSocketCloseStatus.NormalClosure, "MCP server shutting down", CancellationToken.None).Wait(2000); }
        catch { }

        _ws?.Dispose();
        _sendLock.Dispose();
    }
}

/// <summary>
/// Represents a connected relay client (secondary MCP instance).
/// Handles forwarding MCP JSON-RPC messages between the relay client and the primary server's tool infrastructure.
/// </summary>
internal class RelayClientConnection : IDisposable
{
    private readonly Guid _id;
    private readonly WebSocket _ws;
    private readonly TcpClient _tcp;
    private readonly BridgeWebSocketServer _server;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public RelayClientConnection(Guid id, WebSocket ws, TcpClient tcp, BridgeWebSocketServer server, ILogger logger)
    {
        _id = id;
        _ws = ws;
        _tcp = tcp;
        _server = server;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var buffer = new byte[65536];

        while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            try
            {
                var result = await ReceiveFullMessageAsync(buffer, _cts.Token);
                if (result.messageType == WebSocketMessageType.Close)
                {
                    _logger.LogDebug("Relay client {Id} closed connection", _id);
                    break;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, result.count);
                _logger.LogDebug("Relay client {Id} sent: {Chars} chars", _id, message.Length);

                // Parse the MCP JSON-RPC message
                var jsonRpc = JObject.Parse(message);
                var method = jsonRpc["method"]?.ToString();
                var id = jsonRpc["id"];
                var paramsObj = jsonRpc["params"] as JObject;

                string responseJson;

                if (method == "tools/list")
                {
                    // Return the tool list
                    // Note: This requires access to the tool registry. For now, we'll forward to Unity.
                    responseJson = await HandleToolsListAsync(id);
                }
                else if (method == "tools/call")
                {
                    // Forward tool call to Unity
                    var toolName = paramsObj?["name"]?.ToString() ?? "";
                    var arguments = paramsObj?["arguments"] as JObject;
                    responseJson = await HandleToolCallAsync(id, toolName, arguments);
                }
                else
                {
                    // Unknown method - return error
                    responseJson = CreateErrorResponse(id, -32601, $"Method not found: {method}");
                }

                // Send response back to relay client
                var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                await _ws.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Text, true, _cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                _logger.LogDebug("Relay client {Id} WebSocket error: {Error}", _id, ex.Message);
                break;
            }
            catch (JsonReaderException ex)
            {
                _logger.LogWarning("Relay client {Id} invalid JSON: {Error}", _id, ex.Message);
                // Continue - don't break on bad messages
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Relay client {Id} unexpected error", _id);
            }
        }
    }

    private Task<string> HandleToolsListAsync(JToken? id)
    {
        // Use the tool list provider if available
        if (_server.RelayToolListProvider != null)
        {
            try
            {
                var tools = _server.RelayToolListProvider();
                var response = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = new JObject
                    {
                        ["tools"] = JArray.FromObject(tools)
                    }
                };
                return Task.FromResult(response.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to get tool list: {Error}", ex.Message);
            }
        }

        // Fallback to empty list
        var fallbackResponse = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = new JObject
            {
                ["tools"] = new JArray()
            }
        };
        return Task.FromResult(fallbackResponse.ToString(Formatting.None));
    }

    private async Task<string> HandleToolCallAsync(JToken? id, string toolName, JObject? arguments)
    {
        try
        {
            // Use the relay tool dispatcher if available
            if (_server.RelayToolDispatcher != null)
            {
                var args = arguments?.ToObject<Dictionary<string, object?>>();
                var (success, resultText) = await _server.RelayToolDispatcher(toolName, args);

                if (!success)
                {
                    return CreateErrorResponse(id, -32000, resultText);
                }

                var response = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = new JObject
                    {
                        ["content"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "text",
                                ["text"] = resultText
                            }
                        }
                    }
                };
                return response.ToString(Formatting.None);
            }

            // Fallback: Forward directly to Unity via WebSocket
            if (!_server.IsConnected)
            {
                return CreateErrorResponse(id, -32000, "Unity Bridge not connected");
            }

            // Parse domain and action from tool name (format: "domain_action" or just "action")
            var parts = toolName.Split('_', 2);
            var domain = parts.Length > 1 ? parts[0] : "bridge";
            var action = parts.Length > 1 ? parts[1] : parts[0];

            var result = await _server.SendCommandAsync(domain, action, arguments, 30000);

            if (result.Type == MessageType.error)
            {
                return CreateErrorResponse(id, -32000, result.Payload?["error"]?.ToString() ?? "Unknown error");
            }

            var fallbackResponse = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JObject
                {
                    ["content"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "text",
                            ["text"] = result.Payload?.ToString(Formatting.Indented) ?? "{}"
                        }
                    }
                }
            };

            return fallbackResponse.ToString(Formatting.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Relay tool call failed: {Error}", ex.Message);
            return CreateErrorResponse(id, -32000, ex.Message);
        }
    }

    private string CreateErrorResponse(JToken? id, int code, string message)
    {
        var response = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
        return response.ToString(Formatting.None);
    }

    private async Task<(WebSocketMessageType messageType, int count)> ReceiveFullMessageAsync(byte[] buffer, CancellationToken ct)
    {
        int totalBytes = 0;

        while (true)
        {
            var segment = new ArraySegment<byte>(buffer, totalBytes, buffer.Length - totalBytes);
            var result = await _ws.ReceiveAsync(segment, ct);

            totalBytes += result.Count;

            if (result.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, 0);

            if (result.EndOfMessage)
                return (result.MessageType, totalBytes);

            if (totalBytes >= buffer.Length)
            {
                var newBuffer = new byte[buffer.Length * 2];
                Buffer.BlockCopy(buffer, 0, newBuffer, 0, totalBytes);
                buffer = newBuffer;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();

        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Relay closing", CancellationToken.None)
                   .Wait(1000);
            }
        }
        catch { }

        _ws.Dispose();
        _tcp.Close();
    }
}
