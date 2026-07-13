# The `.lvns` language — full reference

`.lvns` is the human-readable source that the **lvnconv** transcoder compiles
into the `.lvn` container (a flat JSON array of commands), which is then executed
by the runtime (the Unity package `com.lvn.engine`). Write your game in `.lvns`;
`.lvn` is a machine artifact — never edit it by hand.

> This is the **source of truth for the syntax**. The parser lives in
> `tools/lvnconv/internal/lvns/convert.go`, the expression evaluator in
> `unity/Packages/com.lvn.engine/Runtime/LvnExpression.cs`. The container and
> commands are described in `docs/lvn-format.md`; staging in `docs/placement.md`;
> characters in `docs/cast.md`; animation in `docs/animation-system.md`.

All 12 examples in this folder are compiled and verified with this transcoder.

---

## 1. File skeleton

```
scene my-scene          // (optional) scene id — an informational tag

// comments use double slash; blank lines are ignored
bg /content/bg/room.jpg  // background
This is a narration line. // narration (no speaker)
Mara: Hi.                // dialogue line - "Name: text"

- Say hello -> hello     // choice - each line is "- text -> label"
- Leave -> bye

:hello                   // label - a jump target
You waved back.
-> __end                 // goto; __end is the built-in end of the script

:bye
-> __end
```

Core principles:

- **Top-to-bottom flow.** Commands run in order, except the ones that move the
  cursor: `-> label` (goto), `if`, `choice`, `call`/`return`.
- **Labels `:name`** are the only points you can jump to. `__end` is the
  built-in "end" label.
- **Any unknown `op` is a build error**, not a silent skip. Same for a jump to a
  nonexistent label. Run `lvnconv validate` (see below).

---

## 2. Dialogue, narration, emotions

| You write | What you get |
|---|---|
| `Plain text without a colon.` | Narration (`say` with no speaker). |
| `Mara: Line of dialogue.` | Dialogue: `Mara` goes on the name plate. |
| `Mara [smile]: Text.` | Dialogue + switching the cast emotion (the `emotion` axis). |
| `«multi-line … text»` | Guillemets keep text spanning several lines as one logical line. |

**`actor_map Name=id`** binds the displayed Name to the character's id in the
cast catalog, so that `Mara [smile]:` drives the `mara` cast entry. Without a
mapping, the id is derived as the lowercase name with spaces replaced by `_`.

Interpolation: `{expression}` in any text substitutes the value —
`Gold: {gold}, attack {atk + watk}.` Escape literal braces by doubling them:
`{{` and `}}`. Unbalanced `{`/`}` trigger a validator warning.

---

## 3. Staging: background, actors, objects

### Background
```
bg /content/bg/room.jpg              // terse: id is derived from the file name
bg id=room sprite_url="/path.jpg"    // legacy key=value
```

### Actor (a character — dims when not speaking)
```
actor mara left smile                // terse: id, position, emotion/pose
actor hero center w=.5 h=.6 x=.5 y=.5 armor={arm}
actor mara hide                      // hide (art is kept until show)
```
Terse form: the first token is the id; bare words are recognized as
`hide`/`show`, a named position (`left`/`center`/`right`/`far_left`/`far_right`/
`offscreen_left`/`offscreen_right`), or otherwise as a value for the `emotion`
axis (pose/emotion). `k=v` pairs: `w`(→width) `h`(→height) `x` `y` `scale` `anchor` `z`
`flip` `rotation` `opacity` `on_click`, plus any cast axes (`armor=`,
`weapon=`, …).

### Object (everything else — does NOT dim; use key=value)
```
obj id=letter sprite_url="/art/letter.png" x=0.7 y=0.45 width=0.18 anchor="0.5,0.5" z=5
obj id=play   sprite_url="/ui/play.png"    x=0.5 y=0.4  anchor="0.5,0.5" on_click="start"
```

