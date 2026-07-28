<!-- СГЕНЕРИРОВАНО. Правь исходные файлы в howto/, затем пересобери:
     go test ./server -run TestAgentBundleIsUpToDate -update -->

# Шпаргалка: весь язык на одной странице

<!-- источник: howto/CHEATSHEET.md -->

## `.lvns` Cheatsheet (one page)

A dense syntax map. Details — [`LANGUAGE.md`](LANGUAGE.md);
capabilities/limits — [`CAPABILITIES.md`](CAPABILITIES.md).

```
scene my-game                  // (recommended) chapter header
// comment
```

### Text
```
Just a plain line.             // narration
Mara: A spoken line.           // speaker: text
Mara [smile]: A spoken line.   // + cast emotion (emotion axis)
voice "/content/voice/a1.ogg"  // voiceover for the NEXT line (auto-stops on a new one)
voice "/content/voice/a1.ogg"  // voiceover for the NEXT line (auto-stops on a new one)
actor_map Mara=mara            // bind Name ↔ cast id
«A long text,                  // guillemets = multiline
 over a couple of lines.»
Gold: {gold}, atk {atk+5}.     // interpolation {expression}
{{ and }}                      // literal curly braces
```

### Choices and jumps
```
- Option A -> labelA           // menu (consecutive «- » lines)
- Option B -> labelB cost="3 turns"
- Hidden -> labelC expr="gold >= 5"       // option hidden when false
choice timeout=10 timeout_goto=late       // ⏱ timer for the NEXT menu: a bar
- Make it -> ok                           //   above the buttons, expired → late
input var=name prompt="Who are you?" default="Guest" max=24   // text input → {name}
-> label                        // unconditional jump (goto)
:label                          // target label
-> __end                        // built-in ending
```

### Conditions and state
```
gold = 12                       // assign (declaration = mutation)
gold = gold - 6
name = "Mara"                   // ONE statement per line (a second `x = …` on
inv = []                        //   the same line lands inside the first expr)
if gold >= 10 -> rich           // true → jump; otherwise fall through
if has(inv,"key") {             // if/else block
  ...
} else {
  ...
}
```

### Loops, subroutines, functions
```
for it in inv { Item — {it}. }
while xp >= need {              // one statement per line inside a block
  xp = xp - need
  level = level + 1
}
call fight                      // jump with return
return                          // come back after call

func add(a,b){ return a + b }   // EXPRESSION function: a single `return <expr>`,
s = add(2,3) * add(1,1)         //   INLINED at compile time → usable in any
Total {add(gold,tax)}.          //   expression, {interpolation}, if, choice expr=
func show_hero() {              // PROCEDURE: commands, so it's a STATEMENT
  actor hero left armor={arm}
}
show_hero()                     // call it on its own line
                                // no recursion in either kind (compile error)
save                            // snapshot (default slot)
load
def enter actor mara left smile x=.24   // preset: names a line prefix…
enter armor=chain               // …usage expands it (extra k=v appends)
```

### Staging
```
bg /content/bg/room.jpg                 // background
actor mara left smile                   // character: id, position, emotion/pose
actor hero center w=.5 h=.6 x=.5 armor={arm}
actor mara hide                         // hide
obj id=key sprite_url="/ui/key.png" x=.2 y=.7 anchor="0.5,0.5" on_click="take"
text hud x=4 y=8 size=42 color=#f1e4c9 «♥{hp}/{maxhp}  💰{gold}»   // reactive HUD (200ms)
text hud hide
```
Positions: `far_left left center_left center center_right right far_right`.
Fields: `w`(width) `h`(height) `x` `y` `scale` `anchor="ax,ay"` `z` `flip` `rotation` `opacity` `on_click`.

### Effects / sound / timing
```
fade to="black" duration=0.8     dim alpha=0.6 duration=0.5     flash color="white" duration=0.3
tint ...    blur ...
camera action=shake amplitude=0.02 duration=0.4      // shake/zoom/pan/reset
particles type=rain on=true                          // rain/snow
audio channel=music action=play url="/a.ogg"         // music/sfx/ambient; play/stop
wait ms=500
```

### Animation (channels || in parallel, keys within a channel run in order)
```
anim mara scale to=1.1 dur=0.4 ease=outBack          // one-liner (terse ok)
anim mara scale [1 1.03 1] 3s yoyo                   // bracket list (terse ok)
anim id=mara prop=rotation keys="0:0 1:8 2:-8 3:0" loop=yoyo ease=inOutSine  // keys= → legacy id=/prop= ONLY
move id=mara path="-0.2,0.5 0.28,0.5" dur=1.2 ease=outCubic                  // path= → legacy id= ONLY
anim mara stop
```
props: `x y screen_x screen_y scale scalex scaley rotation alpha frame` · ease: `linear inOutSine outCubic outBack inBack` · loop: `once|restart|yoyo`.

### Built-in functions (expressions)
```
rand() 0..1 float · rand(n) 0..n · rand(a,b) a..b   // both ends INCLUSIVE
chance(p)  // p is a FRACTION, not percent: chance(0.35), never chance(35)
min(a,b) max(a,b)   // first two arguments only — extra ones are ignored
abs floor round     // NO ceil
len(x) has(coll,x) get(coll,k[,def]) indexof(arr,x) count(arr,x) sum(arr) first(arr) last(arr) keys(o) vals(o)
list(...) push(arr,x) pop(arr) removeat(arr,i) remove(arr,x) slice(arr,s[,e]) concat(...) put(m,k,v) del(m,k)
```
Operators: `+ - * /` · `== != > >= < <=` · `&& || !`. An unset variable = `0`/`""`/`false`.
The list above is **closed** — there are no other functions. Your own go in a
`func` (see above), which the compiler inlines; a call to anything else is a
validator warning, because at runtime it would evaluate to nothing.

### Build / validate
```
cd tools/lvnconv && go build -o /tmp/lvnconv .
/tmp/lvnconv convert -i game.lvns -o /tmp/game.lvn
/tmp/lvnconv validate /tmp/game.lvn        # goal: OK ... 0 warning(s)
```

### ⚠ Limit traps
- The cast is defined in `manifest.json`/the `cast` block, **not in `.lvns`** (there you only get `actor <id>`).
- `keys=`/`path=` with spaces → use the `id=`/`prop=` form (compile error otherwise).
- `hint text="…" duration=6` — a popup at the top center; `show=false` removes it, `duration>0` auto-hides. `cost`/`requires_stat` do **not** deduct resources themselves — deduct explicitly via `set`/`inc`.
- No `ceil` (round with arithmetic). A timer and text input DO exist: `choice timeout=` and `input var=`.
- **One statement per line.** `gold = gold - 5  potions = potions + 1` on a single
  line puts the second half inside the first expression, where it evaluates to
  nothing (the validator warns about the stray `=`).
