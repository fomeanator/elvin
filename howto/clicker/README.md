# 🍪 Clicker / idle

An incremental game on the bare engine: a reactive HUD, a click loop, and a variable-driven economy — without a single line of runtime code.

## What the example does

You open a bakery and bake cookies. Every "Bake a cookie" click adds a resource, and your earnings buy ovens (passive income — each oven bakes cookies on its own) and hand upgrades (each click counts for more). The oven price rises after every purchase, so you have to balance clicking against investing. The goal is to stockpile 50 cookies and become a baking tycoon.

## The core: reactive HUD + click loop

The genre stands on two pillars.

**1. A reactive panel.** A single `text` line with `{…}` interpolation recomputes itself on the tick (~200 ms) — values update with no manual redraw:

```
text hud x=4 y=8 size=44 color=#ffe9b0 «🍪 {cookies}   per click: {per_click}   ovens: {ovens}   goal: {goal}»
```

You change variables anywhere in the script — the panel shows fresh numbers by itself. That is what makes the counter feel alive.

**2. A click is a choice option.** The "click" in a clicker is an ordinary `choice` item that adds a resource and returns to the loop via `-> loop`:

```
- 🍪 Bake a cookie (+{per_click}) -> bake

:bake
cookies = cookies + per_click
-> loop
```

Why does `-> loop` come **before** the `:loop` label? Flow in `.lvns` runs top to bottom, and if the target label can also be entered by falling through from above, `validate` complains "label … reached by fall-through". The explicit `-> loop` before `:loop` cuts off the top-down path, so the loop is only entered by a jump. The same trick closes every handler (`:bake`, `:do_oven`, …) — each one ends with `-> loop`, closing the cycle.

## Engine features this example uses

- **Reactive `text`** — the panel `text hud «🍪 {cookies}   per click: {per_click}   ovens: {ovens}   goal: {goal}»` recomputes all `{…}` on its own on the tick.
- **Resource variables** — plain globals: `cookies = 0`, `per_click = 1`, `ovens = 0`, `oven_cost = 15`, `goal = 50`.
- **Click loop via `choice` + `goto`** — the option `- 🍪 Bake a cookie (+{per_click}) -> bake`, where `:bake` does `cookies = cookies + per_click` and `-> loop`.
- **Price scaling** — an oven gets more expensive after each purchase: `oven_cost = floor(oven_cost * 1.6)` (the engine has no `ceil` — use `floor`).
- **Passive income** — each pass through the loop credits `cookies = cookies + ovens`.

## Step-by-step walkthrough

**Resources and price.** Declare the state up front. An undeclared variable would read as `0`, but explicit initialization keeps the economy readable:

```
cookies = 0
per_click = 1
ovens = 0
oven_cost = 15
goal = 50
```

**The reactive panel.** One `text hud …` line with `{…}` — placed once, it updates itself from then on.

**The `:loop` cycle — passive income and the goal check.** Every entry into the loop first credits oven income, then checks for the win, then shows the options:

```
:loop
cookies = cookies + ovens
if cookies >= goal -> win
- 🍪 Bake a cookie (+{per_click}) -> bake
- 🔥 Buy an oven ({oven_cost}🍪, +1/tick) -> buy_oven
- ⬆ Upgrade hands (20🍪, +1 per click) -> buy_hands
```

`if cookies >= goal -> win` is a conditional jump — true goes to the ending, otherwise we fall into the choice list.

**Purchase handlers with an affordability check.** A purchase is two labels — a "check" and an "execute". First verify there are enough cookies, and on a shortfall return to the loop with a message:

```
:buy_oven
if cookies >= oven_cost -> do_oven
Not enough cookies for an oven.
-> loop
:do_oven
cookies = cookies - oven_cost
ovens = ovens + 1
oven_cost = floor(oven_cost * 1.6)
A new oven hums to life! Every tick it bakes cookies on its own.
-> loop
```

The hand upgrade works the same way, just with a fixed price of `20` and a `per_click` bump:

```
:buy_hands
if cookies >= 20 -> do_hands
Not enough cookies for the upgrade.
-> loop
:do_hands
cookies = cookies - 20
per_click = per_click + 1
Nimble hands! Every click counts for more now.
-> loop
```

**The `win` ending.** Goal reached — print the result with interpolation and finish via the built-in `__end` label:

```
:win
Goal reached — {goal} cookies! You are a baking tycoon. 🎉
-> __end
```

## About "idle" income without a timer

Honestly — the engine has no dedicated real-time timer command yet. Passive income here is credited not "by the clock" but **on every pass through the loop**, via the `cookies = cookies + ovens` line at the top of `:loop`. In other words, income drips "per action/tick" — every choice you make first adds cookies from all ovens and only then handles the click or purchase itself. For a turn-based clicker that is a perfectly sane and predictable model — the player sees ovens pay off with every move. If you ever want income "in the background", it will be a layer on top of the same `text` tick, and the crediting logic stays exactly the same.

## Run and check

```sh
# build the transcoder
cd tools/lvnconv && go build -o /tmp/lvnconv .

# compile .lvns → .lvn
/tmp/lvnconv convert -i howto/clicker/clicker.lvns -o /tmp/clk.lvn

# structural check — aim for «0 warning(s)»
/tmp/lvnconv validate /tmp/clk.lvn
```

## Make it yours

- **New upgrades.** Add a resource (`workers = 0`) and a pair of labels modeled on `:buy_oven`/`:do_oven` — a rising price via `floor(cost * k)`.
- **Prestige / reset.** On hitting a threshold, zero out `cookies`/`ovens` but remember the multiplier in a separate variable (`prestige = prestige + 1`) and factor it into income crediting.
- **Multiple resources.** Introduce `flour`, `milk`; an oven costs both cookies and flour — extend the `if` check in the purchase handler.
- **Achievements.** At the top of `:loop`, check thresholds — `if cookies >= 100 -> ach_100` — show a congratulation via `say` or a reactive `text` line and return with `-> loop` (remember a flag so it does not fire twice). The `hint` command is not suitable for this — at runtime it is a no-op.
- **Balance for prestige.** Move the price growth factor into a variable and tune the economy without touching the loop structure.

## Next

- [Language reference](../LANGUAGE.md)
- [Recipe book](../recipes.md)
- [All genres](../README.md)
