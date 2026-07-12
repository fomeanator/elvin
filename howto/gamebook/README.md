# 🗺 Gamebook / CYOA

The "choose your own adventure" genre: a tree of forks the player steers through by making choices, with collected items unlocking new branches.

## What the example does

`gamebook.lvns` is a mini-adventure in a cave. Two branches split off from the entrance: left (a hall with a torch and a dark tunnel) and right (an underground lake with a key). The torch unlocks a safe passage through the dark tunnel, the key opens the final iron-bound door; without them you have to take risks or turn back. The player has an inventory and health points (`hp`), and the story has two endings — victory in the treasure room and death in the dark.

## Engine features used here

- **A list as inventory** and adding to it: `inv = []`, then `inv = push(inv, "torch")`.
- **A hidden choice option** via `expr=` with a boolean filter: `- Light the way with the torch -> bridge expr="has(inv, \"torch\")"` — the option appears in the menu only when the condition is true.
- **Randomness and a fallback loop**: `if chance(0.5) -> grope_ok` with a return via `-> grope`.
- **A tree of forks and returns by label**: `- Go left (the draft) -> left`, `:label` nodes, a redirect node `:reset` → `-> left`.
- **Health points and losing**: `hp = hp - 1`, `if hp <= 0 -> dead`, the `(♥ {hp})` interpolation.
- **Endings**: `-> __end` and the `fade to="black" duration=0.8` blackout before death.

## Step-by-step walkthrough

### 1. State — inventory and health

At the start of the scene we declare two state carriers:

```
inv = []
hp = 3
```

`inv` is an empty list (see §6 of the reference), `hp` is a counter. Any undeclared variable would read as `0`/`""`/`false`, but explicit initialization makes the script clearer.

Items go into the inventory with the `push` function, which **returns a new list** — so the result is assigned back:

```
:take_torch
inv = push(inv, "torch")
The torch is yours now. Fire drives back the darkness.
-> dark
```

The same trick is used for the key in the lake branch (`inv = push(inv, "key")`).

### 2. A hidden option via `expr=`

The core gamebook mechanic is gated options. In the dark tunnel, the "by torchlight" passage is visible only to those who carry a torch:

```
:dark
bg /content/bg/dark_tunnel.jpg
The tunnel plunges into pitch darkness. Without light it's easy to fall.
- Light the way with the torch -> bridge expr="has(inv, \"torch\")"
- Feel your way forward -> grope
```

`expr=` is a boolean filter on the option: **when the expression is false, the option is hidden** (§5). The quoted text lives inside the choice line, so inner quotes are escaped as `\"` — `has(inv, \"torch\")` checks whether the list contains an element (§7). The same pattern locks the final door behind the key:

```
- Open the door with the key -> win expr="has(inv, \"key\")"
```

### 3. A fallback loop on `chance(0.5)`

Without a torch, all that remains is to feel your way — at a risk. The `:grope` node loops back on itself until the attempt succeeds or health runs out:

```
:grope
if chance(0.5) -> grope_ok
hp = hp - 1
You stumble in the dark and take a painful fall. (♥ {hp})
if hp <= 0 -> dead
-> grope
:grope_ok
Groping along the wall, you somehow reach the bridge.
-> bridge
```

`chance(0.5)` is true with 50% probability (§7) — on success we jump to `grope_ok`. Otherwise we lose `hp`, show the counter via the `{hp}` interpolation, check for death, and **return to the top of the node** with an unconditional `-> grope`. That is the retry loop — no `while` needed.

### 4. The fork tree and returns by label

Each "screen" is a background, a pause line, and a choice menu leading to labels:

```
bg /content/bg/cave_mouth.jpg
A fork in the cave. A draft pulls from the left, water drips on the right.
- Go left (the draft) -> left
- Go right (the dripping) -> right
```

To let the player fetch the key and come back to the main route, a redirect node is used:

```
:reset
-> left
```

The `take_key` and `stuck` branches and the lake fork all lead `-> reset`, and `reset` sends you to `left` — this keeps the graph connected without duplicating code.

### 5. Endings

Victory and death close with the built-in `__end` label:

```
:win
bg /content/bg/treasure_room.jpg
The key turns. Beyond the door lies the treasure room. You beat the adventure!
-> __end

:dead
fade to="black" duration=0.8
The darkness of the cave swallows you. The adventure is over.
-> __end
```

`fade to="black"` before `dead` gives the death a dramatic blackout (§4).

## Run and check

```sh
# build the transcoder
cd tools/lvnconv && go build -o /tmp/lvnconv .

# compile .lvns → .lvn
/tmp/lvnconv convert -i howto/gamebook/gamebook.lvns -o /tmp/gb.lvn

# structural check: unknown ops, dangling jumps, duplicate labels
/tmp/lvnconv validate /tmp/gb.lvn
```

The goal is **0 warning(s)**. The most common warning, "label … reached by fall-through", means execution also "falls into" the target label from above — put an explicit `-> label` or `-> __end` before it.

## Make it yours

- **New gating items.** Add `inv = push(inv, "rope")` in a new branch and lock a cliff behind it: `- Climb down the rope -> ... expr="has(inv, \"rope\")"`. Built on `push` + `expr=`.
- **New forks.** Any new `:node` with a `bg`, a pause line, and a `- ... -> label` block extends the tree; wire it back in via the `:reset` idiom.
- **Risk points instead of pure chance.** Replace `chance(0.5)` with a resource check — collected torches/potions lower the threshold. Built on variables and `if ... ->`.
- **A fork on health.** Before a dangerous branch, add `- Take the risk -> trap expr="hp >= 2"` so a wounded hero can't charge in recklessly. Built on `expr=` over `hp`.
- **An alternative path to victory.** Make a second ending — for example, an exit without the treasure when the key wasn't found but the torch was: one more `:label` and `-> __end`.

## Next

- [Language reference](../LANGUAGE.md)
- [Recipe book](../recipes.md)
- [All genres](../README.md)