- **`func` has no recursion** — an expression `func` is inlined, a procedure has no
  frames. Both report it as a compile error instead of guessing.
- Before a jump-target label that can be «fallen into» from above, put `-> __end`/`-> label`.

---

# С чего начать (модель, рабочий цикл, типичные ошибки)

<!-- источник: howto/AGENTS.md -->

## Elvin — how to make a game (start here)

Entry point for building a game on the **Elvin** engine. After this file you can
build a game in any supported genre without reading the engine sources — everything
you need is in `howto/` and `docs/`.

### Mental model

```
    source              Elvin                 Unity
  ┌──────────┐  lvnconv ┌──────────┐  loads ┌──────────┐  plays ┌──────────┐
  │  .lvns   │ ───────► │  .lvn    │ ──────► │ runtime  │ ─────► │  screen  │
  │ (game    │ convert  │ (JSON,   │         │(com.lvn. │        │          │
  │  text)   │          │ commands)│         │ engine)  │        │          │
  └──────────┘          └──────────┘         └──────────┘        └──────────┘
        ▲                                          ▲
        │ human-readable,                          │ CAST and ASSETS come from manifest.json,
        │ this is what you write                   │ NOT from .lvns
```

- **`.lvns`** (Elvin Script) — the human-readable game source. This is what you write.
- **`.lvn`** — machine JSON (a flat list of commands). Generated, never edited by hand.
- **Runtime** — the Unity package `com.lvn.engine`, executes `.lvn`.
- **`manifest.json`** — holds the cast (characters), assets, and the chapter table
  of contents separately. A character cannot be defined in `.lvns` — it is only
  referenced by id (`actor mara …`); the cast definition lives in the manifest
  (see `CAPABILITIES.md` §7).

All logic, branching, stats, combat, and economy are described by commands in
`.lvns`; the engine plays them as a real game in Unity, with no code for the
dialogue/branching system.

### Workflow

In Unity it is enough to drop a `.lvns` into `Assets/` — the ScriptedImporter
compiles it automatically. For CLI/CI/checking:

```sh
cd tools/lvnconv && go build -o /tmp/lvnconv .          # once
/tmp/lvnconv convert -i path/game.lvns -o /tmp/game.lvn # .lvns → .lvn
/tmp/lvnconv validate /tmp/game.lvn                     # goal: OK ... 0 warning(s)
/tmp/lvnconv probe    /tmp/game.lvn                     # brief summary
```

After every edit — `convert` + `validate` until `0 warning(s)`. The validator
catches dangling jumps, unknown commands, duplicate labels, and "chapter ended
early". What it checks — `CAPABILITIES.md` §9. That is enough to verify game
correctness without launching the engine.

### What you can build (and what you can't)

Any game driven by **choice and state**: visual/kinetic novels, gamebooks/CYOA,
point-and-click and adventure games, RPGs, dating sims, quizzes, detective
stories, tycoons, roguelikes, puzzles. Worked examples live in the genre folders.

You **do** get two doses of real clock and real typing: `choice timeout=10
timeout_goto=late` (a countdown bar over the menu) and `input var=name
prompt="…"` (a text-entry overlay that blocks the story until confirmed) —
the playground's own default scene uses both.

What is missing (full list — `CAPABILITIES.md` §8): a script-visible game loop
(no `every`/`sleep`/clock ticks — `wait ms=` is a fixed pause, not a
condition), keyboard input beyond that `input` overlay (no hotkeys, no
key-down), and timing-based arcade mechanics. Otherwise time is measured in
**turns**, and interaction is a `choice` or a click on an `obj on_click`.

### Documentation map

| Question | File |
|---|---|
| Full `.lvns` syntax | [`LANGUAGE.md`](LANGUAGE.md) |
| What the engine can and can NOT do (runtime + limits) | [`CAPABILITIES.md`](CAPABILITIES.md) |
| Dense one-screen cheatsheet | [`CHEATSHEET.md`](CHEATSHEET.md) |
| Reusable patterns | [`recipes.md`](recipes.md) |
| Guide + working example per genre | `howto/<genre>/` (see [`README.md`](README.md)) |
| Dressing a character (wardrobe) | [`wardrobe/`](wardrobe/) |
| Importing a novel from articy files | [`import-articy/`](import-articy/) |
| Object placement, hotspots | `../docs/placement.md` |
| Cast (parametric characters) | `../docs/cast.md` |
| Animation (full spec) | `../docs/animation-system.md` |
| `.lvn` container contract | `../docs/lvn-format.md` |
| Large real games | `../server/content/scripts/*.lvns` |

Order for a new task: this file → `CHEATSHEET.md` → the nearest genre's example →
`LANGUAGE.md`/`CAPABILITIES.md` as needed.

### A complete minimal game

```
scene hello

bg /content/bg/room.jpg
gold = 0
A stranger holds out a coin to you.
- Take it -> take
- Refuse -> refuse

:take
gold = gold + 1
You now have {gold} gold.
-> __end

:refuse
You shook your head and walked away.
-> __end
```

From here it grows: variables → `if` → `call`/`return` subroutines → a reactive
HUD `text` → staging (`actor`/`anim`/`fade`). Each step is covered in the genre
examples.

### Common mistakes

1. **Defining a character in `.lvns`.** Not possible — the cast lives in
   `manifest.json` (`sprites`) or in the `.lvn` `cast` block; the script only has
   `actor <id> …`.
2. **`anim`/`move` with quoted spaces in terse form.** `keys="…"`/`path="…"`
   require the legacy form `id=`/`prop=`, otherwise it is a compile error.
   Bracket `[…]` and `to=` work in terse form (`CAPABILITIES.md` §6).
3. **`hint`** draws a window at the top center (`hint text="…" duration=6`); for
   a persistent HUD label use a reactive `text`, not `hint`.
4. **Expecting `cost`/`requires_stat` to deduct the resource themselves.** They
   don't: `cost` is a caption, gates only show/hide the option. Deduct explicitly
   with `set`/`inc`.
5. **Relying on a real-time timer.** There is none — measure in turns.
6. **Calling `ceil`.** No such function — use `floor`/`round`.
7. **Falling through into a jump target.** If a label that is jumped to can also
   be "fallen into" from above — put `-> label`/`-> __end` before it.
