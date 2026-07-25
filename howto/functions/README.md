# 🧮 Functions & procedures

Not a genre — the reference for `func`, the one keyword that means **two
different things** depending on its body. A lighthouse keeper does the evening
arithmetic; every number on screen comes out of a `func`.

## The two kinds

```
func fuel_cost(hours) { return 3 * hours + 2 }     // EXPRESSION function
func log_entry(what, amount) {                     // PROCEDURE
  Keeper: {what} — {amount} shillings.
}
```

**Expression function** — the body is a single `return <expression>`. The
compiler **inlines** it: `purse = purse + fuel_cost(hours)` compiles to
`set key=purse expr="purse + (3 * hours + 2)"`. There is no call, no label, no
extra op — which is why it works in every place an expression is evaluated:

```
purse = purse + payout(nights, rate, hours)              // assignment
Keeper: minus {fuel_cost(hours)} for the oil.            // interpolation
text hud … «⛽{fuel_cost(hours)}»                         // reactive HUD
if payout(nights, rate, hours) > 0 -> solvent            // condition
- 💰 Bank it -> bank expr="bonus(nights, rate) > 5"       // choice filter
```

They may call each other (`payout` uses `bonus` and `fuel_cost`); the compiler
inlines the whole chain. **Recursion is a compile error** — inlining a function
into itself has no end. Rewrite it as a `while` loop.

**Procedure** — the body is commands, so it is *executed*, not evaluated. It
lowers to `label`/`call`/`return` and is invoked as a statement on its own line:

```
log_entry("Ledger", purse)
```

Arguments bind to ordinary variables right before the jump, so there are no
frames and no recursion here either. A procedure cannot appear **inside** an
expression (`x = log_entry(…) + 1` is a compile error) — it has no value.

Which kind you wrote is decided by the body alone, and the compiler tells you
when a call does not match: wrong number of arguments, a procedure used as a
value, an expression function used as a bare statement.

## Where the values come from

With `nights = 6`, `rate = 25`, `hours = 4`:

| Call | Inlined expression | Value |
|---|---|---|
| `fuel_cost(hours)` | `(3 * hours + 2)` | 14 |
| `bonus(nights, rate)` | `(floor(nights * rate / 10))` | 15 |
| `payout(nights, rate, hours)` | `((floor(nights * rate / 10)) - (3 * hours + 2))` | 1 |
| `bonus(nights + 1, rate)` | `(floor((nights + 1) * rate / 10))` | 17 |

Note the brackets: an argument that is not a bare name or number is wrapped, so
`bonus(nights + 1, rate)` keeps its arithmetic (`(nights + 1) * rate`, never
`nights + 1 * rate`).

## Run and check

```sh
cd tools/lvnconv && go build -o /tmp/lvnconv .
/tmp/lvnconv convert -i howto/functions/functions.lvns -o /tmp/fn.lvn
/tmp/lvnconv validate /tmp/fn.lvn
```

The target is **0 warning(s)** — this file is in the CI compile-gate precisely
so `func` can never become a documented feature that quietly does nothing.
Inspect `/tmp/fn.lvn` to see the inlining: the expression functions leave no
trace at all, while the procedure appears as `label __fn_log_entry` + `call`.

## Make it your own

- **A formula you repeat** — anywhere the same arithmetic appears twice in a
  script (price with a discount, damage with armour, a percentage bar), name it
  with an expression `func` and the formula lives in one place.
- **A staging routine** — a procedure that re-poses a character from variables
  (`func show_hero() { actor hero left armor={arm} weapon={wpn} }`) and gets
  called after every equipment change.
- **A cap or a curve** — `func clamp01(x) { return min(1, max(0, x)) }`, then
  use it wherever a fraction is shown.

## Next

- [Language reference](../LANGUAGE.md) — §Functions
- [Cheat sheet](../CHEATSHEET.md)
- [All genres](../README.md)
