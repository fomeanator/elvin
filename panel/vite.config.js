import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import path from "node:path";
import { fileURLToPath } from "node:url";

// The Atelier is a standalone dev app; it talks to the running Go LVN server
// for the manifest, admin writes, and served content. We proxy those paths so
// the browser sees a single origin (no CORS, same as when the server hosts /panel).
const SERVER = process.env.LVN_SERVER || "http://127.0.0.1:8077";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "..");
// The shared LVNScript language core (grammar + analysis) lives outside the
// panel so the language server reuses the exact same brain.
const lvnLang = path.resolve(repoRoot, "tools/lvn-lang/src");

export default defineConfig({
  plugins: [react()],
  // Deploys behind a slow origin can push the heavy chunks (JS ~4MB, wasm
  // ~4MB) onto a pull-CDN: LVN_CDN_BASE=https://cdn.example.com/ makes the
  // built index.html load its assets from there while the app itself and the
  // API stay on the origin. Unset → same-origin (default, dev unchanged).
  base: process.env.LVN_CDN_BASE || "/",
  resolve: {
    alias: { "lvn-lang": lvnLang },
  },
  server: {
    port: 5173,
    proxy: {
      "/v1": SERVER,
      "/content": SERVER,
    },
    fs: { allow: [repoRoot] },
  },
  build: {
    // Build straight into the Go server's static dir so `Save to app` flows
    // can later host the compiled panel too if desired.
    outDir: "dist",
    emptyOutDir: true,
  },
});
