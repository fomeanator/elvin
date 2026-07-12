# Elvin Studio

The **optional** authoring workspace for the Elvin engine, as a standalone
React app (Vite). You do not need it to make games: the base workflow is a
text editor (or an AI agent — see the repo's `llms.txt` and the MCP server)
writing `.lvns`, validated by `lvnconv`, dropped into Unity. Studio is the
pro tool on top:

- **Library / Chapter** — write `.lvns` in a Monaco editor with the real
  compiler behind it (live diagnostics, completion, ghost text — the same
  `lvnconv` pipeline compiled to WASM), preview, and *Save to app* to push it
  to the running game.
- **Sprites** — a guided manager for entities (characters, backgrounds,
  props): pick a template, add poses/emotions from presets, drop art into
  per-state upload slots, preview the composite, upload Spine characters.
  Saves the catalog into the manifest.
- **Admin** — the operations dashboard for the product backend (users,
  grants, orders, saves, analytics, economy manifest).

## Run

Studio talks to the Go server for the manifest, admin writes and served
content. Start the server **with the studio surface enabled**, then the app:

```sh
# 1. the server (from repo root) — -studio also serves the built app at /
scripts/fetch-demo-content.sh   # demo content lives in its own repo
go run ./server -content ./server/content -addr :8077 -admin-token devtoken -studio

# 2. Studio, dev mode
cd panel
npm install
npm run dev        # http://localhost:5173
```

Vite proxies `/v1` and `/content` to `http://127.0.0.1:8077` (override with
`LVN_SERVER`). Enter the admin token (`devtoken` above) in the top bar to
enable saving and uploads.

## Build & deploy

`npm run build` emits a static bundle into `panel/dist` (the WASM +
`wasm_exec.js` live in `public/` and are copied verbatim). The deploy script
copies it into `server/website`, which the Go server hosts at `/` — only when
started with `-studio`; a base server is a pure game API (content, saves,
product services) and serves no authoring UI.

The public playground and docs site (`panel/public/play`, `panel/public/docs`)
are deployed to GitHub Pages separately — they are the language's shop window,
not part of Studio.
