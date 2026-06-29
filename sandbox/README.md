# LVN Engine — Sandbox

A clean, minimal Unity project whose only job is to **run the engine**
(`com.lvn.engine`) against the local authoring panel, so you iterate on chapters
without touching any product project.

It consumes the engine as a **local `file:` package** (see
`Packages/manifest.json` → `../../unity/Packages/com.lvn.engine`), so edits to the
engine show up here immediately — no rsync, no copying.

## Run

1. Start the panel's content server (from the repo root):
   ```sh
   go run ./server -content ./server/content -addr :8077 -admin-token devtoken
   ```
   (and the authoring panel, if you want to edit chapters: `cd panel && npm run dev`)

2. Open **this folder** (`sandbox/`) in Unity Hub → it imports the engine +
   Newtonsoft and compiles.

3. Press **Play**. `Assets/Sandbox/Boot.cs` auto-creates a `NovelApp` pointed at
   `http://127.0.0.1:8077` with the default runtime theme — no scene to wire up.
   Edit a chapter in the panel and it live-reloads here within ~2s.

## What's here

The manifest also pulls in **MCP for Unity** (`com.coplaydev.unity-mcp`), so the
editor exposes the MCP bridge automatically — no manual install. With both Liminal
and this sandbox open, the MCP server sees two instances; target this one by its
`Name@hash`.

```
sandbox/
  Packages/manifest.json         engine (file:) + mcp + newtonsoft + ugui + test-framework
  Assets/Sandbox/Boot.cs         RuntimeInitializeOnLoad → NovelApp @ localhost:8077
  Assets/Sandbox/Lvn.Sandbox.asmdef
  Assets/Resources/UI/AppLoading/UnityDefaultRuntimeTheme.tss   (so text renders)
  ProjectSettings/ProjectVersion.txt   6000.4.5f1
```

Everything else (the rest of `ProjectSettings/`, `Library/`) Unity generates on
first open. The engine's own EditMode tests run from the Test Runner once open.
