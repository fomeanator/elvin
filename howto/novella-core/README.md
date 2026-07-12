# 🧱 Novel Core

The five basic building blocks every novel on this engine is made of. This example is not a story but a **reference**: a minimal pass over each of the five mechanisms so you can copy them into your own story.

## What the example does

`novella-core.lvns` walks the player through a short "first case": a slide intro with no characters, lines from the protagonist and the mentor, one paid and two free choices, stat gains in all four combinations, and then a fork **without a choice**, which the engine resolves on its own from the accumulated stat `X`. No art is required: missing sprites are drawn as gray placeholders, while text, stats, and logic work in full.

## The five bricks

**1. Slide.** Three kinds of frame:
- no characters — just text over a background (`bg …` + a narration line);
- with the protagonist — the hero is always on the **left** (`actor hero left`);
- with another character — everyone else is always on the **right** (`actor nata right neutral`).

**2. Choice — paid and free.** The first line is marked with `cost=` (a price label next to the option); the rest are free. `cost` is only a label: deduct the resource explicitly in the handler label.

```
- Work through everything by the book. -> book cost="1 hour"
- Trust your gut. -> gut
- Ask Nata for advice. -> ask
```

**3. Stat gain per choice** — all four combinations:
- **one stat, one point:** `inc key="exp" by=1`
- **one stat, several points:** `inc key="X" by=6`
- **several stats, one point each:** `inc key="trust" by=1` + `inc key="exp" by=1`
- **several stats, several points each:** `set key="X" expr="X+2"` + `set key="exp" expr="exp+2"`

**4. Fork without a choice, driven by stats.** The player reaches slide 100. From there it is not the player who chooses but the stat `X`: `X > 5` leads to slide 101 (the fast path), otherwise to slide 110 (the long path), and both branches converge at slide 120.

```
:slide100
if X > 5 -> slide101
-> slide110

:slide101
...
-> slide120

:slide110
...
-> slide120

:slide120   // both branches converge here
```

**5. Hint popup.** `hint` pops up as a window at the **top center** of the scene. `duration>0` — auto-hide after N seconds; `show=false` removes it manually. The text interpolates `{vars}`.

```
hint text="Hint: the choice below changes your stats." duration=6
hint text="Stats at the finish — exp: {exp}, trust: {trust}, X: {X}." duration=8
```

## Build and check

```
lvnconv convert  -i novella-core.lvns -o novella-core.lvn
lvnconv validate novella-core.lvn        # OK: 61 command(s), 0 warning(s)
```

## Engine features used

- Slides: `bg …` (text) / `actor hero left` (protagonist) / `actor nata right neutral` (others)
- Paid/free choice: `- text -> label cost="…"`
- Stat gains: `inc key="…" by=N`, `set key="…" expr="…"`
- Stat-driven fork without a choice: `if X > 5 -> label` / `-> label`
- Hint popup: `hint text="…" duration=6`
- Stat interpolation in text: `{exp}`, `{X}`
