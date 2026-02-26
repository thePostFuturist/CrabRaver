using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace DigitRaverHelperMCP;

/// <summary>
/// Discovers Bridge servers via UDP multicast beacons broadcast by BridgeDiscoveryAdvertiser.
/// Listens on 239.255.42.99:18801 for JSON beacons with service="digitraver-bridge".
/// </summary>
public static class BridgeDiscovery
{
    private const string MulticastAddress = "239.255.42.99";
    private const int MulticastPort = 18801;

    /// <summary>
    /// Listen for a Bridge discovery beacon on the multicast group.
    /// Returns (hostname, port) if a valid beacon is received within the timeout, or null if not found.
    /// </summary>
    public static async Task<(string host, int port)?> DiscoverAsync(int timeoutMs, ILogger logger, CancellationToken ct = default)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient();
            udp.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
            udp.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));

            logger.LogInformation("Listening for Bridge discovery beacons on {Address}:{Port} (timeout: {Timeout}ms)...",
                MulticastAddress, MulticastPort, timeoutMs);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            while (!timeoutCts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await udp.ReceiveAsync(timeoutCts.Token);
                    var json = Encoding.UTF8.GetString(result.Buffer);

                    logger.LogDebug("Received beacon from {Remote}: {Json}", result.RemoteEndPoint, json);

                    var beacon = JObject.Parse(json);
                    var service = beacon["service"]?.ToString();

                    if (service != "digitraver-bridge")
                    {
                        logger.LogDebug("Ignoring beacon with service={Service}", service);
                        continue;
                    }

                    var version = beacon["version"]?.Value<int>() ?? 0;
                    if (version < 1)
                    {
                        logger.LogDebug("Ignoring beacon with unsupported version={Version}", version);
                        continue;
                    }

                    var beaconHostname = beacon["hostname"]?.ToString() ?? "unknown";
                    var host = result.RemoteEndPoint.Address.ToString();
                    var port = beacon["port"]?.Value<int>() ?? 0;

                    if (port <= 0)
                    {
                        logger.LogDebug("Ignoring beacon with invalid port: {Port}", port);
                        continue;
                    }

                    var deviceId = beacon["deviceId"]?.ToString() ?? "unknown";
                    logger.LogInformation("Discovered Bridge: {Host}:{Port} (beacon hostname: {Hostname}, device: {DeviceId})",
                        host, port, beaconHostname, deviceId);

                    return (host, port);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogDebug("Error parsing beacon: {Error}", ex.Message);
                }
            }

            logger.LogInformation("No Bridge discovered within {Timeout}ms", timeoutMs);
            return null;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Discovery cancelled");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Discovery failed: {Error}", ex.Message);
            return null;
        }
        finally
        {
            try { udp?.DropMulticastGroup(IPAddress.Parse(MulticastAddress)); } catch { }
            udp?.Dispose();
        }
    }
}
