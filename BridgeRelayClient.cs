using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DigitRaverHelperMCP;

/// <summary>
/// Relay client that connects to a primary MCP server instance.
/// Forwards MCP JSON-RPC messages between stdio and the primary server's relay port.
/// </summary>
public class BridgeRelayClient : IDisposable
{
    private readonly string _host;
    private readonly int _relayPort;
    private readonly ILogger _logger;
    private readonly ClientWebSocket _ws;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

    public BridgeRelayClient(string host, int relayPort, ILogger logger)
    {
        _host = host;
        _relayPort = relayPort;
        _logger = logger;
        _ws = new ClientWebSocket();
        _cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Run the relay client - connects to primary server and forwards stdio ↔ WebSocket.
    /// This method blocks until stdin is closed or the WebSocket disconnects.
    /// </summary>
    public async Task RunAsync()
    {
        var uri = new Uri($"ws://{_host}:{_relayPort}");
        _logger.LogInformation("Connecting to primary server relay port at {Uri}", uri);

        try
        {
            await _ws.ConnectAsync(uri, _cts.Token);
            _logger.LogInformation("Connected to primary server relay port");

            // Run stdin reader and WebSocket receiver concurrently
            var stdinTask = ReadStdinAndForwardAsync(_cts.Token);
            var wsTask = ReadWebSocketAndWriteStdoutAsync(_cts.Token);

            // Wait for either task to complete (stdin EOF or WebSocket close)
            await Task.WhenAny(stdinTask, wsTask);

            // Cancel the other task
            _cts.Cancel();

            // Wait for both to finish
            try { await Task.WhenAll(stdinTask, wsTask); }
            catch (OperationCanceledException) { }

            _logger.LogInformation("Relay client shutting down");
        }
        catch (WebSocketException ex)
        {
            _logger.LogError("Failed to connect to primary server: {Error}", ex.Message);
            throw;
        }
    }

    private async Task ReadStdinAndForwardAsync(CancellationToken ct)
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (line == null)
            {
                _logger.LogDebug("Stdin EOF - stopping relay");
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            _logger.LogDebug("Relaying from stdin: {Chars} chars", line.Length);

            var buffer = Encoding.UTF8.GetBytes(line);
            await _ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, ct);
        }
    }

    private async Task ReadWebSocketAndWriteStdoutAsync(CancellationToken ct)
    {
        var buffer = new byte[65536];
        using var stdout = Console.OpenStandardOutput();
        using var writer = new StreamWriter(stdout, Encoding.UTF8) { AutoFlush = true };

        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            try
            {
                var result = await ReceiveFullMessageAsync(buffer, ct);
                if (result.messageType == WebSocketMessageType.Close)
                {
                    _logger.LogDebug("WebSocket closed by primary server");
                    break;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, result.count);
                _logger.LogDebug("Received from primary: {Chars} chars", message.Length);

                await writer.WriteLineAsync(message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                _logger.LogWarning("WebSocket error: {Error}", ex.Message);
                break;
            }
        }
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

            // Expand buffer if needed
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
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Relay client closing", CancellationToken.None)
                   .Wait(2000);
            }
        }
        catch { }

        _ws.Dispose();
    }
}
