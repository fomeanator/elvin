# `.lvns` recipe book

Short reusable snippets that combine into almost any mechanic.
Every pattern comes from the verified examples in this folder — copy and adapt.
For a reference on any element, see [`LANGUAGE.md`](LANGUAGE.md).

---

## Counter / accumulation

```
score = 0
score = score + 1          // increment
score = score - 1          // decrement
hp = min(maxhp, hp + 10)   // add, but cap at a ceiling
gold = max(0, gold - 5)    // subtract, but never below zero
```

## Reactive HUD panel

A `text` with a template in `«…»` re-evaluates itself (~5 times per second) — perfect for
score, health, resources.

```
text hud x=4 y=8 size=42 color=#f1e4c9 «♥{hp}/{maxhp}   💰{gold}   lv.{level}»
text hud hide              // hide the panel
```

## Conditional branch

```
if gold >= 100 -> rich      // true → jump; otherwise fall through
if hp <= 0 -> dead
-> normal                   // the "default" branch
```

## if / else block

```
if has(inv, "key") {
  The door opens with the key.
  -> next_room
} else {
  Locked. You need a key.
}
```

## Choice menu

```
- Fight -> fight
- Run -> flee
- Talk -> talk cost="costs a turn"
```

## Hidden/locked option

The option appears in the menu only while `expr` is true.

```
- Cast a spell -> cast expr="mana >= 10"
- Open the door with the key -> open expr="has(inv, \"key\")"
```
> Escape quotes inside `expr=` as `\"`.

## Inventory (list)

```
inv = []                            // create
inv = push(inv, "potion")           // add
if has(inv, "potion") -> use_potion // check presence
inv = removeat(inv, indexof(inv, "potion"))   // drop one item
Items in the bag — {len(inv)}.      // counter
for it in inv {                      // iterate
  - {it}
}
```

## Shop (purchase with a money check)

```
:buy_sword
if gold >= 12 {
  gold = gold - 12
  atk = atk + 3
  Bought a sword (+3 attack).
} else {
  Not enough gold.
}
-> shop
```

## Dice roll / randomness

```
r = rand(1, 6)              // integer 1..6 inclusive
if chance(0.7) -> success   // 70% chance
crit = rand(0, 3)           // 0..3 (damage spread)
loot = rand(8, 20)
```

## Random event (weighted branch pick)

```
roll = rand(1, 10)
if roll <= 4 -> common      // 40%
if roll <= 7 -> uncommon    // 30%
if roll <= 9 -> rare        // 20%
-> jackpot                  // 10%
```

## Relationship / reputation meter

```
affection = 0
affection = affection + 2          // good line
affection = affection - 1          // misstep
text hud «❤ {affection}»
// the final route unlocks past a threshold:
- Confess -> confession expr="affection >= 5"
```

## Loop scene (a screen you keep returning to)

A hub you come back to after every action. Put `-> hub` before the label
to avoid the fall-through warning.

```
-> hub
:hub
What will you do?
- Look around -> look
- Move on -> leave
:look
You look around...
-> hub
```

## Clickable room (point-and-click)

```
:room
obj id=door sprite_url="/ui/door.png" x=0.8 y=0.5 anchor="0.5,0.5" on_click="door"
obj id=key  sprite_url="/ui/key.png"  x=0.2 y=0.7 anchor="0.5,0.5" on_click="take_key"
Examine the room.
-> room                     // the pause screen keeps hotspots alive
:take_key
has_key = 1
You picked up the key.
-> room
```

## Dragging an item (drag & drop)

`draggable=true` + an `on_drop="target:label"` map (pairs separated by space/comma);
`on_drop_miss` is the miss branch (without it the item just stays where dropped).
A short press is still a click — `on_click` keeps working alongside.

```
:scene
obj id=apple sprite_url="/obj/apple.png" x=0.3 y=0.6 width=0.12 draggable=true on_drop="bag:in_bag" on_drop_miss=missed
obj id=bag sprite_url="/obj/bag.png" x=0.8 y=0.7 width=0.2
Drag the apple into the bag.
-> scene
:in_bag
obj id=apple show=false
inventory_apples = 1
The apple is in the bag!
-> __end
:missed
Missed. Try again.
-> scene
```

`drag_bounds="none"` removes the screen constraint (default is `screen`).

## CG gallery and UI sounds (manifest, not script)

These are not commands — they are blocks in `manifest.json`. Gallery: an art piece
unlocks forever on the first `bg` shown with the same url; a "Gallery" item appears
in the quick menu.

```json
"titles": [{ "id": "my-novel", "gallery": [
    { "id": "cg-beach", "url": "/content/bg/beach.png", "name": "Beach" }
] }],
"ui": { "sounds": {
    "click": "/content/ui/sounds/click.wav",
    "choice": "/content/ui/sounds/choice.wav",
    "type": "/content/ui/sounds/type.wav",
    "volume": 0.8
} }
```

