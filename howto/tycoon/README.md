# 🏪 Tycoon / management

A turn-based economic game where each turn is a "month" and the whole gameplay boils down to variables, income formulas, and random events.

## What the example does

The player runs a coffee shop chain. One turn equals one month: at the start of the month income is calculated, then the player invests money in a new location or in advertising, after which a random event rolls. The goal is to accumulate 500💵 within `months` = 8 months. When time runs out, the script checks the capital and routes to the win or lose branch.

## The turn loop

The core pattern is a turn-based loop with the `:turn` label, which first checks for game over and then calculates income:

```
:turn
if month > months -> finale
// Monthly income depends on the number of locations and reputation.
income = shops * 25 + rep * 5
cash = cash + income
```

Income comes from the formula `income = shops * 25 + rep * 5` — the more locations and reputation, the higher the profit. Then the player picks an investment (a choice `- … -> label`), the random event resolves in the `:event` node, and the turn closes:

```
:next_month
month = month + 1
-> turn
```

`month = month + 1` advances the counter, and `-> turn` sends the flow back to the start of the loop. There is exactly one exit from the loop: when `month > months`, the jump goes to `:finale`.

## Engine features used here

- **Reactive HUD** — a `text` line with `{…}` interpolation redraws itself whenever variables change:
  `text hud x=3 y=8 size=40 color=#c9f0d0 «Month {month}/{months}  💵{cash}  🏪{shops}  ⭐{rep}»`
- **Turn loop** via a label and goto: `if month > months -> finale` on entry and `-> turn` at the end.
- **Choice-based investments with a funds check** — each option leads to a node that checks the money first: `if cash >= 120 -> do_shop`, otherwise a message and a return `-> turn`.
- **Random events** via `roll = rand(1, 6)` and a cascade of thresholds `if roll <= 2 -> ev_good` / `if roll <= 3 -> ev_bad`.
- **Balancing via `max`** — reputation never goes negative: `rep = max(0, rep - 1)`.

## Step-by-step breakdown

**Resources.** All state variables are declared in the header:

```
cash = 100
shops = 1
rep = 0
month = 1
months = 8
```

**The panel.** A single `text hud …` line with interpolation assembles the entire game status; there is no need to update it manually each turn — the values are substituted automatically.

**The `:turn` loop — income and choice.** On entry, a game-over check, then income is credited and the investment fork follows:

```
Month {month}. Income came to {income}💵. What will you invest in?
- 🏪 New coffee shop — 120💵 -> invest_shop
- 📣 Advertising — 40💵 (+reputation) -> invest_ad
- 💰 Nothing, keep saving -> hold
```

**Investment handlers.** Each one checks the funds first, and only when there is enough does it deduct the money and change the stats:

```
:invest_shop
if cash >= 120 -> do_shop
Not enough money for a new location.
-> turn
:do_shop
cash = cash - 120
shops = shops + 1
A new coffee shop is open! That makes {shops} now.
-> event
```

Advertising works the same way (`-40💵`, `rep = rep + 2`), and the "keep saving" option (`:hold`) simply proceeds to `:event`. Note that when funds are short, the flow returns to `-> turn` (the same month), while after a successful purchase it goes to `-> event`.

**The `:event` node with weighted outcomes.** A die roll and a cascade of thresholds set the probabilities: outcomes `ev_good` (1–2), `ev_bad` (3), and neutral (4–6):

```
:event
roll = rand(1, 6)
if roll <= 2 -> ev_good
if roll <= 3 -> ev_bad
A quiet month with no surprises.
-> next_month
```

`:ev_good` grants a bonus that scales with the number of locations (`bonus = 30 + shops * 10`), `:ev_bad` takes money and a bit of reputation, protected by `max`. All three branches converge on `:next_month`.

**`:next_month`** advances the counter and closes the loop (`month = month + 1` → `-> turn`).

**The finale with a check.** When the months run out, the summary and the outcome fork:

```
:finale
bg /content/bg/city_office.jpg
The year has come to an end. Capital {cash}💵, {shops} locations, reputation {rep}.
if cash >= 500 -> win
-> lose
```

`if cash >= 500 -> win`, otherwise "failure" via `-> lose`. Both endings go to `-> __end`.

## On balancing

The entire economy here is just variables and formulas in the `.lvns`. The investment prices (`120`, `40`), income multipliers (`shops * 25 + rep * 5`), bonus size (`30 + shops * 10`), win threshold (`500`), and event probabilities (the `roll <= 2` / `roll <= 3` thresholds) are plain numbers in the script text. You can tune the difficulty right in the file without touching engine code: nudge a multiplier and the whole pace of the game changes.

## Run and check

```sh
# build the transcoder
cd tools/lvnconv && go build -o /tmp/lvnconv .

# compile .lvns → .lvn
/tmp/lvnconv convert -i howto/tycoon/tycoon.lvns -o /tmp/tyc.lvn

# structural check: unknown op, dangling jumps, duplicate labels
/tmp/lvnconv validate /tmp/tyc.lvn
```

The target is **0 warning(s)**. The most common warning, "label … reached by fall-through", is fixed by an explicit `-> label` before the target label.

## Make it your own

- **New investment types** — add an option to the `:turn` fork and an `invest_*`/`do_*` pair (for example, staff training that raises income per location).
- **Loans and debt** — a `debt` variable with interest deducted in `:next_month`; the win only counts when `debt == 0`.
- **A competitor** — a `rival` variable that grows on its own and cuts your `income` until you invest in fighting it.
- **More events** — extend the cascade in `:event` (widen the `rand` range and add `if roll <= N` branches).
- **Multiple resources/departments** — separate counters (inventory, staff, loyalty), each with its own formula and its own contribution to total income.

## Next

- [Language reference](../LANGUAGE.md)
- [Recipe book](../recipes.md)
- [All genres](../README.md)
