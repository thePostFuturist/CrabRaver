using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DigitRaverHelperMCP;

/// <summary>
/// Dynamic tool registry that discovers tools from the Bridge server at startup
/// and dispatches MCP tool calls to the appropriate Bridge domain/action.
/// </summary>
public class BridgeToolRegistry
{
    private readonly ILogger<BridgeToolRegistry> _logger;
    private readonly Dictionary<string, ToolRegistryEntry> _tools = new();
    private bool _loaded;

    public BridgeToolRegistry(ILogger<BridgeToolRegistry> logger)
    {
        _logger = logger;
        RegisterLocalTools();
    }

    public bool IsLoaded => _loaded;
    public int ToolCount => _tools.Count;

    /// <summary>
    /// Load tool definitions from the Bridge server via WebSocket.
    /// Falls back to local-only tools if Bridge is unavailable.
    /// </summary>
    public async Task LoadToolsAsync(BridgeWebSocketServer client)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _logger.LogInformation("Loading tools from Bridge via bridge.get_tools...");

            var response = await client.SendCommandAsync("bridge", "get_tools", timeoutMs: 10000);

            if (response.Type == MessageType.error)
            {
                var errorMsg = response.Payload?["error"]?.ToString() ?? "Unknown error";
                _logger.LogWarning("bridge.get_tools returned error: {Error}. Starting with local-only tools.", errorMsg);
                _loaded = true;
                return;
            }

            var toolsArray = response.Payload?["tools"] as JArray;
            if (toolsArray == null)
            {
                _logger.LogWarning("bridge.get_tools returned no tools array. Starting with local-only tools.");
                _loaded = true;
                return;
            }

            int registered = 0;
            foreach (var toolToken in toolsArray)
            {
                var name = toolToken["name"]?.ToString();
                var description = toolToken["description"]?.ToString();
                var inputSchema = toolToken["input_schema"] as JObject;

                if (string.IsNullOrEmpty(name)) continue;

                // Parse domain and action from name (split on first __)
                var separatorIndex = name.IndexOf("__", StringComparison.Ordinal);
                if (separatorIndex < 0)
                {
                    _logger.LogDebug("Skipping tool with no domain separator: {Name}", name);
                    continue;
                }

                var domain = name[..separatorIndex];
                var action = name[(separatorIndex + 2)..];

                var schema = inputSchema ?? new JObject { ["type"] = "object", ["properties"] = new JObject() };
                var entry = new ToolRegistryEntry
                {
                    Name = name,
                    Description = description ?? "",
                    Domain = domain,
                    Action = action,
                    InputSchemaNewtonsoft = schema,
                    InputSchemaJsonElement = ConvertToJsonElement(schema),
                    IsScreenshot = name == "vision__take_screenshot",
                    IsLocal = false
                };

                _tools[name] = entry;
                registered++;
            }

