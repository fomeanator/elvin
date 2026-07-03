# Animation — script-driven motion for any narrative game

Goal: the **strongest** sprite-animation system that is still the **simplest** to
author — enough to stage any narrative game, with the common case a one-liner.

This spec is the source of truth. It is distilled from how the mature engines do
it (Ren'Py ATL, Naninovel, TyranoScript, Monogatari) and the tweening/timeline
libraries (DOTween, GSAP, Unity Timeline) — they all converge on one model.

---

## The one principle

> **Lanes run in parallel. Steps inside a lane run in sequence.**

Everything else is sugar over this. In our engine:

- a **lane** is an animation **channel** on an entity (`base`, `blink`, `talk`,
  `gesture` are engine lanes; script animation uses `script:*` lanes);
- a **step** is a keyframe on a track; multiple keys on one track play in order;
- different channels **composite** every frame (they add up), so they run at once;
- a new animation on a **busy channel replaces** the old one (or queues — below).

Two more rules keep it ergonomic, copied from the systems that got it right:

1. **The common case is a one-liner**: *target + duration + easing*, tweening
   **from the current value**. No keyframe block needed for a simple move
   (Naninovel `@char pos:… time:…`, Ren'Py `linear 1.0 xpos …`).
2. **Parallel costs one token, not a construct**: writing two `anim` lines is
   already parallel; you only reach for grouping when you mean it (GSAP `<`,
   DOTween `Join`).

---

## Authoring in `.lvns`

Two verbs: **`anim`** (tween a property) and **`move`** (tween a screen path).
Both compile to a single runtime `anim` command (the engine learns one verb).

### Tier 1 — one-liner tween (the 90% case)  ◻ planned (`to=`)

Tween one property from its current value to a target.

```
anim id=codel prop=scale to=1.15 dur=0.4 ease=outBack     // a little "pop"
anim id=codel prop=rotation to=8 dur=0.3                   // tilt the head
move id=codel to=0.2,-0.05 dur=1 ease=inOutSine            // glide to a point
```

### Tier 2 — keyframes (multi-step motion inside one lane)  ✓ implemented

Several keys on one track = a sequence within that lane.

```
anim id=codel prop=y keys="0:0 0.5:-0.04 1:0" loop=yoyo ease=inOutSine
anim id=codel layer=face prop=rotation keys="0:0 1:8 2:-8 3:0" loop=true
move id=codel path="-0.18,0.04 0,-0.03 0.18,0.04" dur=2 ease=outCubic
```

`keys="t:v t:v …"` — time (seconds) `:` value, space-separated.
`path="x,y x,y …"` — screen-fraction control points; times spread across `dur`.

### Tier 3 — parallel / sequential between lanes  (partly ✓)

- **Parallel**: just write several lines. Different properties → different
  channels → they composite. ✓
  ```
  anim id=codel prop=rotation keys="0:-4 1:4 2:-4" loop=yoyo   // sway …
  anim id=codel prop=scale    keys="0:1 1:1.04 2:1" loop=yoyo  // …and breathe at once
  ```
- **Sequential on one lane**: `mode=queue` — wait for the current step on that
  channel, then play (non-blocking; FIFO chain). ✓ Don't queue behind a `loop`
  anim (it never finishes).
  ```
  anim id=codel channel=hero prop=x to=0.2 dur=1
  anim id=codel channel=hero prop=x to=-0.2 dur=1 mode=queue   // after the first
  ```
- **Sequential by pausing the script**: put a `wait` (or a `say`) between
  animations — the script blocks, the motions play back-to-back. ✓ (`wait` exists)

### Parameters

| Param | Verb | Meaning |
|---|---|---|
| `id` | both | target entity (must be on stage) |
| `prop` | `anim` | property to tween (see table below) |
| `to` | both | target value (one-liner; tween from current) ◻ |
| `keys` | `anim` | `"t:v …"` keyframes (seconds : value) ✓ |
| `path` | `move` | `"x,y …"` screen-fraction control points ✓ |
| `dur` | both | duration in seconds (default = max key time, or 1 for `to`/`path`) ✓ |
| `layer` | `anim` | target a sprite layer (face/mouth/…); omit = whole rig ✓ |
| `ease` | both | easing curve (see below) ✓ |
| `interp` | both | `linear` (default) \| `spline` \| `step` ✓ |
| `loop` | both | `once` (default) \| `true`/`restart` \| `yoyo` ✓ |
| `orient` | `move` | `true` = rotate to face the path tangent ✓ |
| `channel` | both | explicit lane name; omit = derived per property ✓ |
| `mode` | both | `replace` (default) \| `queue` ✓ |

---

## Channels (lanes) & parallelism

- **Default channel is derived from the target**: `script:<layer>:<prop>`
  (e.g. `script:rotation`, `script:face:y`, `move` → `script:screen`). So
  distinct properties run at once and compose; re-animating the **same** property
  replaces it (two rotations can't physically coexist). ✓
- **Explicit `channel=`** groups several properties as one unit (a `jump` that
  moves *y* and *scale* together, started/stopped/replaced as one), or forces
  exclusivity ("body mood" that supersedes a previous one). ✓
- **Reserved engine channels** `base`/`blink`/`talk`/`gesture` are never touched
  by script animation — `anim`/`move` always live under `script:*` or a custom
  name, so they can't clobber idle/blink/lip-sync. ✓

### Conflict rule

If two active channels write the **same** property in one frame, the result is
**last-writer-wins** for that frame — deterministic, never undefined (Ren'Py
leaves parallel-property conflicts undefined; we don't). The per-property default
channel makes conflicts rare by construction.

---

## Lifecycle

- **`loop=once`** (default): plays once, then the lane is removed and the property
  returns to its base/idle value.
- **`loop=restart`** (`true`): repeats from the start.
- **`loop=yoyo`**: ping-pongs forward/back — with easing this is the cheap path to
  idle motion (breath, float, sway) without authoring return keyframes. ◻
- **Loops never block** the script — a looping animation can never deadlock a
  chapter (a documented footgun in Naninovel we design out). ✓
- **Stop**: `anim id=x stop` clears all `script:*` lanes; `anim id=x stop=<channel>`
  clears one. Hiding the entity or a structural hot-reload also clears. ◻ stop op
- **Transitions vs animation**: scene swaps (`fade`/`dim`/`flash`/`tint`) are a
  separate concept from per-entity property animation (Ren'Py `with` vs ATL).
  Keep them distinct; don't express a cross-fade as an `anim`. ✓

---

## Properties & easing

**Properties** (`prop`): `x` `y` (translate by a fraction of own size) ·
`screen_x` `screen_y` (move across the screen, fraction of screen) · `scale`
(uniform) · `scalex` `scaley` (squash/stretch) · `rotation` (degrees) · `alpha` ·
`frame` (swap a layer's sprite by an axis value — blink/lip-sync/curl). `move`
drives `screen_x`+`screen_y` together. ✓

**Easing** (`ease`): `linear` · `inOutSine` · `outCubic` · `outBack` · `inBack`
(extend freely; an easing is just `f(t∈[0,1])→[0,1]`). Shared by both render
paths via the one static sampler. ✓

**Interpolation** (`interp`) — *how* the curve runs between keys:

- `linear` (default): each segment is an independent eased tween. Passes exactly
  through every key; velocity may break at keys. Right for snappy/state-to-state.
- `spline`: Catmull-Rom **through** the keys — C¹, velocity flows through
  waypoints without stopping. Right for drifting through several points. ◻
- `step`: hold each key's value until the next (discrete). ◻

---

## Splines (phase 2)

Layered on the **same** keys — not a separate engine (the Ren'Py `knot` lesson).
All of it lives in the shared `ActorAnimator.Sample` / a new `SamplePath`, so it
benefits both render paths and existing catalog animations at once.

- **Value spline** (`interp=spline`): Catmull-Rom over key values.
  `m₁=(P₂−P₀)/2`, `m₂=(P₃−P₁)/2`, Hermite blend. Clamp or use a monotone variant
  for bounded props (alpha 0..1) to avoid overshoot.
- **Path spline** (`move … interp=spline`): **centripetal** Catmull-Rom (α=0.5)
  so arbitrary author-placed waypoints never cusp or loop.
- **Constant speed**: build an **arc-length** table once; the easing layer drives
  a normalized 0..1 distance → constant (or eased) speed regardless of waypoint
  spacing.
- **Orientation** (`orient=true`): rotate to the path tangent
  `θ = atan2(C′.y, C′.x)`.

---

## Compiled `.lvn` shape

`anim`/`move` compile to one runtime `anim` command carrying an `LvnAnim` payload
(same shape as catalog animations, so the runtime has one code path):

```json
{ "op": "anim", "id": "codel",
  "anim": {
    "loop": true, "duration": 2,
    "tracks": [
      { "prop": "rotation", "ease": "inOutSine",
        "keys": [[0,-4],[1,4],[2,-4]] }
    ]
  } }
```

`channel` is included only when the author set one; otherwise the runtime derives
it per property. `move` emits synced `screen_x`/`screen_y` tracks.

---

## Composition — named, reusable animations (phase 3)  ◻

The real power multiplier in Ren'Py/GSAP: define once, reference anywhere.

```
defanim shake  prop=x  keys="0:0 .05:-0.02 .1:0.02 .15:0"
defanim breathe prop=scale keys="0:1 1:1.04 2:1" loop=yoyo

play id=codel anim=shake
play id=guard anim=breathe
```

A named animation can also be referenced as a step inside another (self-similar
units), which turns a feature list into a small language.

---

## Implementation map

| Area | Where | Status |
|---|---|---|
| `anim`/`move` parse → `LvnAnim` payload | `tools/lvnconv/internal/lvns/convert.go` (`buildAnimCmd`) | ✓ |
| `anim` in runtime op registry | `tools/lvnconv/lvn/validate.go` | ✓ |
| `case "anim"` dispatch → play on channel | `Runtime/UI/VnStage.cs` (`ApplyAnim`, `ScenePlayAnim`) | ✓ |
| generic channel play | `Runtime/UI/ActorLayer.cs`, `Runtime/UI/World/WorldStage.cs` (`PlayAnim`) | ✓ |
| per-property channel derivation | `VnStage.ApplyAnim` | ✓ |
| keyframe sampling + easing (shared) | `Runtime/UI/ActorAnimator.cs` (`Sample`/`SampleFrame`), `WorldActor.Tick` | ✓ |
| `interp` (`spline` Catmull-Rom / `step`) | `LvnManifest.cs` (`LvnAnimTrack.interp`) + `ActorAnimator.Sample` | ✓ |
| `to=` one-liner tween (from rest value) | `convert.go` (`buildAnimCmd`, `propIdentity`) | ✓ |
| `loop=yoyo` (ping-pong) | convert.go + `ActorAnimator`/`WorldActor` (`Mathf.PingPong`) | ✓ |
| `stop=all` / `stop=<channel/prop>` | convert.go + `StopScript`/`StopTarget` + `VnStage.ApplyAnim` | ✓ |
| `mode=queue` (sequential on one lane) | convert.go + `PlayQueued`/per-channel FIFO in `ActorAnimator`/`WorldActor` (dequeues on completion) | ✓ |
| `orient` (face along the path tangent) | `ActorAnimator.OrientAngle` + `Composite`/`WorldActor.Tick` | ✓ |
| arc-length parameterised paths (constant speed on spline pairs) | `ActorAnimator.BuildArcTable`/`WarpProgress`/`ArcTime` | ✓ |
| `defanim`/`play` named anims (compile-time expansion) | convert.go (Convert: defAnims map) | ✓ |
| panel `lvns.wasm` rebuild (anim/move show in the IDE) | `panel/public/lvns.wasm` from `tools/lvnconv/wasm` | ✓ |

---

## Sources (why this design)

Ren'Py ATL (sequential-by-default, one interpolation primitive, `knot` splines,
`with` vs ATL split); Naninovel (one-liner tween, wait/easing, loop-must-not-
block); TyranoScript/Monogatari (parallel-by-default + explicit barriers, and
their pitfalls: twin waits, no stop verb); DOTween/GSAP/Unity Timeline
(lanes=parallel / steps=sequential, `Join`/`<` for parallel, nesting/composition,
loop modes incl. yoyo); spline math (Catmull-Rom, centripetal α=0.5, arc-length,
tangent orientation).
