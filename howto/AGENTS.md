# Elvin — how to make a game (start here)

Entry point for building a game on the **Elvin** engine. After this file you can
build a game in any supported genre without reading the engine sources — everything
you need is in `howto/` and `docs/`.

## Mental model

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

## Workflow

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

## What you can build (and what you can't)

Any game driven by **choice and state**: visual/kinetic novels, gamebooks/CYOA,
point-and-click and adventure games, RPGs, dating sims, quizzes, detective
stories, tycoons, roguelikes, puzzles. Worked examples live in the genre folders.

What is missing (full list — `CAPABILITIES.md` §8): a real-time timer, free-text
player input, timing-based arcade mechanics. Time is measured in **turns**, and
any "input" is a `choice` or a click on an `obj on_click`.

## Documentation map

| Question | File |
|---|---|
| Full `.lvns` syntax | [`LANGUAGE.md`](LANGUAGE.md) |
| What the engine can and can NOT do (runtime + limits) | [`CAPABILITIES.md`](CAPABILITIES.md) |
| Dense one-screen cheatsheet | [`CHEATSHEET.md`](CHEATSHEET.md) |
| Reusable patterns | [`recipes.md`](recipes.md) |
| Guide + working example per genre | `howto/<genre>/` (see [`README.md`](README.md)) |
| Object placement, hotspots | `../docs/placement.md` |
| Cast (parametric characters) | `../docs/cast.md` |
| Animation (full spec) | `../docs/animation-system.md` |
| `.lvn` container contract | `../docs/lvn-format.md` |
| Large real games | `../server/content/scripts/*.lvns` |

Order for a new task: this file → `CHEATSHEET.md` → the nearest genre's example →
`LANGUAGE.md`/`CAPABILITIES.md` as needed.

## A complete minimal game

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

## Common mistakes

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

## Readiness checklist

- The `.lvns` compiles (`convert` with no errors) and `validate` reports `0 warning(s)`.
- All `choice`/`if` branches lead to existing labels or `__end`.
- The reactive HUD (if any) shows up-to-date variables.
- The cast referenced by `actor <id>` is defined in the manifest — or it is a
  deliberate greybox.
- Stable label/ending ids do not change (matters for save/load).
