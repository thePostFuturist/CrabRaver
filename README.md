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

**Just clone and go** — the `.mcp.json` at the repo root is pre-configured:

```json
{
  "mcpServers": {
    "bridge": {
      "type": "stdio",
      "command": "node",
      "args": ["Tools/DigitRaverHelperMCP/bridge-launcher.mjs"]
    }
  }
}
```

The launcher (`bridge-launcher.mjs`) works on **Windows, macOS, and Linux** using only Node.js built-in modules. Node.js is guaranteed on PATH wherever Claude Code runs.

On first launch it:
1. Detects your OS and architecture → maps to a .NET Runtime Identifier (RID)
2. Looks for a local build first (for developers who run `publish.sh`)
3. Downloads the correct pre-built binary from GitHub Releases if needed
4. Caches it in `~/.digitraver/mcp/bridge/{version}/{rid}/` (persists across re-clones)
5. Spawns the MCP server with stdio inherited

### Binary Search Order

The launcher checks for the server binary in this order:

1. **Project-local**: `bin/publish/{rid}/` — output from `publish.sh`
2. **User cache**: `~/.digitraver/mcp/bridge/{version}/{rid}/` — auto-downloaded binaries
3. **Auto-download**: fetches from GitHub Releases → saved to user cache
4. **Fallback**: `dotnet run --project .` (requires .NET 8 SDK)

### Supported Platforms

| RID | OS | Architecture |
|-----|----|-------------|
| `win-x64` | Windows | x86-64 |
| `osx-arm64` | macOS | Apple Silicon |
| `osx-x64` | macOS | Intel |
| `linux-x64` | Linux | x86-64 |
| `linux-arm64` | Linux | ARM64 |

### Launcher Files

| File | Purpose |
|------|---------|
| `bridge-launcher.mjs` | **Primary** — cross-platform Node.js launcher (used by `.mcp.json`) |
| `bridge-mcp.sh` | Bash launcher (macOS/Linux alternative) |
| `bridge-mcp.cmd` | Windows batch launcher (legacy) |
| `VERSION` | Single source of truth for the version string — read by all launchers |

## Configuration

### Debug Mode

Set the `BRIDGE_DEBUG` environment variable for verbose logging:

```json
{
  "mcpServers": {
    "bridge": {
      "type": "stdio",
      "command": "node",
      "args": ["Tools/DigitRaverHelperMCP/bridge-launcher.mjs"],
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
| `BRIDGE_VERSION` | *(from VERSION file)* | Override the version to download |
| `BRIDGE_REPO` | `thePostFuturist/CrabRaver` | Override the GitHub repo for downloads |

### CLI Arguments

These are passed to the MCP server binary (not the launcher):

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
   - `DigitRaverHelperMCP-osx-x64` (macOS Intel)
   - `DigitRaverHelperMCP-linux-x64` (Linux x64)
   - `DigitRaverHelperMCP-linux-arm64` (Linux ARM64)
3. Place in `~/.digitraver/mcp/bridge/{version}/{rid}/` and rename to `DigitRaverHelperMCP` (or `.exe` on Windows):

```bash
# Example: macOS Apple Silicon, version 1.0.0
mkdir -p ~/.digitraver/mcp/bridge/1.0.0/osx-arm64
mv DigitRaverHelperMCP-osx-arm64 ~/.digitraver/mcp/bridge/1.0.0/osx-arm64/DigitRaverHelperMCP
chmod +x ~/.digitraver/mcp/bridge/1.0.0/osx-arm64/DigitRaverHelperMCP
```

## Development

### Dev mode (requires .NET 8 SDK)

```bash
dotnet run --project Tools/DigitRaverHelperMCP
```

The launcher falls back to `dotnet run` automatically if no binary is found and .NET SDK is available.

### Publishing locally

```bash
# Build all 5 platforms
./Tools/DigitRaverHelperMCP/publish.sh

# Build a single platform
./Tools/DigitRaverHelperMCP/publish.sh win-x64
```

Output:
- `bin/publish/{rid}/` — executables (used first by the launcher)
- `bin/publish/release/` — binaries renamed for GitHub Release upload

### Versioning

The `VERSION` file is the single source of truth. All launchers read it:
- `bridge-launcher.mjs` — `readFileSync("VERSION")`
- `bridge-mcp.sh` — `cat "$SCRIPT_DIR/VERSION"`
- `bridge-mcp.cmd` — `set /p VERSION=<"%SCRIPT_DIR%VERSION"`
- `dist/install.sh` — `cat "$INSTALL_SCRIPT_DIR/VERSION"`

Override at runtime with `BRIDGE_VERSION` env var.

### .csproj Notes

The `<RuntimeIdentifier>` is **not** set in the `.csproj`. The RID is supplied by the `-r` flag when publishing:

```bash
dotnet publish -c Release -r osx-arm64
```

This allows `dotnet run` (dev fallback) to work as framework-dependent without a hardcoded RID.

### Release Process

#### Automated (CI)

Push a `v*` tag to trigger the GitHub Actions workflow:

```bash
# 1. Update VERSION file
echo "1.1.0" > Tools/DigitRaverHelperMCP/VERSION