### Placement fields (screen fractions, 0..1)
| Field | Meaning |
|---|---|
| `position` | Named horizontal slot (replaces `x`). |
| `x`,`y` | Where the object's anchor point sits (0,0 — top left; 1,1 — bottom right). Default `y`=1 (bottom of the screen). |
| `width`,`height` | Size in screen fractions. Default 0.46 × 0.62. |
| `anchor` | Custom anchor point `"ax,ay"`. Default `"0.5,1"` (bottom-center, "on the floor"). `"0.5,0.5"` is the center. |
| `z` | Draw order; higher is closer. |
| `flip` | `true` mirrors the sprite. |
| `rotation` | Degrees. `opacity` — 0..1. `show` — `false` hides. |

### `on_click` → clickable games
Give an object `on_click="label"` and it becomes a hotspot: a click jumps to the
label and **swallows the tap** (dialogue does not advance). A "screen" is a pause
(a narration line) with hotspots arranged around it, and the label routes back.
This plus `goto`/`if`/variables is enough for point-and-click, menus, gamebooks,
hidden-object games.

In the `.lvn` container, `on_click` also has an object form that sets variables
along the way: `"on_click": { "goto": "label", "set": { "flag": 1 } }` (the
validator checks the inner `goto` like a regular jump target).

---

## 4. Effects, sound, timing

| Command | Example | Meaning |
|---|---|---|
| `fade` | `fade to="black" duration=0.8` | Full-screen fade (`black`/`white`/`clear`). |
| `dim` | `dim alpha=0.6 duration=0.5` | Dim the scene (focus). `alpha=0` restores. |
| `flash` | `flash color="white" duration=0.3` | Flash. |
| `tint` / `blur` | `tint …` `blur …` | Color filter / blur. |
| `camera` | `camera action=shake amplitude=0.02 duration=0.4` | `shake`/`zoom`/`pan`. |
| `particles` | `particles type=rain on=true` | Particle layer (`rain`/`snow`/…), `on=false` turns it off. |
| `audio` | `audio channel=music action=play url="/a.ogg"` | Channels `music`/`sfx`/`ambient`; `play`/`stop`/… |
| `wait` | `wait ms=500` | Pause before the next command. |
| `hint` | `hint text="…" show=true duration=0` | Pop-up hint at the top center of the scene. `show=false` removes it; `duration>0` auto-hides after N seconds. Text interpolates `{vars}`. |
| `preload` | `preload assets=…` | Hint for the loader to warm up assets. |
| `text_pace` | `text_pace cps=40` | Typing speed (chars/sec; `0` — instant). |

---

## 5. Control flow

### Jumps and branches
```
-> label                 // unconditional jump (goto)
if cond -> label         // if true — jump, otherwise fall through
```

### Block `if / else`
```
if gold >= 10 {
  gold = gold - 10
  Purchased.
} else {
  Not enough.
}
```
A single-line form works too: `if c { … } else { … }`. The `{` brace must
end the opening line (or use the single-line form — the transcoder unrolls it
itself).

### Choice
```
- Option text -> label
- With parameters -> label cost="3 turns" expr="gold >= 5"
```
Option parameters: `cost=` (just a "price" caption — **it deducts nothing by
itself**; subtract resources explicitly in the handler label),
`requires_stat`/`min` (threshold — the option is hidden if the variable < min),
`expr=` (boolean filter — **the option is hidden when the expression is
false**). In the `.lvn` container an option may carry a `body` instead of
`goto` — an inline command list executed on selection (only `set`/`inc`/
staging commands and `goto`; **no** `if`/`choice`/`call` inside — move complex
logic to a separate label).

### Loops
```
for it in inv {          // iterate a collection
  Item - {it}.
}

while xp >= need {        // while the condition holds
  xp = xp - need
  level = level + 1
}
```

### Subroutines
```
call fight               // jump that remembers the return point
return                   // return after the matching call
```

### Functions (sugar over call/return)
```
func show_hero() {
  actor hero left armor={arm} weapon={wpn}
}
func add(a, b) {
  return a + b
}

show_hero()              // call
sum = add(2, 3)          // call with a return value
```
Arguments bind to parameters **positionally**. Return a value with
`return <expression>`.

### Save/load
```
save                     // snapshot the state
load                     // restore the snapshot
```

---

## 6. Variables and state

```
gold = 12                // assignment (both declaration and mutation)
gold = gold - 6
name = "Mara"
inv = []                 // empty list
flags = {}               // empty map (via builder, see below)
```

