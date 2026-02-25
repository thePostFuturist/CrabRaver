using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace DigitRaverHelperMCP;

/// <summary>
/// Persistent WebSocket client that connects to the Bridge server.
/// Maintains a single connection and correlates request/response by message ID.
/// </summary>
public class BridgeWebSocketClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;
    private readonly ILogger<BridgeWebSocketClient> _logger;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private Task? _keepaliveTask;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageEnvelope>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Event buffering for Phase 2.2 polling
    private readonly ConcurrentQueue<MessageEnvelope> _eventBuffer = new();
    private readonly HashSet<string> _activeSubscriptions = new();
    private readonly object _subscriptionLock = new();
    private const int MaxEventBufferSize = 200;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        Converters = { new StringEnumConverter() }
    };

    // Receive buffer: 512KB for large screenshot responses
    private const int ReceiveBufferSize = 524288;
    private const int KeepaliveIntervalSeconds = 30;
    private const int KeepaliveTimeoutSeconds = 10;
    private const int MaxReconnectAttempts = 3;

    private DateTime _connectedAt;
    private int _reconnectCount;
    private string? _lastError;
    private bool _disposed;
    private TaskCompletionSource<bool>? _receiveReady;

    // Keepalive health tracking
    private DateTime? _lastKeepaliveAt;
    private DateTime? _lastKeepaliveResponseAt;
    internal long _lastCallDurationMs;

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public string Host => _host;
    public int Port => _port;
    public int ReconnectCount => _reconnectCount;
    public string? LastError => _lastError;
    public TimeSpan Uptime => IsConnected ? DateTime.UtcNow - _connectedAt : TimeSpan.Zero;
    public DateTime? LastKeepaliveAt => _lastKeepaliveAt;
    public DateTime? LastKeepaliveResponseAt => _lastKeepaliveResponseAt;
    public long LastCallDurationMs => _lastCallDurationMs;

    public BridgeWebSocketClient(string host, int port, int timeoutMs, ILogger<BridgeWebSocketClient> logger)
    {
        _host = host;
        _port = port;
        _timeoutMs = timeoutMs;
        _logger = logger;
    }

    public async Task ConnectAsync()
    {
        var delays = new[] { 2000, 4000, 8000 };

        for (int attempt = 0; attempt <= MaxReconnectAttempts; attempt++)
        {
            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                _ws.Options.SetBuffer(ReceiveBufferSize, 65536);

                var uri = new Uri($"ws://{_host}:{_port}");
                _logger.LogInformation("State: connecting → {Uri} (attempt {Attempt})", uri, attempt + 1);

                using var connectCts = new CancellationTokenSource(_timeoutMs);
                await _ws.ConnectAsync(uri, connectCts.Token);

                _connectedAt = DateTime.UtcNow;
                _lastError = null;
                _lastKeepaliveAt = null;
                _lastKeepaliveResponseAt = null;
                _logger.LogInformation("State: connected → {Uri}", uri);

                // Start receive loop
                _receiveCts = new CancellationTokenSource();
                _receiveReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));

                // Wait for receive loop to be ready before returning
                await _receiveReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

                // Start keepalive
                _keepaliveTask = Task.Run(() => KeepaliveLoopAsync(_receiveCts.Token));

                // Re-register event subscriptions (critical after reconnect — new connection has zero subscriptions)
                await ResubscribeAllAsync();

                return;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogWarning("Connection attempt {Attempt} failed: {Error}", attempt + 1, ex.Message);

                if (attempt < MaxReconnectAttempts)
                {
                    await Task.Delay(delays[Math.Min(attempt, delays.Length - 1)]);
                }
            }
        }

        _logger.LogError("Failed to connect after {MaxAttempts} attempts", MaxReconnectAttempts + 1);
    }

    public async Task<MessageEnvelope> SendCommandAsync(string domain, string action, JObject? payload = null, int? timeoutMs = null)
    {
        if (!IsConnected)
        {
            await ReconnectAsync();
            if (!IsConnected)
                throw new InvalidOperationException($"Not connected to Bridge at ws://{_host}:{_port}");
        }

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

            await _sendLock.WaitAsync();
            try
            {
                await _ws!.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }

            _logger.LogDebug("Sent: {Domain}.{Action} (id={Id})", domain, action, envelope.Id);

            var effectiveTimeout = timeoutMs ?? _timeoutMs;
            using var cts = new CancellationTokenSource(effectiveTimeout);
            cts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);

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
            _receiveReady?.TrySetResult(true);

            try
            {
                var result = await ReceiveFullMessageAsync(buffer, ct);
                if (result.messageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Bridge server closed connection");
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

        // If connection dropped unexpectedly, fail all pending requests
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetException(new WebSocketException("Connection lost"));
            }
        }

        // Auto-reconnect if exit was unexpected (not cancellation, not disposal)
        if (!ct.IsCancellationRequested && !_disposed)
        {
            _logger.LogInformation("State: disconnected — connection dropped, scheduling reconnect...");
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                if (!_disposed)
                {
                    try { await ReconnectAsync(); }
                    catch (Exception ex) { _logger.LogWarning("Auto-reconnect failed: {Error}", ex.Message); }
                }
            });
        }

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

            // Message larger than buffer — expand
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

                if (!IsConnected) continue;

                _lastKeepaliveAt = DateTime.UtcNow;
                var sw = Stopwatch.StartNew();

                try
                {
                    await SendCommandAsync("auth", "get_status", timeoutMs: KeepaliveTimeoutSeconds * 1000);
                    sw.Stop();
                    _lastKeepaliveResponseAt = DateTime.UtcNow;
                    _logger.LogDebug("Keepalive OK ({Elapsed}ms)", sw.ElapsedMilliseconds);
                }
                catch (TimeoutException)
                {
                    sw.Stop();
                    _logger.LogWarning("Keepalive timeout after {Elapsed}ms — triggering reconnect", sw.ElapsedMilliseconds);

                    if (!ct.IsCancellationRequested && !_disposed)
                    {
                        try { await ReconnectAsync(); }
                        catch (Exception rex) { _logger.LogWarning("Keepalive-triggered reconnect failed: {Error}", rex.Message); }
                    }
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

    private async Task ReconnectAsync()
    {
        _logger.LogInformation("State: disconnected → reconnecting...");
        _receiveCts?.Cancel();
        if (_receiveTask != null)
        {
            try { await Task.WhenAny(_receiveTask, Task.Delay(3000)); }
            catch { /* old loop may fault — safe to ignore */ }
        }
        _reconnectCount++;
        await ConnectAsync();
    }

    /// <summary>
    /// Subscribe to a Bridge event type. Sends a WebSocket subscribe message and tracks the subscription
    /// for automatic re-registration on reconnect.
    /// </summary>
    public async Task<MessageEnvelope> SubscribeAsync(string domain, string action)
    {
        if (!IsConnected)
        {
            await ReconnectAsync();
            if (!IsConnected)
                throw new InvalidOperationException($"Not connected to Bridge at ws://{_host}:{_port}");
        }

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

            // Track subscription on success
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

    /// <summary>
    /// Unsubscribe from a Bridge event type. Sends a WebSocket unsubscribe message and removes tracking.
    /// </summary>
    public async Task<MessageEnvelope> UnsubscribeAsync(string domain, string action)
    {
        if (!IsConnected)
        {
            await ReconnectAsync();
            if (!IsConnected)
                throw new InvalidOperationException($"Not connected to Bridge at ws://{_host}:{_port}");
        }

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

    /// <summary>
    /// Drain up to maxEvents from the event buffer. Returns all drained events.
    /// </summary>
    public List<MessageEnvelope> DrainEvents(int maxEvents = 50)
    {
        var result = new List<MessageEnvelope>();
        while (result.Count < maxEvents && _eventBuffer.TryDequeue(out var evt))
        {
            result.Add(evt);
        }
        return result;
    }

    /// <summary>
    /// Drain events matching a specific domain and/or action. Non-matching events are re-enqueued.
    /// </summary>
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

        // Re-enqueue non-matching events
        foreach (var evt in requeue)
        {
            _eventBuffer.Enqueue(evt);
        }

        return matched;
    }

    /// <summary>
    /// Returns the set of currently active subscriptions (for diagnostics).
    /// </summary>
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

        // Drop oldest events if buffer exceeds cap
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

        _logger.LogInformation("Re-registering {Count} event subscriptions after reconnect", subscriptions.Count);

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

        // Remove failed subscriptions
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
        if (_disposed) return;
        _disposed = true;

        _receiveCts?.Cancel();
        _receiveCts?.Dispose();

        try { _ws?.CloseAsync(WebSocketCloseStatus.NormalClosure, "MCP server shutting down", CancellationToken.None).Wait(2000); }
        catch { /* best effort */ }

        _ws?.Dispose();
        _sendLock.Dispose();
    }
}

// Mirror of Bridge protocol types — matches MessageEnvelope.cs wire format exactly
[JsonConverter(typeof(StringEnumConverter))]
public enum MessageType
{
    command,
    subscribe,
    unsubscribe,
    @event,
    result,
    error
}

[Serializable]
public class MessageEnvelope
{
    [JsonProperty("id")]
    public string Id = "";

    [JsonProperty("type")]
    public MessageType Type;

    [JsonProperty("domain")]
    public string Domain = "";

    [JsonProperty("action")]
    public string Action = "";

    [JsonProperty("payload", NullValueHandling = NullValueHandling.Ignore)]
    public JObject? Payload;

    [JsonProperty("timestamp", NullValueHandling = NullValueHandling.Ignore)]
    public string? Timestamp;
}
