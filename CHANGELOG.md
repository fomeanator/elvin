# Changelog

All notable changes to Elvin. Format based on [Keep a Changelog](https://keepachangelog.com/);
the project aims for [Semantic Versioning](https://semver.org/). This file tracks
the repo as a whole; the Unity package keeps its own detailed
[CHANGELOG](unity/Packages/com.lvn.engine/CHANGELOG.md).

## [Unreleased]

### Added
- **Bundle import** — one-shot "five files → playable novel" in the admin panel:
  an articy archive + backgrounds/characters/heroine zips + a variables `.xlsx`,
  mapped onto real art and wardrobe wiring (`lvnconv importer.RunBundle`,
  `POST /v1/admin/import-bundle`).
- **Per-project import templates** — the authoring conventions of an articy
  project (narrator roles, scene regexes, wardrobe/audio naming) live in
  `content/import-templates/<name>.json`; the default `cold` template keeps the
  previous behaviour. New projects add a JSON, not a fork.
- **Unified admin dashboard** inside the React panel (`/?admin=1`): overview,
  users/grants, orders, saves, economy configs, assets, manifest draft/publish
  with history rollback, analytics — all over the same `/v1/admin/*` API. The
  old vanilla `/admin/` page is retired (the server redirects it here).
- **Hub & collections novel-shell** — `ui.browse.layout="hub"` renders a
  hub → collections → title-detail flow (`BrowseHub`) with per-title
  type/unlock/cost, chapter-energy gating, wardrobe preview contract, actor
  mirror and the standard novel pose; "Полночь" design tokens across screens.
- **Regenerating currencies** — energy-style refill (`services/energy.json`):
  cap/interval/start, HUD countdown, popup paywall seam.
- **Cross-novel global stats** — `global.*` variables persist in a shared
  per-player blob and drive `unlock` expressions in the hub.
- **Server durability for money** — `atomicWrite` now fsyncs the file and its
  directory; wallet/auth/ads persist failures surface as 500s (money never
  moves in a response the disk didn't confirm); a corrupt wallet or
  `users.json` fails closed instead of silently becoming empty. New
  `-wallet-earn=false` kill switch closes the client-initiated earn route
  before real payments go live.
- **lvnconv miscompile guard** — a command-shaped line with an unknown op (or a
  known op with unparsable params) is a compile error with a "did you mean"
  hint, never silently swallowed into dialogue.
- **Ext-grammar** — `ext-grammar.json` declares a project's host ops (fields,
  required, enums, label-reference fields, docs/snippets): declared `ext` ops
  validate like built-ins across the CLI (`validate -ext-grammar` +
  auto-detected sidecar), the wasm playground, MCP `lvns_check` and the panel
  editor (completion/hover/ghost + live diagnostics). Code generation is
  untouched.
- **The Unity package split** — the engine core is now a thin narrative
  runtime; the product layer ships as optional first-party packages:
  `com.lvn.engine.shell` (the ready novel app: NovelApp + every product
  screen), `com.lvn.engine.services` (offline-first wallet/IAP/ads/
  analytics/leaderboards clients + `ext` economy ops), `com.lvn.engine.spine`
  (the Spine driver behind the `LvnSpineBridge` seam) and
  `com.lvn.engine.addressables` (bundle loading). File GUIDs kept, behaviour
  unchanged; the **Extension plugin (template)** sample shows how to build
  your own.

## [0.7.0] — 2026-07-03

- Engine: UI interaction sounds, History tap-to-return, read tracking +
  skip-read-only, CG gallery; library-first embedding seams (`LvnOps.Register`,
  menu slots, `VnStage.Saved`), sticky placement. Voice-over, timed choices and
  text input land across the language, validator and runtime.
- Removed dead `BacklogPanel`/`SaveLoadPanel` and the `MetaShell` prototypes.

## [0.6.0] — 2026-07-03

- Script-driven animation with splines/orient/arc-length and `defanim`;
  paper-doll bones with spring physics (`BoneSolver`); optional Spine runtime
  integration (version-define module); drag & drop objects.

## [0.5.0] — 2026-07-02

- QoL box: multi-step rollback, settings screen, save/load panel with
  thumbnails & migration, autosave/resume, quick menu. Self-hosted Unity CI
  job (EditMode + PlayMode) and the release-APK pipeline.

## [0.4.0]

- Transcoder (`lvnconv`): Elvin Script + Ink + articy (XML and binary `.adpd`)
  front-ends, structural validator, `probe`, WASM build.
- Unity runtime (`com.lvn.engine`): interpreter (flow, vars, expressions,
  subroutines, autosave, `wait`, `preload`), parametric cast/compositor, animation
  engine (channels, easing, yoyo, queue), effects (fade/dim/flash/tint/blur/
  camera/particles/audio), reactive HUD, save/load, and the novel-shell.
- Server template (Go) + web authoring panel.

[Unreleased]: https://github.com/fomeanator/unity-lvn-vn-engine/compare/v0.7.0...HEAD
[0.7.0]: https://github.com/fomeanator/unity-lvn-vn-engine/releases/tag/v0.7.0
[0.6.0]: https://github.com/fomeanator/unity-lvn-vn-engine/releases/tag/v0.6.0
[0.5.0]: https://github.com/fomeanator/unity-lvn-vn-engine/releases/tag/v0.5.0
[0.4.0]: https://github.com/fomeanator/unity-lvn-vn-engine/releases/tag/v0.4.0
