# Elvin engine capabilities and limits

This is a **map of what the engine can and can NOT do**, verified against the sources
(the `tools/lvnconv/` transcoder, the `unity/Packages/com.lvn.engine/Runtime/` runtime).
The goal is that "can we do X" decisions get made from this file, **without
reading the code**. If a feature is marked "❌ no" — it does not exist in the reference
runtime, and you must work around it with language means (variables, labels, loops),
not hope that "it will somehow work".

Related docs: syntax — [`LANGUAGE.md`](LANGUAGE.md); orientation for an AI agent
— [`AGENTS.md`](AGENTS.md); cheatsheet — [`CHEATSHEET.md`](CHEATSHEET.md).

---

## 0. What can be built at all

The engine is an executor of a flat command list (`.lvn`) with variables, branching,
subroutines, a reactive HUD, staging (background/actors/effects/sound) and
scripted animation. That is enough for:

> visual novels, kinetic novels, gamebooks/CYOA, point-and-click and
> adventure games, RPGs (stats/combat/inventory/levels), dating sims, quizzes, detective
> stories, tycoon/management, roguelike runs, puzzles, and almost any game
> driven by **buttons + state**.

What the engine fundamentally does NOT do: it is **not** a physics/realtime engine.
There is no real-time game loop available to the script, no arbitrary
keyboard/mouse input beyond clicking objects and picking menu options, no timing-based
arcade mechanics. All "time" is measured in **turns/player actions**, not clock hours.

---

## 1. Full command catalog (runtime behavior)

These are all the `op`s the runtime understands (registry — `validate.go` `KnownOps`).
Any other `op` is a build error. In `.lvns` you write the human-readable form
(see `LANGUAGE.md`), which compiles into these commands.

### Text and choice
| op | What it does at runtime | Fields |
|---|---|---|
| `say` | Shows a line; interpolates `{expression}`; waits for a click (except when a `choice` follows). Voice-over: the `voice` field (in `.lvns` — a `voice "<url>"` line before the dialogue line) — the clip starts with the text and is muted by the next line/reset; volume — the "Voice" slider (key `voice`), the typing blip stays silent under voice. | `text`, `who?`, `style?`, `voice?` |
| `choice` | Choice menu. Filters options by `requires_stat`/`min` and `expr` (options that fail are **hidden**, not "grayed out"). Timer: `timeout` (sec) + `timeout_goto` — a countdown bar above the buttons; expiry sends the flow to a branch (in `.lvns`: a `choice timeout=10 timeout_goto=late` line before the `- …` block; menus/art view freeze the clock). | `options[]`, `timeout?`, `timeout_goto?` |
| `text` | Persistent **reactive** HUD label: the template is re-evaluated **every 200 ms**. `hide=true` removes it. | `id`, `text`(template), `x`,`y`,`anchor`,`size`,`color`,`font`,`hide` |
| `text_pace` | Text typing speed (chars/sec; `0` — instant). | `cps` |

### Control flow
| op | What it does | Fields |
|---|---|---|
| `label` | Jump target label (a no-op when executed). | `id` |
| `goto` | Jump to a label or `__end`. Unknown label → a warning and a jump to the end. | `label` |
| `if` | Conditional jump. Accepts a **structural** `cond` `{key,op,value}` (`eq/ne/lt/lte/gt/gte`) **or** a string `expr` (if both are present, `expr` wins). | `cond`/`expr`, `then`, `else` |
| `call` | Jump that remembers the return point (call stack). | `label` |
| `return` | Return to the point after `call`; with an empty stack — to the end. | — |

### State
| op | What it does | Fields |
|---|---|---|
| `set` | Assign a variable. `expr` (expression string) wins over `value` (literal). | `key`, `value`/`expr` |
| `inc` | Add a number (default `+1`). | `key`, `by?` |

### Staging: scene
| op | What it does | Key fields |
|---|---|---|
| `bg` | Background. Resolves a catalog id or a direct `sprite_url`. Loads asynchronously. | `sprite_url`, `id?` |
| `actor` | Place/update/hide a character. Resolves layers from the cast/catalog/direct urls; starts idle/blink/lip-sync. | `id`, cast axes, `show`,`position`,`x`,`y`,`width`,`height`,`scale`,`anchor`,`z`,`flip`,`rotation`,`opacity`,`on_click`,`hover_opacity` |
| `obj` | Same as `actor` (same code), but semantically "not a character" (does not dim). | (same as `actor`) |

