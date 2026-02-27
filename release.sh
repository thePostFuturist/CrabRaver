#!/bin/bash
# release.sh — Bump version, commit, tag, push, create GitHub Release.
#
# Usage:
#   ./release.sh patch       # 1.0.0 → 1.0.1
#   ./release.sh minor       # 1.0.0 → 1.1.0
#   ./release.sh major       # 1.0.0 → 2.0.0
#   ./release.sh 2.3.0       # explicit version
#   ./release.sh             # show current version

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
VERSION_FILE="$SCRIPT_DIR/VERSION"
REPO="thePostFuturist/CrabRaver"

CURRENT="$(cat "$VERSION_FILE" | tr -d '[:space:]')"
echo "Current version: $CURRENT"

if [[ $# -eq 0 ]]; then
  echo "Usage: ./release.sh <patch|minor|major|X.Y.Z>"
  exit 0
fi

BUMP="$1"
IFS='.' read -r V_MAJOR V_MINOR V_PATCH <<< "$CURRENT"

case "$BUMP" in
  patch)  NEW="$V_MAJOR.$V_MINOR.$((V_PATCH + 1))" ;;
  minor)  NEW="$V_MAJOR.$((V_MINOR + 1)).0" ;;
  major)  NEW="$((V_MAJOR + 1)).0.0" ;;
  [0-9]*) NEW="$BUMP" ;;
  *)      echo "Error: unknown bump type '$BUMP'"; exit 1 ;;
esac

TAG="v$NEW"
echo "New version:     $NEW  ($TAG)"

# Preflight
command -v gh &>/dev/null || { echo "Error: 'gh' CLI required"; exit 1; }
git rev-parse "$TAG" &>/dev/null && { echo "Error: tag '$TAG' exists"; exit 1; }

read -rp "Release $TAG? [y/N] " CONFIRM
[[ "$CONFIRM" =~ ^[Yy]$ ]] || { echo "Aborted."; exit 0; }

# Update VERSION + README URLs
echo -n "$NEW" > "$VERSION_FILE"
README="$SCRIPT_DIR/README.md"
[[ -f "$README" ]] && sed -i "s|/v${CURRENT}/|/v${NEW}/|g" "$README"

# Commit, tag, push
cd "$SCRIPT_DIR"
git add VERSION
[[ -f "$README" ]] && git add README.md
git commit -m "Bump version to $NEW"
git tag "$TAG"
git push && git push --tags

# Release notes — just quick-install one-liners
BASE="https://github.com/$REPO/releases/download/$TAG"
NOTES="## Install

**macOS / Linux:**
\`\`\`
curl -fsSL $BASE/install.sh | bash
\`\`\`

**Windows (PowerShell):**
\`\`\`
irm $BASE/install.ps1 | iex
\`\`\`

**Uninstall:**
\`\`\`
curl -fsSL $BASE/install.sh | bash -s -- --uninstall
\`\`\`"

echo ""
echo "Tag $TAG pushed. CI will build the release."
echo "Monitor: https://github.com/$REPO/actions"

# Wait for CI to create the release, then patch notes
echo "Waiting for release to appear..."
for i in $(seq 1 40); do
  if gh release view "$TAG" -R "$REPO" &>/dev/null; then
    echo "$NOTES" | gh release edit "$TAG" -R "$REPO" --notes-file -
    echo "Release notes updated."
    break
  fi
  sleep 5
done
