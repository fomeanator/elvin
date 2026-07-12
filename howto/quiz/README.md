# ❓ Quiz

A series of multiple-choice questions with score tracking and a final grade — a classic quiz built on the engine's bare primitives.

## What the example does

`quiz.lvns` asks three questions, each posed as a choice between three options. Every correct answer bumps the `score` variable by one, and the running score is always visible on screen via a reactive badge. After the last question the game moves on to the wrap-up and hands out an A/B/C/D grade based on the points earned (from "🏆 Flawless" to "😅 Rematch?").

## The question pattern

The heart of the genre: each answer option is a `choice` that jumps to its own label. The correct option jumps to the "right" label (`:r1`), where `score` is incremented; wrong options lead to the "wrong" label (`:w1`). Both branches end with a jump to the next question, `-> q2`, so the flow converges back into a single point:

```
:q1
Question 1. How many bytes are in a kilobyte (in binary)?
- 1000 -> w1
- 1024 -> r1
- 512 -> w1
:r1
score = score + 1
Correct! 1024 = 2¹⁰.
-> q2
:w1
Sorry, the right answer is 1024.
-> q2
```

All three options address a label explicitly via `-> label`, so no branch "falls through" by accident — the flow is predictable.

## Engine features used here

- **Choice (`choice`)** — the answer options: `- 1024 -> r1` (the "`- text -> label`" syntax).
- **A counter variable** — `score = 0` at the start and `score = score + 1` in every correct branch.
- **A reactive on-screen `text`** — `text hud x=4 y=8 size=42 color=#bfe3ff «Score: {score}»`; the `{score}` interpolation updates as the score grows.
- **Final branching** — a cascade of conditional jumps `if score == 3 -> grade_a`, `if score >= 2 -> grade_b`, `if score == 1 -> grade_c` with an unconditional fallback `-> grade_d`.

## Step-by-step walkthrough

1. **Initialization.** `score = 0` declares the counter. Right after it comes the persistent score badge: `text hud x=4 y=8 size=42 color=#bfe3ff «Score: {score}»`. It is reactive: every time `score` changes, the text is redrawn.
2. **Intro.** `bg /content/bg/quiz_stage.jpg`, a narration line with the rules, and `-> q1` — the first question begins.
3. **The `q1 → r1/w1 → q2` question structure.** Label `:q1` shows the question and three options; the correct one (`1024`) leads to `:r1` (where `score = score + 1` and a jump `-> q2`), the wrong ones lead to `:w1` (no points, also `-> q2`). Questions 2 and 3 repeat exactly the same template — only the texts and labels change (`q2→r2/w2`, `q3→r3/w3`). The final `:r3`/`:w3` already jump to `-> result`.
4. **The `:result` wrap-up.** Swap the background to `bg /content/bg/quiz_result.jpg`, show the score `{score} out of 3` and run the cascade:

```
if score == 3 -> grade_a
if score >= 2 -> grade_b
if score == 1 -> grade_c
-> grade_d
```

Order matters: the checks run top to bottom, and the first true condition fires and jumps. `score == 3` is caught before `score >= 2`, so the cascade never overlaps. If no condition fires (`score == 0`), the unconditional `-> grade_d` catches the remainder as a fallback.

5. **Grades.** Labels `:grade_a`…`:grade_d` print their verdict, each ending with `-> __end` — the built-in end-of-script label.

## Running and checking

```sh
# build the transcoder
cd tools/lvnconv && go build -o /tmp/lvnconv .

# compile .lvns → .lvn
/tmp/lvnconv convert -i howto/quiz/quiz.lvns -o /tmp/quiz.lvn

# structural check: the goal is 0 warning(s)
/tmp/lvnconv validate /tmp/quiz.lvn
```

If `validate` complains about "label … reached by fall-through", it means a label is both jumped into and "fallen into" from above — add an explicit `-> label` before it.

## Make it your own

- **More questions.** The `qN → rN/wN → q(N+1)` template copies as is — extend the chain and adjust the thresholds in the final cascade.
- **Penalty for a wrong answer.** In the `:wN` labels add `score = score - 1` (or `score = max(score, 0)` via `max` so the score never goes negative).
- **Blitz mode (a real timer).** The line `choice timeout=5 timeout_goto=slow` before the options shows a countdown bar — when it runs out, the story goes down the too-slow branch (question 3 in the example). The menu and art view honestly freeze the clock.
- **Name input.** `input var=player_name prompt="Introduce yourself!"` — the input lives in `{player_name}` (the example opens with it).
- **Attempt limit / a turn-based "timer".** When you need a game-turn count rather than real time: `tries = tries + 1` on every answer and `if tries >= 5 -> result`.
- **Categories.** Group questions by topic and keep separate counters (`sci`, `geo`, …), then show the breakdown in the wrap-up via interpolation.
- **Randomized order.** At the entry point branch via `if rand(2) == 0 -> q_alt` to shuffle questions or their options between playthroughs.
- **Replay.** On any `:grade_*`, replace `-> __end` with a "Play again" choice that resets `score = 0` and jumps back to `-> q1`.

## Next

- [Language reference](../LANGUAGE.md)
- [Recipe book](../recipes.md)
- [All genres](../README.md)
