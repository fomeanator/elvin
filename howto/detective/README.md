# 🔍 Detective / investigation

A nonlinear investigation: the player roams freely between locations, gathers clues, and files an accusation that only counts once the full picture is in place.

## What the example does

The setting is Grayson Manor: the master of the house has been found dead in his study, and the player is a detective. There are three places to gather clues — the study, the kitchen (the butler), and the parlor (the widow) — and the player moves between them through a hub node. Each location yields one clue, and the final accusation only "lands" once all three are collected. That is how the nonlinear structure converges into a single resolution — `:solved`.

## Investigation structure: hub + clues

The heart of the genre is the **hub node** `:hub`, a kind of "investigation map". From it the player heads to any location, and each location ends with `-> hub`, sending them back. This gives a free, unordered traversal instead of a linear flow.

Clues accumulate in an ordinary list: on initialization `clues = []`, and at the scene of a discovery — `clues = push(clues, "poison")`. Remember that `push` returns a **new** list, so the result is assigned back into `clues`.

Progress is visible to the player at all times — reactive text in the hub recomputes the list length:

```
text hud x=4 y=8 size=40 color=#e8e0c8 «Clues collected: {len(clues)}/3»
```

Re-examining a location is guarded by a `has` check: if the clue is already in the list, jump to the "empty" variant of the scene.

```
if has(clues, "poison") -> study_seen
clues = push(clues, "poison")
```

The final accusation is gated by a threshold on the number of clues — without the full picture, the case cannot be closed:

```
if len(clues) >= 3 -> solved
Too few clues — the accusation would fall apart in court. Keep looking.
-> hub
```

## Engine features used here

- **Choice hub with return** — choice options lead to locations, each location does `-> hub`:
  ```
  - 🔍 Examine the study -> study
  - 🍷 Question the butler -> butler
  - 💍 Talk to the widow -> widow
  - ⚖ Name the killer -> accuse
  ```
- **Clue list via `push`/`has`** — state accumulation plus duplicate protection:
  `clues = push(clues, "one glass")` and `if has(clues, "one glass") -> butler_seen`.
- **`len` in reactive text** — a live progress counter: `«Clues collected: {len(clues)}/3»`.
- **Accusation gate** — a clue threshold decides the outcome: `if len(clues) >= 3 -> solved`.

## Step-by-step walkthrough

1. **Initialization.** In the scene prologue — `clues = []`. The empty list is created before the first `push` so the `{len(clues)}` counter reads as `0/3` right away. Then the background and the opening narration are set, followed by `-> hub`.

2. **Hub with a reactive panel and options.** The `:hub` label draws the HUD text with the clue counter as its first line, then holds a pause with a narration line ("The parlor. Where do you start?") under which four choice options hang — three locations and the accusation. The HUD text is recomputed on every visit to the hub, so progress is always up to date.

3. **Collecting a clue with repeat protection.** Every location (`:study`, `:butler`, `:widow`) is built the same way: it sets its background, delivers a line/narration, then `if has(clues, "...") -> ..._seen` — if the clue is already collected, control goes to the "seen" node (`:study_seen` etc.), which simply speaks a stub line and returns `-> hub`. If the clue is not there yet — `clues = push(clues, "...")`, a "[noted]" confirmation, and `-> hub`. This way the same clue can never be counted twice.

4. **The `:accuse` node with a threshold.** The accusation does not branch by suspect — it checks clue completeness: `if len(clues) >= 3 -> solved`. If the threshold is not met, the flow falls through to the "Too few clues…" line and `-> hub`, sending the player back to gather what is missing.

5. **The `:solved` resolution.** The final node ties the chain of clues into the accusation of the widow and ends the script via `-> __end`. This is the single "correct" ending that the entire nonlinear traversal converges into.

## Run and check

```sh
# build the transcoder
cd tools/lvnconv && go build -o /tmp/lvnconv .

# compile .lvns → .lvn
/tmp/lvnconv convert -i howto/detective/detective.lvns -o /tmp/det.lvn

# structural check: unknown ops, dangling jumps, duplicate labels
/tmp/lvnconv validate /tmp/det.lvn
```

The goal is **0 warning(s)**. Every "seen" node (`:study_seen` and the rest) is reached by a jump from `if has(...)`, with an explicit `-> hub` before it, so there should be no "reached by fall-through" warning.

## Make it your own

- **More clues and suspects.** Add locations and raise the threshold: `if len(clues) >= 5 -> solved`, and switch the counter to `{len(clues)}/5`.
- **False clues.** Put decoys into the list too, but in the gate check for the specific key clues via `has`, not just `len` — then extra clues won't help.
- **Accusing a specific suspect.** Replace the single `:accuse` with a "Whom do you accuse?" choice and check the required set in each branch: `if has(clues, "poison") && has(clues, "widow's wine") -> solved`; otherwise — a false accusation and a bad ending.
- **Time as moves.** Add `moves = 0`, increment on every location visit (`moves = moves + 1`), and close the case on a deadline: `if moves >= 8 -> too_late`.
- **Branching interrogations.** Inside `:butler`/`:widow`, add sub-choices (press / believe), unlocking different clues or changing the witness's reaction.

## Next

- [Language reference](../LANGUAGE.md)
- [Recipe book](../recipes.md)
- [All genres](../README.md)
