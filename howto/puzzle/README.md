# 🧩 Puzzle / logic lock

The player never types an answer — they change **state stored in variables** through choices, and the engine checks the solution with an ordinary condition.

## What the example does

A stone door with three levers `A`/`B`/`C`, each sitting in the "on" (`1`) or "off" (`0`) position. There are faded hints on the wall the player can read. The goal is to set the combination `A=on, B=off, C=on` and pull the door ring. Any attempt with a wrong combination returns to the lever panel; the right one opens the door.

## The core idea: state instead of input

The `.lvns` language has **no text-input command**. That is not a limitation but a way of thinking: a puzzle is not "guess the word" — it is **state the player manipulates**.

- The lock's state lives in three ordinary variables:

```
a = 0
b = 0
c = 0
```

- The player changes this state **via choices** — each panel option toggles its lever.
- A reactive HUD constantly shows the lock's current position through `{…}` interpolation:

```
text hud x=4 y=8 size=40 color=#cfe8d8 «Levers:  A:{a}  B:{b}  C:{c}»
```

- The solution is checked by a **chain of conditions** — no string parsing, just number comparisons.

This combo of "state variables + choice as mutation + condition as check" is the universal skeleton of any puzzle on the engine.

## Engine features used here

- **State variables** — `a = 0`, `b = 0`, `c = 0` hold each lever's position.
- **Choice panel** — a list of options, each leading to its own label:

```
:panel
- 🔺 Lever A (now {a}) -> toggle_a
- 🔺 Lever B (now {b}) -> toggle_b
- 🔺 Lever C (now {c}) -> toggle_c
- 📜 Read the hints -> hints
- ⚙ Pull the door ring -> check
```

- **Toggle via condition** — `if a == 0 -> a_on` splits the flow into "turn on" and "turn off":

```
:toggle_a
if a == 0 -> a_on
a = 0
Lever A creaks down.
-> panel
:a_on
a = 1
Lever A rises up.
-> panel
```

- **Reactive HUD** — `text hud … «… A:{a} …»` updates itself as soon as the variable changes.
- **Check chain** — sequential `if … -> next` links leading to `solved` or `wrong`.

## Step-by-step breakdown

1. **Initialization.** At the start of the scene we set the lock's state: `a = 0`, `b = 0`, `c = 0`. Right there we declare the reactive `text hud`, which will reflect any change to these variables.

2. **Entry.** We set the background `bg /content/bg/ancient_door.jpg`, give an opening line of narration, and jump to the panel: `-> panel`.

3. **The `:panel` panel.** This is the puzzle's hub — a choice with all the actions: three levers, reading the hints, and an attempt to open the door. After any action the flow returns here.

4. **Toggling a lever (two labels).** Each lever is a pair of labels. `if a == 0 -> a_on` sends flow to the "turn on" branch (label `:a_on`, where `a = 1`); if the lever was already on, we fall through and turn it off (`a = 0`). Both branches end with `-> panel`, so the HUD immediately shows the new position.

5. **Hints `:hints`.** The label simply prints the riddle text and returns to the panel:

```
:hints
«The outer ones look to the sky, the middle one — to the earth.»
(Meaning — A and C raised, B lowered.)
-> panel
```

6. **THE KEY PART — the check cascade.** The `:check` label tests conditions one at a time. Each match leads to the next check; any mismatch goes to `:wrong`:

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
```

7. **Failure `:wrong`.** We report that the ring won't budge and send the player back via `-> panel` to keep working the levers.

8. **Ending `:solved`.** We swap the background to `bg /content/bg/light_passage.jpg`, describe the door opening, and end the scene with `-> __end`.

## Why the check is a chain

The cascade `:check → ck2 → ck3 → solved` is a sequential check of conditions, one at a time. Each `if … -> next` either passes flow onward when true or "falls through" to the next line, `-> wrong`. Pass every link — you land on `solved`; stumble on any — you go to `wrong` and back to the panel. The same trick works for code locks, action sequences, and crafting recipes: only the conditions change, the structure stays.

## Run and verify

```sh
# build the transcoder
cd tools/lvnconv && go build -o /tmp/lvnconv .

# compile the example .lvns → .lvn
/tmp/lvnconv convert -i howto/puzzle/puzzle.lvns -o /tmp/pz.lvn

# structural check (unknown ops, dangling jumps, duplicate labels)
/tmp/lvnconv validate /tmp/pz.lvn
```

The goal is **0 warning(s)**.

## Make it your own

- **More levers and states.** Add `d`, `e` and extend the HUD and the check cascade — the structure doesn't change.
- **Numeric code lock.** Instead of a toggle, offer digit choices (`-> digit_up`, `-> digit_down`) and compare each digit in `:check`.
- **Order of actions.** Record presses into a list with `push(seq, x)` and check the sequence, not just the final state.
- **Combining items.** Keep an inventory and use `has(inv, "…")` in the opening condition — the puzzle turns into a quest one.
- **Several linked puzzles.** Solving one (`solved`) sets a flag variable that unlocks the next.

## Next

- [Language reference](../LANGUAGE.md)
- [Recipe book](../recipes.md)
- [All genres](../README.md)