- Any **undeclared variable reads as `0`/`""`/`false`** — but explicit
  initialization makes the script clearer.
- **The `__` prefix is reserved** for auto-variables of the transcoder/runtime
  (`__ret`, `__seen_*`, `__i1` …). Do not name your variables like that.
- Dotted names are allowed: `ns.flag` accesses an object field (`set/inc key="ns.flag"`
  writes into the `ns` object, `if ns.flag` / `{ns.flag}` read it).
- **Cross-novel player stats use the `global.` prefix**: everything under
  `global.` accumulates PER PLAYER and is shared across all novels (one novel
  reads what another left behind):
  `set key="global.reputation" value=1`, `inc key="global.visits" by=1`,
  `if global.reputation >= 5 -> ...`, `{global.visits}`. Persisted in a separate
  shared blob (`__global`), synced like regular stats. Restarting a chapter does
  not roll them back. Plain variables (without `global.`) are scoped to the
  novel, as before.
- **Value types:** number, string, bool, `null`, list `[]`, map `{}`.
- In the `.lvn` container, `if` accepts not only a string `expr` but also a
  **structural** `cond` — `{ "key": "courage", "op": "gte", "value": 2 }`
  (`eq/ne/lt/lte/gt/gte`); `set` takes `value` (literal) or `expr` (expression,
  takes precedence); `inc` takes `by` (default `+1`). In `.lvns` you usually
  write the short form (`gold = gold + 1`, `if gold >= 2 -> …`) and the
  transcoder emits the right shape.

---

## 7. Expressions

Operators: `+ - * /`, comparisons `== != > >= < <=`, logic `&& || !`,
parentheses. String literals are quoted. Inside `«…»` text escape quotes as `\"`
(e.g. `expr="has(inv, \"key\")"`).

### Built-in functions (evaluator — `LvnExpression.cs`)

**Numbers and randomness**
| Function | Result |
|---|---|
| `rand()` | float 0..1 |
| `rand(n)` | integer 0..n inclusive |
| `rand(a, b)` | integer a..b inclusive |
| `chance(p)` | `true` with probability p (default 0.5) |
| `min(a,b)` `max(a,b)` | minimum / maximum |
| `abs(x)` `floor(x)` `round(x)` | absolute / floor / rounding |

**Reading collections**
| Function | Result |
|---|---|
| `len(x)` | length of a list/map/string |
| `has(coll, x)` | whether the element / key / substring exists |
| `get(coll, key[, default])` | safe read |
| `indexof(arr, x)` | index or `-1` |
| `count(arr, x)` | number of occurrences |
| `sum(arr)` | sum of numbers |
| `first(arr)` `last(arr)` | first / last |
| `keys(obj)` `vals(obj)` | map keys / values |

**Building collections** (they return a NEW value — assign it back: `inv = push(inv, x)`)
| Function | Result |
|---|---|
| `list(a, b, …)` | new list |
| `push(arr, x)` | list + element at the end |
| `pop(arr)` | list without the last element |
| `removeat(arr, i)` | list without the element at index i |
| `remove(arr, x)` | list without the first element equal to x |
| `slice(arr, s[, e])` | slice |
| `concat(a, b, …)` | list concatenation |
| `put(map, key, val)` | map with the key set |
| `del(map, key)` | map without the key |

> Note: there is no `ceil` — use `floor(x)` or `round(x)`.

---

## 8. Cast — parametric characters

A character is a **named entity** with **axes** (pose, emotion, outfit…), and
its art is a set of **layer templates** with `{axis}` tokens. To show a
character in any state, you name the entity and the axis values; the runtime
substitutes the tokens and stacks the layers bottom-up.

```json
"cast": {
  "mara": {
    "name": "Mara",
    "layers": [
      "/art/mara/body_{pose}.png",
      "/art/mara/face_{emotion}.png",
      "/art/mara/{prop}.png"
    ],
    "defaults": { "pose": "stand", "emotion": "neutral" }
  }
}
```

- A token resolves → the layer is drawn; a token without a value → the layer is
  **skipped** (so optional parts like `{prop}` appear only on request).