8. **Variables prefixed with `__`** are reserved; do not use them for your own.
9. **Treating missing art as an error.** It isn't: the layer is simply skipped,
   the logic is visible even without graphics.

### Readiness checklist

- The `.lvns` compiles (`convert` with no errors) and `validate` reports `0 warning(s)`.
- All `choice`/`if` branches lead to existing labels or `__end`.
- The reactive HUD (if any) shows up-to-date variables.
- The cast referenced by `actor <id>` is defined in the manifest — or it is a
  deliberate greybox.
- Stable label/ending ids do not change (matters for save/load).

---

# Полное описание языка

<!-- источник: howto/LANGUAGE.md -->

## The `.lvns` language — full reference

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

### 1. File skeleton

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

### 2. Dialogue, narration, emotions

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

### 3. Staging: background, actors, objects

#### Background
```
bg /content/bg/room.jpg              // terse: id is derived from the file name
bg id=room sprite_url="/path.jpg"    // legacy key=value
```

#### Actor (a character — dims when not speaking)
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

#### Object (everything else — does NOT dim; use key=value)
```
obj id=letter sprite_url="/art/letter.png" x=0.7 y=0.45 width=0.18 anchor="0.5,0.5" z=5
obj id=play   sprite_url="/ui/play.png"    x=0.5 y=0.4  anchor="0.5,0.5" on_click="start"
```

#### Placement fields (screen fractions, 0..1)
| Field | Meaning |
|---|---|
| `position` | Named horizontal slot (replaces `x`). |
| `x`,`y` | Where the object's anchor point sits (0,0 — top left; 1,1 — bottom right). Default `y`=1 (bottom of the screen). |
| `width`,`height` | Size in screen fractions. Default 0.46 × 0.62. |
| `anchor` | Custom anchor point `"ax,ay"`. Default `"0.5,1"` (bottom-center, "on the floor"). `"0.5,0.5"` is the center. |
| `z` | Draw order; higher is closer. |
| `flip` | `true` mirrors the sprite. |
| `rotation` | Degrees. `opacity` — 0..1. `show` — `false` hides. |

#### `on_click` → clickable games
Give an object `on_click="label"` and it becomes a hotspot: a click jumps to the
label and **swallows the tap** (dialogue does not advance). A "screen" is a pause
(a narration line) with hotspots arranged around it, and the label routes back.
This plus `goto`/`if`/variables is enough for point-and-click, menus, gamebooks,
hidden-object games.

In the `.lvn` container, `on_click` also has an object form that sets variables
along the way: `"on_click": { "goto": "label", "set": { "flag": 1 } }` (the
validator checks the inner `goto` like a regular jump target).

---

### 4. Effects, sound, timing

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

### 5. Control flow

#### Jumps and branches
```
-> label                 // unconditional jump (goto)
if cond -> label         // if true — jump, otherwise fall through
```

#### Block `if / else`
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

#### Choice
```
- Option text -> label
- With parameters -> label cost="3 turns" expr="gold >= 5"
```
Option parameters: `cost=` (just a "price" caption — **it deducts nothing by
itself**; subtract resources explicitly in the handler label),
`requires_stat`/`min` (threshold — the option is hidden if the variable < min),
`expr=` (boolean filter — **the option is hidden when the expression is
false**).

An option can also carry a **body** — commands that run the moment it is
picked, before the jump. Write it as a `{ … }` block on the option line (the
brace ends the line, the `}` stands alone):
```
- Why were you in prison? -> q_prison expr="!_once_prison" {
    _once_prison = true
}
- Close the menu {
    menu_open = false
}
```
The first is the "ask this once" shape: the flag is set on pick, so its own
`expr` gate hides the option afterwards. The second has no `-> label` at all —
it runs its body and the flow falls through past the choice.

