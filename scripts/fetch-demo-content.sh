#!/usr/bin/env bash
# Fetch the demo content (the Sovet novel + showcase assets) into
# server/content. The content lives in its own repo so the engine repo stays
# code-sized; nothing in the engine needs it — dev servers and the release-APK
# pipeline do.
#
#   scripts/fetch-demo-content.sh          # clone or update server/content
set -euo pipefail
cd "$(dirname "$0")/.."
DEST=server/content
REPO=https://github.com/fomeanator/lvn-demo-content.git

if [ -d "$DEST/.git" ]; then
  echo "updating $DEST…"
  git -C "$DEST" pull --ff-only
elif [ -e "$DEST/manifest.json" ]; then
  # A pre-split working copy (or a hand-managed content dir): leave it alone.
  echo "$DEST already has content (no .git) — leaving it untouched."
else
  echo "cloning demo content into $DEST…"
  rm -rf "$DEST"
  git clone --depth 1 "$REPO" "$DEST"
fi
echo "ok: $(du -sh "$DEST" | cut -f1) in $DEST"
