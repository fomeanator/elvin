# LVN — an open engine for narrative games, where `.lvn` is the universal format

[![CI](https://github.com/fomeanator/unity-lvn-vn-engine/actions/workflows/ci.yml/badge.svg)](https://github.com/fomeanator/unity-lvn-vn-engine/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**LVN is "ffmpeg for narrative games."** You write your game in a simple
authoring language (or import from Ink / articy:draft); a transcoder compiles it
to **`.lvn`** — one flat, declarative command container — and a runtime plays
that container. New authoring formats plug in as front-ends. New effects plug in
as runtime modules. The game data never knows or cares which is which.

The result is a **construction kit**: write `.lvns`, compile, validate, and you
have a shippable game — visual novel, gamebook, point-and-click, RPG, clicker,
dating sim, detective, roguelike, puzzle, and more. The same engine, different
data.

```
  LVNScript ─┐
        Ink ─┼─▶  lvnconv  ─▶  chapter.lvn  ─▶  LVN runtime (Unity)  ─▶  your game
  articy ────┼─▶  (transcoder)  (container)        + LVN server (Go, optional)
   (.adpd) ──┘                                     + web panel / IDE
```

A narrative game is a stream of commands: *show this background, say this line,
branch on this choice, raise this stat, tween this sprite, save here*. Tie that
stream to one authoring tool and you marry the tool; tie it to one engine and you
marry the engine. `.lvn` is the neutral middle — a small JSON command list any
front-end can emit and any runtime can play. It is to narrative what a codec
container is to media: producers and players evolve independently.

> 🤖 **Building a game (human or AI agent)? Start at [`howto/AGENTS.md`](howto/AGENTS.md).**
> The `howto/` folder is a self-contained build kit: a mental model, the full
> [language reference](howto/LANGUAGE.md), an explicit
> [capabilities-and-limitations map](howto/CAPABILITIES.md), a
> [one-page cheatsheet](howto/CHEATSHEET.md), a [recipe book](howto/recipes.md),
> and **12 genre guides each with a working, validated `.lvns` example**. Written
> so a fresh session can build any supported game without reading engine source.

---

## What's actually built

LVN is not a spec on paper — it is a working, end-to-end pipeline. Here is the
realized system and what each part really does today.

### 1. LVNScript (`.lvns`) — the native authoring language
A terse, human- and AI-friendly language that compiles to `.lvn`. What it
supports, all implemented:

- **Dialogue & narration** with per-speaker name plates and **emotions**
  (`Mara [smile]: …`), multi-line strings (`«…»`), and `{expression}`
  interpolation in any text.
- **Branching**: choice menus (`- text -> label`) with options that can be
  **gated** (`expr=`, `requires_stat`/`min`) or carry a displayed `cost`.
- **Flow**: labels, `goto`, single-branch `if … -> label`, block `if/else`,
  `for … in`, `while`, functions (`func name(args){…}` with `return`), and
  `call`/`return` subroutines.
- **State & logic**: variables holding numbers, strings, bools, lists and maps;
  an expression engine with arithmetic, comparisons, boolean logic, and **~25
  built-in functions** (`rand`, `chance`, `min/max`, `floor/round`, `len`,
  `has`, `push`, `pop`, `slice`, `keys`, `put`, …).
- **Reactive HUD**: a `text` label whose template re-evaluates automatically
  (~200 ms) — perfect for health bars, gold, scores, progress.
- **Staging**: `bg`, `actor`, `obj` (with screen-fraction placement, anchors, z,
  flip, and clickable `on_click` hotspots), `fade`, `dim`, `flash`, `tint`,
  `blur`, `camera` (shake/zoom/pan), `particles`, `audio`, `wait`.
- **Animation**: `anim`/`move` — script-driven tweens on parallel channels, with
  easing, keyframes, `to=` one-liners, `loop`/`yoyo`, `queue`, and `stop`.
- **Persistence**: `save` / `load` snapshots.

### 2. `lvnconv` — the transcoder (Go CLI)
- **`convert`**: LVNScript, **Ink**, and **articy:draft** (both the XML export
  and the reverse-engineered binary **`.adpd`** project format) → `.lvn`.
- **`validate`**: source-agnostic structural checks — unknown ops, dangling
  jumps, duplicate labels, fall-through traps, unbalanced interpolation braces.
  `-strict` turns warnings into errors for CI.
- **`probe`**: one-line summary of a compiled `.lvn`.
- **WASM build**: the same transcoder compiled to WebAssembly so the web
  panel/playground compile `.lvns` **in the browser**.
- Importer extras: stable-id localization and **placeholder art generation**
  (grey stand-ins) so an imported game is fully playable before any real art.

### 3. The Unity runtime (`com.lvn.engine`)
Plays `.lvn` with no per-game code:

- **Cast system** — parametric, layered sprites: a character is a set of layer
  templates with named axes (pose/emotion/outfit…). *K poses + M emotions need
  K+M images, not K×M.*
- **Placement** — everything positioned in screen fractions; the same fields
  place a character, a prop, a UI button, or a clickable hotspot.
- **Reactive text**, **save/load** (PlayerPrefs), **animation engine**
  (channels/lanes, easing, yoyo, queue), **effects/camera/particles/audio**.
- **Content loader** with versioned asset cache and **offline bundle** support.
- **Novel-shell** — a ready meta-game around the script: boot screen, title
  carousel, chapter list, in-game HUD, loading screen, name input, save/load
  panel, and an optional premium meta-shell (hub, lives, paywall gate).
- **Greybox mode** — run with no asset provider to test pure logic.

### 4. Content server (Go)
Serves the content manifest, scripts and assets; admin asset upload (token-gated)
with a versioned asset-version index for cache busting; and a **documentation
website** with an interactive **LVNScript playground** and a copy-pasteable
**AI system-prompt spec**.

### 5. Web panel / IDE
A browser authoring tool: a **visual cast editor** (build parametric characters,
axes, upload art), script editing with **in-browser compilation** (the WASM
transcoder), and asset upload to the server.

---

## Genres you can ship

The engine targets any game driven by **buttons + state**. Each of these has a
**working, validated example** under [`howto/`](howto/):

| | | | |
|---|---|---|---|
| 📖 Visual novel | 🎬 Kinetic novel | 🗺 Gamebook / CYOA | 🖱 Point-and-click |
| ⚔ RPG | 🍪 Clicker / idle | 💕 Dating sim | ❓ Quiz |
| 🔍 Detective | 🏪 Tycoon | 🗡 Roguelike | 🧩 Puzzle |

Larger real scripts live in `server/content/scripts/` (`rpg-inv.lvns`,
`goblin-battle.lvns`, `showcase.lvns`, `strandgade.lvns`, `soviet-ch1.lvns`).

**Honest about the edges:** LVN is not a real-time/physics engine. There is no
script-level real-time timer and no in-script text input (time is measured in
turns; input is choices and clicks). Some animation features (spline/step
interpolation, path orientation, named reusable animations) are designed but not
yet in the runtime. The full, code-verified list of what works and what doesn't
is in [`howto/CAPABILITIES.md`](howto/CAPABILITIES.md).

---

## Repository layout

| Path | What |
|---|---|
| `howto/` | **Build-a-game kit + AI-agent onboarding**: entry point, full language reference, capabilities/limits, cheatsheet, recipes, and 12 genre guides with validated `.lvns` examples. Start at `howto/AGENTS.md`. |
| `tools/lvnconv/` | The transcoder CLI (Go): `convert` (LVNScript/Ink/articy/.adpd), `validate`, `probe`; plus the WASM build and the importer. |
| `tools/lvn-lang/` | Language tooling for `.lvns` (grammar + static analysis). |
| `tools/artmatte/` | Small art-matte helper utility. |
| `docs/` | Format & system specs: `lvn-format.md` (container), `staging-tags.md`, `cast.md`, `placement.md`, `animation-system.md`. |
| `server/` | Go backend: content manifest, assets + admin upload, player state, and the documentation website with the LVNScript playground. |
| `panel/` | Web authoring panel / IDE (visual cast editor, in-browser compile). |
| `unity/Packages/com.lvn.engine/` | The Unity runtime, installable via Package Manager. |
| `sandbox/` | A Unity sandbox project for exercising the runtime in-editor. |
| `examples/` | Minimal scripts in Ink, LVNScript, and compiled `.lvn`. |

---

## Quickstart

### 1. Transcode & validate a script

```sh
cd tools/lvnconv
go run . convert  -i ../../examples/hello.lvns -o /tmp/hello.lvn   # LVNScript → .lvn
go run . convert  -i ../../examples/hello.ink  -o /tmp/hello.lvn   # Ink → .lvn
go run . validate /tmp/hello.lvn      # dangling jumps, unknown ops, dup labels, traps
go run . probe    /tmp/hello.lvn      # one-line summary
```

`lvnconv` infers the format from the extension; force it with `-f ink|articy|adpd`.
Aim for `OK … 0 warning(s)` — that is the build-correctness gate, no engine
needed.

### 2. Serve content (optional)

```sh
cd server
go run . -content ./content -addr :8000
# GET /v1/content/manifest, GET /content/<path>, GET/PUT /v1/state, admin asset upload
```

The runtime plays equally well from a bundled `.lvn` offline or from the server —
the backend only adds saves, entitlements, live content updates, and the docs
website. See [`server/README.md`](server/README.md).

### 3. Author in the web panel (optional)

```sh
cd panel
npm install && npm run dev   # visual cast editor + in-browser .lvns compile
```

See [`panel/README.md`](panel/README.md).

### 4. Plug the engine into Unity

Add the package via **Package Manager → Add package from git URL**:

```
https://github.com/fomeanator/unity-lvn-vn-engine.git?path=/unity/Packages/com.lvn.engine
```

Drop a **`VnStage`** on a GameObject with a `UIDocument`, assign a `.lvn`
TextAsset, press Play — it renders and runs: background, characters, a dialogue
box with typewriter reveal, branching choices, the reactive HUD, animation, and
the novel-shell. Tap to advance, click to choose. Restyle everything from one
`VnTheme`; load art through your own `ILvnAssets`. For a bespoke skin, use the
headless `LvnPlayer` + `ILvnStage` and draw it yourself. See
[`unity/Packages/com.lvn.engine/README.md`](unity/Packages/com.lvn.engine/README.md).

---

## Design rules

- **Unknown is an error, never a silent skip.** An unregistered op or staging tag
  fails the build with a precise message (which command, which file) — content
  bugs surface at compile time, not in a player's hands.
- **Stable ids.** Labels, choices and endings keep stable ids across reimports so
  saves and analytics survive content edits.
- **Offline-first.** The container and its assets play without a network; the
  server is additive.
- **The runtime is content-agnostic.** Effects are declared by data and applied
  by modules; the engine hardcodes no scene. The whole game is data.

---

## Status

`v0.4` — a full, working narrative-game engine:

- **Transcoder** (`lvnconv`): LVNScript + Ink + articy (XML and binary `.adpd`)
  front-ends, structural validator, `probe`, and a WASM build.
- **Container spec**: the `.lvn` command catalog and shared staging vocabulary.
- **Unity runtime**: `LvnPlayer` interpreter (flow, vars, expressions, tunnels,
  autosave, `wait`, `preload`) + the reference UI (`VnTheme`, `DialogueBox`,
  `ChoiceList`, `ActorLayer`, `VnStage`), cast system, animation engine, reactive
  HUD, effects/camera/particles, save/load, and the novel-shell incl. an optional
  premium meta-shell.
- **Server template** (Go): manifest, content, player state, admin upload, docs
  website + playground.
- **Web panel**: visual cast editor and in-browser compilation.
- **Docs**: the `howto/` build kit (genre guides + validated examples + a
  code-verified capabilities/limitations map).

Planned next: spline/named animations, broader importer connectivity, server
persistence polish. See [`docs/lvn-format.md`](docs/lvn-format.md) for the command
catalog, [`howto/CAPABILITIES.md`](howto/CAPABILITIES.md) for exact limits, and the
package [CHANGELOG](unity/Packages/com.lvn.engine/CHANGELOG.md) for detail.

## License

MIT — see [LICENSE](LICENSE).