Keep a body to **`set`/`inc` and the jump**. Those are state, and state is what
a save restores. **Staging in a body does not survive a save**: the resume
rebuilds the scene from a trace of command *indices*, and a body command has no
index in the script — so an `actor`/`bg`/`hint`/`fade`/`audio` inside a body
plays once and is gone the moment the player loads. Put staging after the label
instead (the validator warns if you don't).

A body is also **flat**: no `if`/`choice`/`call` and no nested block — the
compiler rejects those outright, because the runtime would forward them to the
stage and drop them without a trace. Move branching to a label.

#### Loops
```
for it in inv {          // iterate a collection
  Item - {it}.
}

while xp >= need {        // while the condition holds
  xp = xp - need
  level = level + 1
}
```

#### Subroutines
```
call fight               // jump that remembers the return point
return                   // return after the matching call
```

#### Functions

`func` writes two different things, and the **body** decides which one you get.
Arguments bind to parameters **positionally** in both.

**1. An expression function** — the body is a single `return <expression>`:

```
func add(a, b) { return a + b }
func upkeep(day) {
  return 2 + floor(day / 3)
}
```

The compiler **inlines** it: `tax = upkeep(day)` compiles to
`set key=tax expr="(2 + floor(day / 3))"`. The declaration emits no commands at
all, and because the result is an ordinary expression it works in every place an
expression is evaluated:

```
gold = gold - upkeep(day)                    // assignment
The stop cost {upkeep(day)} coins.           // {interpolation}
text hud … «⛽{upkeep(day)}»                  // reactive HUD
if upkeep(day) > 5 -> lean_week              // condition
- Pay up -> pay expr="gold >= upkeep(day)"    // choice filter
```

Expression functions may call each other and the built-ins; the whole chain is
inlined. An argument that is not a bare name or number is bracketed, so
`upkeep(day + 1)` keeps its arithmetic. A name in the body that is *not* a
parameter is an ordinary variable, read at the call site. A `func` may not take a
built-in's name. **Recursion is a compile error** — a function inlined into
itself has no end; use a `while` loop.

**2. A procedure** — the body is commands:

```
func show_hero() {
  actor hero left armor={arm} weapon={wpn}
}

show_hero()              // called as a STATEMENT, on its own line
```

This is the `call`/`return` sugar: it lowers to a `label __fn_show_hero` routine
plus a `call`, and arguments are bound to ordinary variables just before the
jump — so there are no frames and no recursion here either. A procedure has no
value, so it **cannot appear inside an expression** (`x = show_hero() + 1` is a
compile error). If a procedure does `return <expression>`, the value is available
in the statement form `x = the_procedure(…)`.

Every mismatch is a compile error rather than a silent zero: wrong number of
arguments, a procedure used as a value, an expression function used as a bare
statement, a duplicate declaration, recursion. A worked example lives in
[`functions/`](functions/).

#### Save/load
```
save                     // snapshot the state
load                     // restore the snapshot
```

#### Presets — `def`
```
def code text code x=3 y=12.5 size=50 color=#9fe8a8   // name a line prefix
def enter actor hill left idle x=.24 y=.92 h=1.3

code «actor hill hair=red»     // → text code x=3 y=12.5 … «actor hill hair=red»
enter                          // → actor hill left idle x=.24 y=.92 h=1.3
enter hair=red                 // usage args append — later k=v wins
```
A pure compile-time text macro: the runtime never sees a `def`. Define before
use; a preset may expand to another preset (recursion depth is capped). A def
cannot shadow a built-in op.

---

### 6. Variables and state

```
gold = 12                // assignment (both declaration and mutation)
gold = gold - 6
name = "Mara"
inv = []                 // empty list
flags = {}               // empty map (via builder, see below)
```

- Any **undeclared variable reads as `0`/`""`/`false`** — but explicit
  initialization makes the script clearer.
- **One statement per line**, inside a block too. `gold = gold - 5  potions =
  potions + 1` on one line makes the second half part of the FIRST expression,
  which then evaluates to nothing at all (the validator warns about the stray
  `=`, but the honest fix is a line break).
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

### 7. Expressions

Operators: `+ - * /`, comparisons `== != > >= < <=`, logic `&& || !`,
parentheses. String literals are quoted. Inside `«…»` text escape quotes as `\"`
(e.g. `expr="has(inv, \"key\")"`).

#### Built-in functions (evaluator — `LvnExpression.cs`)

**Numbers and randomness**
| Function | Result |
|---|---|
| `rand()` | float 0..1 |
| `rand(n)` | integer 0..n inclusive |
| `rand(a, b)` | integer a..b inclusive |
| `chance(p)` | `true` with probability p, a **fraction** 0..1 (default 0.5). `chance(35)` is not "35 %" — it is always true. |
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

**This table is the complete set.** The evaluator has no user-defined functions:
your own live in a `func` (§5), which the compiler inlines before the runtime ever
sees the expression. A call to any other name is a validator warning, because a
failed expression degrades softly at runtime — the variable keeps its old value
and a `{span}` prints verbatim, with nothing to tell the player something broke.

---

### 8. Cast — parametric characters

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

### 9. Animation — `anim` and `move`

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

### 10. Building and checking

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

### 11. Where to go next

- `howto/AGENTS.md` — the entry point and mental model (especially for an AI agent).
- `howto/CAPABILITIES.md` — **what the engine can and CANNOT do** (runtime + limits).
- `howto/CHEATSHEET.md` — this whole page condensed onto one screen.
- `howto/README.md` — the genre map and quick start.
- `howto/recipes.md` — short reusable patterns.
- `howto/<genre>/` — a guide + working example for each game type.
- Large real scripts: `server/content/scripts/` (`rpg-inv.lvns`,
  `goblin-battle.lvns`, `showcase.lvns`, `tour-ch01.lvns`).

---

# Возможности движка и его пределы

<!-- источник: howto/CAPABILITIES.md -->

## Elvin engine capabilities and limits

This is a **map of what the engine can and can NOT do**, verified against the sources
(the `tools/lvnconv/` transcoder, the `unity/Packages/com.lvn.engine/Runtime/` runtime).
The goal is that "can we do X" decisions get made from this file, **without
reading the code**.

Read the markers literally:

- **✅** — it exists in the reference runtime **and** some example under `howto/`
  compiles it (CI proves both).
- **❌** — it does not exist; work around it with language means (variables,
  labels, loops) instead of hoping "it will somehow work".
- **⚠** — it exists, but not for the case you probably mean (a host package owns
  it, or it is only reachable from the manifest / raw `.lvn`). Read the row.

This file is a contract, not prose: `tools/lvnconv/lvn/docs_contract_test.go`
fails the build if the §1 catalog drifts from the validator's op registry, if the
file both claims and denies the same construct, or if something marked ✅ has no
example that compiles it. If you find yourself editing a claim here, edit the
example too.

Related docs: syntax — [`LANGUAGE.md`](LANGUAGE.md); orientation for an AI agent
— [`AGENTS.md`](AGENTS.md); cheatsheet — [`CHEATSHEET.md`](CHEATSHEET.md);
the witness example for the rarer commands —
[`every-command/`](every-command/).

---

### 0. What can be built at all

The engine is an executor of a flat command list (`.lvn`) with variables, branching,
subroutines, a reactive HUD, staging (background/actors/effects/sound) and
scripted animation. That is enough for:

> visual novels, kinetic novels, gamebooks/CYOA, point-and-click and
> adventure games, RPGs (stats/combat/inventory/levels), dating sims, quizzes, detective
> stories, tycoon/management, roguelike runs, puzzles, and almost any game
> driven by **buttons + state**.

What the engine fundamentally does NOT do: it is **not** a physics/realtime engine.
There is no real-time game loop available to the script, no arbitrary
keyboard/mouse input beyond clicking objects, picking menu options and the `input`
overlay, no timing-based arcade mechanics. The script sees the clock only in
fixed doses — `wait ms=` and a `choice timeout=` countdown; everything else is
measured in **turns/player actions**, not clock hours.

---

### 1. Full command catalog (runtime behavior)

These are all the `op`s the runtime understands (registry — `validate.go` `KnownOps`;
this catalog is pinned to it by a test, so it can not drift). Any other `op` is a
build error — the one escape hatch is `ext <op> k=v …`, which compiles a
**host-defined** op that the game's own C# handles via `LvnOps.Register` (see
`docs/embedding.md`). In `.lvns` you write the human-readable form (see
`LANGUAGE.md`), which compiles into these commands.

#### Text and choice
| op | What it does at runtime | Fields |
|---|---|---|
| `say` | Shows a line; interpolates `{expression}`; waits for a click (except when a `choice` follows). Voice-over: the `voice` field (in `.lvns` — a `voice "<url>"` line before the dialogue line) — the clip starts with the text and is muted by the next line/reset; volume — the "Voice" slider (key `voice`), the typing blip stays silent under voice. | `text`, `who?`, `style?`, `voice?` |
| `choice` | Choice menu. Filters options by `requires_stat`/`min` and `expr` (options that fail are **hidden**, not "grayed out"). Timer: `timeout` (sec) + `timeout_goto` — a countdown bar above the buttons; expiry sends the flow to a branch (in `.lvns`: a `choice timeout=10 timeout_goto=late` line before the `- …` block; menus/art view freeze the clock). | `options[]`, `timeout?`, `timeout_goto?` |
| `text` | Persistent **reactive** HUD label: the template is re-evaluated **every 200 ms**. `hide=true` removes it. | `id`, `text`(template), `x`,`y`,`anchor`,`size`,`color`,`font`,`hide` |
| `text_pace` | Text typing speed (chars/sec; `0` — instant). | `cps` |

