# LVN — an open visual-novel engine where `.lvn` is the universal format

[![CI](https://github.com/fomeanator/unity-lvn-vn-engine/actions/workflows/ci.yml/badge.svg)](https://github.com/fomeanator/unity-lvn-vn-engine/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**LVN is "ffmpeg for visual novels."** You write your story in whatever
authoring tool you like (Ink, articy:draft, …); a transcoder compiles it to
**`.lvn`** — one flat, declarative command container — and the runtime plays
that container. New authoring formats plug in as front-ends. New effects plug
in as runtime modules. The story data never knows or cares which is which.

The result is a **construction kit**: drop the Unity package into your project,
point it at a `.lvn` (bundled or served), wire the optional Go backend for
saves/entitlements, and you have a shippable narrative game. Swap the content,
keep the engine.

```
   Ink ─┐
articy ─┼─▶  lvnconv  ─▶  chapter.lvn  ─▶  LVN runtime (Unity)  ─▶  your game
  … ────┘  (transcoder)   (container)        + LVN server (Go, optional)
```

## Why a container format

A visual novel is a stream of presentation commands: *show this background,
say this line, branch on this choice, tint the frame cold*. Tie that stream to
one authoring tool and you marry the tool; tie it to one engine and you marry
the engine. `.lvn` is the neutral middle — a small JSON command list that any
front-end can emit and any runtime can play. It is to narrative what a codec
container is to media: producers and players evolve independently.

## Repository layout

| Path | What |
|---|---|
| `tools/lvnconv/` | The transcoder CLI (Go). `convert` Ink/articy → `.lvn`, `validate`, `probe`. |
| `docs/lvn-format.md` | The `.lvn` command catalog — the container spec. |
| `docs/staging-tags.md` | The staging-tag vocabulary front-ends share. |
| `server/` | Optional Go backend template: content manifest, assets, player state. |
| `unity/Packages/com.lvn.engine/` | The Unity runtime, installable via Package Manager. |
| `examples/` | A tiny `hello.ink` and its compiled `hello.lvn`. |

## Quickstart

### 1. Transcode a script to `.lvn`

```sh
cd tools/lvnconv
go run . convert -i ../../examples/hello.ink -o /tmp/hello.lvn
go run . validate /tmp/hello.lvn      # catches dangling jumps, unknown ops, dup labels
go run . probe    /tmp/hello.lvn       # one-line summary
```

`lvnconv` infers the format from the extension (`.ink` → Ink, `.json` →
articy export); force it with `-f ink|articy`.

### 2. Serve content (optional)

```sh
cd server
go run . -content ./content -addr :8000
# GET /v1/content/manifest, GET /content/<path>, GET/PUT /v1/state
```

The runtime plays equally well from a bundled `.lvn` offline or from the
server — the backend only adds saves, entitlements, and live content updates.

### 3. Plug the engine into Unity

Add the package via **Package Manager → Add package from git URL**:

```
https://github.com/fomeanator/unity-lvn-vn-engine.git?path=/unity/Packages/com.lvn.engine
```

Drop a **`VnStage`** on a GameObject with a `UIDocument`, assign a `.lvn`
TextAsset, press Play — it renders and runs: background, characters, a dialogue
box with typewriter reveal, and branching choices. Tap to advance, click to
choose. Restyle everything from one `VnTheme`; load art through your own
`ILvnAssets`. Want a bespoke skin instead? Use the headless `LvnPlayer` +
`ILvnStage` and draw it yourself. See
`unity/Packages/com.lvn.engine/README.md`.

## Design rules

- **Unknown is an error, never a silent skip.** An unregistered staging tag or
  command op fails the build with a precise message (which command, which
  file) — content bugs surface at compile time, not in a player's hands.
- **Stable ids.** Labels, choices and endings keep stable ids across reimports
  so saves and analytics survive content edits.
- **Offline-first.** The container and its assets play without a network; the
  server is additive.
- **The runtime is content-agnostic.** Effects are declared by tag and applied
  by data-driven modules; the engine hardcodes no scene.

## Status

`v0.2` — playable end-to-end and verified live in Unity 6:

- **Transcoder** (`lvnconv`): Ink + articy front-ends, validator, `probe`.
- **Container spec**: the `.lvn` command catalog and shared staging-tag vocabulary.
- **Server template** (Go): manifest, content, player state, admin upload.
- **Unity runtime**: `LvnPlayer` interpreter (flow, vars, tunnels, autosave) with
  a built-in `LvnExpression` evaluator, plus the reference component set —
  `VnTheme`, `DialogueBox`, `ChoiceList`, `BackgroundLayer`, `ActorLayer`, and
  the `VnStage` drop-in. **11/11 EditMode tests green.**

Next: effect modules (fade/tint/camera/particles), the layered-sprite
compositor, and a premium meta-shell template. See
[`docs/lvn-format.md`](docs/lvn-format.md) for the command catalog and the
package [CHANGELOG](unity/Packages/com.lvn.engine/CHANGELOG.md) for detail.

## License

MIT — see [LICENSE](LICENSE).
