#!/usr/bin/env bash
# DigitRaver Bridge MCP — Single-file installer (configures mcporter).
#
# Usage:
#   curl -fsSL https://github.com/REPO/releases/download/vVERSION/install.sh | bash
#   bash install.sh                  # install (default)
#   bash install.sh --uninstall      # remove everything
#
# Environment variables:
#   GITHUB_TOKEN    — for private repo downloads (Authorization: token <TOKEN>)
#   BRIDGE_VERSION  — override version (default: built-in)
#   BRIDGE_REPO     — override GitHub repo (default: thePostFuturist/CrabRaver)

set -euo pipefail

# ── Configuration ──────────────────────────────────────────────────────
INSTALL_SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
VERSION="${BRIDGE_VERSION:-$(cat "$INSTALL_SCRIPT_DIR/VERSION" 2>/dev/null || echo 1.0.0)}"
REPO="${BRIDGE_REPO:-thePostFuturist/CrabRaver}"
BINARY_NAME="DigitRaverHelperMCP"
SKILL_NAME="digitraver-agent"

BRIDGE_DIR="$HOME/.digitraver/mcp/bridge"
OPENCLAW_DIR="$HOME/.openclaw"
SKILL_DIR="$OPENCLAW_DIR/skills/$SKILL_NAME"
MCPORTER_DIR="$HOME/.mcporter"
MCPORTER_CONFIG="$MCPORTER_DIR/mcporter.json"

# ── Helpers ────────────────────────────────────────────────────────────
info()  { echo >&2 "[install] $*"; }
error() { echo >&2 "[install] ERROR: $*"; }
die()   { error "$*"; exit 1; }

# ── Detect platform → binary asset name ───────────────────────────────
detect_platform() {
  local os arch
  os="$(uname -s)"
  arch="$(uname -m)"

  case "$os" in
    Darwin)
      case "$arch" in
        arm64) RID="osx-arm64";   ASSET="${BINARY_NAME}-osx-arm64" ;;
        *)     RID="osx-x64";     ASSET="${BINARY_NAME}-osx-x64"   ;;
      esac
      ;;
    Linux)
      case "$arch" in
        aarch64) RID="linux-arm64"; ASSET="${BINARY_NAME}-linux-arm64" ;;
        *)       RID="linux-x64";   ASSET="${BINARY_NAME}-linux-x64"   ;;
      esac
      ;;
    MINGW*|MSYS*|CYGWIN*|Windows_NT)
      RID="win-x64"
      ASSET="${BINARY_NAME}.exe"
      ;;
    *)
      die "Unsupported OS: $os"
      ;;
  esac
}

# ── Download a GitHub Release asset ───────────────────────────────────
download_asset() {
  local asset_name="$1" dest="$2"
  local url="https://github.com/${REPO}/releases/download/v${VERSION}/${asset_name}"

  local curl_args=(-fSL --progress-bar -o "$dest")

  # Private repo: use token if provided
  if [[ -n "${GITHUB_TOKEN:-}" ]]; then
    curl_args+=(-H "Authorization: token ${GITHUB_TOKEN}")
    # GitHub Release assets need Accept header for private repos
    curl_args+=(-H "Accept: application/octet-stream")
  fi

  info "Downloading: $asset_name"
  if ! curl "${curl_args[@]}" "$url" 2>&1 | while IFS= read -r line; do echo >&2 "  $line"; done; then
    rm -f "$dest"
    echo ""
    error "Download failed: $url"
    if [[ -z "${GITHUB_TOKEN:-}" ]]; then
      info "If this is a private repo, set GITHUB_TOKEN:"
      info "  GITHUB_TOKEN=ghp_xxx bash install.sh"
    fi
    return 1
  fi
}

# ── Uninstall ─────────────────────────────────────────────────────────
do_uninstall() {
  info "Uninstalling DigitRaver Bridge MCP..."

  # Remove binary
  local bin_dir="$BRIDGE_DIR/$VERSION"
  if [[ -d "$bin_dir" ]]; then
    rm -rf "$bin_dir"
    info "Removed: $bin_dir"
  fi
  # Remove empty parent dirs
  rmdir "$BRIDGE_DIR" 2>/dev/null || true
  rmdir "$HOME/.digitraver/mcp" 2>/dev/null || true
  rmdir "$HOME/.digitraver" 2>/dev/null || true

  # Remove skill
  if [[ -d "$SKILL_DIR" ]]; then
    rm -rf "$SKILL_DIR"
    info "Removed: $SKILL_DIR"
  fi

  # Remove server entry from mcporter.json
  if [[ -f "$MCPORTER_CONFIG" ]] && command -v python3 >/dev/null 2>&1; then
    python3 -c "
import json, sys
try:
    with open('$MCPORTER_CONFIG', 'r') as f:
        cfg = json.load(f)
    servers = cfg.get('mcpServers', {})
    if 'digitraver-bridge' in servers:
        del servers['digitraver-bridge']
        with open('$MCPORTER_CONFIG', 'w') as f:
            json.dump(cfg, f, indent=2)
        print('[install] Removed digitraver-bridge from mcporter.json', file=sys.stderr)
    else:
        print('[install] digitraver-bridge not found in mcporter.json', file=sys.stderr)
except Exception as e:
    print(f'[install] Could not patch mcporter.json: {e}', file=sys.stderr)
"
  fi

  info "Uninstall complete."
}

