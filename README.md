# DigitRaverHelperMCP — Bridge MCP Server

Standalone .NET 8 MCP server that bridges MCP clients (Claude Code, OpenClaw, etc.) to DigitRaver's Bridge WebSocket server running inside Unity.

```
MCP Client ←── stdio ──→ MCP Server ←── WebSocket ──→ Bridge (Unity)
```

## For OpenClaw Users

One-line install — downloads the binary, installs the agent skill, and configures OpenClaw:

```bash
curl -fsSL https://github.com/thePostFuturist/CrabRaver/releases/download/v1.0.0/install.sh | bash
```

For private repos, provide a GitHub token:

```bash
GITHUB_TOKEN=ghp_xxx curl -fsSL https://github.com/thePostFuturist/CrabRaver/releases/download/v1.0.0/install.sh | bash
```

**What it installs:**

| Component | Location |
|-----------|----------|
| MCP server binary | `~/.digitraver/mcp/bridge/{version}/{rid}/` |
| Agent skill | `~/.openclaw/skills/digitraver-agent/SKILL.md` |
| Server config | `~/.openclaw/openclaw.json` (bridge entry added) |

After install, restart OpenClaw and invoke the agent:

```bash
openclaw gateway restart
# Then use: /digitraver-agent
```

**Uninstall:**

```bash
curl -fsSL https://github.com/thePostFuturist/CrabRaver/releases/download/v1.0.0/install.sh | bash -s -- --uninstall
```

---

## For Developers (Repo Access)

### Quick Start

**Just clone and go** — the binary auto-downloads on first tool call:

```json
// .mcp.json (already configured in this repo)
{
  "mcpServers": {
    "bridge": {
      "type": "stdio",
      "command": "Tools/DigitRaverHelperMCP/bridge-mcp.sh",
      "args": []
    }
  }
}
```

The launcher script (`bridge-mcp.sh`) handles everything:
1. Detects your OS and architecture
2. Looks for a local build first (for developers)
3. Downloads the correct pre-built binary from GitHub Releases if needed
4. Caches it in `~/.digitraver/mcp/bridge/` (persists across re-clones)
5. Runs the server

## Configuration

### Debug Mode

Set the `BRIDGE_DEBUG` environment variable for verbose logging:

```json
{
  "mcpServers": {
    "bridge": {
      "type": "stdio",
      "command": "Tools/DigitRaverHelperMCP/bridge-mcp.sh",
      "args": [],
      "env": { "BRIDGE_DEBUG": "1" }
    }
  }
}
```

This injects `--verbose` into the server args, producing debug-level output on stderr.

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `BRIDGE_DEBUG` | `0` | Set to `1` to enable verbose logging |
| `BRIDGE_VERSION` | *(built-in)* | Override the version to download |
| `BRIDGE_REPO` | `thePostFuturist/CrabRaver` | Override the GitHub repo for downloads |

## CLI Arguments

| Flag | Default | Description |
|------|---------|-------------|
| `--host <ip>` | *(auto-discover)* | Bridge host address (skips UDP discovery) |
| `--port <n>` | `18800` | Bridge WebSocket port |
| `--timeout <ms>` | `10000` | Default command timeout |
| `--verbose` | off | Debug-level logging to stderr |
| `--no-discovery` | off | Skip UDP discovery, use localhost |

## Manual Install

If auto-download doesn't work (e.g., no internet, corporate firewall), download the binary manually:

