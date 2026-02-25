#!/usr/bin/env bash
# Cross-platform launcher for Bridge MCP server.
# Auto-detects OS, finds or downloads the correct binary, runs it.
#
# Binary search order:
#   1. Project-local:  {script_dir}/bin/publish/{rid}/
#   2. User-global:    ~/.digitraver/mcp/bridge/{version}/{rid}/
#   3. Auto-download from GitHub Releases → saved to user-global cache
#   4. Fallback: dotnet run (dev only, requires .NET SDK)
#
# Environment variables:
#   BRIDGE_DEBUG=1    — inject --verbose flag for debug logging
#   BRIDGE_VERSION    — override version (default: built-in)
#   BRIDGE_REPO       — override GitHub repo for downloads

set -euo pipefail

# ── Configuration ──────────────────────────────────────────────────────
VERSION="${BRIDGE_VERSION:-1.0.0}"
REPO="${BRIDGE_REPO:-thePostFuturist/CrabRaver}"
BINARY_NAME="DigitRaverHelperMCP"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# ── Detect platform → RID ─────────────────────────────────────────────
detect_rid() {
  local os arch
  os="$(uname -s)"
  arch="$(uname -m)"

  case "$os" in
    Darwin)
      case "$arch" in
        arm64) echo "osx-arm64" ;;
        *)     echo "osx-x64"   ;;
      esac
      ;;
    Linux)
      case "$arch" in
        aarch64) echo "linux-arm64" ;;
        *)       echo "linux-x64"   ;;
      esac
      ;;
    MINGW*|MSYS*|CYGWIN*|Windows_NT)
      echo "win-x64"
      ;;
    *)
      echo >&2 "[bridge-mcp] Unsupported OS: $os"
      exit 1
      ;;
  esac
}

RID="$(detect_rid)"

# Binary name has .exe suffix on Windows
EXE="$BINARY_NAME"
if [[ "$RID" == win-* ]]; then
  EXE="${BINARY_NAME}.exe"
fi

# ── Search for binary ─────────────────────────────────────────────────
LOCAL_DIR="$SCRIPT_DIR/bin/publish/$RID"
CACHE_DIR="$HOME/.digitraver/mcp/bridge/$VERSION/$RID"
BINARY=""

if [[ -x "$LOCAL_DIR/$EXE" ]]; then
  BINARY="$LOCAL_DIR/$EXE"
  echo >&2 "[bridge-mcp] Using local build: $BINARY"
elif [[ -x "$CACHE_DIR/$EXE" ]]; then
  BINARY="$CACHE_DIR/$EXE"
  echo >&2 "[bridge-mcp] Using cached binary: $BINARY"
fi

# ── Map RID → release asset name ─────────────────────────────────────
release_asset_name() {
  case "$RID" in
    win-x64)     echo "${BINARY_NAME}.exe"           ;;
    osx-arm64)   echo "${BINARY_NAME}-osx-arm64"     ;;
    osx-x64)     echo "${BINARY_NAME}-osx-x64"       ;;
    linux-x64)   echo "${BINARY_NAME}-linux-x64"     ;;
    linux-arm64)  echo "${BINARY_NAME}-linux-arm64"   ;;
    *)           echo "${BINARY_NAME}-${RID}"         ;;
  esac
}

# ── Download if not found ─────────────────────────────────────────────
if [[ -z "$BINARY" ]]; then
  RELEASE_ASSET="$(release_asset_name)"
  URL="https://github.com/${REPO}/releases/download/v${VERSION}/${RELEASE_ASSET}"

  echo >&2 "[bridge-mcp] Binary not found locally. Downloading v${VERSION} for ${RID}..."
  echo >&2 "[bridge-mcp] URL: $URL"

  mkdir -p "$CACHE_DIR"

  if command -v curl >/dev/null 2>&1; then
    if curl -fSL --progress-bar -o "$CACHE_DIR/$EXE" "$URL" 2>&1 | \
       while IFS= read -r line; do echo >&2 "$line"; done; then

      # Ensure executable on non-Windows
      if [[ "$RID" != win-* ]]; then
        chmod +x "$CACHE_DIR/$EXE"
      fi

      BINARY="$CACHE_DIR/$EXE"
      echo >&2 "[bridge-mcp] Installed to: $CACHE_DIR/$EXE"
    else
      echo >&2 "[bridge-mcp] Download failed."
      rm -f "$CACHE_DIR/$EXE"
    fi
  else
    echo >&2 "[bridge-mcp] curl not found, cannot download binary."
  fi
fi

# ── Fallback: dotnet run (dev only) ───────────────────────────────────
if [[ -z "$BINARY" ]]; then
  if command -v dotnet >/dev/null 2>&1; then
    echo >&2 "[bridge-mcp] Falling back to 'dotnet run' (requires .NET 8 SDK)"
    ARGS=("$@")
    if [[ "${BRIDGE_DEBUG:-0}" == "1" ]]; then
      ARGS=("--verbose" "${ARGS[@]}")
    fi
    exec dotnet run --project "$SCRIPT_DIR" -- "${ARGS[@]}"
  else
    echo >&2 "[bridge-mcp] ERROR: No binary found and .NET SDK not available."
    echo >&2 "[bridge-mcp] Install from: https://github.com/${REPO}/releases/tag/v${VERSION}"
    echo >&2 "[bridge-mcp] Or install .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
  fi
fi

# ── Build args and exec ──────────────────────────────────────────────
ARGS=("$@")
if [[ "${BRIDGE_DEBUG:-0}" == "1" ]]; then
  ARGS=("--verbose" "${ARGS[@]}")
fi

exec "$BINARY" "${ARGS[@]}"
