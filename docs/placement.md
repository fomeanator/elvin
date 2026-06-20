# Placement — put any object on screen from the script

An actor is just the common case of a **stage object**: a sprite placed by the
script. The same fields position a character, a prop, a UI button or a hotspot —
all in **screen fractions**, so a script controls everything without knowing the
resolution. Use `actor` for characters (they dim when not speaking) and `obj`
for everything else; both take the same placement.

## Placement fields

| Field | Meaning |
|---|---|
| `position` | Named horizontal slot: `far_left` `left` `center_left` `center` `center_right` `right` `far_right`. A shorthand for `x`. |
| `x`, `y` | Where the object's **anchor point** sits, as 0..1 of the screen (0,0 = top-left, 1,1 = bottom-right). Default `y` = 1 (screen bottom). |
| `width`, `height` | Size as 0..1 of the screen. Defaults: width 0.46, height 0.62. |
| `anchor` | The object's own pivot as `"ax,ay"` (0..1). Default `"0.5,1"` = bottom-centre, so characters stand on the floor. `"0.5,0.5"` centres an object on `x,y`. |
| `z` | Paint order; higher is in front. Default is insertion order. |
| `flip` | `true` mirrors horizontally (face the other way). |
| `rotation` | Degrees. |
| `opacity` | 0..1. |
| `show` | `false` hides the object (keeps its art for a later `show`). |

Anchor + position cover every common VN composition: a two-shot (`left` /
`right`), a crowd (`far_left`…`far_right`), a close-up (`width`/`height` up,
`y` down), a floating object (`anchor "0.5,0.5"`, custom `x,y`), a character
entering mirrored (`flip`), layered overlaps (`z`).

```json
{ "op": "actor", "id": "mara", "position": "left", "flip": true },
{ "op": "obj", "id": "letter", "sprite_url": "/art/letter.png",
  "x": 0.7, "y": 0.45, "width": 0.18, "anchor": "0.5,0.5", "z": 5 }
```

## Clickable objects → button-driven games

Give any object an **`on_click`** (a label) and it becomes a tappable hotspot:
clicking it jumps the script there (and swallows the tap so it doesn't also
advance the dialogue). A "screen" is just a pause (a `say`, often
`style: narration` to hide the box) with its hotspots placed around it:

```json
{ "op": "label", "id": "menu" },
{ "op": "obj", "id": "play", "sprite_url": "/ui/play.png",  "x": 0.5, "y": 0.4, "anchor": "0.5,0.5", "on_click": "start" },
{ "op": "obj", "id": "quit", "sprite_url": "/ui/quit.png",  "x": 0.5, "y": 0.6, "anchor": "0.5,0.5", "on_click": "bye" },
{ "op": "say", "text": "", "style": "narration" },
{ "op": "label", "id": "start" }, { "op": "say", "text": "Let's begin." }
```

With placement, `on_click`, and the flow/state ops (`goto` / `if` / `set` /
`inc`), the engine isn't only for visual novels — it's enough to assemble almost
any button-driven game: menus, point-and-click rooms, hidden-object screens,
gamebooks. The runtime hook is `LvnPlayer.GoTo(label)`; the rest is data.
