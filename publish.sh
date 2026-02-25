#!/bin/bash
# Publish DigitRaverHelperMCP as self-contained single-file executables.
#
# Usage:
#   ./publish.sh              # Build ALL platforms + copy binaries to release/
#   ./publish.sh win-x64      # Build single platform only
#   ./publish.sh osx-arm64    # macOS Apple Silicon only
#
# Output:
#   bin/publish/{rid}/          — Executable per platform
#   bin/publish/release/        — Release binaries for GitHub upload

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ALL_RIDS=("win-x64" "osx-arm64" "osx-x64" "linux-x64" "linux-arm64")
RELEASE_DIR="$SCRIPT_DIR/bin/publish/release"
BINARY_NAME="DigitRaverHelperMCP"

# ── Determine targets ─────────────────────────────────────────────────
if [[ $# -gt 0 ]]; then
  RIDS=("$1")
else
  RIDS=("${ALL_RIDS[@]}")
fi

echo "═══════════════════════════════════════════════════"
echo "  DigitRaverHelperMCP — Publish"
echo "  Targets: ${RIDS[*]}"
echo "═══════════════════════════════════════════════════"
echo ""

mkdir -p "$RELEASE_DIR"

# ── Map RID → release asset name ─────────────────────────────────────
release_asset_name() {
  local rid="$1"
  case "$rid" in
    win-x64)    echo "${BINARY_NAME}.exe"           ;;
    osx-arm64)  echo "${BINARY_NAME}-osx-arm64"     ;;
    osx-x64)    echo "${BINARY_NAME}-osx-x64"       ;;
    linux-x64)  echo "${BINARY_NAME}-linux-x64"     ;;
    linux-arm64) echo "${BINARY_NAME}-linux-arm64"   ;;
    *)          echo "${BINARY_NAME}-${rid}"         ;;
  esac
}

# ── Build each target ─────────────────────────────────────────────────
for RID in "${RIDS[@]}"; do
  OUT_DIR="$SCRIPT_DIR/bin/publish/$RID"

  echo "──────────────────────────────────────────────────"
  echo "  Building: $RID"
  echo "──────────────────────────────────────────────────"

  dotnet publish "$SCRIPT_DIR/DigitRaverHelperMCP.csproj" \
    -c Release \
    -r "$RID" \
    -o "$OUT_DIR"

  # Determine local executable name (what dotnet produces)
  EXE="$BINARY_NAME"
  if [[ "$RID" == win-* ]]; then
    EXE="${BINARY_NAME}.exe"
  fi

  # Copy binary to release folder with platform-specific name
  RELEASE_ASSET="$(release_asset_name "$RID")"
  cp "$OUT_DIR/$EXE" "$RELEASE_DIR/$RELEASE_ASSET"
  echo "  Copied to release: $RELEASE_ASSET"

  echo "  Done: $RID"
  echo ""
done

# ── Copy installer and skill to release ──────────────────────────────
DIST_DIR="$SCRIPT_DIR/dist"
if [[ -f "$DIST_DIR/install.sh" ]]; then
  cp "$DIST_DIR/install.sh" "$RELEASE_DIR/install.sh"
  echo "  Copied to release: install.sh"
fi
if [[ -f "$DIST_DIR/SKILL.md" ]]; then
  cp "$DIST_DIR/SKILL.md" "$RELEASE_DIR/SKILL.md"
  echo "  Copied to release: SKILL.md"
fi
echo ""

# ── Summary ───────────────────────────────────────────────────────────
echo "═══════════════════════════════════════════════════"
echo "  Build Summary"
echo "═══════════════════════════════════════════════════"
echo ""

for RID in "${RIDS[@]}"; do
  RELEASE_ASSET="$(release_asset_name "$RID")"
  ASSET_PATH="$RELEASE_DIR/$RELEASE_ASSET"

  if [[ -f "$ASSET_PATH" ]]; then
    BIN_SIZE=$(ls -lh "$ASSET_PATH" | awk '{print $5}')
    printf "  %-35s  %s\n" "$RELEASE_ASSET" "$BIN_SIZE"
  fi
done

# Show installer + skill
for EXTRA in install.sh SKILL.md; do
  if [[ -f "$RELEASE_DIR/$EXTRA" ]]; then
    EXTRA_SIZE=$(ls -lh "$RELEASE_DIR/$EXTRA" | awk '{print $5}')
    printf "  %-35s  %s\n" "$EXTRA" "$EXTRA_SIZE"
  fi
done

echo ""
echo "  Binaries:  $SCRIPT_DIR/bin/publish/{rid}/"
echo "  Release:   $RELEASE_DIR/"
echo ""
echo "  To create a GitHub Release:"
echo "    gh release create v1.0.0 $RELEASE_DIR/* --title 'Bridge MCP v1.0.0' --notes 'Initial release'"
echo ""
