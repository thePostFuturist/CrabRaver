using System.Net;
using System.Net.Sockets;
using System.Reflection;
using DigitRaverHelperMCP;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// Parse CLI arguments
var bind = "0.0.0.0";
var port = 18800;
var timeout = 10000;
var verbose = false;
var noBeacon = false;
var forceRelay = false;
var forcePrimary = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--bind" when i + 1 < args.Length:
            bind = args[++i];
            break;
        case "--port" when i + 1 < args.Length:
            port = int.Parse(args[++i]);
            break;
        case "--timeout" when i + 1 < args.Length:
            timeout = int.Parse(args[++i]);
            break;
        case "--verbose":
            verbose = true;
            break;
        case "--no-beacon":
            noBeacon = true;
            break;
        case "--relay":
            forceRelay = true;
            break;
        case "--primary":
            forcePrimary = true;
            break;
    }
}

var builder = Host.CreateApplicationBuilder(args);

// Configure logging to stderr (MCP servers must not write to stdout)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning);

// Build a temporary logger for startup (before DI is ready)
using var loggerFactory = LoggerFactory.Create(lb =>
{
    lb.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    lb.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning);
});
var startupLogger = loggerFactory.CreateLogger("Startup");

// Check if primary server is already running (unless forced to be primary or relay).
// Must check both IPv4 and IPv6 since the server uses dual-stack sockets.
bool IsPortInUse(int checkPort)
{
    // Check IPv6 (dual-stack) first — this is what the server binds to
    try
    {
        using var listener6 = new TcpListener(IPAddress.IPv6Any, checkPort);
        listener6.Server.DualMode = true;
        listener6.Start();
        listener6.Stop();
        return false;
    }
    catch (SocketException)
    {
        return true;
    }
}

if (!forcePrimary && (forceRelay || IsPortInUse(port)))
{
    // Primary server already running - connect as relay client
    var relayPort = port + 1; // 18801
    startupLogger.LogInformation("Primary server detected on port {Port}, connecting as relay client to port {RelayPort}...", port, relayPort);

    var relayClient = new BridgeRelayClient("127.0.0.1", relayPort, startupLogger);
    try
    {
        await relayClient.RunAsync();
    }
    catch (Exception ex)
    {
        startupLogger.LogError("Relay client failed: {Error}", ex.Message);
        Environment.Exit(1);
    }
    finally
    {
        relayClient.Dispose();
    }
    return; // Exit after relay mode completes
}

startupLogger.LogInformation("MCP server starting — listening on {Bind}:{Port}", bind, port);

// Register the Bridge WebSocket server as singleton
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<BridgeWebSocketServer>>();
    return new BridgeWebSocketServer(bind, port, timeout, logger);
});

// Register the dynamic tool registry as singleton
builder.Services.AddSingleton<BridgeToolRegistry>();

// Register MCP server with dynamic tool handlers (no WithToolsFromAssembly)
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "digitraver-bridge",
            Version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "0.0.0"
        };
    })
    .WithStdioServerTransport()
    .WithListToolsHandler((context, _) =>
    {
        var registry = context.Services!.GetRequiredService<BridgeToolRegistry>();
        return ValueTask.FromResult(registry.GetToolList());
    })
    .WithCallToolHandler(async (context, _) =>
    {
        var registry = context.Services!.GetRequiredService<BridgeToolRegistry>();
        var server = context.Services!.GetRequiredService<BridgeWebSocketServer>();
        var toolName = context.Params?.Name ?? "";
        var arguments = context.Params?.Arguments;
        return await registry.DispatchAsync(toolName, arguments, server);
    });

var app = builder.Build();

// Start WebSocket server (listens for Unity connections)
var listenSw = System.Diagnostics.Stopwatch.StartNew();
var wsServer = app.Services.GetRequiredService<BridgeWebSocketServer>();
await wsServer.StartListeningAsync();
var listenMs = listenSw.ElapsedMilliseconds;

// Start beacon broadcaster (so Unity can discover us)
BridgeBeaconBroadcaster? beacon = null;
if (!noBeacon)
{
    beacon = new BridgeBeaconBroadcaster(port, startupLogger);
    beacon.Start();
}

