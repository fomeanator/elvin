# Elvin

**You write a story. Elvin plays it as a game.**

[![CI](https://github.com/fomeanator/elvin/actions/workflows/ci.yml/badge.svg)](https://github.com/fomeanator/elvin/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
&nbsp; **[▶ Playground](https://fomeanator.github.io/elvin/)** ·
[Docs](https://fomeanator.github.io/elvin/docs/) ·
[Try it](#try-it) ·
[Products](#the-products)

[![Write on the left — it plays on the right. Click to open this exact scene, editable.](docs/img/playground.png)](https://fomeanator.github.io/elvin/#s=c2NlbmUgYXV0dW1uX2xldHRlcgoKYmcgL2NvbnRlbnQvYmcvQXV0dW1uX3N0cmVldC5qcGcKCmNvdXJhZ2UgPSAyCnRleHQgaHVkIHg9MyB5PTUgc2l6ZT0zNCBjb2xvcj0jZmZkNzZhIMKr4pyoIGNvdXJhZ2Uge2NvdXJhZ2V9wrsKCk1hcmE6IFlvdSBhY3R1YWxseSBjYW1lLiBJIHdhc24ndCBzdXJlIHlvdSB3b3VsZC4KTWFyYTogVGhlIGxldHRlciBzYWlkIG1pZG5pZ2h0LiBJdCdzIGJhcmVseSBzaXguCgotIEFzayBhYm91dCB0aGUgbGV0dGVyIC0+IGxldHRlcgotIFRha2UgaGVyIGhhbmQgLT4gaGFuZAotIFNheSBub3RoaW5nIC0+IHF1aWV0Cgo6bGV0dGVyCk1hcmE6IE5vdCBoZXJlLiBXYWxrIHdpdGggbWUgZmlyc3QuCi0+IF9fZW5kCgo6aGFuZApjb3VyYWdlID0gY291cmFnZSArIDEKTWFyYTogLi4ub2guIFdlbGwuIFRoYXQncyBvbmUgd2F5IHRvIGFuc3dlci4KLT4gX19lbmQKCjpxdWlldApNYXJhOiBNeXN0ZXJpb3VzLiBJIGNhbiB3b3JrIHdpdGggbXlzdGVyaW91cy4KLT4gX19lbmQ=)

**↑ Click the screenshot** — that exact scene opens in the playground,
editable. Its entire source fits on one screen of **Elvin Script**:

```lvns
scene autumn_letter

bg /content/bg/Autumn_street.jpg

courage = 2
text hud x=3 y=5 size=34 color=#ffd76a «✨ courage {courage}»

Mara: You actually came. I wasn't sure you would.
Mara: The letter said midnight. It's barely six.

- Ask about the letter -> letter
- Take her hand -> hand
- Say nothing -> quiet

:hand
courage = courage + 1
Mara: ...oh. Well. That's one way to answer.
-> __end
```

No dialogue system to build. No branching engine. No save system. **That *is*
the engine.** Think Ren'Py's authoring comfort plus Ink's plain-text
portability — living inside Unity, with a product layer when you need one.

## Why Elvin

- **The whole game is a text file.** Screenplay-readable, git-diffable — and
  simple enough that **an LLM writes a complete game in one shot**. Point
  your agent at [`llms.txt`](llms.txt), wire the toolchain in over the
  [MCP server](docs/mcp.md), or install the
  [VS Code extension](tools/vscode-lvn/) — the real compiler behind every
  keystroke.
- **A real compiler, honest errors.** Dangling jumps, unknown ops, dead
  labels — caught at build time, not in a player's hands. The 0-warnings
  gate runs in CI. Stable ids mean saves, analytics and **translations
  survive content edits** — the player can switch language mid-story.
- **Batteries included.** A parametric cast (*K poses + M emotions = K+M
  images, not K×M*), bones with spring physics, optional Spine skeletons,
  timed choices, text input, voice-over, a CG gallery, saves with
  thumbnails and migration, a reactive HUD, camera/particles/fades.
- **It comes apart.** Use just the language, just the Unity runtime, or the
  full stack with a content server and a ready novel-app shell. Custom game
  logic is a [plugin](docs/embedding.md) — an `ext` op with flow control —
  not a fork.
- **Honest limits.** Not a realtime or physics engine: time is turns, input
  is choices and clicks. The code-verified capability list lives in
  [`howto/CAPABILITIES.md`](howto/CAPABILITIES.md).

## What people build with it

Every genre below ships as a working, compile-gated example in
[`howto/`](howto/):

| | | | |
|---|---|---|---|
| 📖 Visual novel | 🎬 Kinetic novel | 🗺 Gamebook / CYOA | 🖱 Point-and-click |
| ⚔ RPG | 🍪 Clicker / idle | 💕 Dating sim | ❓ Quiz |
| 🔍 Detective | 🏪 Tycoon | 🗡 Roguelike | 🧩 Puzzle |

## Try it

**In the browser — 10 seconds.** Open the
[playground](https://fomeanator.github.io/elvin/): write on the left, it
plays on the right. **Share** packs your game into a link; **⬇ HTML**
exports a single file that plays anywhere, saves included.

**In Unity — 2 minutes.**

1. Package Manager → *Add package from git URL*:
   ```
   https://github.com/fomeanator/lvn-engine.git
   ```
2. Drop a `.lvns` file into `Assets/` — Unity compiles it on import.
3. Put a `VnStage` on a GameObject with a `UIDocument`, point it at the
   asset, press **Play**.

Building a stand-alone novel **app**? Add
[`lvn-engine-shell`](https://github.com/fomeanator/lvn-engine-shell) and use
`NovelApp` — boot, title browse, store/wardrobe/gallery screens: you write
content, not UI code. The 15-minute path:
[`howto/TUTORIAL.md`](howto/TUTORIAL.md).

**From the command line** — for CI, or to import **Ink** and **articy:draft**
(including raw binary `.adpd` projects):

```sh
cd tools/lvnconv
go run . convert  -i ../../examples/hello.lvns -o /tmp/hello.lvn
go run . validate /tmp/hello.lvn            # the 0-warnings gate
go run . locale   -lang de /tmp/hello.lvn   # translation catalog, gettext-style
```

## The products

One repo, four products — take only what you need:

| | What | Who it's for |
|---|---|---|
| **Language** | `.lvns` + [`lvnconv`](tools/lvnconv/): compiler, validator, Ink/articy importers, [MCP](docs/mcp.md), [llms.txt](llms.txt), [VS Code](tools/vscode-lvn/) | everyone — an AI agent plus a text editor is a complete authoring setup |
| **Engine** | [`lvn-engine`](https://github.com/fomeanator/lvn-engine) + optional [shell](https://github.com/fomeanator/lvn-engine-shell) / [services](https://github.com/fomeanator/lvn-engine-services) / [Spine](https://github.com/fomeanator/lvn-engine-spine) / [Addressables](https://github.com/fomeanator/lvn-engine-addressables) | Unity developers |
| **Services** | the Go [server](server/): content streaming, saves, wallet/IAP/ads/leaderboards, one-click APK export | games shipping as a live product |
| **Studio** | the [authoring workspace](panel/): Monaco IDE, visual cast editor, admin dashboard (the server's `-studio` flag) | teams that want a GUI — entirely optional |

## Principles

- **Unknown is an error, never a silent skip** — content bugs surface at
  compile time.
- **The whole game is data** — the engine hardcodes no scene; swap the
  content, keep the engine.
- **Offline-first** — a game and its assets play with no network at all.
- **`.lvn` is a neutral container** — any tool can produce it, any runtime
  can play it. If you know media codecs: it's the container, `lvnconv` is
  the transcoder. That's the whole architecture.

<details>
<summary><b>Repository map</b></summary>

| Path | What |
|---|---|
| `howto/` | the build-a-game kit: tutorial, language reference, cheatsheet, recipes, 12 genre guides with validated examples; AI onboarding at `howto/AGENTS.md` |
| `tools/lvnconv/` | the transcoder CLI (Go) + its WASM build |
| `tools/lvn-lang/` | the language core: grammar + analysis shared by Studio and the VS Code extension |
| `tools/vscode-lvn/` | the VS Code extension |
| `docs/` | specs: `lvn-format.md`, `cast.md`, `placement.md`, `staging-tags.md`, `animation-system.md`, `embedding.md`, `releasing.md` |
| `server/` | the Go backend: content, state, product services, APK export |
| `panel/` | Elvin Studio (React) + the playground and docs-site sources in `panel/public/` |
| `unity/Packages/` | the Unity packages — the development home; consumers install the mirrors |
| `examples/` | the minimal sources the README and CI point at |

Demo content (the Sovet demo's art/audio) lives in
[`lvn-demo-content`](https://github.com/fomeanator/lvn-demo-content);
`scripts/fetch-demo-content.sh` pulls it in for dev servers and releases.

</details>

**Status:** `v0.9` — a complete, working narrative-game engine. Every release
tags all packages together, ships a playable demo APK and re-splits the
package mirrors. History: the
[CHANGELOG](unity/Packages/com.lvn.engine/CHANGELOG.md).

> *The name: Elvin is how you say LVN — the `.lvn` container it plays. You
> write Elvin Script (`.lvns`), `lvnconv` compiles it, runtimes play it.*

## License

MIT — see [LICENSE](LICENSE).