            sw.Stop();
            _logger.LogInformation("Loaded {Count} Bridge tools in {Elapsed}ms ({Total} total including local)", registered, sw.ElapsedMilliseconds, _tools.Count);
            _loaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to load tools from Bridge: {Error}. Starting with local-only tools.", ex.Message);
            _loaded = true;
        }
    }

    /// <summary>
    /// Reload tools from Bridge after a reconnect.
    /// Clears non-local tools and re-fetches from the Bridge server.
    /// </summary>
    public async Task ReloadToolsAsync(BridgeWebSocketServer client)
    {
        _logger.LogInformation("Reloading tools after Unity reconnect...");

        // Remove all non-local tools
        var bridgeKeys = _tools.Where(kvp => !kvp.Value.IsLocal).Select(kvp => kvp.Key).ToList();
        foreach (var key in bridgeKeys)
            _tools.Remove(key);

        _loaded = false;
        await LoadToolsAsync(client);

        _logger.LogInformation("Tools reloaded after reconnect — {Count} total", _tools.Count);
    }

    /// <summary>
    /// Returns all registered tools for MCP tools/list requests.
    /// </summary>
    public ListToolsResult GetToolList()
    {
        var tools = new List<Tool>();

        foreach (var entry in _tools.Values)
        {
            tools.Add(new Tool
            {
                Name = entry.Name,
                Description = entry.Description,
                InputSchema = entry.InputSchemaJsonElement
            });
        }

        return new ListToolsResult { Tools = tools };
    }

    /// <summary>
    /// Dispatch a tool call to the Bridge or handle locally.
    /// </summary>
    public async Task<CallToolResult> DispatchAsync(string toolName, IDictionary<string, JsonElement>? arguments, BridgeWebSocketServer client)
    {
        // Lazy-load Bridge tools if Unity connected after startup
        if (!_loaded && client.IsConnected)
        {
            await LoadToolsAsync(client);
        }

        if (!_tools.TryGetValue(toolName, out var entry))
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Unknown tool: {toolName}" }],
                IsError = true
            };
        }

        var sw = Stopwatch.StartNew();

        // Local tools — some need WebSocket (subscribe/unsubscribe), some don't
        if (entry.IsLocal)
        {
            var result = await HandleLocalToolAsync(entry, arguments, client);
            sw.Stop();
            Interlocked.Exchange(ref client._lastCallDurationMs, sw.ElapsedMilliseconds);
            _logger.LogDebug("{Tool} completed in {Elapsed}ms", toolName, sw.ElapsedMilliseconds);
            return result;
        }

        // Convert arguments from System.Text.Json to Newtonsoft JObject
        var payload = ConvertArguments(arguments);

        // Special handling for screenshot (returns image content)
        if (entry.IsScreenshot)
        {
            var result = await HandleScreenshot(client, payload);
            sw.Stop();
            Interlocked.Exchange(ref client._lastCallDurationMs, sw.ElapsedMilliseconds);
            _logger.LogDebug("{Tool} completed in {Elapsed}ms", toolName, sw.ElapsedMilliseconds);
            return result;
        }

        // Special handling for bridge__get_tools (refresh registry)
        if (toolName == "bridge__get_tools")
        {
            await LoadToolsAsync(client);
            sw.Stop();
            Interlocked.Exchange(ref client._lastCallDurationMs, sw.ElapsedMilliseconds);
            _logger.LogDebug("{Tool} completed in {Elapsed}ms", toolName, sw.ElapsedMilliseconds);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Tool registry refreshed. {_tools.Count} tools available." }]
            };
        }

        // Standard Bridge command dispatch
        {
            var result = await ExecuteBridgeCommand(client, entry.Domain, entry.Action, payload);
            sw.Stop();
            Interlocked.Exchange(ref client._lastCallDurationMs, sw.ElapsedMilliseconds);
            _logger.LogDebug("{Tool} completed in {Elapsed}ms", toolName, sw.ElapsedMilliseconds);
            return result;
        }
    }

    private void RegisterLocalTools()
    {
        _tools["connection_status"] = new ToolRegistryEntry
        {
            Name = "connection_status",
            Description = "Check the MCP server's connection status to the Bridge WebSocket server. No WebSocket round-trip needed.",
            Domain = "",
            Action = "",
            InputSchemaNewtonsoft = new JObject { ["type"] = "object", ["properties"] = new JObject() },
            IsLocal = true
        };

        _tools["events_subscribe"] = new ToolRegistryEntry
        {
            Name = "events_subscribe",
            Description = "Subscribe to a Bridge event type. Events will be buffered and can be retrieved with events_poll. The Bridge only sends events to subscribed clients.",
            Domain = "",
            Action = "",
            InputSchemaNewtonsoft = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["domain"] = new JObject { ["type"] = "string", ["description"] = "Event domain (e.g. fx, auth, nav, party, emotion, ui, world, bridge)" },
                    ["action"] = new JObject { ["type"] = "string", ["description"] = "Event action (e.g. blurb_dispatched, user_joined, chat_received)" }
                },
                ["required"] = new JArray("domain", "action")
            },
            IsLocal = true
        };

        _tools["events_unsubscribe"] = new ToolRegistryEntry
        {
            Name = "events_unsubscribe",
            Description = "Unsubscribe from a Bridge event type. Stops buffering events of this type.",
            Domain = "",
            Action = "",
            InputSchemaNewtonsoft = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["domain"] = new JObject { ["type"] = "string", ["description"] = "Event domain to unsubscribe from" },
                    ["action"] = new JObject { ["type"] = "string", ["description"] = "Event action to unsubscribe from" }
                },
                ["required"] = new JArray("domain", "action")
            },
            IsLocal = true
        };

        _tools["events_poll"] = new ToolRegistryEntry
        {
            Name = "events_poll",
            Description = "Drain and return all buffered events (up to maxEvents). Events are removed from the buffer once returned.",
            Domain = "",
            Action = "",
            InputSchemaNewtonsoft = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["maxEvents"] = new JObject { ["type"] = "integer", ["description"] = "Maximum number of events to return (default: 50)", ["default"] = 50 }
                }
            },
            IsLocal = true
        };

        _tools["events_poll_filtered"] = new ToolRegistryEntry
        {
            Name = "events_poll_filtered",
            Description = "Poll events filtered by domain and/or action. Non-matching events remain in the buffer.",
            Domain = "",
            Action = "",
            InputSchemaNewtonsoft = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["domain"] = new JObject { ["type"] = "string", ["description"] = "Filter by event domain (optional — omit to match all domains)" },
                    ["action"] = new JObject { ["type"] = "string", ["description"] = "Filter by event action (optional — omit to match all actions)" },
                    ["maxEvents"] = new JObject { ["type"] = "integer", ["description"] = "Maximum number of events to return (default: 50)", ["default"] = 50 }
                }
            },
            IsLocal = true
        };

        _tools["world__load_and_wait"] = new ToolRegistryEntry
        {
            Name = "world__load_and_wait",
            Description = "Load a world by station name and wait for the world_loaded event. Orchestrates subscribe → load → poll → unsubscribe automatically.",
            Domain = "world",
            Action = "load_and_wait",
            InputSchemaNewtonsoft = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["station"] = new JObject { ["type"] = "string", ["description"] = "Station name to load" },
                    ["timeout"] = new JObject { ["type"] = "integer", ["description"] = "Timeout in milliseconds (default: 60000)", ["default"] = 60000 }
                },
                ["required"] = new JArray("station")
            },
            IsLocal = true
        };

        _tools["world__unload_and_wait"] = new ToolRegistryEntry
        {
            Name = "world__unload_and_wait",
            Description = "Unload the current world and wait for the world_unloaded event. Orchestrates subscribe → unload → poll → unsubscribe automatically.",
            Domain = "world",
            Action = "unload_and_wait",
            InputSchemaNewtonsoft = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["timeout"] = new JObject { ["type"] = "integer", ["description"] = "Timeout in milliseconds (default: 30000)", ["default"] = 30000 }
                }
            },
            IsLocal = true
        };

        _tools["nav__walk_to_and_wait"] = new ToolRegistryEntry
        {
            Name = "nav__walk_to_and_wait",
            Description = "Walk to a destination and wait until arrival (within threshold distance). Dispatches nav.walk_to then polls nav.get_position every 1.5s. Eliminates the LLM polling loop.",
            Domain = "nav",
            Action = "walk_to_and_wait",
            InputSchemaNewtonsoft = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["destination"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = new JObject { ["type"] = "number" },
                        ["minItems"] = 3,
                        ["maxItems"] = 3,
                        ["description"] = "Target position [x, y, z]"
                    },
                    ["timeout"] = new JObject { ["type"] = "integer", ["description"] = "Max wait time in milliseconds (default: 45000)", ["default"] = 45000 },
                    ["threshold"] = new JObject { ["type"] = "number", ["description"] = "Arrival distance in meters, XZ plane (default: 2.0)", ["default"] = 2.0 }
                },
                ["required"] = new JArray("destination")
            },
            IsLocal = true
        };

        _tools["init_checklist"] = new ToolRegistryEntry
        {
            Name = "init_checklist",
            Description = "Gather all initial agent state in one call: auth status, world status, room users, party members, map data, and event subscriptions. Replaces 6+ sequential tool calls from the bridge-agent init step.",
            Domain = "",
            Action = "",
            InputSchemaNewtonsoft = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["subscribe"] = new JObject { ["type"] = "boolean", ["description"] = "Subscribe to standard agent events (default: true)", ["default"] = true },
                    ["loadWorld"] = new JObject { ["type"] = "string", ["description"] = "Station name to load if no world is currently loaded (optional)" }
                }
            },
            IsLocal = true
        };

        // Pre-compute cached JsonElement for all local tools
        foreach (var entry in _tools.Values)
            entry.InputSchemaJsonElement = ConvertToJsonElement(entry.InputSchemaNewtonsoft);
    }

    private async Task<CallToolResult> HandleLocalToolAsync(ToolRegistryEntry entry, IDictionary<string, JsonElement>? arguments, BridgeWebSocketServer client)
    {
        switch (entry.Name)
        {
            case "connection_status":
            {
                var keepalive = new JObject
                {
                    ["lastSentAt"] = client.LastKeepaliveAt?.ToString("o"),
                    ["lastResponseAt"] = client.LastKeepaliveResponseAt?.ToString("o"),
                    ["healthy"] = client.LastKeepaliveAt == null
                        || client.LastKeepaliveResponseAt == null
                        || client.LastKeepaliveResponseAt >= client.LastKeepaliveAt
                };
                var status = new JObject
                {
                    ["connected"] = client.IsConnected,
                    ["bind"] = client.Bind,
                    ["port"] = client.Port,
                    ["uptimeSeconds"] = (int)client.Uptime.TotalSeconds,
                    ["reconnectCount"] = client.ReconnectCount,
                    ["lastError"] = client.LastError,
                    ["lastCallDurationMs"] = client.LastCallDurationMs,
                    ["keepalive"] = keepalive,
                    ["toolsLoaded"] = _loaded,
                    ["toolCount"] = _tools.Count,
                    ["activeSubscriptions"] = JArray.FromObject(client.GetActiveSubscriptions())
                };
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = status.ToString(Formatting.Indented) }]
                };
            }

            case "events_subscribe":
            {
                var args = ConvertArguments(arguments);
                var domain = args?["domain"]?.ToString();
                var action = args?["action"]?.ToString();
                if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(action))
                {
                    return new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = "Both 'domain' and 'action' are required" }],
                        IsError = true
                    };
                }

                try
                {
                    var response = await client.SubscribeAsync(domain, action);
                    var msg = response.Payload?["message"]?.ToString() ?? $"Subscribed to {domain}.{action}";
                    return new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = msg }]
                    };
                }
                catch (Exception ex)
                {
                    return new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = $"Subscribe failed: {ex.Message}" }],
                        IsError = true
                    };
                }
            }

            case "events_unsubscribe":
            {
                var args = ConvertArguments(arguments);
                var domain = args?["domain"]?.ToString();
                var action = args?["action"]?.ToString();
                if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(action))
                {
                    return new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = "Both 'domain' and 'action' are required" }],
                        IsError = true
                    };
                }

                try
                {
                    var response = await client.UnsubscribeAsync(domain, action);
                    var msg = response.Payload?["message"]?.ToString() ?? $"Unsubscribed from {domain}.{action}";
                    return new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = msg }]
                    };
                }
                catch (Exception ex)
                {
                    return new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = $"Unsubscribe failed: {ex.Message}" }],
                        IsError = true
                    };
                }
            }

            case "events_poll":
            {
                var args = ConvertArguments(arguments);
                var maxEvents = args?["maxEvents"]?.Value<int>() ?? 50;
                var events = client.DrainEvents(maxEvents);
                var json = SerializeEvents(events);
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = json }]
                };
            }

            case "events_poll_filtered":
            {
                var args = ConvertArguments(arguments);
                var domain = args?["domain"]?.ToString();
                var action = args?["action"]?.ToString();
                var maxEvents = args?["maxEvents"]?.Value<int>() ?? 50;
                var events = client.DrainEventsFiltered(domain, action, maxEvents);
                var json = SerializeEvents(events);
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = json }]
                };
            }

            case "world__load_and_wait":
                return await HandleLoadAndWaitAsync(arguments, client);

            case "world__unload_and_wait":
                return await HandleUnloadAndWaitAsync(arguments, client);

            case "nav__walk_to_and_wait":
                return await HandleNavWalkToAndWaitAsync(arguments, client);

            case "init_checklist":
                return await HandleInitChecklistAsync(arguments, client);

            default:
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"Unknown local tool: {entry.Name}" }],
                    IsError = true
                };
        }
    }

    private static string SerializeEvents(List<MessageEnvelope> events)
    {
        var array = new JArray();
        foreach (var evt in events)
        {
            array.Add(new JObject
            {
                ["domain"] = evt.Domain,
                ["action"] = evt.Action,
                ["payload"] = evt.Payload,
                ["timestamp"] = evt.Timestamp
            });
        }
        return array.ToString(Formatting.Indented);
    }

    private async Task<CallToolResult> HandleLoadAndWaitAsync(IDictionary<string, JsonElement>? arguments, BridgeWebSocketServer client)
    {
        var args = ConvertArguments(arguments);
        var station = args?["station"]?.ToString();
        var timeout = args?["timeout"]?.Value<int>() ?? 60000;

        if (string.IsNullOrEmpty(station))
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "'station' parameter is required" }],
                IsError = true
            };
        }

        _logger.LogInformation("world__load_and_wait: station={Station}, timeout={Timeout}ms", station, timeout);

        try
        {
            // 1. Subscribe to world_loaded event
            await client.SubscribeAsync("world", "world_loaded");

            try
            {
                // 2. Send load_world command
                var loadPayload = new JObject { ["station"] = station };
                var loadResponse = await client.SendCommandAsync("world", "load_world", loadPayload, timeoutMs: 10000);

                if (loadResponse.Type == MessageType.error)
                {
                    var errorMsg = loadResponse.Payload?["error"]?.ToString() ?? "load_world failed";
                    return new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = $"load_world error: {errorMsg}" }],
                        IsError = true
                    };
                }

                _logger.LogInformation("load_world acknowledged, waiting for world_loaded event...");

                // 3. Poll for world_loaded event
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeout)
                {
                    var events = client.DrainEventsFiltered("world", "world_loaded", 1);
                    if (events.Count > 0)
                    {
                        var evt = events[0];
                        var result = new JObject
                        {
                            ["status"] = "loaded",
                            ["station"] = station,
                            ["event"] = evt.Payload,
                            ["elapsedMs"] = sw.ElapsedMilliseconds
                        };
                        _logger.LogInformation("world_loaded event received after {Elapsed}ms", sw.ElapsedMilliseconds);
                        return new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = result.ToString(Formatting.Indented) }]
                        };
                    }

                    await Task.Delay(500);
                }

                // 4. Timeout — get final world status for diagnostics
                _logger.LogWarning("world__load_and_wait timed out after {Timeout}ms", timeout);
                JObject? worldStatus = null;
                try
                {
                    var statusResponse = await client.SendCommandAsync("world", "get_world_status", timeoutMs: 5000);
                    if (statusResponse.Type == MessageType.result)
                        worldStatus = statusResponse.Payload;
                }
                catch { /* best effort */ }

                var timeoutResult = new JObject
                {
                    ["status"] = "timeout",
                    ["station"] = station,
                    ["timeoutMs"] = timeout,
                    ["worldStatus"] = worldStatus
                };
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = timeoutResult.ToString(Formatting.Indented) }],
                    IsError = true
                };
            }
            finally
            {
                // 5. Always unsubscribe
                try { await client.UnsubscribeAsync("world", "world_loaded"); }
                catch (Exception ex) { _logger.LogDebug("Unsubscribe cleanup failed: {Error}", ex.Message); }
            }
        }
        catch (Exception ex)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"world__load_and_wait error: {ex.Message}" }],
                IsError = true
            };
        }
    }

    private async Task<CallToolResult> HandleUnloadAndWaitAsync(IDictionary<string, JsonElement>? arguments, BridgeWebSocketServer client)
    {
        var args = ConvertArguments(arguments);
        var timeout = args?["timeout"]?.Value<int>() ?? 30000;

        _logger.LogInformation("world__unload_and_wait: timeout={Timeout}ms", timeout);

        try
        {
            // 1. Subscribe to world_unloaded event
            await client.SubscribeAsync("world", "world_unloaded");

            try
            {
                // 2. Send unload_world command
                var unloadResponse = await client.SendCommandAsync("world", "unload_world", timeoutMs: 10000);

                if (unloadResponse.Type == MessageType.error)
                {
                    var errorMsg = unloadResponse.Payload?["error"]?.ToString() ?? "unload_world failed";
                    return new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = $"unload_world error: {errorMsg}" }],
                        IsError = true
                    };
                }

                _logger.LogInformation("unload_world acknowledged, waiting for world_unloaded event...");

                // 3. Poll for world_unloaded event
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeout)
                {
                    var events = client.DrainEventsFiltered("world", "world_unloaded", 1);
                    if (events.Count > 0)
                    {
                        var evt = events[0];
                        var result = new JObject
                        {
                            ["status"] = "unloaded",
                            ["event"] = evt.Payload,
                            ["elapsedMs"] = sw.ElapsedMilliseconds
                        };
                        _logger.LogInformation("world_unloaded event received after {Elapsed}ms", sw.ElapsedMilliseconds);
                        return new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = result.ToString(Formatting.Indented) }]
                        };
                    }

                    await Task.Delay(500);
                }

                // 4. Timeout — get final world status for diagnostics
                _logger.LogWarning("world__unload_and_wait timed out after {Timeout}ms", timeout);
                JObject? worldStatus = null;
                try
                {
                    var statusResponse = await client.SendCommandAsync("world", "get_world_status", timeoutMs: 5000);
                    if (statusResponse.Type == MessageType.result)
                        worldStatus = statusResponse.Payload;
                }
                catch { /* best effort */ }

                var timeoutResult = new JObject
                {
                    ["status"] = "timeout",
                    ["timeoutMs"] = timeout,
                    ["worldStatus"] = worldStatus
                };
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = timeoutResult.ToString(Formatting.Indented) }],
                    IsError = true
                };
            }
            finally
            {
                // 5. Always unsubscribe
                try { await client.UnsubscribeAsync("world", "world_unloaded"); }
                catch (Exception ex) { _logger.LogDebug("Unsubscribe cleanup failed: {Error}", ex.Message); }
            }
        }
        catch (Exception ex)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"world__unload_and_wait error: {ex.Message}" }],
                IsError = true
            };
        }
    }

    private async Task<CallToolResult> HandleNavWalkToAndWaitAsync(IDictionary<string, JsonElement>? arguments, BridgeWebSocketServer client)
    {
        var args = ConvertArguments(arguments);

        var destArray = args?["destination"] as JArray;
        if (destArray == null || destArray.Count != 3)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "'destination' must be an array of 3 numbers [x, y, z]" }],
                IsError = true
            };
        }

        var destX = destArray[0].Value<float>();
        var destY = destArray[1].Value<float>();
        var destZ = destArray[2].Value<float>();
        var timeout = args?["timeout"]?.Value<int>() ?? 45000;
        var threshold = args?["threshold"]?.Value<float>() ?? 2.0f;

        _logger.LogInformation("nav__walk_to_and_wait: dest=[{X},{Y},{Z}], timeout={Timeout}ms, threshold={Threshold}m",
            destX, destY, destZ, timeout, threshold);

        try
        {
            // 1. Dispatch walk_to command (fire-and-forget)
            var walkPayload = new JObject { ["destination"] = new JArray(destX, destY, destZ) };
            var walkResponse = await client.SendCommandAsync("nav", "walk_to", walkPayload, timeoutMs: 10000);

            if (walkResponse.Type == MessageType.error)
            {
                var errorMsg = walkResponse.Payload?["error"]?.ToString() ?? "walk_to failed";
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"walk_to error: {errorMsg}" }],
                    IsError = true
                };
            }

            // Use snapped destination from response if available
            var snappedDest = walkResponse.Payload?["destination"] as JArray;
            if (snappedDest != null && snappedDest.Count == 3)
            {
                destX = snappedDest[0].Value<float>();
                destY = snappedDest[1].Value<float>();
                destZ = snappedDest[2].Value<float>();
            }

            // 2. Poll get_position every 1500ms until within threshold (XZ distance)
            var sw = Stopwatch.StartNew();
            float lastX = 0, lastZ = 0;
            float distanceRemaining = float.MaxValue;

            while (sw.ElapsedMilliseconds < timeout)
            {
                await Task.Delay(1500);

                try
                {
                    var posResponse = await client.SendCommandAsync("nav", "get_position", timeoutMs: 5000);
                    if (posResponse.Type == MessageType.error) continue;

                    var posArray = posResponse.Payload?["position"] as JArray;
                    if (posArray == null || posArray.Count < 3) continue;

                    var curX = posArray[0].Value<float>();
                    var curZ = posArray[2].Value<float>();

                    // XZ distance (ignore Y — terrain height varies)
                    var dx = curX - destX;
                    var dz = curZ - destZ;
                    distanceRemaining = MathF.Sqrt(dx * dx + dz * dz);

                    _logger.LogDebug("nav__walk_to_and_wait: pos=[{X},{Z}], distance={Dist:F1}m, elapsed={Elapsed}ms",
                        curX, curZ, distanceRemaining, sw.ElapsedMilliseconds);

                    if (distanceRemaining < threshold)
                    {
                        sw.Stop();
                        var result = new JObject
                        {
                            ["status"] = "arrived",
                            ["destination"] = new JArray(destX, destY, destZ),
                            ["finalPosition"] = posArray,
                            ["distanceRemaining"] = Math.Round(distanceRemaining, 1),
                            ["elapsedMs"] = sw.ElapsedMilliseconds
                        };
                        _logger.LogInformation("nav__walk_to_and_wait: arrived in {Elapsed}ms, distance={Dist:F1}m",
                            sw.ElapsedMilliseconds, distanceRemaining);
                        return new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = result.ToString(Formatting.Indented) }]
                        };
                    }

                    lastX = curX;
                    lastZ = curZ;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("nav__walk_to_and_wait: position poll error: {Error}", ex.Message);
                }
            }

            // 3. Timeout
            sw.Stop();
            _logger.LogWarning("nav__walk_to_and_wait: timed out after {Timeout}ms, distance={Dist:F1}m", timeout, distanceRemaining);
            var timeoutResult = new JObject
            {
                ["status"] = "timeout",
                ["destination"] = new JArray(destX, destY, destZ),
                ["lastPosition"] = new JArray(lastX, 0, lastZ),
                ["distanceRemaining"] = Math.Round(distanceRemaining, 1),
                ["timeoutMs"] = timeout
            };
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = timeoutResult.ToString(Formatting.Indented) }],
                IsError = true
            };
        }
        catch (Exception ex)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"nav__walk_to_and_wait error: {ex.Message}" }],
                IsError = true
            };
        }
    }

    private async Task<CallToolResult> HandleInitChecklistAsync(IDictionary<string, JsonElement>? arguments, BridgeWebSocketServer client)
    {
        var args = ConvertArguments(arguments);
        var subscribe = args?["subscribe"]?.Value<bool>() ?? true;
        var loadWorld = args?["loadWorld"]?.ToString();

        _logger.LogInformation("init_checklist: subscribe={Subscribe}, loadWorld={LoadWorld}", subscribe, loadWorld ?? "(none)");
        var sw = Stopwatch.StartNew();
        var result = new JObject();

        try
        {
            // 1. Parallel queries: auth, world status, room users, party members
            var authTask = SafeQueryAsync(client, "auth", "get_status");
            var worldTask = SafeQueryAsync(client, "world", "get_world_status");
            var usersTask = SafeQueryAsync(client, "auth", "get_room_users");
            var partyTask = SafeQueryAsync(client, "party", "get_members");

            await Task.WhenAll(authTask, worldTask, usersTask, partyTask);

            result["auth"] = authTask.Result;
            result["world"] = worldTask.Result;
            result["roomUsers"] = usersTask.Result;
            result["partyMembers"] = partyTask.Result;

            // 2. Conditional world load
            var worldLoaded = worldTask.Result?["loaded"]?.Value<bool>() == true;
            var worldLoading = worldTask.Result?["loading"]?.Value<bool>() == true;

            if (!string.IsNullOrEmpty(loadWorld) && !worldLoaded)
            {
                _logger.LogInformation("init_checklist: world not loaded, loading station '{Station}'...", loadWorld);

                // Validate station name
                JObject? stationsResult = null;
                try
                {
                    var stationsResponse = await client.SendCommandAsync("world", "get_stations", timeoutMs: 10000);
                    if (stationsResponse.Type == MessageType.result)
                        stationsResult = stationsResponse.Payload;
                }
                catch { /* best effort */ }

                // Delegate to existing load_and_wait logic
                var loadArgs = new Dictionary<string, JsonElement>();
                using var stationDoc = JsonDocument.Parse($"\"{loadWorld}\"");
                loadArgs["station"] = stationDoc.RootElement.Clone();

                var loadResult = await HandleLoadAndWaitAsync(loadArgs, client);

                // Parse the load result to check if it succeeded
                try
                {
                    var loadText = (loadResult.Content?.FirstOrDefault() as TextContentBlock)?.Text;
                    if (loadText != null)
                    {
                        var loadJson = JObject.Parse(loadText);
                        result["worldLoad"] = loadJson;
                        worldLoaded = loadJson["status"]?.ToString() == "loaded";
                    }
                }
                catch
                {
                    result["worldLoad"] = new JObject { ["error"] = "Failed to parse load result" };
                }

                // Refresh world status after load
                if (worldLoaded)
                {
                    var refreshWorld = await SafeQueryAsync(client, "world", "get_world_status");
                    result["world"] = refreshWorld;
                }
            }

            // 3. Get map (only if world is loaded)
            if (worldLoaded)
            {
                result["map"] = await SafeQueryAsync(client, "nav", "get_map");
            }
            else
            {
                result["map"] = null;
            }

            // 4. Event subscriptions
            if (subscribe)
            {
                var events = new[]
                {
                    ("party", "member_joined"),
                    ("party", "member_left"),
                    ("party", "roster_changed"),
                    ("ui", "chat_received"),
                    ("bridge", "nudge_received"),
                    ("nav", "walk_dispatched")
                };

                var subscribed = new JArray();
                foreach (var (domain, action) in events)
                {
                    try
                    {
                        await client.SubscribeAsync(domain, action);
                        subscribed.Add($"{domain}.{action}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("init_checklist: failed to subscribe to {Domain}.{Action}: {Error}", domain, action, ex.Message);
                    }
                }
                result["subscriptions"] = subscribed;
            }

            sw.Stop();
            result["elapsedMs"] = sw.ElapsedMilliseconds;

            _logger.LogInformation("init_checklist: completed in {Elapsed}ms", sw.ElapsedMilliseconds);

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = result.ToString(Formatting.Indented) }]
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"init_checklist error: {ex.Message}" }],
                IsError = true
            };
        }
    }

    /// <summary>
    /// Safely query a Bridge domain/action. Returns the payload on success, or an error JObject on failure.
    /// Never throws — failures are captured as { "error": "..." }.
    /// </summary>
    private async Task<JObject?> SafeQueryAsync(BridgeWebSocketServer client, string domain, string action)
    {
        try
        {
            var response = await client.SendCommandAsync(domain, action, timeoutMs: 10000);
            if (response.Type == MessageType.error)
            {
                var errorMsg = response.Payload?["error"]?.ToString() ?? $"{domain}.{action} failed";
                return new JObject { ["error"] = errorMsg };
            }
            return response.Payload ?? new JObject();
        }
        catch (Exception ex)
        {
            return new JObject { ["error"] = ex.Message };
        }
    }

    private async Task<CallToolResult> HandleScreenshot(BridgeWebSocketServer client, JObject? payload)
    {
        try
        {
            var response = await client.SendCommandAsync("vision", "take_screenshot", payload, timeoutMs: 15000);

            if (response.Type == MessageType.error)
            {
                var errorMsg = response.Payload?["error"]?.ToString() ?? "Screenshot failed";
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = errorMsg }],
                    IsError = true
                };
            }

            var imageBase64 = response.Payload?["image"]?.ToString();
            if (string.IsNullOrEmpty(imageBase64))
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "Screenshot returned no image data" }],
                    IsError = true
                };
            }

            var width = response.Payload?["width"]?.Value<int>() ?? 0;
            var height = response.Payload?["height"]?.Value<int>() ?? 0;
            var sizeKb = (imageBase64.Length * 3 / 4) / 1024;
            var metaText = $"Screenshot: {width}x{height}, {sizeKb}KB";

            var imageBytes = Convert.FromBase64String(imageBase64);

            return new CallToolResult
            {
                Content =
                [
                    ImageContentBlock.FromBytes(imageBytes, "image/jpeg"),
                    new TextContentBlock { Text = metaText }
                ]
            };
        }
        catch (TimeoutException)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "Screenshot timed out after 15 seconds" }],
                IsError = true
            };
        }
        catch (Exception ex)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Screenshot error: {ex.Message}" }],
                IsError = true
            };
        }
    }

    private async Task<CallToolResult> ExecuteBridgeCommand(BridgeWebSocketServer client, string domain, string action, JObject? payload)
    {
        try
        {
            var response = await client.SendCommandAsync(domain, action, payload);

            if (response.Type == MessageType.error)
            {
                var errorMsg = response.Payload?["error"]?.ToString() ?? $"Bridge error on {domain}.{action}";
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = errorMsg }],
                    IsError = true
                };
            }

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = response.Payload?.ToString(Formatting.Indented) ?? "{}" }]
            };
        }
        catch (TimeoutException ex)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = ex.Message }],
                IsError = true
            };
        }
        catch (InvalidOperationException ex)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = ex.Message }],
                IsError = true
            };
        }
    }

    private static JObject? ConvertArguments(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments == null || arguments.Count == 0)
            return null;

        // Convert per-property using GetRawText() — avoids serializing the entire dictionary
        var obj = new JObject();
        foreach (var kvp in arguments)
            obj[kvp.Key] = JToken.Parse(kvp.Value.GetRawText());
        return obj;
    }

    private static JsonElement ConvertToJsonElement(JObject newtonsoftObj)
    {
        var json = newtonsoftObj.ToString(Formatting.None);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

public class ToolRegistryEntry
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Domain { get; init; }
    public required string Action { get; init; }
    public required JObject InputSchemaNewtonsoft { get; init; }
    public JsonElement InputSchemaJsonElement { get; set; }
    public bool IsScreenshot { get; init; }
    public bool IsLocal { get; init; }
}
