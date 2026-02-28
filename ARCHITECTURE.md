# Architecture — Bridge MCP Server

## Overview

The Bridge MCP Server (`DigitRaverHelperMCP`) translates between the [Model Context Protocol](https://modelcontextprotocol.io/) (stdio/JSON-RPC) and the DigitRaver Bridge WebSocket protocol. An MCP client (Claude Code, OpenClaw, etc.) sees a flat list of tools; the server maps each tool call to a Bridge command and returns the result.

```
MCP Client ←── stdio (JSON-RPC) ──→ MCP Server ←── WebSocket ──→ Bridge (DigitRaver)
```

## Source Files

| File | Responsibility |
|------|---------------|
| [`Program.cs`](Program.cs) | Entry point — CLI parsing, DI wiring, startup orchestration |
| [`BridgeBeaconBroadcaster.cs`](BridgeBeaconBroadcaster.cs) | UDP multicast broadcaster — advertises the MCP server on the LAN |
| [`BridgeWebSocketServer.cs`](BridgeWebSocketServer.cs) | WebSocket server — accepts Unity connection, send/receive, keepalive, event buffering |
| [`BridgeToolRegistry.cs`](BridgeToolRegistry.cs) | Dynamic tool registry — loads schemas from Bridge, dispatches MCP tool calls |
| [`bridge-launcher.mjs`](bridge-launcher.mjs) | Cross-platform Node.js launcher — RID detection, binary resolution, auto-download |

## Startup Sequence

```
bridge-launcher.mjs
│
├─ 1. Detect RID (os + arch → win-x64, osx-arm64, …)
├─ 2. Find binary (local build → user cache → GitHub download → dotnet run)
└─ 3. Spawn DigitRaverHelperMCP, inherit stdio
       │
       ├─ 4. Parse CLI flags (--host, --port, --verbose, --no-discovery)
       │
       ├─ 5. Resolve host
       │     ├─ --host flag provided  → use it directly
       │     ├─ UDP discovery          → listen on 239.255.42.99:18801
       │     │                           use sender IP from UDP packet
       │     └─ fallback               → localhost:18800
       │
       ├─ 6. WebSocket connect → ws://{host}:{port}
       │     └─ retries with backoff (2s, 4s, 8s), max 3 attempts
       │
       ├─ 7. Load tools → bridge.get_tools over WebSocket
       │     ├─ register ~15 Bridge tools ({domain}__{action})
       │     └─ 9 local tools always registered
       │
       └─ 8. MCP stdio transport ready
             ├─ tools/list  → BridgeToolRegistry.GetToolList()
             └─ tools/call  → BridgeToolRegistry.DispatchAsync()
```

## Data Flow: Tool Call

A single MCP `tools/call` request follows this path:

```
MCP Client                    MCP Server                         Bridge (DigitRaver)
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
Bridge                         MCP Server                      MCP Client
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

The client must explicitly subscribe first (`events_subscribe` tool), which sends a `subscribe` message over WebSocket. Subscriptions survive reconnects — `ResubscribeAllAsync()` re-registers them after every new connection.

## UDP Discovery

The Bridge advertiser (Unity-side `BridgeDiscoveryAdvertiser`) broadcasts JSON beacons on multicast group `239.255.42.99:18801`:

```json
{
  "service": "digitraver-bridge",
  "version": 1,
  "hostname": "GAMING-PC",
  "port": 18800,
  "deviceId": "abc123"
}
```

The MCP server **ignores the `hostname` field** for connection purposes and instead uses the UDP packet's source IP address (`UdpReceiveResult.RemoteEndPoint.Address`). This is critical for LAN discovery — the hostname is often a machine name (e.g., "GAMING-PC") that won't resolve across machines, while the source IP is always the correct routable address. The beacon hostname is still logged for diagnostics.

```
Bridge (Machine A)                              MCP Server (Machine B)
  │                                                   │
  │── UDP beacon ────────────────────────────────────→│
  │   src: 192.168.1.42:random                        │
  │   dst: 239.255.42.99:18801                        │
  │   payload: {"hostname":"GAMING-PC", "port":18800} │
  │                                                   │
  │                        host = RemoteEndPoint.Address (192.168.1.42)
  │                        port = beacon["port"] (18800)
  │                                                   │
  │←──────────── WebSocket connect ───────────────────│
  │              ws://192.168.1.42:18800              │
```

**Fallback chain**: `--host` flag → UDP discovery (5s timeout) → `localhost:18800`

## WebSocket Connection

`BridgeWebSocketClient` maintains a single persistent connection with:

- **Auto-reconnect**: On unexpected disconnect, schedules reconnect after 1s delay. `ConnectAsync` retries up to 3 times with exponential backoff (2s, 4s, 8s).
- **Keepalive**: Sends `auth.get_status` every 30s. Timeout after 10s triggers a reconnect.
- **Send serialization**: A `SemaphoreSlim` ensures only one frame is written at a time.
- **Receive loop**: Runs on a background `Task`, reassembles multi-frame messages into a single buffer (initial 512KB, auto-expands), deserializes the `MessageEnvelope`, and either completes a pending request or enqueues an event.
- **Subscription persistence**: Active subscriptions are tracked in a `HashSet` and re-registered after every reconnect.

## Tool Registry

`BridgeToolRegistry` holds two categories of tools:

### Bridge tools (~15, dynamic)

Loaded at startup from `bridge.get_tools`. Each tool's name encodes its routing: `{domain}__{action}`. The registry stores the JSON schema from the Bridge and dispatches calls as `SendCommandAsync(domain, action, payload)`.

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

All WebSocket messages use the `MessageEnvelope` format:

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

- `command` → client-to-Bridge request (expects `result` or `error` back)
- `subscribe` / `unsubscribe` → event registration (expects `result` back)
- `event` → Bridge-to-client push (no response expected)
- `result` / `error` → Bridge-to-client response (correlated by `id`)
