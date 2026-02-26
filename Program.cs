using DigitRaverHelperMCP;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// Parse CLI arguments
var host = (string?)null; // null = not explicitly provided
var port = 18800;
var timeout = 10000;
var verbose = false;
var noDiscovery = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host" when i + 1 < args.Length:
            host = args[++i];
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
        case "--no-discovery":
            noDiscovery = true;
            break;
    }
}

var builder = Host.CreateApplicationBuilder(args);

// Configure logging to stderr (MCP servers must not write to stdout)
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning);

// Build a temporary logger for discovery (before DI is ready)
using var loggerFactory = LoggerFactory.Create(lb =>
{
    lb.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    lb.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning);
});
var startupLogger = loggerFactory.CreateLogger("Startup");

// Resolve host via discovery or explicit flag
string connectionMethod;
var discoveryMs = 0L;
var discoverySw = System.Diagnostics.Stopwatch.StartNew();
if (host != null)
{
    // Explicit --host provided
    connectionMethod = "explicit";
    startupLogger.LogInformation("Using explicit host: {Host}:{Port}", host, port);
}
else if (!noDiscovery)
{
    // Try UDP auto-discovery
    startupLogger.LogInformation("No --host specified, attempting UDP discovery...");
    var discovered = await BridgeDiscovery.DiscoverAsync(5000, startupLogger);

    if (discovered != null)
    {
        host = discovered.Value.host;
        port = discovered.Value.port;
        connectionMethod = "discovered";
        startupLogger.LogInformation("Using discovered Bridge: {Host}:{Port}", host, port);
    }
    else
    {
        host = "localhost";
        connectionMethod = "fallback";
        startupLogger.LogInformation("Discovery timeout, falling back to {Host}:{Port}", host, port);
    }
}
else
{
    // Discovery disabled, use default
    host = "localhost";
    connectionMethod = "default";
    startupLogger.LogInformation("Discovery disabled, using default: {Host}:{Port}", host, port);
}
discoveryMs = discoverySw.ElapsedMilliseconds;

// Register the Bridge WebSocket client as singleton
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<BridgeWebSocketClient>>();
    return new BridgeWebSocketClient(host, port, timeout, logger);
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
            Version = "3.0.0"
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
        var client = context.Services!.GetRequiredService<BridgeWebSocketClient>();
        var toolName = context.Params?.Name ?? "";
        var arguments = context.Params?.Arguments;
        return await registry.DispatchAsync(toolName, arguments, client);
    });

var app = builder.Build();

// Connect WebSocket client before handling MCP requests
var connectSw = System.Diagnostics.Stopwatch.StartNew();
var wsClient = app.Services.GetRequiredService<BridgeWebSocketClient>();
await wsClient.ConnectAsync();
var connectMs = connectSw.ElapsedMilliseconds;

// Load tools from Bridge (falls back to local-only if Bridge unavailable)
var toolsSw = System.Diagnostics.Stopwatch.StartNew();
var toolRegistry = app.Services.GetRequiredService<BridgeToolRegistry>();
await toolRegistry.LoadToolsAsync(wsClient);
var toolsMs = toolsSw.ElapsedMilliseconds;

// Log startup summary with timing breakdown
var appLogger = app.Services.GetRequiredService<ILogger<BridgeWebSocketClient>>();
appLogger.LogInformation(
    "Bridge MCP server ready — connection: {Method}, host: {Host}:{Port}, tools: {ToolCount} (discovery: {DiscoveryMs}ms, connect: {ConnectMs}ms, tools: {ToolsMs}ms)",
    connectionMethod, host, port, toolRegistry.ToolCount, discoveryMs, connectMs, toolsMs);

await app.RunAsync();