# 2. Commit and tag
git add Tools/DigitRaverHelperMCP/VERSION
git commit -m "Bump version to 1.1.0"
git tag v1.1.0
git push && git push --tags
```

The workflow (`.github/workflows/release.yml`):
1. Builds all 5 platforms via matrix on `ubuntu-latest`
2. Renames binaries to release asset names
3. Creates a GitHub Release with all binaries + `install.sh` + `SKILL.md`

#### Manual

```bash
# 1. Cross-compile all platforms
./Tools/DigitRaverHelperMCP/publish.sh

# 2. Verify release folder
ls bin/publish/release/
# DigitRaverHelperMCP.exe          (win-x64)
# DigitRaverHelperMCP-osx-arm64    (macOS Apple Silicon)
# DigitRaverHelperMCP-osx-x64      (macOS Intel)
# DigitRaverHelperMCP-linux-x64    (Linux x64)
# DigitRaverHelperMCP-linux-arm64  (Linux ARM64)
# install.sh
# SKILL.md

# 3. Create GitHub Release
gh release create v1.0.0 bin/publish/release/* \
  -R thePostFuturist/CrabRaver \
  --title "Bridge MCP v1.0.0" \
  --notes "Initial release"
```

## Architecture

Four C# source files plus cross-platform launcher scripts:

| File | Role |
|------|------|
| `Program.cs` | Entry point — CLI parsing, DI setup, MCP server wiring, startup orchestration |
| `BridgeWebSocketClient.cs` | Persistent WebSocket connection — send/receive, auto-reconnect, keepalive, event buffering |
| `BridgeToolRegistry.cs` | Dynamic tool registry — loads schemas from `bridge.get_tools`, dispatches calls, local compound tools |
| `BridgeDiscovery.cs` | UDP auto-discovery — finds Bridge server on LAN without hardcoded host |
| `bridge-launcher.mjs` | Cross-platform Node.js launcher — RID detection, binary resolution, auto-download |
| `VERSION` | Version string — single source of truth for all launchers and CI |

### Startup Sequence

```
1. node bridge-launcher.mjs
   ├── detect RID (platform + arch)
   ├── find binary (local → cache → download → dotnet run)
   └── spawn DigitRaverHelperMCP

2. DigitRaverHelperMCP (MCP server)
   ├── UDP discovery (find Bridge host, or --host/--no-discovery)
   ├── WebSocket connect to ws://{host}:{port}
   ├── bridge.get_tools → register all Bridge tools + local compound tools
   └── MCP stdio transport ready (tools/list, tools/call)
```

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

### Local tools (9)

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
- Bridge tools load dynamically. If Bridge was unavailable at startup, call `bridge__bridge_get_tools` to re-discover.

**Reconnection**
- Auto-reconnects with exponential backoff (1–30s)
- Keepalive pings every 15s detect stale connections
- Tool calls during disconnect return an error immediately (no silent hang)

**Auto-download fails**
- Check internet connectivity
- Try manual download (see [Manual Install](#manual-install))
- Corporate firewalls may block GitHub — use `BRIDGE_REPO` env var to point to a mirror

**Launcher doesn't start**
- Verify Node.js is on PATH: `node --version`
- Check syntax: `node --check Tools/DigitRaverHelperMCP/bridge-launcher.mjs`
- Enable diagnostics: `BRIDGE_DEBUG=1 node Tools/DigitRaverHelperMCP/bridge-launcher.mjs`

## Repository Structure

This repo (`CrabRaver`) is the **public** home for the Bridge MCP server. It is consumed by the private `DigitRaver-3` repo as a git submodule at `Tools/DigitRaverHelperMCP/`.

```
Tools/DigitRaverHelperMCP/
├── Program.cs                    # MCP server entry point
├── BridgeWebSocketClient.cs      # WebSocket client with reconnect
├── BridgeToolRegistry.cs         # Dynamic tool registry
├── BridgeDiscovery.cs            # UDP auto-discovery
├── bridge-launcher.mjs           # Cross-platform Node.js launcher
├── bridge-mcp.sh                 # Bash launcher (macOS/Linux)
├── bridge-mcp.cmd                # Windows batch launcher (legacy)
├── VERSION                       # Version single source of truth
├── DigitRaverHelperMCP.csproj    # .NET 8 project
├── .mcp.json                     # MCP config (for standalone use)
├── .gitattributes                # LF line endings for .sh and .mjs
├── .github/
│   └── workflows/
│       └── release.yml           # CI: build 5 platforms + create release
├── dist/
│   ├── install.sh                # One-line installer for OpenClaw
│   └── SKILL.md                  # Agent skill definition
└── publish.sh                    # Local cross-compile script
```

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

## Further Reading

- [Bridge ARCHITECTURE.md](https://github.com/thePostFuturist/DigitRaver-3/blob/master/Assets/_DigitRaver/Code/Bridge/ARCHITECTURE.md) — Unity-side Bridge module architecture *(private repo)*
- [USAGE.md](https://github.com/thePostFuturist/DigitRaver-3/blob/master/Assets/_DigitRaver/Code/Bridge/USAGE.md) — Tool reference and usage patterns *(private repo)*
- [PLAN_MCP.md](https://github.com/thePostFuturist/DigitRaver-3/blob/master/Assets/_DigitRaver/Code/Bridge/PLAN_MCP.md) — Design document and decision log *(private repo)*