#### Control flow
| op | What it does | Fields |
|---|---|---|
| `label` | Jump target label (a no-op when executed). | `id` |
| `goto` | Jump to a label or `__end`. Unknown label → a warning and a jump to the end. | `label` |
| `if` | Conditional jump. Accepts a **structural** `cond` `{key,op,value}` (`eq/ne/lt/lte/gt/gte`) **or** a string `expr` (if both are present, `expr` wins). | `cond`/`expr`, `then`, `else` |
| `call` | Jump that remembers the return point (call stack). | `label` |
| `return` | Return to the point after `call`; with an empty stack — to the end. | — |

#### State
| op | What it does | Fields |
|---|---|---|
| `set` | Assign a variable. `expr` (expression string) wins over `value` (literal). | `key`, `value`/`expr` |
| `inc` | Add a number (default `+1`). | `key`, `by?` |

#### Staging: scene
| op | What it does | Key fields |
|---|---|---|
| `bg` | Background. Resolves a catalog id or a direct `sprite_url`. Loads asynchronously. | `sprite_url`, `id?` |
| `actor` | Place/update/hide a character. Resolves layers from the cast/catalog/direct urls; starts idle/blink/lip-sync. | `id`, cast axes, `show`,`position`,`x`,`y`,`width`,`height`,`scale`,`anchor`,`z`,`flip`,`rotation`,`opacity`,`on_click`,`hover_opacity` |
| `obj` | Same as `actor` (same code), but semantically "not a character" (does not dim). | (same as `actor`) |
| `clear` | Take every actor and `obj` off stage at once — the same removal `show=false` performs, per body. Leaves the background, effects and HUD alone, so a scene change is `clear` then a new `bg`. Placement is remembered: a later `actor id=…` with no position returns to the slot it left. | (none — any field is a typo) |

#### Staging: effects, sound, timing
| op | What it does | Fields / defaults |
|---|---|---|
| `fade` | Full-screen fade. | `to`=black/white/clear, `duration`≈0.5 |
| `dim` | Dim the scene (focus). | `alpha`≈0.4, `duration`≈0.5 |
| `flash` | Short flash. | `color`=white, `duration`≈0.2 |
| `tint` | Colored overlay veil. | `color`,`alpha`≈0.3,`duration` |
| `blur` | Screen blur (`alpha`≤0 removes it). | `alpha`≈0.5,`duration` |
| `camera` | `shake`/`zoom`/`pan`/`reset`. | `action`,`amplitude`,`factor`,`x`,`y`,`duration` |
| `particles` | Weather layer. | `type`=rain/snow, `on`(bool) |
| `fx` | Full-frame multi-effect stack (Canvas path; vignette, grain, bloom, rays, glitch, black-hole space lens, …). `space_x/y`, `space_radius` and `space_color` tune the lens. Sticky fields; `fx off` resets. | `vignette grain chromatic scanlines pixelate glitch bloom rays rays_x rays_y distort space space_x space_y space_radius space_color saturation contrast tint frost blink invert dur off` |
| `sfx` | Per-actor/per-layer sprite effects: outline, glow, dissolve, status transformations and manhwa-style body/weapon auras. `aura_style=basic|guard|fire|frost|storm|shadow|holy|space|distortion` selects motion and a default palette; explicit colours override it. `space` pairs with `fx space`; `distortion` is a slow black/red broken contour. `part=body|weapon|…` scopes a composite layer; `sfx id=… off` resets all its layers. | `id part outline outline_color glow glow_color dissolve flash dark tint tint_color ghost ghost_color petrify hologram hologram_color burn burn_color rim rim_color shake aura aura_style aura_color aura_color2 blade blade_color lightning lightning_color runes runes_color dur off` |
| `audio` | Sound on a channel (async). | `channel`=music/sfx/ambient, `action`=play/stop, `url?` |
| `wait` | Pause the script for N ms (default 1000). | `ms` |
| `hint` | Popup card at the top center of the scene (`VnStage.cs`: `ApplyHint`); `show=false` removes it, `duration>0` auto-hides. The text interpolates `{vars}`. | `text`, `show?`, `duration?` |
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

#### Saving
| op | What it does | Fields |
|---|---|---|
| `save` | Snapshot the state (see §5). | `slot?` (default `quick`) |
| `load` | Restore a snapshot and redraw the scene. | `slot?` |

#### Host-provided ops (⚠ not in the bare engine)
| op | What it does at runtime | Fields |
|---|---|---|
| `wardrobe_show` | Opens the in-story wardrobe for a character and holds the story until it closes. Implemented by the **novel-shell** package (`com.lvn.engine.shell`: `NovelApp` registers it, `WardrobeSheet` draws it) — a host that installed only `com.lvn.engine` gets a **silent no-op**. Emitted by the bundle importer; valid to write by hand only if you ship the shell. | `char` |

---

### 2. Reactive `text` (HUD)

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

### 3. Choice (`choice`) — what actually works