### Staging: effects, sound, timing
| op | What it does | Fields / defaults |
|---|---|---|
| `fade` | Full-screen fade. | `to`=black/white/clear, `duration`≈0.5 |
| `dim` | Dim the scene (focus). | `alpha`≈0.4, `duration`≈0.5 |
| `flash` | Short flash. | `color`=white, `duration`≈0.2 |
| `tint` | Colored overlay veil. | `color`,`alpha`≈0.3,`duration` |
| `blur` | Screen blur (`alpha`≤0 removes it). | `alpha`≈0.5,`duration` |
| `camera` | `shake`/`zoom`/`pan`/`reset`. | `action`,`amplitude`,`factor`,`x`,`y`,`duration` |
| `particles` | Weather layer. | `type`=rain/snow, `on`(bool) |
| `audio` | Sound on a channel (async). | `channel`=music/sfx/ambient, `action`=play/stop, `url?` |
| `wait` | Pause the script for N ms (default 1000). | `ms` |
| `input` | Text input overlay; the string goes into a variable, the story waits for confirmation. | `var` (required), `prompt?`, `default?`, `max?` |
| `anim` | Scripted tween on a channel (in `.lvns` this is `anim`/`move`). `mode=queue` enqueues on the channel; `stop` clears the channel. | `id`,`anim`(payload),`channel?`,`mode?`,`stop?` |
| `preload` | Warm up assets asynchronously (non-blocking). | `assets[]` `{url,kind}` |

UI interface sounds (a manifest, not a command): `manifest.ui.sounds =
{ click?, choice?, type?, volume? }` — short one-shots on dialogue tap,
option pick and the typewriter tick (throttled). Urls are content urls, scaled
by the user's SFX volume; a missing field means silence.

Read-text tracking (automatic, not a command): the engine remembers every
line shown, per-title (survives save deletion); the settings have a
"Skip: read only" toggle (label key `skip_read_only`) — fast-forward stops
at the first line not yet seen.

CG gallery (manifest, per-title): `title.gallery = [{id, url, name?}, …]` —
a curated list of unlockable art. An art piece unlocks forever on the first
`bg` shown with the same url (survives save deletion and new playthroughs);
a "Gallery" entry appears in the quick menu (label key `gallery`): a grid,
locked pieces show "?", tapping an unlocked one opens full-screen view. Keep `id`
stable between releases, otherwise players lose their unlocks.

### Saving
| op | What it does | Fields |
|---|---|---|
| `save` | Snapshot the state (see §5). | `slot?` (default `quick`) |
| `load` | Restore a snapshot and redraw the scene. | `slot?` |

### ⚠ Reserved / non-working
| op | Status |
|---|---|
| `hint` | ✅ Rendered (`VnStage.cs`: `ApplyHint`). A popup at the top center of the scene; `show=false` removes it, `duration>0` — auto-hide. The text interpolates `{vars}`. |

---

## 2. Reactive `text` (HUD)

- Re-evaluated **every 200 ms** automatically — perfect for points,
  health, resources, progress.
- The template in `«…»` may contain any `{expressions}`: variables, arithmetic,
  function calls (`len`, `has`, `min`, …), indexing.
- Multiple HUD labels coexist (under different `id`s). Do not put two labels at
  the same `x/y/anchor` spot — they will overlap.
- A bad/unknown expression in the template renders literally as `{key}` (does not
  crash) — that is a typo signal.
- Hide with `text hud hide`.

---

## 3. Choice (`choice`) — what actually works

| Option field | Type | Behavior |
|---|---|---|
| `text` | display | Option text, interpolated with `{…}`. |
| `cost` | display | The "price" caption under the option, interpolated. **Purely visual** — deducts nothing by itself. |
| `goto` | functional | Jump on pick. |
| `body` | functional | Inline command list, executed on pick (see the limit in §8). |
| `requires_stat` + `min` | functional | Gate: the option is **hidden** if `variable < min`. |
| `expr` | functional | Boolean expression gate: the option is **hidden** if false. |
| `hint` | functional | A popup hint at the top center (`duration>0` — auto-hide). |

A failed gate **hides** the option entirely (does not show it "grayed out").

---

## 4. Variables, conditions, expressions

- **Value types:** number, string, bool, `null`, list (`[]`), map (`{}`).
- **An unset variable** reads as `0` / `""` / `false` (ink-style:
  `null == 0 == false == ""`).
- **`if`** accepts a structural `cond` and a string `expr` (the latter wins).
- **`set`**: `value` (a literal of any type) or `expr` (an expression string, which wins).
  **`inc`**: `by` (default 1, coerced to a number).
- **Namespacing:** dotted keys (`ns.flag`) are allowed; the `__` prefix is
  reserved for the engine/transcoder — **do not use it for your own variables**.
