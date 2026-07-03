# Changelog

All notable changes to the LVN Engine package are documented here. The format
follows [Keep a Changelog](https://keepachangelog.com/); versions are SemVer.

## [Unreleased]

## [0.7.0] — 2026-07-03

### Added
- **UI interaction sounds** — `manifest.ui.sounds { click, choice, type,
  volume }`: short one-shots for tap-to-advance, picking a choice and the
  typewriter tick (throttled). Clips resolve through ILvnAssets, play on
  a dedicated StageAudio channel (never cut a story sfx) and scale by the
  player's SFX volume. Missing urls stay silent.
- **History tap-to-return** — tapping a line in the History panel rewinds
  the story to it: variables as they were, scene rebuilt, later beats
  dropped — a multi-beat rollback in one hop (`VnStage.RollbackSteps`,
  `LvnPlayer.PopRollback(steps)`). Lines beyond the snapshot history
  (or before a load) stay inert.
- **Read tracking + skip-read-only** — the engine remembers every line
  the player has seen (`LvnReadStore`, per-title, survives deleted
  saves), and the new Settings toggle "Skip read text only" (label key
  `skip_read_only`) stops fast-forward at the first never-seen line —
  the genre's safe re-read gear. Hosts can read `ReadCount` for
  seen-percentage UI.
- **CG gallery** — `title.gallery = [{id, url, name?}]` curates unlockable
  art: a shown `bg` with a matching url unlocks the item forever
  (per-title PlayerPrefs meta-progress, survives deleted saves). The
  quick menu grows a Gallery entry (label key `gallery`): thumbnail grid,
  locked cells render `?`, tap opens a fullscreen viewer.

### Removed
- Dead duplicate components `BacklogPanel` and `SaveLoadPanel` (never
  referenced; StageMenu's History and save/load slots are the shipped
  equivalents). Hosts needing custom panels build on the public
  `VnStage.Backlog` / `LvnSaveStore` instead.
- The `MetaShell` prototypes (`HubScreen`, `PaywallGate`,
  `LifeCardSystem`) — unreferenced monetization scaffolding. The engine
  is a narrative library; lives/paywalls/IAP belong to the embedding
  game, which has all the seams it needs (`LvnOps`, menu items, events,
  overlay drawing).

### Added
- **Library-first embedding** — the engine is now a proper library with
  documented extension seams (docs/embedding.md): `LvnOps.Register` for
  host-defined script ops with flow control (Hold/Resume/GoTo, story
  vars) authored as `ext <op> k=v` in .lvns; `StageMenu.AddMenuItem`
  for the game's own quick-menu entries; `VnStage.Saved` event for
  cloud sync/achievements. The validator now treats unknown ops as a
  warning (they may be host-defined), never an error.

### Added
- **Sticky placement** — an actor command only changes the fields it
  explicitly mentions; everything else inherits the actor's current
  placement (including a drag's drop spot). `actor id=knight
  play="Jump"` no longer teleports him to the defaults. Transitions
  stay one-shot (always from the command).
- **Spine support (optional)** — `kind: "spine"` catalog entities render
  real Spine skeletons through the official spine-unity runtime: the
  export's json/atlas/png stream like any other content (no Unity
  assets), `spine.auto` loops on show, `actor … play="Run" loop=true`
  drives the skeleton's own animations. The integration is an optional
  assembly compiled only when com.esotericsoftware.spine.spine-unity is
  installed (version define — the Addressables pattern); without it the
  engine logs a clear warning and carries no Spine dependency or
  license obligation. Canvas scene path; asset loaders gain
  LoadTextAsync across the chain.
- **Drag & drop** — `obj … draggable=true on_drop="bag:label"
  [on_drop_miss=label]`: grab an object and it follows the pointer
  (slot travel feeds the spring joints, so cloth/hair sway while
  dragging); release over a mapped target runs that label (the branch
  hides the item, bumps vars, plays its animation); release anywhere
  else and it stays where dropped. A short press is still a tap
  (on_click keeps working). UITK actors now expose ActorScreenRect for
  the drop hit-testing.

### Added
- **Bones + spring physics (paper-doll FK)** — catalog layers gain
  `parent` (attach to another layer), `px`/`py` (the joint) and
  `spring`/`damping` (secondary motion: hair/tails swing from the
  parent's movement AND rotation — the VRM spring-bone model — and
  settle by themselves). Transforms compose down the chain in a pure,
  unit-tested solver; draw order stays the layer list order; both
  renderers supported. Rotate an `arm` — the `hand` follows; `move`
  the doll — her hair sways, no keyframes.
- **Named animations** — `defanim shake prop=x keys="…"` defines once,
  `play id=x anim=shake` (terse: `play x shake`) stamps it anywhere;
  play-side params override the definition. Compile-time expansion — the
  runtime only ever sees plain `anim` commands.
- **Constant-speed spline paths** — a spline `move` path now covers equal
  distance per second (arc-length table, built lazily per playing anim),
  with easing driving progress along the length; `orient` follows the
  warped time. No more speed jumps between unevenly spaced path points.
- **Language picker in Settings** — when the manifest declares shipped
  catalogs (`languages: ["en", …]`, auto-declared by a localized import),
  Settings shows a cycling Original → lang → … row. The choice persists
  (`LvnPrefs.Locale`), overrides the host default, and switching
  mid-chapter reloads the catalog — subsequent lines render in the new
  language at once.
- **Save slot thumbnails** — opening the menu snapshots the clean frame
  (before the scrim), and every manual save (slots, quick save) stores it
  as a PNG beside the saves; slot rows show an 80×45 preview. Autosave
  stays text-only (its capture moment would be arbitrary). Requires the
  screencapture module — now declared by the package.

## [0.6.0] — 2026-07-03

The "update without fear" release: animation phase 2 lands in the runtime,
saves get schema versioning, exported projects pin the engine to a release
tag, and the two long-standing player gaps (history marks, overwrite
confirmation) close — plus the historical blank-stage bug dies and texture
memory halves.

### Fixed
- **Blank stage after a disable/enable cycle** — UIDocument brings up a
  fresh empty panel root on re-enable, and the build guard used to skip
  the rebuild. The stage now tears down on disable (canvas scene,
  audio component) and rebuilds its chrome on enable, re-rendering a
  live player's current beat via the rollback anchor — no duplicate
  backlog entries, story continues.

### Changed
- **Texture memory halved** — loaded art frees its CPU-side copy
  (`makeNoLongerReadable`); nothing reads pixels back. On mobile,
  oversized art is GPU-resampled to ≤2560 px on the longest side at
  load (a 4K background drops 4× in memory, visually lossless on
  phone screens).

### Added
- **Save schema versioning** — every slot write stamps
  `LvnSaveSlot.CurrentVersion`; reads migrate older schemas up and HIDE
  slots written by a newer build instead of misreading them (they survive
  unrelated writes untouched, so upgrading the app brings them back).
- **History shows the branches you took** — a picked choice lands in the
  backlog as an accented "▸ option" line (indented, theme accent colour);
  rollback of an undone pick removes its mark so a re-pick records fresh.
- **Save-overwrite confirmation** — saving into an occupied slot asks
  first (localizable `overwrite_q`/`overwrite`/`cancel` labels); empty
  slots still save in one tap.
- **Animation phase 2** — `interp=spline` (Catmull-Rom through the keys) and
  `interp=step` in the shared sampler (UITK + Canvas paths), `move …
  orient=true` rotates the actor along the path tangent. A typo'd `interp`
  value now fails the `.lvns` compile instead of silently playing linear.

## [0.5.0] — 2026-07-02

The "reads like a book, engineered like a product" release: the full player
QoL box, chapters that flow into each other with genre-correct restart
semantics, hardened offline/state sync, and the invariant net in CI.

### Added
- **Player QoL box** — rollback (bounded beat history; undoing a choice
  restores pre-choice variables; wheel-up on desktop), persistent save slots
  per title (timestamps + line previews), autosave (every choice / 5th line /
  app pause) with in-place resume, auto-advance (reading delay scales with
  line length), player preferences (`LvnPrefs`: text speed, volumes per
  channel, reduce-motion, dialogue-window opacity), and an in-game quick menu
  (Save / Load / History / Auto / Settings) — themeable from `manifest.ui.menu`.
- **Chapter flow** — chapters follow each other seamlessly; a per-title
  Continue marker resumes the furthest chapter (the card's Play button reads
  "Continue" with the episode name); a chapter picker lists episodes by name,
  unlocking as reached. Picking a chapter restarts it with the variables it
  ORIGINALLY began with (per-chapter entry checkpoints) — future stats never
  leak into a replayed past.
- **Cross-chapter save loading** — a slot from another chapter resolves its
  chapter by script url, plays it and restores in place.
- **State sync hardening** — server-owned versioning on `/v1/state` (stale
  writes 409 + merge-retry), field-level conflict merge against a sync base
  (two devices touching different stats both keep progress), and a per-blob
  TOFU key (`X-State-Key`) so a user id leaked via URL logs can't read or
  overwrite a save.
- **Offline correctness** — the offline script fallback serves the RIGHT
  chapter (url sidecars), atomic cache writes, sha256 integrity verification
  against the version index, automatic online recovery (health-probe loop),
  and a bounded LRU sprite cache with look-ahead prefetch (no mid-scene
  pop-in, no OOM on long sessions).
- **Reflow-free typewriter** — the whole line lays out from glyph 0 (the tail
  hidden via alpha), so word-wrap and box height never shift mid-reveal.
- **Scene renderer seam** (`ISceneRenderer`) — the UITK and uGUI scene paths
  are proper implementations behind one contract instead of per-call-site
  conditionals.
- **Grammar single source of truth** — `grammar.json` drives the editor
  grammar, docs, AI prompt and the Go validator; drift fails tests.
- **CI** — Go suites, grammar contract, panel build and the package's
  EditMode tests (via the committed `unity/TestHost`) run on every push.

### Fixed
- Malformed command fields degrade to defaults instead of aborting the
  chapter; `narration` style no longer hides its own text; the engine package
  now declares its built-in module dependencies (it compiles in a sterile
  project).

### Added (pre-0.5.0 backlog)
- **Manifest-driven screen kit** (`Lvn.UI.Screens`) — three fully themeable novel
  screens built in code from the manifest's `ui` block: `LoadingScreen` (backdrop,
  scrim, fog, progress bar with optional track/fill/frame art, percent / current-file
  / rotating-tip labels), `TitleCard` (chapter + subtitle reveal with fog and frame),
  and `NameInputScreen` (backdrop, character art, prompt, field, confirm). Every
  colour, image url, text, size and duration comes from `LvnUiConfig`
  (`loading` / `title` / `name_input`); all optional with sensible defaults. The bar
  maths (`LoadingProgressModel`, `ProgressRenderGate`) and the name rules
  (`PlayerNameInput`) are pure and unit-tested. Referenced from Liminal's shipping
  loading/title/name-input screens.
- **Content pipeline** (`Lvn.Content`) — a networked, disk-cached content system
  ported from a shipping VN client. `ContentLoader` (sha1(url@version) disk cache,
  in-memory sprite cache, dedup of parallel fetches, `asset-versions.json`
  cache-busting, byte-level progress, resumable retries, pipelined preload batch,
  audio via `UnityWebRequestMultimedia`); `AssetScheduler` (prioritized
  required/deferred release set, per-tier concurrency, EDF ordering);
  `DownloadPolicy` (pure URL classification); `DownloadManager` (four phases —
  boot / menu refresh / chapter entry / in-game look-ahead — over a generic
  `LvnManifest`/`LvnTitle`/`LvnSeason`/`LvnChapter` model). Bridged to the engine
  via `CachingAssets : ILvnAssets`.
- **Composable asset loaders** — `MemoryCache` (L1), `ChainAssets` (try loaders in
  order), and an optional Addressables backend (`Lvn.Engine.Addressables`, an
  assembly auto-gated by the `com.unity.addressables` package — zero footprint
  when it isn't installed).
- **`flash` op** — quick coloured flash (white/red/etc.) that fades back to clear.
- **`tint` op** — coloured tint wash (cold/warm/sepia) with configurable alpha.
- **`blur` op** — blur overlay for depth-of-field simulation.
- **`text_pace` op** — global characters-per-second override for typewriter speed.
- **`camera` pan** — smooth camera pan to target x/y coordinates.
- **Actor transitions** — `enter`/`exit` fields on `actor` op: `fade`, `slide_left`,
  `slide_right`, `pop` animations with configurable duration.
- **Backlog UI** — `BacklogPanel` component for scrollable dialogue history.
- **Premium Meta-Shell** — `HubScreen` (chapter select), `LifeCardSystem`
  (lives/regen), `PaywallGate` (IAP prompt).
- **`wait` op** — blocking pause with configurable duration (`ms`). The player
  halts execution and resumes automatically after the delay.
- **`preload` op** — speculative asset loading. Non-blocking: the player
  continues immediately while assets load in the background.
- **Backlog** — `LvnPlayer.OnSay` event fires on every `say` command. `VnStage`
  records dialogue history in `Backlog` (read-only list of who/text/style).
- **Hover feedback** — `hover_opacity` field on `actor`/`obj` ops. Hotspots
  brighten on mouse-enter and restore on mouse-leave.
- **Richer `on_click`** — `on_click` now accepts an object: `{ "goto": "label",
  "set": { "key": value } }`. The `set` ops run before the jump.
- **Save/Load** — `LvnPlayer.Save()` returns an `LvnSnapshot` (IP, vars, call
  stack). `Restore(LvnSnapshot)` resumes. `SaveLoadPanel` provides a slot-based
  UI for save/load.
- `FxLayer` — full-screen effects overlay; `VnStage` now renders the `fade`
  (to black/white/clear) and `dim` (focus-pull) ops as animated veils.
- `CameraRig` — `camera` op: shake (diminishing jitter) and zoom on the world
  layer, leaving the UI chrome steady.
- `ParticleField` — `particles` op: procedural rain / snow weather, no textures.
- Audio: `audio` op with music / ambient / sfx channels, looping beds and
  one-shot sfx, with volume fades. `ILvnAssets` gains `LoadAudioAsync`.
- `VnStage` wraps background + actors in a "world" layer so camera effects move
  the scene but not the dialogue/choices.

- **Cast — named, parametric sprite entities** (`SpriteComposer` + the `cast`
  block). A character is a list of layer URL templates parameterised by named
  axes (pose, emotion, outfit…); the `actor` command names the entity and the
  axis values, and the runtime fills the templates and stacks the layers.
  K poses + M emotions need K + M images, not K × M. Pure, engine-agnostic
  resolution — see `docs/cast.md`. `ActorLayer` now composites layered sprites.
- `actor` also takes direct `body_url` / `clothes_url` / `hair_url` layers
  (composited bottom-to-top) for characters authored without a cast block.
- `DirectoryAssets` — a reference `ILvnAssets` that loads sprites from a local
  folder (offline/bundled content, and for tests).
- **Full object placement** — `actor`/`obj` place any sprite by screen fraction:
  `x`/`y`, `width`/`height`, `anchor` (pivot %), `z` (paint order), `flip`,
  `rotation`, `opacity`, plus named slots `far_left`…`far_right`. `obj` puts any
  sprite on screen; `actor` is the same with speaker dimming. See `docs/placement.md`.
- **Clickable hotspots** — `on_click: "label"` makes any object tappable; the tap
  jumps the script (via `LvnPlayer.GoTo`) and is swallowed so it doesn't advance
  the dialogue. With placement + flow + state, the engine assembles button-driven
  games (menus, point-and-click), not only visual novels.

### Verified
- Live in Unity 6: rain renders over the dialogue while the typewriter reveals
  the line; a two-layer cast character (body + face) composites correctly.
- Played a real 338-command production VN chapter end-to-end (its own
  backgrounds, layered characters, fades/camera/dim/particles) through the
  engine via `DirectoryAssets` — characters composite from their body/outfit
  layers over the real art.
- 15/15 EditMode tests green (expression, player, sprite composer).
- New tests: `WaitPreloadTests`, `BacklogTests`, `HotspotTests`, `SaveLoadTests`.

### Added
- `VnStage.ContentRoot` — a serialized content-folder path. When set (and
  `Assets` is unwired) the stage auto-creates a `DirectoryAssets`, so a scene
  plays with real art straight from Play with no code.

### Fixed
- **Compile blockers** that broke the whole `Lvn.Engine.UI` assembly: (1)
  `DirectoryAssets.LoadAudioAsync` constructed an `AudioClip` (no public ctor) and
  called `AudioClip.Create`/`SetData` on a background thread — replaced with
  `UnityWebRequestMultimedia` decoding a `file://` url on the main thread (handles
  wav/ogg/mp3); (2) `CameraRig.Pan` compared a `Length` struct against `null` —
  now reads `.value` directly; (3) the Addressables loader referenced a
  non-dependency package — moved into a separate assembly auto-gated by
  `com.unity.addressables`, so the package compiles with or without it.
- Freeze on click / advancing to the next op: `DirectoryAssets` decoded large
  textures synchronously on the main thread for every show (no cache), so each
  transition that revealed a background or character hitched. It now caches
  sprites by url (instant re-show) and reads files off the main thread, so the
  click → `Advance` path no longer blocks on a decode (measured ~1 ms per op).
- `LvnPlayer.Advance` now guards against a cyclic `goto` with no pause between
  jumps — it fails loudly instead of spinning the main thread forever.
- Black screen on play, two causes: (1) `VnStage` could miss building its layers
  when `UIDocument.rootVisualElement` was still null in `OnEnable` (a script-order
  race) — it now builds in `OnEnable` **and** `Start`; (2) the asset loader was
  code-only (always null on a plain Play, so backgrounds/characters never loaded)
  — `ContentRoot` fixes that from the Inspector.

## [0.2.0] — 2026-06-20

### Added
- `LvnExpression` — built-in evaluator for string `expr` conditions
  (`|| && !`, comparisons, arithmetic, strings; unset vars default like ink).
  `if` and option `expr` filters now work out of the box; `LvnPlayer.ExprEvaluator`
  becomes an optional override rather than a requirement.
- `LvnException` — runtime error type for malformed scripts/expressions.
- Reference UI Toolkit component set (`Lvn.Engine.UI`): `VnTheme`, `DialogueBox`,
  `ChoiceList`, `BackgroundLayer`, `ActorLayer`, `ILvnAssets`, and `VnStage` —
  a `MonoBehaviour : ILvnStage` drop-in that plays a `.lvn` in a `UIDocument`.
  Plus `RichTextTypewriter` / `TypewriterClock` (typewriter core).

### Fixed
- An unset variable now compares as 0 / false / "" (ink defaulting), so
  once-only choice gates (`__once == 0`) and first-visit checks pass on the
  first pass instead of filtering every option out.

### Tests
- EditMode tests for `LvnExpression` and `LvnPlayer` (flow, set/inc, once-only
  gating, call/return tunnels) — 11/11 green in Unity 6's Test Runner, with
  regression cover for the unset-variable fix at both the expression and player
  levels.

### Verified
- The full engine plays a `.lvn` end-to-end in Unity 6 (6000.4): scene → stage →
  dialogue with typewriter → branching choice. Compiles clean, runs error-free.
- Ships with `.meta` files (stable GUIDs) for clean Package Manager installs.

## [0.1.0] — 2026-06-20

### Added
- `LvnDocument` — parse the `.lvn` container (Newtonsoft-backed command list).
- `LvnPlayer` — the interpreter: cursor, variable bag, and flow control for
  `goto` / `if` / `choice` / `call`-`return`, with autosave snapshot/restore.
- `ILvnStage` — the host contract (say, choice, stage commands, end).
- `LvnOption`, `StagingOps` — choice presentation and the op registry.
- Pluggable `ExprEvaluator` hook for string `expr` conditions.
- **Hello LVN** sample: a console host that plays a bundled `.lvn`.

### Known gaps (planned)
- Reference UI Toolkit component set (dialogue box, choice list, background,
  actor layer) — the drop-in "constructor" rendering layer.
- Effect modules (camera, particles, tint) and the layered-sprite compositor.
- Premium meta-shell template (hub / life-card / paywall).
