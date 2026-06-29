# LVN Atelier

The authoring panel for the LVN visual-novel engine, as a standalone React app
(Vite). It replaces the static `server/website` panel with a designed, two-mode
workspace:

- **Chapter** — write `.lvns`, see it compiled to engine `.lvn` live (the same
  `lvnconv` pipeline compiled to WASM), and *Save to app* to push it to the
  running game.
- **Sprites** — a guided manager for entities (characters, backgrounds, props):
  pick a template, add poses/emotions from presets, drop art into per-state
  upload slots, and preview the composite. Saves the catalog into the manifest.

## Run

The panel talks to the Go LVN server for the manifest, admin writes, and served
content. Start the server first, then the panel:

```sh
# 1. the engine server (from repo root)
go run ./server -content ./server/content -addr :8077 -admin-token devtoken

# 2. the panel
cd panel
npm install
npm run dev        # http://localhost:5173
```

Vite proxies `/v1` and `/content` to `http://127.0.0.1:8077` (override with
`LVN_SERVER`). Enter the admin token (`devtoken` above) in the top bar to enable
saving and uploads.

## Build

`npm run build` emits a static bundle into `panel/dist` (the WASM + `wasm_exec.js`
live in `public/` and are copied verbatim). The Go server can host that bundle in
place of the old `/panel` directory.