- An error in a condition expression degrades softly (treated as `false`) rather
  than crashing the game. The full list of built-in functions is in `LANGUAGE.md` §7. **There is no `ceil`**
  — use `floor`/`round`.

---

## 5. Save / load

- `save` stores: **the cursor position, the variable dictionary, the call stack**.
- `load` restores them, redraws the scene (background/actors as of the snapshot point) and
  resumes execution from the saved spot.
- Storage: Unity `PlayerPrefs`, key `lvn_save_<slot>` (default slot
  `quick`). Any number of slots by name; the total PlayerPrefs limit is ~1 MB.
- **Design-critical:** save/load is tied to **stable label ids**.
  Renaming TEXT is fine; renaming label/ending ids is not, or old
  saves break.

---

## 6. Animation — what is in, what is not yet

The detailed model and notation forms are in `LANGUAGE.md` §9 and `docs/animation-system.md`.
Checked against the runtime:

| Capability | Status |
|---|---|
| Properties `x` `y` `screen_x` `screen_y` `scale` `scalex` `scaley` `rotation` `alpha` `frame` | ✅ yes |
| Easing `linear` `inOutSine` `outCubic` `outBack` `inBack` | ✅ yes |
| Loop `once` / `restart`(`true`) / `yoyo` | ✅ yes |
| One-liner `to=` (tween from the current value) | ✅ yes |
| `stop` / `stop=<channel>` | ✅ yes |
| `mode=queue` (sequence on a channel) | ✅ yes |
| Parallelism (multiple channels = multiple lines) | ✅ yes |
| `interp=spline` / `interp=step` | ✅ yes (Catmull-Rom through the keys / step; a typo in the value is a compile error) |
| `orient=true` (rotate along the path tangent) | ✅ yes (for `move`; respects easing and spline) |
| `defanim` / `play` (named reusable animations) | ❌ no (planned) |

**Quoting rule (a common mistake):** values with **spaces** in quotes
(`keys="…"`, `path="…"`) require the **legacy form** `id=`/`prop=`. A bracket list
`[…]` and the one-liner `to=` also work in terse form. A malformed `anim`/`move` is
a **compile error** (not a silent skip).

---

## 7. Cast and assets — how art gets on screen

This is critical: **a character can NOT be defined in `.lvns`** — there is no cast
directive there. `.lvns` only **references** the cast by id (`actor mara ...`). The
definition itself lives in the **manifest** or in a `cast` block of the `.lvn`.

### Where the cast lives
- **`manifest.json` → `sprites`** (an `id → entity` map) — the **global** catalog,
  available to every chapter. The primary way.
- **A `cast` block in `.lvn`** — local to one chapter (optional).
- The `.lvns` source has no cast — it is mixed in by the runtime from the manifest.

### How `actor mara emotion=smile` becomes a picture
1. The entity is looked up by `id` in the catalog (manifest) or in the document's `_cast`.
2. Axes: the entity's `defaults` are overridden by the command's fields
   (`emotion=smile`, `pose=…`, …).
3. For every layer template (`/content/sprites/mara/face_{emotion}.png`)
   all `{tokens}` are substituted.
4. A token without a value → **the layer is skipped** (optional parts appear
   only on request). **K poses + M emotions = K+M images, not K×M.**

### Cast entity fields (in the manifest)
`name` (name plate), `color` (name color), `layers` (url template list, bottom
to top; a layer may have a `when` condition, an `id`, a partial rectangle `x/y/w/h`),
`defaults` (default axis values), `axes` (allowed axis values —
for the editor), `kind` (`static`(default)/`rigged`/…), `anim` (named
animations of the rigged doll: idle/blink/…).

### How to add art
- **The cast editor in Studio** (`panel`, Sprites tab): you create an entity,
  axes, upload images — Studio writes files to the server (`PUT /v1/admin/assets/…`,
  requires an admin token) and stores the entity in `manifest.json`.
- **Manually:** edit `manifest.json` (`sprites`) and place files following the
  path conventions. The server re-reads the manifest (`Cache-Control: no-store`).

### Path conventions
```
/content/sprites/<id>/<layer>_<axis>_<value>.png    // characters
/content/bg/<name>.jpg                              // backgrounds
/content/ui/<purpose>/<name>.png                    // UI/hotspots
/content/scripts/<chapter>.lvn                      // compiled scripts
```

### Missing art (important for workflow order)
- If an asset url **fails to load** (404) — that **layer is simply skipped**,
  the rest is drawn. **There are no automatic gray placeholders at runtime.**
