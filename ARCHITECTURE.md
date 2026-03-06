# Architecture — Bridge MCP Server

## Overview

The Bridge MCP Server (`DigitRaverHelperMCP`) translates between the [Model Context Protocol](https://modelcontextprotocol.io/) (stdio/JSON-RPC) and the DigitRaver Bridge WebSocket protocol. An MCP client (Claude Code, OpenClaw, etc.) sees a flat list of tools; the server maps each tool call to a Bridge command and returns the result.

The MCP server **listens** for inbound connections — Unity connects **outward** to it:

```
MCP Client ←── stdio (JSON-RPC) ──→ MCP Server ←── WebSocket ──── Unity (Bridge)
                                     (server)        (connects inbound)
```

## Source Files

| File | Responsibility |
|------|---------------|
| [`Program.cs`](Program.cs) | Entry point — CLI parsing, DI wiring, startup orchestration, daemon mode |
| [`BridgeBeaconBroadcaster.cs`](BridgeBeaconBroadcaster.cs) | UDP multicast broadcaster — advertises the MCP server on the LAN |
| [`BridgeWebSocketServer.cs`](BridgeWebSocketServer.cs) | WebSocket server — accepts Unity connection, manual handshake, send/receive, keepalive, event buffering, relay client handling |
| [`BridgeToolRegistry.cs`](BridgeToolRegistry.cs) | Dynamic tool registry — loads schemas from Bridge, dispatches MCP tool calls |
| [`BridgeRelayClient.cs`](BridgeRelayClient.cs) | Relay client — connects secondary MCP instances to a running primary server |
| [`MessageProtocol.cs`](MessageProtocol.cs) | Wire protocol types — `MessageEnvelope` and `MessageType` enum |
| [`bridge-launcher.mjs`](bridge-launcher.mjs) | Cross-platform Node.js launcher — RID detection, binary resolution, auto-download |

## Startup Sequence

```
bridge-launcher.mjs
│
├─ 1. Detect RID (os + arch → win-x64, osx-arm64, …)
├─ 2. Find binary (local build → user cache → GitHub download → dotnet run)
└─ 3. Spawn DigitRaverHelperMCP, inherit stdio
       │
       ├─ 4. Parse CLI flags (--bind, --port, --verbose, --no-beacon, --relay, --primary)
       │
       ├─ 5. Detect primary/relay mode
       │     ├─ --primary flag        → always start as primary
       │     ├─ --relay flag          → always connect as relay client
       │     └─ default               → probe port with TcpListener (dual-stack)
       │                                if port in use → relay mode
       │                                if port free   → primary mode
       │
       ├─ [RELAY PATH] → BridgeRelayClient connects to ws://127.0.0.1:{port+1}
       │                  forwards stdio ↔ WebSocket, exits when stdin closes
       │
       ├─ [PRIMARY PATH]
       │
       ├─ 6. Start TCP listeners
       │     ├─ Main listener on {bind}:{port}    (Unity Bridge connections)
       │     └─ Relay listener on {bind}:{port+1} (secondary MCP instances)
       │     Both use dual-stack sockets (IPv6Any + DualMode) for IPv4/IPv6
       │
       ├─ 7. Start beacon broadcaster (unless --no-beacon)
       │     └─ UDP multicast on 239.255.42.99:18801 every 5s
       │
       ├─ 8. Wait for Unity to connect (with --timeout, default 10s)
       │     └─ polls IsConnected every 100ms
       │
       ├─ 9. Load tools → bridge.get_tools over WebSocket
       │     ├─ register Bridge tools ({domain}__{action})
       │     ├─ 9 local tools always registered
       │     └─ send tools/list_changed notification to MCP client
       │
       ├─ 10. Wire OnReconnected → ReloadToolsAsync → tools/list_changed
       │
       ├─ 11. Wire relay dispatchers (RelayToolListProvider, RelayToolDispatcher)
       │
       ├─ 12. Set KeepAliveAfterHostShutdown = true
       │
       └─ 13. MCP stdio transport ready (app.RunAsync)
              ├─ tools/list  → BridgeToolRegistry.GetToolList()
              ├─ tools/call  → BridgeToolRegistry.DispatchAsync()
              └─ on stdio close → daemon mode (if Unity/relay still connected)
```

## Data Flow: Tool Call

A single MCP `tools/call` request follows this path:

```
MCP Client                    MCP Server                         Bridge (Unity)
    │                             │                                    │
    │── tools/call ──────────────→│                                    │
    │   {name, arguments}         │                                    │
    │                             │── DispatchAsync() ────────────────→│
    │                             │   MessageEnvelope {                │
    │                             │     id, type:"command",            │
    │                             │     domain, action, payload        │
    │                             │   }                                │
    │                             │                                    │
    │                             │←── MessageEnvelope {type:"result"} │
    │                             │    correlated by envelope.id       │
    │                             │                                    │
    │←── CallToolResult ──────────│                                    │
    │   TextContent or            │                                    │
    │   ImageContent (screenshot) │                                    │
```

**Key details:**

- **Name mapping**: MCP tool name `nav__walk_to` → Bridge domain `nav`, action `walk_to`
- **ID correlation**: Each command gets a GUID; the receive loop matches responses by `envelope.id` via a `ConcurrentDictionary<string, TaskCompletionSource>`
- **Serialization boundary**: MCP uses `System.Text.Json`; Bridge protocol uses `Newtonsoft.Json`. `BridgeToolRegistry` converts between them at dispatch time.
- **Screenshots**: `vision__take_screenshot` returns base64 image data, which is wrapped as MCP `ImageContent` instead of `TextContent`

## Data Flow: Events

Bridge events are push-based. The MCP server buffers them for poll-based retrieval by the MCP client:

```
Bridge (Unity)                 MCP Server                      MCP Client
  │                                │                               │
  │── event {domain, action} ─────→│                               │
  │                                │── EnqueueEvent() ────→ buffer │
  │                                │   (max 200, FIFO eviction)    │
  │                                │                               │
  │                                │←── events_poll ───────────────│
  │                                │                               │
  │                                │── DrainEvents() ─────────────→│
  │                                │   returns buffered events     │
```

The client must explicitly subscribe first (`events_subscribe` tool), which sends a `subscribe` message over WebSocket. Subscriptions survive reconnects — `ResubscribeAllAsync()` re-registers them after every new Unity connection.

## UDP Beacon

The MCP server **broadcasts** UDP beacons on multicast group `239.255.42.99:18801` every 5 seconds so Unity can discover it:

```json
{
  "service": "digitraver-mcp",
  "version": 2,
  "port": 18800,
  "hostname": "GAMING-PC"
}
```

Unity listens for these beacons and uses the UDP packet's source IP to connect back.

```
MCP Server (Machine A)                          Unity (Machine B)
  │                                                   │
  │── UDP beacon ────────────────────────────────────→│
  │   src: 192.168.1.10:random                        │
  │   dst: 239.255.42.99:18801                        │
  │   payload: {"service":"digitraver-mcp",           │
  │             "version":2, "port":18800,            │
  │             "hostname":"GAMING-PC"}               │
  │                                                   │
  │                        host = RemoteEndPoint.Address (192.168.1.10)
  │                        port = beacon["port"] (18800)
  │                                                   │
  │←──────────── WebSocket connect ───────────────────│
  │              ws://192.168.1.10:18800              │
```

Disabled with `--no-beacon`. The MCP server always listens on `--bind`:`--port` regardless of beacon status — there is no fallback chain.

## WebSocket Connection

`BridgeWebSocketServer` uses a `TcpListener` with manual HTTP→WebSocket upgrade (no HttpListener or Kestrel dependency):

### Accept Loop
- Listens on `{bind}:{port}` using dual-stack sockets (IPv6Any + DualMode) for both IPv4 and IPv6
- On incoming TCP connection, enables TCP keepalive (10s time, 5s interval, 3 retries)
- Performs manual WebSocket handshake: reads HTTP upgrade request, extracts `Sec-WebSocket-Key`, computes SHA-1 accept hash, sends `101 Switching Protocols`, creates `WebSocket` from the upgraded stream
- Handshake has a 10-second timeout — if the client connects TCP but doesn't send upgrade headers, the connection is dropped
- **Single-connection model**: Only one Unity connection at a time. A new connection replaces the previous one (cancels receive/keepalive loops, disposes old WebSocket)

### Keepalive
- Sends `bridge.ping` every **15 seconds**
- Timeout after **30 seconds** triggers connection close (keepalive loop breaks, accept loop waits for Unity to reconnect)

### Reconnection
- Unity is responsible for reconnecting. The accept loop runs continuously, waiting for new TCP connections
- On reconnect: re-subscribes all active event subscriptions, fires `OnReconnected` event (triggers tool reload)
- `_reconnectCount` tracks how many times Unity has reconnected

### Send Serialization
- A `SemaphoreSlim` ensures only one WebSocket frame is written at a time

### Receive Loop
- Runs on a background task, reassembles multi-frame messages (initial 512KB buffer, auto-expands)
- Deserializes `MessageEnvelope` and either completes a pending request (`result`/`error`) or enqueues an event

## Relay Mode

Allows multiple MCP clients to share a single Unity Bridge connection. The first MCP instance becomes the **primary**; subsequent instances connect as **relay clients**.

```
MCP Client A ── stdio ──→ Primary MCP Server ←── WebSocket ──── Unity
                             │ port 18800
                             │ port 18801 (relay)
MCP Client B ── stdio ──→ Relay Client ── WebSocket ──→ │
```

### Primary Side

- Detects it should be primary when port is free (or `--primary` flag)
- Listens on two ports: `{port}` for Unity, `{port}+1` for relay clients
- `RelayAcceptLoopAsync` accepts relay WebSocket connections on the relay port
- Each relay client gets a `RelayClientConnection` that:
  - Receives JSON-RPC messages (`tools/list`, `tools/call`)
  - Dispatches through the same `BridgeToolRegistry` as the primary
  - Returns results as JSON-RPC responses over WebSocket

### Relay Side

- Detects it should be relay when port is already in use (or `--relay` flag)
- `BridgeRelayClient` connects to `ws://127.0.0.1:{port+1}`
- Forwards bidirectionally: stdin → WebSocket, WebSocket → stdout
- Exits when stdin closes or WebSocket disconnects

## Daemon Mode

After `app.RunAsync()` returns (stdio closed by the MCP client), the server checks if Unity or relay clients are still connected:

- **If connected**: enters a keep-alive loop, checking every second
- **If no connections for 5 minutes**: exits
- **If a connection exists**: resets the idle counter

This is enabled by setting `KeepAliveAfterHostShutdown = true` before `app.RunAsync()`. The `Dispose()` method becomes a no-op the first time it's called (by the host shutdown), preserving the WebSocket server. `ForceDispose()` is called on final exit.

## Tool Registry

`BridgeToolRegistry` holds two categories of tools:

### Bridge tools (dynamic)

Loaded from Unity via `bridge.get_tools`. Each tool's name encodes its routing: `{domain}__{action}`. The registry stores the JSON schema from the Bridge and dispatches calls as `SendCommandAsync(domain, action, payload)`.

- **Lazy load**: If Unity isn't connected at startup, the server starts with local-only tools. Bridge tools are loaded once Unity connects.
- **Reload on reconnect**: `OnReconnected` fires `ReloadToolsAsync` which clears non-local tools and re-fetches from Bridge, then sends a `tools/list_changed` notification to the MCP client.

### Local tools (9, static)

Registered in the constructor. These implement logic that doesn't map 1:1 to a Bridge command:

| Tool | What it does |
|------|-------------|
| `connection_status` | Returns WebSocket health, uptime, reconnect count (no round-trip) |
| `events_subscribe` | Sends `subscribe` message, tracks for reconnect |
| `events_unsubscribe` | Sends `unsubscribe` message, removes tracking |
| `events_poll` | Drains buffered events |
| `events_poll_filtered` | Drains events matching domain/action filter |
| `world__load_and_wait` | subscribe → `world.load` → poll for `world_loaded` → unsubscribe |
| `world__unload_and_wait` | subscribe → `world.unload` → poll for `world_unloaded` → unsubscribe |
| `nav__walk_to_and_wait` | `nav.walk_to` → poll `nav.get_position` every 1.5s until arrival |
| `init_checklist` | Batches auth, world status, room users, party, map, and subscriptions into one call |

The compound tools (`*_and_wait`, `init_checklist`) eliminate multi-step LLM polling loops by orchestrating the full sequence server-side.

## Wire Protocol

All WebSocket messages use the `MessageEnvelope` format (defined in `MessageProtocol.cs`):

```json
{
  "id": "guid",
  "type": "command | subscribe | unsubscribe | event | result | error",
  "domain": "nav",
  "action": "walk_to",
  "payload": { ... },
  "timestamp": "2025-01-01T00:00:00Z"
}
```

- `command` → MCP-to-Bridge request (expects `result` or `error` back)
- `subscribe` / `unsubscribe` → event registration (expects `result` back)
- `event` → Bridge-to-MCP push (no response expected)
- `result` / `error` → Bridge-to-MCP response (correlated by `id`)
