# Elvin — narrative games for Unity, written as plain text

[![CI](https://github.com/fomeanator/unity-lvn-vn-engine/actions/workflows/ci.yml/badge.svg)](https://github.com/fomeanator/unity-lvn-vn-engine/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**Write your story as a simple script — Elvin plays it as a real game in Unity.**
Dialogue, branching choices, characters with emotions, stats and inventory,
animation, music, save/load — all from plain text a writer (or an AI) can author.
Drop the script into your Unity project and press Play. You don't build a
dialogue system, a branching engine, or a save system — that **is** the engine.

```
   you write              Elvin                 in Unity
  ┌──────────┐  compiles  ┌──────────┐  plays   ┌──────────────┐
  │  story   │ ─────────► │  game    │ ───────► │  real game:   │
  │ (.lvns)  │            │ (.lvn)   │          │  bg, actors,  │
  │  text    │            │          │          │  choices, HUD │
  └──────────┘            └──────────┘          └──────────────┘
```

## Who it's for

- **Programmers** — stop rebuilding the same visual-novel / dialogue / branching
  / stats / save stack for every project. Drop Elvin in, feed it a script, ship.
  The whole game is **data**, not code.
- **Writers & designers** — author the entire game in readable text (it looks
  like a screenplay with choices) and watch it run as a real game, no engineer in
  the loop.
- **AI-first teams** — the script language is simple and unambiguous enough that
  an **LLM can write an entire game** in one go. Point your agent at
  [`llms.txt`](llms.txt), or plug the toolchain straight in via the
  **[MCP server](docs/mcp.md)** (`lvns_check` / `lvns_convert` / `lvn_doc`).
  Onboarding: [`howto/AGENTS.md`](howto/AGENTS.md).

> The name: **Elvin** is just how you say **LVN** — the `.lvn` format it plays.
> You write **Elvin Script** (`.lvns`); it compiles to the `.lvn` container; the
> Unity runtime plays it.

---

## What you can build

Anything driven by **choices + state**. Each genre below ships with a working,
validated example under [`howto/`](howto/):

| | | | |
|---|---|---|---|
| 📖 Visual novel | 🎬 Kinetic novel | 🗺 Gamebook / CYOA | 🖱 Point-and-click |
| ⚔ RPG | 🍪 Clicker / idle | 💕 Dating sim | ❓ Quiz |
| 🔍 Detective | 🏪 Tycoon | 🗡 Roguelike | 🧩 Puzzle |

It is **not** a real-time/physics engine — time is measured in turns, input is
choices and clicks. The exact, code-verified list of what it can and can't do is
[`howto/CAPABILITIES.md`](howto/CAPABILITIES.md).

---

## What's in the box

- **Elvin Script (`.lvns`)** — a tiny authoring language: dialogue with emotions,
  branching choices (with conditions/costs), variables and an expression engine
  (~25 built-in functions), `if`/`for`/`while`/functions, a reactive on-screen
  HUD, full staging (backgrounds, characters, fades, camera, particles, audio),
  script-driven animation, and save/load.
- **Unity runtime** (`com.lvn.engine`) — plays a game from the script with no
  per-game code: a parametric **cast** system (a character is a few layers ×
  named axes, so *K poses + M emotions = K+M images, not K×M*), placement &
  clickable hotspots, the reactive HUD, animation, effects, save/load, and a
  ready **novel-shell** (boot screen, title carousel, chapter list, loading,
  name input, save/load panel).
- **In-Unity import** — drop a `.lvns` file into `Assets/` and Unity compiles it
  automatically (a ScriptedImporter); no external tool, no terminal.
- **`lvnconv`** — a standalone transcoder (Go CLI + WASM) that also imports
  **Ink** and **articy:draft** (XML export and the binary `.adpd` project format)
  into the same `.lvn`, plus `validate` and `probe`.
- **Content server** (Go, optional) — serves manifest/scripts/assets for live
  content updates, plus player saves and a docs website.
- **Web panel** (optional) — a visual cast editor and in-browser script compiling.

---

## Repository layout

| Path | What |
|---|---|
| `howto/` | **Build-a-game kit + AI-agent onboarding**: entry point, full language reference, capabilities/limits, cheatsheet, recipes, and 12 genre guides with validated examples. Start at `howto/AGENTS.md`. |
| `tools/lvnconv/` | The transcoder CLI (Go): `convert` (Elvin Script/Ink/articy/.adpd), `validate`, `probe`; plus the WASM build and importer. |
| `tools/lvn-lang/` | Language tooling for `.lvns` (grammar + static analysis). |
| `docs/` | Format & system specs: `lvn-format.md` (container), `staging-tags.md`, `cast.md`, `placement.md`, `animation-system.md`. |
| `server/` | Go backend: content manifest, assets + admin upload, player state, docs website. |
| `panel/` | Web authoring panel / IDE (visual cast editor, in-browser compile). |
| `unity/Packages/com.lvn.engine/` | The Unity runtime + the `.lvns` importer, installable via Package Manager. |
| `examples/` | Minimal scripts in Ink, Elvin Script, and compiled `.lvn`. |

---

## Quickstart

### Zero-install: the browser playground

Run the content server (`go run ./server -content ./server/content -addr :8077`)
and open **`/play/`** — write Elvin Script on the left, it plays on the right
(the real compiler in wasm; story core incl. timed choices and text input).
**Share** packs your script into the URL — anyone opening the link plays it.
For the full 15-minute path to a real game, read
[`howto/TUTORIAL.md`](howto/TUTORIAL.md).

### A. In Unity (the common path)

1. Add the package — **Package Manager → Add package from git URL**:
   ```
   https://github.com/fomeanator/unity-lvn-vn-engine.git?path=/unity/Packages/com.lvn.engine
   ```
2. Drop a `.lvns` file into `Assets/` — Unity compiles it automatically.
3. Put a **`VnStage`** on a GameObject with a `UIDocument`, point it at the
   compiled asset, press **Play**. It renders and runs: background, characters,
   typewriter dialogue, branching choices, the reactive HUD, animation, and the
   novel-shell. Restyle from one `VnTheme`; load art via your own `ILvnAssets`.
   See [`unity/Packages/com.lvn.engine/README.md`](unity/Packages/com.lvn.engine/README.md).

### B. From the command line (for CI / Ink / articy)

```sh
cd tools/lvnconv
go run . convert  -i ../../examples/hello.lvns -o /tmp/hello.lvn   # Elvin Script → .lvn
go run . convert  -i ../../examples/hello.ink  -o /tmp/hello.lvn   # Ink → .lvn
go run . validate /tmp/hello.lvn      # dangling jumps, unknown ops, dup labels
go run . probe    /tmp/hello.lvn      # one-line summary
```
Aim for `OK … 0 warning(s)` — that's the build-correctness gate, no engine needed.

### C. Serve content (optional)

```sh
cd server
go run . -content ./content -addr :8000
```
The runtime plays equally well from a bundled script offline or from the server;
the backend only adds saves, live content updates, and the docs site.

---

## Design rules

- **Unknown is an error, never a silent skip.** An unregistered op or staging tag
  fails the build with a precise message — content bugs surface at compile time,
  not in a player's hands.
- **Stable ids.** Labels, choices and endings keep stable ids across reimports so
  saves and analytics survive content edits.
- **Offline-first.** The game and its assets play without a network.
- **The whole game is data.** Effects are declared by data and applied by
  modules; the engine hardcodes no scene — swap the content, keep the engine.

> Under the hood, `.lvn` is a neutral container that any authoring tool can target
> and any runtime can play — producers and players evolve independently. (If you
> know media codecs: it's the container, and `lvnconv` is the transcoder.)

---

## Status

`v0.7` — a full, working narrative-game engine for Unity:

- **Elvin Script** + the `.lvn` container spec: dialogue, branching (incl.
  **timed choices**), **text input**, **voice-over**, vars & expressions,
  subroutines, script-driven animation with splines.
- **Unity runtime**: interpreter, layered cast with **bones + spring physics**,
  optional **Spine** integration (official runtime, version-define module),
  **drag & drop** objects, effects/camera/particles, UI sounds, **CG gallery**,
  read-text tracking + skip-read-only, History tap-to-return, save/load with
  thumbnails & migration, localization, the novel-shell, in-Unity importer.
- **`lvnconv`**: Elvin Script + Ink + articy (XML and binary `.adpd`)
  front-ends, teaching validator, WASM build, **MCP server**.
- **Authoring**: web IDE (cast editor, live-validated script editor, Spine
  upload, articy import, APK export) + the **browser playground** at `/play/`.
- **Pipeline**: tag → CI (self-hosted Unity runner) → GitHub Release with the
  demo APK attached. Library embedding seams (`LvnOps`, menu slots, events)
  under a compatibility contract.

See [`docs/lvn-format.md`](docs/lvn-format.md) for the command catalog,
[`howto/CAPABILITIES.md`](howto/CAPABILITIES.md) for exact limits, and the package
[CHANGELOG](unity/Packages/com.lvn.engine/CHANGELOG.md) for detail.

## License

MIT — see [LICENSE](LICENSE).
