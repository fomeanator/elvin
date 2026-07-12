# 🖱 Point-and-click / escape room

A genre built around examining a scene: clickable objects are arranged around a paused "screen", and all the logic rests on labels, jumps and state variables.

## What the example does

The player is locked in a study and has to get out. Four hotspots are placed over the `study_room.jpg` background — a desk, a painting, a safe and a door. A brass key hides in the desk, the safe code `1962` is scratched behind the painting, the safe opens with that code, and the door opens with the found key. Once the key is in your pocket, clicking the door triggers the ending: the background switches to `corridor.jpg` and the script finishes.

## The core idea: the screen loop

The key pattern is a "screen" that polls the player without advancing. An object with `on_click="label"` becomes a hotspot: a click jumps to the label and **swallows the tap**, so the dialogue does not advance. The "screen" itself is a pause: a narration line that holds the hotspots arranged around it until a click.

```
:room
obj id=desk     sprite_url="/content/ui/hotspot/desk.png"     x=0.22 y=0.70 width=0.18 height=0.16 anchor="0.5,0.5" on_click="desk"
...
The study is locked. Look around — desk, painting, safe, door.
-> room
```

Every handler label ends with `-> room`, closing the loop. Note the `-> room` **before** the `:room` label itself at the top of the file: without it, the flow after initialization would "fall through" into `:room` from above, while the `:room` block is also reachable by jumps from below — the transcoder would emit a "label … reached by fall-through" warning. The explicit jump before the label removes the fall-through.

## Engine features used here

- **Hotspot `obj … on_click`** — a clickable object that jumps to a label and swallows the tap:
  `obj id=door … on_click="door"`
- **Placement fields** in screen fractions: `x`/`y` — the anchor point, `width`/`height` — the size, `anchor="0.5,0.5"` centers the object on `x,y`:
  `obj id=safe … x=0.74 y=0.55 width=0.14 height=0.16 anchor="0.5,0.5" on_click="safe"`
- **State in variables** — flags for what's collected and what's known:
  `has_key = 0` · `knows_code = 0` · `safe_open = 0`
- **Branching `if cond -> label`** — a one-line fork that falls through if false:
  `if has_key == 1 -> escaped`
- **Background change `bg`** for the ending:
  `bg /content/bg/corridor.jpg`
- **Built-in label `__end`** — end of script:
  `-> __end`

Asset paths are just data. If the art is missing, the absent sprite simply isn't drawn (the layer is skipped), while the click logic and state work in full — the prototype is clickable before the desk and the safe are ever drawn.

## Step-by-step walkthrough

**1. State initialization.** Right after `bg`, declare the flags and jump to the screen:

```
bg /content/bg/study_room.jpg
has_key = 0
knows_code = 0
safe_open = 0
-> room
```

**2. Placing the hotspots.** In the `:room` block every object gets coordinates and `on_click`. The screen ends with a narration line (the pause) and a `-> room` return:

```
:room
obj id=desk     … on_click="desk"
obj id=painting … on_click="painting"
obj id=safe     … on_click="safe"
obj id=door     … on_click="door"
The study is locked. Look around — desk, painting, safe, door.
-> room
```

**3. Item handlers guarded against re-triggering.** Before handing out an item, check the flag with `if` so the desk doesn't give away the key twice:

```
:desk
if has_key == 1 -> desk_empty
You search the desk. In the bottom drawer — a brass key.
has_key = 1
-> room
:desk_empty
There is nothing else in the desk.
-> room
```

The painting is simpler — it only sets `knows_code = 1` and returns to the screen.

**4. Checking the code and the key.** The safe reads two flags in order: already open → empty; code known → open it; otherwise — locked:

```
:safe
if safe_open == 1 -> safe_done
if knows_code == 1 -> safe_unlock
The safe is locked with a code. You don't know the digits yet.
-> room
```

The door is the escape "lock": it opens only if you have the key:

```
:door
if has_key == 1 -> escaped
The door is locked. You need a key.
-> room
```

**5. The ending.** The `:escaped` label changes the background and goes to `__end`:

```
:escaped
bg /content/bg/corridor.jpg
The key turns, the door opens. You have escaped the study. Victory!
-> __end
```

## Running and checking

```sh
# build the transcoder
cd tools/lvnconv && go build -o /tmp/lvnconv .

# compile .lvns → .lvn
/tmp/lvnconv convert -i howto/point-and-click/point-and-click.lvns -o /tmp/pnc.lvn

# structural check: unknown ops, dangling jumps, fall-through
/tmp/lvnconv validate /tmp/pnc.lvn
```

The goal is **0 warning(s)**. If "label … reached by fall-through" pops up, the label is both fallen into from above and jumped to from below — put an explicit `-> label` (or `-> __end`) before it, as done before `:room`.

## Make it yours

- **New hotspots.** Add another `obj … on_click="window"` with a matching `:window` label — you can have as many inspectable points as you like.
- **Item combinations via variables.** Introduce flags (`has_screwdriver`, `has_battery`) and unlock the next step only when both equal 1 — via nested `if`.
- **Multi-room scene.** Make separate screen labels `:room1`, `:room2`, each with its own `bg` and a transition hotspot between them — the escape room grows into a suite.
- **Menu/inventory.** The same `on_click` trick builds a start menu or an item panel; to list what's collected, use lists (`inv = push(inv, "key")`) and `for it in inv`.
- **Hints and atmosphere.** Mix in `dim`, `audio` for the lock sound, and show a hint via `say` or a reactive `text` (the `hint` command does not render at runtime — it's a no-op).

## Next

- [Language reference](../LANGUAGE.md) — full `.lvns` syntax.
- [Object placement](../../docs/placement.md) — placement fields and the `on_click` section.
- [Recipe book](../recipes.md) — short reusable patterns.
- [All genres](../README.md) — the genre map and quick start.

When you want to see point-and-click techniques in a large script alongside the rest of the engine's features, check `server/content/scripts/showcase.lvns`.
