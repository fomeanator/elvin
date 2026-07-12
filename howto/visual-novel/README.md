# 📖 Visual novel

The genre of dialogue, speaker emotions and branching choices is native to LVN, because here the whole story is data (`.lvns`), not code.

## What the example does

`visual-novel.lvns` is a short drama about a meeting in the rain. The player comes to Mara, picks a line in the dialogue, and then lands in a nested choice — tell the truth or lie. Along the way two flags accumulate (`warmth`, `told_truth`), and in the finale they split the story into four different endings. No art is required — without graphics the missing sprites simply are not drawn, while the story, text and logic work in full.

## Engine features used here

- **Actor mapping** — links the name in dialogue to a cast id so emotions can switch: `actor_map Mara=mara`
- **Flag variables** — story state, reads as 0 even without a declaration: `warmth = 0`
- **Scene background** — terse form, the id is derived from the file name: `bg /content/bg/porch.jpg`
- **Narration** — a line without a speaker: `Rain drummed on the porch awning. You came back to the place you promised never to return to.`
- **Lines with an emotion** — switches the cast's `emotion` axis: `Mara [smile]: Then come in, don't stand in the rain.`
- **Actor entrance** — position + emotion: `actor mara left neutral`
- **Choice menu** — each line is "`- text -> label`": `- "I missed you." -> warm`
- **Labels and jumps** — jump targets and gotos: `:kitchen` / `-> kitchen`
- **Conditional forks** — jump on a flag or fall through: `if warmth >= 2 -> end_good`
- **Arithmetic on flags**: `warmth = warmth + 1`
- **Fade effect** in an ending: `fade to="black" duration=0.8`
- **Built-in end of script**: `-> __end`

## Step-by-step walkthrough

**1. Mapping and flag initialization.** At the start we link the display name to the cast and declare the flags explicitly. An undeclared variable still reads as 0, but explicit initialization makes the story clearer to whoever edits it.

```
actor_map Mara=mara

warmth = 0
told_truth = 0
```

**2. Background, narration, actor entrance and emotion.** We set the background, give a line of narration, bring the actor to the `left` position with the `neutral` emotion, then — a line of dialogue.

```
bg /content/bg/porch.jpg
Rain drummed on the porch awning. You came back to the place you promised never to return to.

actor mara left neutral
Mara: You came. I wasn't sure you would.
```

**3. Choice menu.** Each `- text -> label` line is a separate option that jumps to its label.

```
- "I missed you." -> warm
- "We need to talk." -> talk
- "I shouldn't have come." -> leave
```

**4. A branch with a flag mutation and an emotion.** Under the label we change the flag, switch the emotion via `[smile]` and head to the shared point `-> kitchen`.

```
:warm
warmth = warmth + 1
Mara [smile]: Then come in, don't stand in the rain.
-> kitchen
```

**5. Nested choice.** Inside the `talk` branch there is another menu. The truth raises `warmth` and sets `told_truth = 1`; the lie leaves the flags alone and gives the sad `[sad]` emotion.

```
:talk
Mara [neutral]: Go on. I'm listening.
- Tell the truth -> truth
- Lie -> lie

:truth
told_truth = 1
warmth = warmth + 1
You told it like it was. Mara was silent for a long time.
Mara [smile]: Thank you for not making things up.
-> kitchen
```

**6. Ending fork on the flags.** All paths converge at `:kitchen`, where the accumulated flags decide the finale. `if cond -> label` jumps when true, otherwise the flow falls to the next line — the final unconditional `-> end_cold` catches the rest.

```
:kitchen
bg /content/bg/kitchen.jpg
Mara: The kettle is still warm. Want some?

if warmth >= 2 -> end_good
if told_truth == 1 -> end_ok
-> end_cold
```

**7. Endings and explicit `-> __end`.** Each ending finishes with a jump to the built-in end label. The `leave` branch additionally dims the lights with `fade` before it ends.

```
:leave
warmth = warmth - 1
Mara [sad]: ...I thought as much.
fade to="black" duration=0.8
ENDING — "Closed Door".
-> __end
```

The explicit `-> __end` before each label matters: it keeps the flow from falling through into someone else's ending and removes the fall-through warning.

## Run and check

```sh
cd tools/lvnconv && go build -o /tmp/lvnconv .
/tmp/lvnconv convert -i howto/visual-novel/visual-novel.lvns -o /tmp/vn.lvn
/tmp/lvnconv validate /tmp/vn.lvn
```

The goal is **0 warning(s)**. The most common warning, "label … reached by fall-through", means the jump target is also being fallen into from above — put an explicit `-> label` or `-> __end` before it.

## Make it your own

- **Add a character** — set up `actor_map Igor=igor`, bring him on stage with `actor igor right neutral` and give him lines `Igor: …`. Built on actor mapping and the `actor` command.
- **A new choice branch** — add a line `- "Say nothing" -> silent` to an existing menu and a `:silent` label with a finale. Built on the "`- text -> label`" construct and labels.
- **Another flag and ending** — set up `trust = 0`, raise it in the right branches and add a fork `if trust >= 1 -> end_trust`. Built on variables, arithmetic and `if ... -> label`.
- **Saving progress** — insert `save` after a key choice and offer `load` to roll back. Built on the `save`/`load` commands.
- **More emotions and backgrounds** — expand the palette of lines (`Mara [angry]: …`) and scene changes via `bg /content/bg/...`. Built on the cast's `emotion` axis and the `bg` command.

## Next

- [Language reference](../LANGUAGE.md) — the single source of truth for syntax.
- [Recipe book](../recipes.md) — short reusable patterns.
- [All genres](../README.md) — the map of game types and a quick start.
- A large real script: `server/content/scripts/soviet-ch1.lvns` — a full-size novel built on the same constructs.
