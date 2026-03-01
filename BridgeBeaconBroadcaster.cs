using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace DigitRaverHelperMCP;

/// <summary>
/// Broadcasts UDP beacons so Unity can discover the MCP server.
/// Replaces BridgeDiscovery (which was a listener). Now the MCP server broadcasts.
/// Beacon payload: { "service": "digitraver-mcp", "version": 2, "port": N }
/// </summary>
public class BridgeBeaconBroadcaster : IDisposable
{
    private const string MulticastAddress = "239.255.42.99";
    private const int MulticastPort = 18801;
    private const int IntervalMs = 5000;

    private readonly int _port;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _broadcastTask;

    public BridgeBeaconBroadcaster(int port, ILogger logger)
    {
        _port = port;
        _logger = logger;
    }

    public void Start()
    {
        if (_cts != null) return;

        _cts = new CancellationTokenSource();
        _broadcastTask = Task.Run(() => BroadcastLoopAsync(_cts.Token));
        _logger.LogInformation("Beacon broadcaster started on {Address}:{BeaconPort} (advertising port {Port})",
            MulticastAddress, MulticastPort, _port);
    }

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient();
            udp.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));
            var endpoint = new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort);

            var json = $"{{\"service\":\"digitraver-mcp\",\"version\":2,\"port\":{_port},\"hostname\":\"{Environment.MachineName}\"}}";
            var bytes = Encoding.UTF8.GetBytes(json);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    udp.Send(bytes, bytes.Length, endpoint);
                }
                catch (SocketException ex)
                {
                    _logger.LogDebug("Beacon send failed: {Error}", ex.Message);
                }

                await Task.Delay(IntervalMs, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning("Beacon broadcaster error: {Error}", ex.Message);
        }
        finally
        {
            udp?.Close();
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