1. Go to [Releases](https://github.com/thePostFuturist/CrabRaver/releases)
2. Download the binary for your platform:
   - `DigitRaverHelperMCP.exe` (Windows x64)
   - `DigitRaverHelperMCP-osx-arm64` (macOS Apple Silicon)
   - `DigitRaverHelperMCP-linux-x64` (Linux x64)
3. Rename to `DigitRaverHelperMCP` (or `.exe` on Windows) and place in `~/.digitraver/mcp/bridge/{version}/{rid}/`:

```bash
# Example: macOS Apple Silicon, version 1.0.0
mkdir -p ~/.digitraver/mcp/bridge/1.0.0/osx-arm64
mv DigitRaverHelperMCP ~/.digitraver/mcp/bridge/1.0.0/osx-arm64/
chmod +x ~/.digitraver/mcp/bridge/1.0.0/osx-arm64/DigitRaverHelperMCP
```

Platform RIDs: `win-x64`, `osx-arm64`, `linux-x64`

## For Developers

### Dev mode (requires .NET 8 SDK)

```bash
dotnet run --project Tools/DigitRaverHelperMCP
```

The launcher falls back to `dotnet run` automatically if no binary is found and .NET SDK is available.

### Publishing locally

```bash
# Build all platforms + create release archives
./Tools/DigitRaverHelperMCP/publish.sh

# Build a single platform
./Tools/DigitRaverHelperMCP/publish.sh win-x64
```

Output:
- `bin/publish/{rid}/` — executables (used first by the launcher)
- `bin/publish/release/` — binaries for GitHub Release upload

### Binary search order

The launcher checks for binaries in this order:
1. **Project-local**: `Tools/DigitRaverHelperMCP/bin/publish/{rid}/` — your `publish.sh` output
2. **User cache**: `~/.digitraver/mcp/bridge/{version}/{rid}/` — auto-downloaded binaries
3. **Auto-download**: fetches from GitHub Releases → saves to user cache
4. **dotnet run**: fallback if .NET SDK is available

### Release Process

1. Update `VERSION` in `bridge-mcp.sh` and `dist/install.sh`
2. Cross-compile all platforms:
   ```bash
   ./Tools/DigitRaverHelperMCP/publish.sh
   ```
3. Verify the release folder contains all 5 artifacts:
   ```
   bin/publish/release/
   ├── install.sh
   ├── SKILL.md
   ├── DigitRaverHelperMCP.exe          (win-x64)
   ├── DigitRaverHelperMCP-osx-arm64    (macOS)
   └── DigitRaverHelperMCP-linux-x64    (linux)
   ```
4. Create a GitHub Release and upload:
   ```bash
   gh release create v1.0.0 \
     Tools/DigitRaverHelperMCP/bin/publish/release/* \
     --title "Bridge MCP v1.0.0" \
     --notes "Initial release"
   ```

## Architecture

Four source files:

| File | Role |
|------|------|
| `Program.cs` | Entry point — CLI parsing, DI setup, MCP server wiring, startup orchestration |
| `BridgeWebSocketClient.cs` | Persistent WebSocket connection — send/receive, auto-reconnect, keepalive, event buffering |
| `BridgeToolRegistry.cs` | Dynamic tool registry — loads schemas from `bridge.get_tools`, dispatches calls, local compound tools |
| `BridgeDiscovery.cs` | UDP auto-discovery — finds Bridge server on LAN without hardcoded host |

### Startup sequence

1. **Discovery** — UDP broadcast to find Bridge, or use `--host`/`--no-discovery`
2. **Connect** — WebSocket handshake to `ws://{host}:{port}`
3. **Load tools** — `bridge.get_tools` → register all Bridge tools + local compound tools
4. **Ready** — MCP stdio transport starts accepting `tools/list` and `tools/call`

## Tool Categories

### Bridge tools (dynamic, ~15)

Loaded at startup from Bridge's `get_tools` endpoint. Named `bridge__{domain}_{action}`:

- `bridge__auth_*` — Authentication status
- `bridge__nav_*` — Navigation (walk, teleport, get position, waypoints)
- `bridge__world_*` — World management (load, unload, status)
- `bridge__party_*` — Party and room users
- `bridge__fx_*` — Effects and chat
- `bridge__vision_*` — Screenshots (returned as MCP `ImageContent`)
- `bridge__emotion_*`, `bridge__ui_*`, `bridge__loopback_ws_*`

### Local tools (7)

Implemented in the MCP server, no 1:1 Bridge command:

| Tool | Description |
|------|-------------|
| `connection_status` | WebSocket health, uptime, reconnect count |
| `events_subscribe` | Subscribe to Bridge event type |
| `events_unsubscribe` | Unsubscribe from event type |
| `events_poll` | Drain buffered events |
| `events_poll_filtered` | Drain events matching domain/action filter |
| `world__load_and_wait` | Load world + wait for `world_loaded` event |
| `world__unload_and_wait` | Unload world + wait for `world_unloaded` event |
| `nav__walk_to_and_wait` | Walk to position + poll until arrival |
| `init_checklist` | Gather all initial agent state in one call |

## Troubleshooting

**Unity not running / Bridge not started**
- MCP server starts with local-only tools and logs a warning
- Bridge tools become available after reconnection when Unity starts

**Port mismatch**
- Bridge defaults to `ws://0.0.0.0:18800`. Pass `--port` if changed.

**UDP discovery timeout**
- Falls back to `localhost:18800` after 5s
- Use `--host` to skip discovery entirely

**"Unknown tool" errors**
- Bridge tools load dynamically. If Bridge was unavailable at startup, call `bridge__get_tools` to re-discover.

**Reconnection**
- Auto-reconnects with exponential backoff (1–30s)
- Keepalive pings every 15s detect stale connections
- Tool calls during disconnect return an error immediately (no silent hang)

**Auto-download fails**
- Check internet connectivity
- Try manual download (see [Manual Install](#manual-install))
- Corporate firewalls may block GitHub — use `BRIDGE_REPO` env var to point to a mirror

## Repository Structure

This repo (`CrabRaver`) is the **public** home for the Bridge MCP server.
It is consumed by the private `DigitRaver-3` repo as a git submodule at
`Tools/DigitRaverHelperMCP/`.

### For contributors

```bash
# Clone the parent repo with submodules
git clone --recurse-submodules https://github.com/thePostFuturist/DigitRaver-3.git

# Or initialize submodules in an existing clone
git submodule update --init --recursive

# To work on CrabRaver directly
cd Tools/DigitRaverHelperMCP
git checkout main
# make changes, commit, push to CrabRaver
# then cd ../.. and commit the submodule pointer update in DigitRaver-3
```

### Release process

Releases are created on **this repo** (CrabRaver). The `publish.sh` script
builds platform binaries and copies release artifacts to `bin/publish/release/`:

```
bin/publish/release/
├── install.sh                        (single-file installer)
├── SKILL.md                          (OpenClaw agent skill)
├── DigitRaverHelperMCP.exe           (Windows x64)
├── DigitRaverHelperMCP-osx-arm64     (macOS Apple Silicon)
└── DigitRaverHelperMCP-linux-x64     (Linux x64)
```

Upload all artifacts to a GitHub Release:

```bash
gh release create v1.0.0 bin/publish/release/* \
  -R thePostFuturist/CrabRaver \
  --title "Bridge MCP v1.0.0" \
  --notes "Initial release"
```

CrabRaver is **private**, but GitHub Releases are configured with
public download URLs — the `install.sh` one-liner works for anyone.
Contributors need repo access to clone or push.

## Further Reading

- [PLAN_MCP.md](https://github.com/thePostFuturist/DigitRaver-3/blob/master/Assets/_DigitRaver/Code/Bridge/PLAN_MCP.md) — Full design document, decision log, phase history *(private repo)*
- [Bridge ARCHITECTURE.md](https://github.com/thePostFuturist/DigitRaver-3/blob/master/Assets/_DigitRaver/Code/Bridge/ARCHITECTURE.md) — Unity-side Bridge module architecture *(private repo)*
