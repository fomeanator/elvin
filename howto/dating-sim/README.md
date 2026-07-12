# 💕 Dating sim / relationship simulator

A game about growing closer to characters: you build up "affection" with the right lines, and the ending depends on who you got closest to.

## What the example does

The story takes place on a university campus: the player has `days = 4` days to get close to one of two heroines — Lia or Kim. Each day the player chooses who to visit, and lines on the date raise (or drop) the affection meter of that specific character. The daily loop spins through the `:day_start` label until the day limit runs out. In the finale the ending "routes" open up: you can only invite someone to the rooftop if their affection reached the threshold — otherwise you get the "alone" ending.

## The three pillars of the genre

1. **Per-character affection meters are ordinary variables.** No magic: `lia` and `kim` are just counters you initialize and change yourself:
   ```
   lia = 0
   kim = 0
   ```
   Lines on dates move them: `lia = lia + 2`, `kim = kim - 1`.

2. **A daily loop via `day`/`days` with an end check.** The day counter and the limit are variables, the loop is a label we jump back to at the end of each day. The very first command in the loop checks the exit condition:
   ```
   :day_start
   if day > days -> finale
   ```

3. **Threshold-gated routes — via `expr=` on a choice option.** An option stays hidden while the expression is false, so a heroine's ending is only available with enough affection:
   ```
   - Invite Lia -> route_lia expr="lia >= 4"
   - Invite Kim -> route_kim expr="kim >= 4"
   - Go alone -> route_alone
   ```

## Engine features used here

- **`actor_map` for two characters** — binds a display name to a cast id, so lines like `Lia [smile]:` drive the right entity:
  ```
  actor_map Lia=lia
  actor_map Kim=kim
  ```
- **Cast emotions in dialogue lines** — the emotion switches right in the line via `[...]`: `Lia [smile]:`, `Kim [sad]:`, `Kim [smile]:`.
- **A reactive HUD with two meters** — a `text` line with `{...}` interpolation recomputes itself whenever the variables change:
  ```
  text hud x=4 y=8 size=40 color=#ffd9e6 «Day {day}/{days}   ❤Lia {lia}   ❤Kim {kim}»
  ```
- **The daily loop** — a `goto` back to `:day_start` after each date (see below).
- **`choice expr=` for gating routes** — the filter expression hides unavailable endings.

## Step-by-step walkthrough

1. **Initialize the meters and the day counter.** Declare the affection values and the calendar before the story begins:
   ```
   lia = 0
   kim = 0
   day = 1
   days = 4
   ```

2. **The reactive panel.** Set `text hud …` with interpolation once — from then on it reflects the current `day`, `lia`, `kim` by itself. The opening background and intro lead into the loop: `-> day_start`.

3. **The `:day_start` loop with the end check.** Check the limit first, otherwise show the "who to visit" choice:
   ```
   :day_start
   if day > days -> finale
   bg /content/bg/campus.jpg
   Day {day}. Who will you spend time with?
   - ☕ Lia at the coffee shop -> see_lia
   - 📚 Kim at the library -> see_kim
   ```

4. **The `see_lia` / `see_kim` dates and ±affection lines.** On a date we show the actor, offer a couple of dialogue choices, and move the meter. The "right" choice gives more, the neutral one less, and a bad one can even subtract:
   ```
   :lia_music
   lia = lia + 2
   Lia [smile]: You're the first person to ask! Here, let me show you my playlist…
   -> end_day
   ```
   ```
   :kim_safe
   kim = kim - 1
   Kim [sad]: I'm actually studying. Later, okay?
   -> end_day
   ```

5. **Day transition.** All date branches converge in `:end_day`, which increments the counter and returns to the top of the loop:
   ```
   :end_day
   day = day + 1
   -> day_start
   ```

6. **The finale with gated routes and an "alone" fallback.** When `day > days`, we land in `:finale`. The heroines' route options stay hidden while affection is below the threshold, while the "go alone" option is always available — a safety path in case the player didn't reach anyone's threshold:
   ```
   :finale
   bg /content/bg/sunset_roof.jpg
   The semester is almost over. Who will you invite to the rooftop at sunset?
   - Invite Lia -> route_lia expr="lia >= 4"
   - Invite Kim -> route_kim expr="kim >= 4"
   - Go alone -> route_alone
   ```
   Each route is a short ending finishing with `-> __end`.

## Run and check

```sh
# build the transcoder
cd tools/lvnconv && go build -o /tmp/lvnconv .

# compile .lvns → .lvn
/tmp/lvnconv convert -i howto/dating-sim/dating-sim.lvns -o /tmp/ds.lvn

# structural check (unknown op, dangling jumps, duplicate labels)
/tmp/lvnconv validate /tmp/ds.lvn
```

The goal is **0 warning(s)**. The most common warning, "label … reached by fall-through", is fixed with an explicit `-> label` or `-> __end` before the target label.

## Make it your own

- **A third character.** Add an `actor_map`, a meter (`mei = 0`), a date branch, and a choice option in `:day_start` — the loop skeleton stays untouched.
- **Event flags.** Remember a past choice in a flag variable and unlock a special line via `expr=` (reference a past date only for players who were there).
- **Gifts/items.** Set up an inventory (`inv = []`, `inv = push(inv, "flowers")`) and grant an affection bonus for a delivered item.
- **Jealousy.** Make one affection's rise drop the other: next to `lia = lia + 2` put `kim = kim - 1`.
- **More days and places.** Increase `days`, add new backgrounds and date spots — the `:day_start` loop scales by itself.

## Next

- [Language reference](../LANGUAGE.md)
- [Recipe book](../recipes.md)
- [All genres](../README.md)
