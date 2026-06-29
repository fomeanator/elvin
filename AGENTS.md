# AGENTS.md

Guidance for coding agents and contributors working in this repository.

## Overview

Elvin is a narrative-game engine for Unity. Games are authored in **Elvin Script**
(`.lvns`), compiled to the **`.lvn`** container (a flat JSON command list), and
played by the Unity runtime (`com.lvn.engine`). The standalone Go transcoder
(`lvnconv`) compiles Elvin Script, Ink and articy to `.lvn`; an in-Unity
ScriptedImporter compiles `.lvns` on import. ("Elvin" is how "LVN" is pronounced;
the format stays `.lvn`.)

To build a game (not the engine), start at [`howto/AGENTS.md`](howto/AGENTS.md).

## Build & test

CI builds each Go module standalone with `GOWORK=off` (no workspace), then runs
`gofmt`, `go vet`, `go build`, `go test`. Reproduce locally:

```sh
# Go modules (transcoder + server)
( cd tools/lvnconv && GOWORK=off gofmt -l . && GOWORK=off go vet ./... && GOWORK=off go build ./... && GOWORK=off go test ./... )
( cd server       && GOWORK=off gofmt -l . && GOWORK=off go vet ./... && GOWORK=off go build ./... && GOWORK=off go test ./... )

# Compile & validate an Elvin Script (the authoring loop)
cd tools/lvnconv
go run . convert  -i ../../examples/hello.lvns -o /tmp/hello.lvn
go run . validate /tmp/hello.lvn      # target: "OK ... 0 warning(s)"
```

`gofmt -l .` must print nothing. The Unity package and its EditMode tests are run
inside the Unity Editor (Test Runner), not from the CLI.

## Project structure

```
tools/lvnconv/   Go transcoder: convert (Elvin Script/Ink/articy/.adpd), validate, probe; WASM build
  internal/lvns/ Elvin Script -> .lvn compiler — SOURCE OF TRUTH for the language
tools/lvn-lang/  Elvin Script tooling (grammar + static analysis)
server/          Go content server: manifest, assets, player state, docs website
panel/           Web authoring panel (visual cast editor, in-browser compile)
unity/Packages/com.lvn.engine/   Unity runtime + the .lvns ScriptedImporter
  Editor/LvnsCompiler.cs         C# port of the Go compiler (Unity import path)
  Tests/Editor/Fixtures/         golden corpus (.lvns + Go-produced .lvn) guarding parity
docs/            Format & system specs (.lvn container, cast, placement, animation)
howto/           Build-a-game kit: language reference, capabilities/limits, genre guides
examples/        Minimal scripts (Ink, Elvin Script, compiled .lvn)
```

## Conventions

- Author in `.lvns`. `.lvn` is a generated artifact — never hand-edit it.
- The language has two implementations: the Go compiler
  (`tools/lvnconv/internal/lvns`) is the single source of truth; the C# port
  (`unity/.../Editor/LvnsCompiler.cs`) mirrors it for the Unity importer. Change
  Go first, then mirror C#; the golden corpus in `Tests/Editor/Fixtures` (run via
  Unity Test Runner) must stay green.
- Label / choice / ending ids are stable. Renaming the *text* is fine; renaming an
  *id* breaks saves and analytics.
- Variables prefixed `__` are reserved for the compiler/runtime — don't author them.
- Go: keep `gofmt` and `go vet` clean; modules must build with `GOWORK=off`.

## Boundaries

- Never commit secrets. The only built-in is the local dev admin token `devtoken`
  (default, local use only).
- Don't commit Unity `Library/`/`Temp/`/`Build/` or `sandbox/` (gitignored).
- Don't rename existing label ids (see above).

## Git

- Branch off `main`; don't commit directly to it.
- End commit messages with: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.
- Aim for `OK … 0 warning(s)` from `lvnconv validate` before committing content.

## See also

- [`howto/`](howto/) — build games (language reference, capabilities/limits, genre guides)
- [`docs/lvn-format.md`](docs/lvn-format.md) — the `.lvn` command catalog
- Format adapted from the [AGENTS.md](https://agents.md/) convention.