Keep the art `id` stable between releases — unlocks are stored by it.
A missing sound is just silence; everything scales with the user's SFX volume.

## Code/logic lock (no text input)

Keep the state in variables, check it with conditions.

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
:wrong
Wrong combination.
-> panel
```

## Timed choice (real-time timer)

A `choice timeout=…` line right before the option block: a countdown bar appears
above the buttons, and expiry jumps to `timeout_goto`. An open menu or art view
freezes the clock — the timer is only honest against the player.

```
Guards at the door! What do you do?
choice timeout=5 timeout_goto=caught
- Hide under the bed -> hide
- Jump out the window -> jump
:caught
You hesitated — they grabbed you.
-> __end
```

## Voiced lines

A `voice "<url>"` line before a dialogue line: the clip starts with the text,
the next line (or leaving the scene) silences it, so voices never stack.
Volume is the "Voice" slider in settings; the typing blip stays quiet under a voice.
Lines without voice files simply play silent — mixing voiced and mute lines is fine.

```
voice "/content/voice/mara_001.ogg"
Mara: You finally came.
voice "/content/voice/mara_002.ogg"
Mara: I was starting to worry.
Unvoiced narration between lines is fine.
```

## Text input (hero name and beyond)

`input` pauses the story with an input-field overlay; what the player types goes
into a variable and works in interpolation.

```
input var=name prompt="What is your name?" default="Guest" max=24
Hello, {name}!
if name == "Gandalf" -> wizard
```

## A "timer" measured in turns (no real time)

When you need game time rather than a real countdown, measure it in **turns/days** in a loop.

```
day = 1
days = 5
:turn
if day > days -> finale     // time is up
// ... the day's actions ...
day = day + 1
-> turn
```

## Subroutine (one piece of code, many call sites)

```
// called from different places:
ename = "Wolf"  ehp = 12  eatk = 5
call fight
// ...
ename = "Orc"   ehp = 40  eatk = 9
call fight

:fight                      // shared combat engine
{ename} attacks!
// ...
return                      // returns to wherever it was called from
```

## Function with a return value

```
func roll_dmg(base) {
  return base + rand(0, 3)
}
dmg = roll_dmg(atk)
```

## Level-up (fires as many times as needed)

```
:levelup
while xp >= need {
  xp = xp - need
  level = level + 1
  need = floor(need * 1.5)
  ✨ Level {level}!
}
return
```

## Online services from the script (wallet, leaderboard, analytics)

When the game runs with the LVN backend (NovelApp wires it up automatically),
the writer gets ready-made `ext` ops — all fire-and-forget and offline-safe:

```
ext wallet_earn currency=gold amount=10 reason="quest_done"
ext wallet_spend currency=gold amount=5 reason="shop" sku=amulet
ext leaderboard_submit board=quiz_score score_var=score name_var=player_name
ext daily_claim
ext track name=secret_ending_found
```

The `*_var` fields read a story variable — "submit whatever the player entered".
The validator flags these ops with a "host-defined" warning — that is expected.
A custom host enables them with one line: `LvnServiceOps.RegisterAll()`.

## Save and load

```
- Save -> dosave
- Load -> doload
:dosave
save
Your progress is recorded.
-> menu
:doload
load
```

## Multiple endings

```
if score == 3 -> end_perfect
if score >= 1 -> end_ok
-> end_fail
:end_perfect
🏆 Perfect!
-> __end
:end_ok
Not bad.
-> __end
:end_fail
Another time.
-> __end
```

## Staging a shot (atmosphere)

```
bg /content/bg/night.jpg
audio channel=music action=play url="/content/audio/theme.ogg"
particles type=rain on=true
fade to="clear" duration=1.2
actor mara left sad
anim mara scale [1 1.03 1] 3s yoyo     // a gentle "breathing"
camera action=shake amplitude=0.02 duration=0.4
flash to="white" duration=0.3
```

## Translating a novel into other languages

Nothing changes in the script: translations live in sidecar catalogs
`<chapter>.<lang>.json` next to the script (key — the source string, value —
the translation). `lvnconv` builds and updates the catalog:

```sh
lvnconv locale -lang en chapter1.lvns   # creates chapter1.en.json with all strings
```

The catalog collects dialogue lines and speaker names, choice options, `input`
prompts and `text` panels — in story order. The translator fills in the values;
after editing the script, a re-run preserves finished translations and appends new
strings (`-check` — coverage report only, `-prune` — drop stale keys).

Declare the languages in `manifest.json` — a switcher appears in settings and the
quick menu, and the language changes on the fly mid-story:

```json
{ "languages": ["ru", "en"] }
```

A string without a translation shows as the original — the catalog can be filled
in gradually.