// Wait for Unity to connect (with timeout)
var waitSw = System.Diagnostics.Stopwatch.StartNew();
var connected = await wsServer.WaitForConnectionAsync(timeout);
var waitMs = waitSw.ElapsedMilliseconds;

// Load tools from Bridge (falls back to local-only if Unity not yet connected)
var toolsSw = System.Diagnostics.Stopwatch.StartNew();
var toolRegistry = app.Services.GetRequiredService<BridgeToolRegistry>();
if (connected)
{
    await toolRegistry.LoadToolsAsync(wsServer);
}
else
{
    startupLogger.LogWarning("Unity not connected after {Timeout}ms — starting with local-only tools. Unity can connect later.", timeout);
}
var toolsMs = toolsSw.ElapsedMilliseconds;

// Wire reconnect → reload tools
wsServer.OnReconnected += async () =>
{
    try
    {
        await toolRegistry.ReloadToolsAsync(wsServer);
    }
    catch (Exception ex)
    {
        startupLogger.LogWarning("Failed to reload tools after reconnect: {Error}", ex.Message);
    }
};

// Wire relay dispatchers so secondary MCP instances can use our tools
wsServer.RelayToolListProvider = () =>
{
    var toolList = toolRegistry.GetToolList();
    return toolList.Tools.Cast<object>().ToList();
};

wsServer.RelayToolDispatcher = async (toolName, arguments) =>
{
    try
    {
        // Convert Dictionary<string, object?> to IDictionary<string, JsonElement>
        // Use Newtonsoft for intermediate serialization — System.Text.Json reflection
        // is disabled in trimmed builds.
        IDictionary<string, System.Text.Json.JsonElement>? jsonArgs = null;
        if (arguments != null)
        {
            jsonArgs = new Dictionary<string, System.Text.Json.JsonElement>();
            foreach (var kvp in arguments)
            {
                var jsonString = Newtonsoft.Json.JsonConvert.SerializeObject(kvp.Value);
                using var doc = System.Text.Json.JsonDocument.Parse(jsonString);
                jsonArgs[kvp.Key] = doc.RootElement.Clone();
            }
        }

        var result = await toolRegistry.DispatchAsync(toolName, jsonArgs, wsServer);
        var content = result.Content?.FirstOrDefault();
        if (content is ModelContextProtocol.Protocol.TextContentBlock textContent)
        {
            return (result.IsError != true, textContent.Text);
        }
        return (result.IsError != true, "Tool executed successfully");
    }
    catch (Exception ex)
    {
        return (false, ex.Message);
    }
};

// Log startup summary with timing breakdown
var appLogger = app.Services.GetRequiredService<ILogger<BridgeWebSocketServer>>();
appLogger.LogInformation(
    "Bridge MCP server ready — bind: {Bind}:{Port}, connected: {Connected}, tools: {ToolCount} (listen: {ListenMs}ms, wait: {WaitMs}ms, tools: {ToolsMs}ms)",
    bind, port, connected, toolRegistry.ToolCount, listenMs, waitMs, toolsMs);

// Set keep-alive flag so that when the host disposes wsServer, it's a no-op.
// This lets the WebSocket server survive stdio closure (daemon mode).
wsServer.KeepAliveAfterHostShutdown = true;

await app.RunAsync(); // stdio closed → host calls Dispose on wsServer (no-op due to flag)

// Daemon mode: WebSocket server still alive, serve Unity + relay clients
if (wsServer.IsConnected || wsServer.HasRelayClients)
{
    startupLogger.LogInformation("stdio closed — entering daemon mode (Unity still connected)");

    var idleSeconds = 0;
    while (true)
    {
        await Task.Delay(1000);
        if (!wsServer.IsConnected && !wsServer.HasRelayClients)
        {
            idleSeconds++;
            if (idleSeconds >= 300) // 5 min idle → exit
            {
                startupLogger.LogInformation("Daemon mode: no connections for 5 minutes — exiting");
                break;
            }
        }
        else
        {
            idleSeconds = 0;
        }
    }
}

wsServer.ForceDispose();
beacon?.Dispose();