- Gray placeholder images are generated **at import time** (the `lvnconv` tool),
  i.e. they are real asset files, not a runtime effect.
- "Graybox" (running with no asset provider at all) yields solid colored backgrounds
  with no characters.
- **Takeaway:** the game's logic, text and structure can be fully written and
  validated **before** the art exists; on the live stage a missing sprite is
  simply not rendered — that is not an error.

### How a script gets into the game
- The manifest describes the table of contents: `titles → seasons → chapters`; a chapter has
  a stable `id`, `number`, a **`script_url`** (that `.lvn`), `bg_url`, a set of
  `assets`.
- The host (novel-shell) loads the manifest and shows the carousel/chapter list; on pick it
  downloads the `script_url` and plays it.
- The `scene <name>` directive in the script is **metadata** (a chapter label for logs/
  saves). It does **not select** which script plays; selection happens via the manifest's
  `script_url`.

Asset resolving: absolute `/content/...` urls are fetched from the server with a versioned
cache key (`/content/asset-versions.json` → sha); a `file://` bundle works offline.
Offline details — project memory/`server/README.md`.

---

## 8. HARD LIMITS (mandatory reading)

This is what the reference runtime does **not** have. Do not try to emulate it "as
if it exists" — use the workarounds in the right column.

| Limit | Workaround |
|---|---|
| ❌ **No realtime timer** (`every`/`sleep`/clock ticks are unavailable to the script). `wait ms=` is only a fixed pause, not a condition. | Measure time in **turns/days** in a loop (`day = day + 1`), and grant "idle" income on every loop pass. |
| ❌ **No player text input** in the script (there is no `prompt`/`input`; the name entry screen is part of the host, not a script command). | Any "input" goes through `choice` (picking from options) or clicks on `obj on_click`. |
| ❌ **Script flow cannot be tied to a looping animation finishing.** A looping animation never blocks the script. | Use `wait` or `say` for pauses; use `mode=queue` for a sequence on one channel. |
| ✅ **`hint` is rendered** — a window at the top center. | `hint text="…" duration=6`; `show=false` removes it manually. For a persistent HUD label there is still the reactive `text`. |
| ✅ **Bones + springs (paper-doll).** | Catalog layer: `parent` (which layer it attaches to), `px`/`py` (the joint, fractions of its own rect), `spring`/`damping` (hair/tail swing on their own from the parent's movement and rotation, VRM model). Draw order = list order (the back arm is a child of the body, but behind it). Both renderers. |
| ✅ **`defanim`/`play` work.** | `defanim shake prop=x keys="…"` + `play id=x anim=shake` (terse: `play x shake`); play parameters override the definition. Spline paths run at constant speed (arc-length). |
| ❌ **A choice option's `body` is limited**: only `set`/`inc`/staging commands and `goto` inside. No `if`/`choice`/`call` inside a body. | Move complex logic to a separate label and lead there with `goto`. |
| ❌ **An option's `cost` is a caption only**; it deducts no resource itself. | Deduct resources explicitly (`set`/`inc`) at the option's handler label. |
| ❌ **A missing asset is not replaced by a placeholder at runtime** — the layer is skipped. | That is normal for graybox; for placeholders generate them with the tool/place the files. |
| ❌ **No `ceil`.** | `floor(x)` / `round(x)`. |
| ❌ **The cast cannot be defined in `.lvns`.** | Define it in `manifest.json` (`sprites`) or in a `cast` block of the `.lvn`; the script only references it by id. |
| ❌ **No error/exception handling in the script.** | Faulty expressions are treated as `false`/`{key}`; design conditions so that the safe value is the default. |

---

## 9. How the engine judges "correctness" (validation)

`lvnconv validate <file.lvn>` is the structural check (source:
`tools/lvnconv/lvn/validate.go`). Use it as the definition of a "correct game".

**Errors (the build must not let them through):**
- a command without an `op`, or an `op` outside the registry (a typo);
- a label without an `id`, or a duplicate `id`;
- a jump (`goto`/`if`/`choice`/`call`/`on_click`) to a nonexistent label.

**Warnings (probably unintended):**
- a jump-target label that is also **fallen into** from above (the classic
  "the chapter suddenly ended" — put `-> __end`/`-> label` before it);
- a label that is defined but leads nowhere and is unreachable (dead);
- unbalanced `{`/`}` in text (interpolation will break; escape literal
  braces as `{{` and `}}`);
- a `choice` option with neither `goto` nor `body` (silently falls through);
- no `scene` header (adding `scene <name>` is recommended).

A healthy chapter's target is `OK ... 0 warning(s)` (and in CI — `validate -strict`, where
warnings = errors).