| Option field | Type | Behavior |
|---|---|---|
| `text` | display | Option text, interpolated with `{…}`. |
| `cost` | display | The "price" caption under the option, interpolated. **Purely visual** — deducts nothing by itself. |
| `goto` | functional | Jump on pick. |
| `body` | functional | Inline command list, executed on pick, **before** the jump. In `.lvns` it is a `{ … }` block on the option line — the brace ends the line, `}` stands alone:<br>`- Спросить -> q1 expr="!_once_q1" {`<br>`    _once_q1 = true`<br>`}`<br>An option written without `-> label` runs its body and then continues past the choice. |
| *(weave)* | compile-time | **Put prose and flow straight in that same block.** One syntax, and the compiler picks the mechanism by what is inside: `set`/`inc`/`goto` stays a runtime `body` (zero labels), anything richer is lowered into script behind a minted label, with every branch converging at the point right after the choice.<br>`- Ударить {`<br>`    Ты бьёшь первым.`<br>`    реп = реп + 3`<br>`}`<br>`- Уйти {`<br>`    Ты уходишь молча.`<br>`}`<br>`Дальше идём вместе.`<br>The compiled `.lvn` is exactly the label-and-goto version you would have hand-written — nothing new reaches the runtime, and no name had to be invented. An option may still carry `-> label`: the block runs, then control goes **to that label** instead of the convergence. Half of every label in real chapters was this scaffolding. |
| `requires_stat` + `min` | functional | Gate: the option is **hidden** if `variable < min`. |
| `expr` | functional | Boolean expression gate: the option is **hidden** if false. |
| `hint` | ignored | Compiles and validates, but **no runtime reads it** — neither `LvnPlayer.BuildOptions` nor the web player. Use the `hint` **command** before the choice (or inside the option's `body`) instead. |

A failed gate **hides** the option entirely (does not show it "grayed out").

---

### 4. Variables, conditions, expressions

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

### 5. Save / load

- `save` stores: **the cursor position, the variable dictionary, the call stack**.
- `load` restores them, redraws the scene (background/actors as of the snapshot point) and
  resumes execution from the saved spot.
- Storage: Unity `PlayerPrefs`, key `lvn_save_<title id>_<slot>` (default slot
  `quick`; the title prefix keeps two novels in one app from reading each other's
  quick save — `VnStage.SaveLoad.cs`, `SaveKey`. Loading falls back to the old
  un-prefixed key). Any number of slots by name; the total PlayerPrefs limit is ~1 MB.
- **Design-critical:** save/load is tied to **stable label ids**.
  Renaming TEXT is fine; renaming label/ending ids is not, or old
  saves break.

---

### 6. Animation — every notation, checked against the runtime

The detailed model and notation forms are in `LANGUAGE.md` §9 and `docs/animation-system.md`.
Checked against the runtime:

| Capability | Status |
|---|---|
| Script-animatable properties `x` `y` `screen_x` `screen_y` `scale` `scalex` `scaley` `rotation` `alpha` | ✅ yes |
| Layer sprite swap (`prop=frame`) | ⚠ **in practice manifest-only.** A `frame` track holds an **axis value** — a string (`ActorAnimator.SampleFrame`) — while `.lvns` `keys=` parses numbers only (`convert.go`, `parseKeys`), so from a script you could address only axis values literally named `0`, `1`, … Blink/lip-sync frame tracks come from the cast entity's `anim` in the manifest. |
| Easing `linear` `inOutSine` `outCubic` `outBack` `inBack` | ✅ yes |
| Loop `once` / `restart`(`true`) / `yoyo` | ✅ yes |
| One-liner `to=` (tween from the current value) | ✅ yes |
| `stop` / `stop=<channel>` | ✅ yes |
| `mode=queue` (sequence on a channel) | ✅ yes |
| Parallelism (multiple channels = multiple lines) | ✅ yes |
| `interp=spline` / `interp=step` | ✅ yes (Catmull-Rom through the keys / step; a typo in the value is a compile error) |
| `orient=true` (rotate along the path tangent) | ✅ yes (for `move`; respects easing and spline) |
| `defanim` / `play` (named reusable animations) | ✅ yes (pure compile-time expansion: `defanim` emits nothing, `play` stamps the stored parameters into a normal `anim` — the runtime only ever sees `anim`) |

**Quoting rule (a common mistake):** values with **spaces** in quotes
(`keys="…"`, `path="…"`) require the **legacy form** `id=`/`prop=`. A bracket list
`[…]` and the one-liner `to=` also work in terse form. A malformed `anim`/`move` is
a **compile error** (not a silent skip).

---

### 7. Cast and assets — how art gets on screen

This is critical: **a character can NOT be defined in `.lvns`** — there is no cast
directive there. `.lvns` only **references** the cast by id (`actor mara ...`). The
definition itself lives in the **manifest** or in a `cast` block of the `.lvn`.

#### Where the cast lives
- **`manifest.json` → `sprites`** (an `id → entity` map) — the **global** catalog,
  available to every chapter. The primary way.
- **A `cast` block in `.lvn`** — local to one chapter (optional).
- The `.lvns` source has no cast — it is mixed in by the runtime from the manifest.

#### How `actor mara emotion=smile` becomes a picture
1. The entity is looked up by `id` in the catalog (manifest) or in the document's `_cast`.
2. Axes: the entity's `defaults` are overridden by the command's fields
   (`emotion=smile`, `pose=…`, …).
3. For every layer template (`/content/sprites/mara/face_{emotion}.png`)
   all `{tokens}` are substituted.
4. A token without a value → **the layer is skipped** (optional parts appear
   only on request). **K poses + M emotions = K+M images, not K×M.**

#### Cast entity fields (in the manifest)
`name` (name plate), `color` (name color), `layers` (url template list, bottom
to top; a layer may have a `when` condition, an `id`, a partial rectangle `x/y/w/h`),
`defaults` (default axis values), `axes` (allowed axis values —
for the editor), `kind` (`static`(default)/`rigged`/…), `anim` (named
animations of the rigged doll: idle/blink/…).

#### How to add art
- **The cast editor in Studio** (`panel`, Sprites tab): you create an entity,
  axes, upload images — Studio writes files to the server (`PUT /v1/admin/assets/…`,
  requires an admin token) and stores the entity in `manifest.json`.
- **Manually:** edit `manifest.json` (`sprites`) and place files following the
  path conventions. The server re-reads the manifest (`Cache-Control: no-store`).

#### Path conventions
```
/content/sprites/<id>/<layer>_<axis>_<value>.png    // characters
/content/bg/<name>.jpg                              // backgrounds
/content/ui/<purpose>/<name>.png                    // UI/hotspots
/content/scripts/<chapter>.lvn                      // compiled scripts
```

#### Missing art (important for workflow order)
- If an asset url **fails to load** (404) — that **layer is simply skipped**,
  the rest is drawn. **There are no automatic gray placeholders at runtime.**
- Gray placeholder images are generated **at import time** (the `lvnconv` tool),
  i.e. they are real asset files, not a runtime effect.
- "Graybox" (running with no asset provider at all) yields solid colored backgrounds
  with no characters.
- **Takeaway:** the game's logic, text and structure can be fully written and
  validated **before** the art exists; on the live stage a missing sprite is
  simply not rendered — that is not an error.

#### How a script gets into the game
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

### 8. HARD LIMITS (mandatory reading)

Mostly what the reference runtime does **not** have: do not try to emulate it "as
if it exists" — use the workarounds in the right column. The ✅ rows are here on
purpose, because they are the things authors most often assume are missing.

| Limit / assumed limit | Workaround / how it really works |
|---|---|
| ❌ **No realtime timer** (`every`/`sleep`/clock ticks are unavailable to the script). The only clock the script gets is a fixed `wait ms=` pause and a `choice timeout=` countdown — neither is a condition you can poll. | Measure time in **turns/days** in a loop (`day = day + 1`), and grant "idle" income on every loop pass. |
| ✅ **Player text input exists** — the `input` op (`case "input"` in `LvnPlayer` and in the web player): an overlay, the typed string lands in a variable, the story waits for confirmation. | `input var=name prompt="Who are you?" default="Guest" max=24`, then `{name}` anywhere. |
| ❌ **No other keyboard input**: no hotkeys, no free typing outside the `input` overlay, no key-down events for the script. | Everything else is taps: `choice` options and `obj on_click` hotspots. |
| ❌ **Script flow cannot be tied to a looping animation finishing.** A looping animation never blocks the script. | Use `wait` or `say` for pauses; use `mode=queue` for a sequence on one channel. |
| ✅ **`hint` is rendered** — a window at the top center. | `hint text="…" duration=6`; `show=false` removes it manually. For a persistent HUD label there is still the reactive `text`. Mind the namesake: a `hint=` **field on a choice option** is ignored (§3) — the command is the real thing. |
| ✅ **Bones + springs (paper-doll).** | Catalog layer: `parent` (which layer it attaches to), `px`/`py` (the joint, fractions of its own rect), `spring`/`damping` (hair/tail swing on their own from the parent's movement and rotation, VRM model). Draw order = list order (the back arm is a child of the body, but behind it). Both renderers. |
| ✅ **`defanim`/`play` work.** | `defanim shake prop=x keys="…"` + `play id=x anim=shake` (terse: `play x shake`); play parameters override the definition. Spline paths run at constant speed (arc-length). |
| ⚠️ **A choice option's runtime `body` is limited**: only `set`/`inc` and `goto` ride inside it. Staging in a body plays once and is **lost on save/restore** — a body command has no index in the script, and the resume trace is a list of indices, so the rebuilt scene never replays it. | Nothing to do by hand: **write it in the block anyway**. The compiler reads the block and picks the mechanism — `set`/`inc`/`goto` stays a runtime `body`, anything richer (prose, `if`, a nested `choice`, `wait`) is *woven*: lowered into ordinary script behind a minted label, with the branches converging right after the choice. See "weave" below. |
| ❌ **An option's `cost` is a caption only**; it deducts no resource itself. | Deduct resources explicitly (`set`/`inc`) at the option's handler label. |
| ❌ **A missing asset is not replaced by a placeholder at runtime** — the layer is skipped. | That is normal for graybox; for placeholders generate them with the tool/place the files. |
| ❌ **No `ceil`.** | `floor(x)` / `round(x)`. |
| ❌ **The cast cannot be defined in `.lvns`.** | Define it in `manifest.json` (`sprites`) or in a `cast` block of the `.lvn`; the script only references it by id. |
| ❌ **No error/exception handling in the script.** | Faulty expressions are treated as `false`/`{key}`; design conditions so that the safe value is the default. |

---

### 9. How the engine judges "correctness" (validation)

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

---

# Готовые приёмы

<!-- источник: howto/recipes.md -->

## `.lvns` recipe book

Short reusable snippets that combine into almost any mechanic.
Every pattern comes from the verified examples in this folder — copy and adapt.
For a reference on any element, see [`LANGUAGE.md`](LANGUAGE.md).

---

### Counter / accumulation

```
score = 0
score = score + 1          // increment
score = score - 1          // decrement
hp = min(maxhp, hp + 10)   // add, but cap at a ceiling
gold = max(0, gold - 5)    // subtract, but never below zero
```

### Reactive HUD panel

A `text` with a template in `«…»` re-evaluates itself (~5 times per second) — perfect for
score, health, resources.

```
text hud x=4 y=8 size=42 color=#f1e4c9 «♥{hp}/{maxhp}   💰{gold}   lv.{level}»
text hud hide              // hide the panel
```

### Conditional branch

```
if gold >= 100 -> rich      // true → jump; otherwise fall through
if hp <= 0 -> dead
-> normal                   // the "default" branch
```

### if / else block

```
if has(inv, "key") {
  The door opens with the key.
  -> next_room
} else {
  Locked. You need a key.
}
```

### Choice menu

```
- Fight -> fight
- Run -> flee
- Talk -> talk cost="costs a turn"
```

### Hidden/locked option

The option appears in the menu only while `expr` is true.

```
- Cast a spell -> cast expr="mana >= 10"
- Open the door with the key -> open expr="has(inv, \"key\")"
```
> Escape quotes inside `expr=` as `\"`.

### Inventory (list)

```
inv = []                            // create
inv = push(inv, "potion")           // add
if has(inv, "potion") -> use_potion // check presence
inv = removeat(inv, indexof(inv, "potion"))   // drop one item
Items in the bag — {len(inv)}.      // counter
for it in inv {                      // iterate
  - {it}
}
```

### Shop (purchase with a money check)

```
:buy_sword
if gold >= 12 {
  gold = gold - 12
  atk = atk + 3
  Bought a sword (+3 attack).
} else {
  Not enough gold.
}
-> shop
```

### Dice roll / randomness

```
r = rand(1, 6)              // integer 1..6 inclusive
if chance(0.7) -> success   // 70% chance
crit = rand(0, 3)           // 0..3 (damage spread)
loot = rand(8, 20)
```

### Random event (weighted branch pick)

```
roll = rand(1, 10)
if roll <= 4 -> common      // 40%
if roll <= 7 -> uncommon    // 30%
if roll <= 9 -> rare        // 20%
-> jackpot                  // 10%
```

### Relationship / reputation meter

```
affection = 0
affection = affection + 2          // good line
affection = affection - 1          // misstep
text hud «❤ {affection}»
// the final route unlocks past a threshold:
- Confess -> confession expr="affection >= 5"
```

### Loop scene (a screen you keep returning to)

A hub you come back to after every action. Put `-> hub` before the label
to avoid the fall-through warning.

```
-> hub
:hub
What will you do?
- Look around -> look
- Move on -> leave
:look
You look around...
-> hub
```

### Clickable room (point-and-click)

```
:room
obj id=door sprite_url="/ui/door.png" x=0.8 y=0.5 anchor="0.5,0.5" on_click="door"
obj id=key  sprite_url="/ui/key.png"  x=0.2 y=0.7 anchor="0.5,0.5" on_click="take_key"
Examine the room.
-> room                     // the pause screen keeps hotspots alive
:take_key
has_key = 1
You picked up the key.
-> room
```

### Dragging an item (drag & drop)

`draggable=true` + an `on_drop="target:label"` map (pairs separated by space/comma);
`on_drop_miss` is the miss branch (without it the item just stays where dropped).
A short press is still a click — `on_click` keeps working alongside.

```
:scene
obj id=apple sprite_url="/obj/apple.png" x=0.3 y=0.6 width=0.12 draggable=true on_drop="bag:in_bag" on_drop_miss=missed
obj id=bag sprite_url="/obj/bag.png" x=0.8 y=0.7 width=0.2
Drag the apple into the bag.
-> scene
:in_bag
obj id=apple show=false
inventory_apples = 1
The apple is in the bag!
-> __end
:missed
Missed. Try again.
-> scene
```

`drag_bounds="none"` removes the screen constraint (default is `screen`).

### CG gallery and UI sounds (manifest, not script)

These are not commands — they are blocks in `manifest.json`. Gallery: an art piece
unlocks forever on the first `bg` shown with the same url; a "Gallery" item appears
in the quick menu.

```json
"titles": [{ "id": "my-novel", "gallery": [
    { "id": "cg-beach", "url": "/content/bg/beach.png", "name": "Beach" }
] }],
"ui": { "sounds": {
    "click": "/content/ui/sounds/click.wav",
    "choice": "/content/ui/sounds/choice.wav",
    "type": "/content/ui/sounds/type.wav",
    "volume": 0.8
} }
```

Keep the art `id` stable between releases — unlocks are stored by it.
A missing sound is just silence; everything scales with the user's SFX volume.

### Code/logic lock (no text input)

Keep the state in variables, check it with conditions.

```
:check
if a == 1 -> ck2
-> wrong
:ck2
if b == 0 -> ck3
-> wrong
:ck3
if c == 1 -> solved
-> wrong
:wrong
Wrong combination.
-> panel
```

### Timed choice (real-time timer)

A `choice timeout=…` line right before the option block: a countdown bar appears
above the buttons, and expiry jumps to `timeout_goto`. An open menu or art view
freezes the clock — the timer is only honest against the player.

```
Guards at the door! What do you do?
choice timeout=5 timeout_goto=caught
- Hide under the bed -> hide
- Jump out the window -> jump
:caught
You hesitated — they grabbed you.
-> __end
```

### Voiced lines

A `voice "<url>"` line before a dialogue line: the clip starts with the text,
the next line (or leaving the scene) silences it, so voices never stack.
Volume is the "Voice" slider in settings; the typing blip stays quiet under a voice.
Lines without voice files simply play silent — mixing voiced and mute lines is fine.

```
voice "/content/voice/mara_001.ogg"
Mara: You finally came.
voice "/content/voice/mara_002.ogg"
Mara: I was starting to worry.
Unvoiced narration between lines is fine.
```

### Text input (hero name and beyond)

`input` pauses the story with an input-field overlay; what the player types goes
into a variable and works in interpolation.

```
input var=name prompt="What is your name?" default="Guest" max=24
Hello, {name}!
if name == "Gandalf" -> wizard
```

### A "timer" measured in turns (no real time)

When you need game time rather than a real countdown, measure it in **turns/days** in a loop.

```
day = 1
days = 5
:turn
if day > days -> finale     // time is up
// ... the day's actions ...
day = day + 1
-> turn
```

### Subroutine (one piece of code, many call sites)

```
// called from different places — one statement per line (two assignments on
// one line silently break: everything after the first `=` is swallowed into
// the expression, and the validator now warns about the stray `=`).
ename = "Wolf"
ehp = 12
eatk = 5
call fight
// ...
ename = "Orc"
ehp = 40
eatk = 9
call fight

:fight                      // shared combat engine
{ename} attacks!
// ...
return                      // returns to wherever it was called from
```

### Function with a return value

```
func roll_dmg(base) {
  return base + rand(0, 3)
}
dmg = roll_dmg(atk)
```

### Level-up (fires as many times as needed)

```
:levelup
while xp >= need {
  xp = xp - need
  level = level + 1
  need = floor(need * 1.5)
  ✨ Level {level}!
}
return
```

### Online services from the script (wallet, leaderboard, analytics)

When the game runs with the LVN backend (NovelApp wires it up automatically),
the writer gets ready-made `ext` ops — all fire-and-forget and offline-safe:

```
ext wallet_earn currency=gold amount=10 reason="quest_done"
ext wallet_spend currency=gold amount=5 reason="shop" sku=amulet
ext leaderboard_submit board=quiz_score score_var=score name_var=player_name
ext daily_claim
ext track name=secret_ending_found
```

The `*_var` fields read a story variable — "submit whatever the player entered".
The validator flags these ops with a "host-defined" warning — that is expected.
A custom host enables them with one line: `LvnServiceOps.RegisterAll()`.

### Save and load

```
- Save -> dosave
- Load -> doload
:dosave
save
Your progress is recorded.
-> menu
:doload
load
```

### Multiple endings

```
if score == 3 -> end_perfect
if score >= 1 -> end_ok
-> end_fail
:end_perfect
🏆 Perfect!
-> __end
:end_ok
Not bad.
-> __end
:end_fail
Another time.
-> __end
```

### Staging a shot (atmosphere)

```
bg /content/bg/night.jpg
audio channel=music action=play url="/content/audio/theme.ogg"
particles type=rain on=true
fade to="clear" duration=1.2
actor mara left sad
anim mara scale [1 1.03 1] 3s yoyo     // a gentle "breathing"
camera action=shake amplitude=0.02 duration=0.4
flash color="white" duration=0.3
```

### Translating a novel into other languages

Nothing changes in the script: translations live in sidecar catalogs
`<chapter>.<lang>.json` next to the script (key — the source string, value —
the translation). `lvnconv` builds and updates the catalog:

```sh
lvnconv locale -lang en chapter1.lvns   # creates chapter1.en.json with all strings
```

The catalog collects dialogue lines and speaker names, choice options, `input`
prompts and `text` panels — in story order. The translator fills in the values;
after editing the script, a re-run preserves finished translations and appends new
strings (`-check` — coverage report only, `-prune` — drop stale keys).

Declare the languages in `manifest.json` — a switcher appears in settings and the
quick menu, and the language changes on the fly mid-story:

```json
{ "languages": ["ru", "en"] }
```

A string without a translation shows as the original — the catalog can be filled
in gradually.

---
