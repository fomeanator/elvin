#!/usr/bin/env bash
# Flatten howto/ and docs/ markdown into content/ with prefixed names
# (howto/quiz/README.md → howto-quiz-README.md), matching pages.js.
set -e
root="${1:-.}"
out="${2:-panel/public/docs/content}"
mkdir -p "$out"
rm -f "$out"/*.md
for f in "$root"/howto/*.md; do cp "$f" "$out/howto-$(basename "$f")"; done
for d in "$root"/howto/*/README.md; do
  [ -f "$d" ] || continue
  genre=$(basename "$(dirname "$d")")
  cp "$d" "$out/howto-$genre-README.md"
done
for f in "$root"/docs/*.md; do cp "$f" "$out/docs-$(basename "$f")"; done
echo "content: $(ls "$out" | wc -l | tr -d ' ') files"