- **K poses + M emotions = K + M images, not K × M** — the savings come from
  templates.
- No paths in the script: `actor mara emotion=smile pose=arms prop=umbrella`.

⚠ **A cast CANNOT be defined in `.lvns`** — there is no cast directive there.
The script only **references** a character by id (`actor mara …`). The
definition itself lives in **`manifest.json` → `sprites`** (global for all
chapters) or in the `cast` block of `.lvn` (local). The convenient way to
create/edit characters is the visual cast editor (the IDE panel), which
writes to `manifest.json`. The full cast and asset pipeline (resolution, adding
art, paths, placeholders) — [`CAPABILITIES.md`](CAPABILITIES.md)
§7; the model — `docs/cast.md`.

---

## 9. Animation — `anim` and `move`

One rule: **different channels run in parallel, keys within a channel run in
sequence.** Two verbs, both compile into a single runtime command `anim`.

```
// One-liner (tween from the current value to the target):
anim mara scale to=1.1 dur=0.4 ease=outBack
move id=mara to=0.2,-0.05 dur=1 ease=inOutSine

// Terse forms: a bracket list of values, stretched over dur:
anim mara scale [1 1.03 1] 3s yoyo

// "time:value" keyframes — needs the legacy id=/prop=/keys= form
// (the terse form breaks on spaces inside quotes):
anim id=mara prop=rotation keys="0:0 1:8 2:-8 3:0" loop=yoyo ease=inOutSine
move id=mara path="-0.18,0.04 0,-0.03 0.18,0.04" dur=2 ease=outCubic

// Stop an entity's script animations:
anim mara stop
```

Properties (`prop`): `x` `y` (offset in fractions of own size) · `screen_x` `screen_y`
(movement across the screen) · `scale` `scalex` `scaley` · `rotation` · `alpha` · `frame`
(switch a layer's frame along an axis). Easing: `linear` `inOutSine` `outCubic` `outBack`
`inBack`. `loop`: `once`(default) / `true`(restart) / `yoyo`. Interpolation
between keys: `interp=linear`(default) / `spline` (smooth Catmull-Rom through
the keys) / `step` (holds the value until the next key); a typo in the value is
a compile error. `move` supports `orient=true` — the actor rotates along the
path tangent (respects easing and spline). Parallelism is just two `anim`
lines. A looping animation **never blocks** the script.

```
// A smooth arc through three points + turning along the direction of motion:
move id=mara path="0.1,0.8 0.5,0.4 0.9,0.8" dur=2 interp=spline orient=true
```

Quoting rule: values with **spaces** (`keys="…"`, `path="…"`) require the
legacy form with `id=`/`prop=`. The bracket list `[…]` and the `to=` one-liner
work in the terse form too. The full spec and feature statuses —
`docs/animation-system.md`.

---

## 10. Building and checking

```sh
# build the transcoder
cd tools/lvnconv && go build -o /tmp/lvnconv .

# compile .lvns → .lvn
/tmp/lvnconv convert -i howto/visual-novel/visual-novel.lvns -o /tmp/out.lvn

# structural check (unknown op, dangling jumps, duplicate labels)
/tmp/lvnconv validate /tmp/out.lvn          # -strict: warnings = errors

# short summary of the .lvn
/tmp/lvnconv probe /tmp/out.lvn
```

Run `validate` after every edit. A good target is **0 warning(s)**: the most
common warning "label … reached by fall-through" means the jump target label is
also being "fallen into" from above — put an explicit `-> label` or `-> __end`
before it (see the idiom in the examples).

---

## 11. Where to go next

- `howto/AGENTS.md` — the entry point and mental model (especially for an AI agent).
- `howto/CAPABILITIES.md` — **what the engine can and CANNOT do** (runtime + limits).
- `howto/CHEATSHEET.md` — this whole page condensed onto one screen.
- `howto/README.md` — the genre map and quick start.
- `howto/recipes.md` — short reusable patterns.
- `howto/<genre>/` — a guide + working example for each game type.
- Large real scripts: `server/content/scripts/` (`rpg-inv.lvns`,
  `goblin-battle.lvns`, `showcase.lvns`, `soviet-ch1.lvns`).
