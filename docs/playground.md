# The browser playground (`/play/`)

Write Elvin Script on the left — it plays on the right. Zero install, zero
tokens: the page ships with the **real** `lvnconv` compiler (wasm build, the
same binary source the CLI and IDE use) and a faithful JS port of the story
interpreter. Served by the content server at `/play/`; the canonical sources
live in `panel/public/play/` (the panel deploy wipes `server/website`, so
anything hand-placed there dies — `public/` survives every deploy).

## What it covers

| Layer | Coverage |
|---|---|
| Story core | `say`, choices (expr/stat filters, costs, **timeouts** with a countdown bar), **text input**, `if`/`set`/`inc`, `goto`/`call`/`return`, `wait`, lists & the expression built-ins, `{interpolation}` |
| Staging | `bg` and `actor`/`obj` as images, reactive `text` HUD labels, `fade`/`dim`/`tint` as a CSS veil, one looping `audio` music channel |
| Not here | bones/springs, Spine, particles, camera, drag & drop, saves — the full staging is the Unity runtime's job (the page says so under the stage) |

## Sharing

- **🔗 Share** packs the script into the URL hash (base64). Anyone
  opening the link plays it immediately — no account, no server round-trip.
- **⬇ HTML** exports ONE self-contained file: the compiled `.lvn`, the
  interpreter, a lean renderer — and **the art, inlined as data URLs**
  (backgrounds, sprites and the catalog layers your script actually uses),
  so the file plays fully offline from `file://`. Unreachable art degrades
  to a text-only story instead of failing.
- **⬇ .lvn** downloads the compiled container for the Unity runtime.

## Saves

Every pause autosaves `{player snapshot, staged bg/actors/HUD}` to
`localStorage` (a key per scene name; the exported HTML uses its title).
Reopening offers **Continue / Start over**; finishing the story clears the
save. A shared link or exported file is playable across sittings.

## Services from the browser

Same-origin with the content server, the playground speaks to the product
services: a device account is minted on demand (localStorage), and the
script's `ext` ops just work — `ext track name=…` posts analytics,
`ext leaderboard_submit board=… score_var=…` lands on a real board and
shows the resulting rank (plus the board's top). A shared quiz link is a
real competition. Exported HTML files skip this (no server on `file://`).

## For maintainers

- Interpreter: `core.js` (pure, DOM-free) + `expr.js` (recursive-descent
  evaluator). Node tests ride the lang CI job:
  `tools/lvn-lang/test/playground.test.js`.
- Renderer/glue: `app.js`; syntax highlighting: `highlight.js` (a painted
  `<pre>` behind a transparent textarea — no editor dependency).
- The one-file exporter: `export.js` (fetches core/expr, strips module
  syntax, inlines into a template with its own lean renderer).
- Drafts autosave to `localStorage` (`lvn-play-draft`); a `#s=` URL wins.