# ── Install ───────────────────────────────────────────────────────────
do_install() {
  detect_platform

  info "Installing DigitRaver Bridge MCP v${VERSION}"
  info "  Platform:  ${RID}"
  info "  Binary:    ${ASSET}"
  info ""

  # ── 1. Download binary ──────────────────────────────────────────────
  local bin_dir="$BRIDGE_DIR/$RID"
  mkdir -p "$bin_dir"

  # Local binary name is always DigitRaverHelperMCP (or .exe on Windows)
  local local_exe="$BINARY_NAME"
  if [[ "$RID" == win-* ]]; then
    local_exe="${BINARY_NAME}.exe"
  fi

  download_asset "$ASSET" "$bin_dir/$local_exe" || exit 1

  # Make executable on non-Windows
  if [[ "$RID" != win-* ]]; then
    chmod +x "$bin_dir/$local_exe"
  fi
  info "Binary installed: $bin_dir/$local_exe"

  # ── 2. Download SKILL.md ────────────────────────────────────────────
  mkdir -p "$SKILL_DIR"
  download_asset "SKILL.md" "$SKILL_DIR/SKILL.md" || exit 1
  info "Skill installed: $SKILL_DIR/SKILL.md"

  # ── 3. Configure mcporter ──────────────────────────────────────────
  local binary_path="$bin_dir/$local_exe"

  mkdir -p "$MCPORTER_DIR"

  if command -v python3 >/dev/null 2>&1; then
    python3 -c "
import json, os, sys

config_path = '$MCPORTER_CONFIG'
binary_path = '$binary_path'

# Load or create config
if os.path.exists(config_path):
    with open(config_path, 'r') as f:
        cfg = json.load(f)
else:
    cfg = {}

# Ensure mcpServers key
cfg.setdefault('mcpServers', {})

# Upsert digitraver-bridge
cfg['mcpServers']['digitraver-bridge'] = {
    'command': binary_path,
    'args': []
}

with open(config_path, 'w') as f:
    json.dump(cfg, f, indent=2)

print(f'[install] mcporter config updated: {config_path}', file=sys.stderr)
"
  else
    info "Warning: python3 not found. Please add the bridge server to $MCPORTER_CONFIG manually:"
    info '  {"mcpServers":{"digitraver-bridge":{"command":"'"$binary_path"'","args":[]}}}'
  fi

  # ── 4. Print success ───────────────────────────────────────────────
  echo ""
  echo "=================================================="
  echo "  DigitRaver Bridge MCP — Installed!"
  echo "=================================================="
  echo ""
  echo "  Binary:  $bin_dir/$local_exe"
  echo "  Skill:   $SKILL_DIR/SKILL.md"
  echo "  Config:  $MCPORTER_CONFIG"
  echo ""
  echo "  Verify:  mcporter config list"
  echo ""
  echo "  Next steps:"
  echo "    1. Make sure the DigitRaver binary is running with Bridge active"
  echo "    2. Use the agent: /digitraver-agent"
  echo ""
  echo "  To uninstall:"
  echo "    bash install.sh --uninstall"
  echo ""
}

# ── Main ──────────────────────────────────────────────────────────────
case "${1:-}" in
  --uninstall|-u)
    do_uninstall
    ;;
  --help|-h)
    echo "Usage: install.sh [--uninstall] [--help]"
    echo ""
    echo "Installs DigitRaver Bridge MCP server and configures mcporter."
    echo ""
    echo "Options:"
    echo "  --uninstall   Remove binary, skill, and config entry"
    echo "  --help        Show this help"
    echo ""
    echo "Environment:"
    echo "  GITHUB_TOKEN     Auth token for private repo downloads"
    echo "  BRIDGE_VERSION   Override version (default: $VERSION)"
    echo "  BRIDGE_REPO      Override GitHub repo (default: $REPO)"
    ;;
  ""|--install)
    do_install
    ;;
  *)
    die "Unknown option: $1 (use --help for usage)"
    ;;
esac
