# Cast — named, parametric sprite entities

A character is not a pile of pre-rendered images. It is a **named entity** with
a few **named axes** (pose, emotion, outfit…), and its art is a small set of
**layer templates** parameterised by those axes. To show a character in any
state you name the entity and the axis values; the runtime fills the templates
and stacks the resulting layers.

This is the whole system. It is pure data plus one substitution rule, so it
ports to any engine and any authoring tool — the model lives in the `.lvn`
container, not in engine code.

## The `cast` block

A `.lvn` document may carry a top-level `cast`: a map of entity id → definition.

```json
{
  "cast": {
    "mara": {
      "name": "Mara",
      "layers": [
        "/art/mara/body_{pose}.png",
        "/art/mara/face_{emotion}.png",
        "/art/mara/{prop}.png"
      ],
      "defaults": { "pose": "stand", "emotion": "neutral" }
    }
  },
  "script": [
    { "op": "actor", "id": "mara", "emotion": "smile", "position": "left", "show": true },
    { "op": "actor", "id": "mara", "pose": "arms", "emotion": "cry", "prop": "umbrella" }
  ]
}
```

- **`layers`** — an ordered list of image URL templates, composited bottom to
  top. Each `{name}` is an axis the `actor` command fills.
- **`defaults`** — axis values used when the `actor` command doesn't give one.
- **`name`** — the display name (drives the dialogue nameplate).

## Resolution rule

For an `actor` command referencing a cast entity, the runtime builds the axis
values as `defaults` overlaid by the command's own fields, then for each layer
template substitutes every `{name}`:

- every token resolves → the filled URL is a layer to draw;
- a token has no value (not in the command, not in defaults) → that layer is
  **skipped** (so optional parts like `{prop}` simply don't appear until asked).

So `actor mara emotion=smile` (pose defaults to `stand`, no prop) →
`[body_stand.png, face_smile.png]`. Adding `prop=umbrella` →
`[…, umbrella.png]` on top. **K poses + M emotions need K + M images, not
K × M** — the memory win comes for free from templating.

## Why this is engine-agnostic

The model is a dictionary, a list of strings, and `{name}` substitution. Any
runtime — Unity, Godot, web, a print preview — implements it in a few lines:
merge defaults with the command, fill the templates, draw the layers in order.
The reference Unity implementation is `Lvn.SpriteComposer` (pure, tested) plus
`WorldStage`/`WorldActor` (stacks the resolved sprites).

## Creating entities

Authors create an entity by adding a `cast` entry — no code, no engine change.
The same shape describes anything visual that varies by named state (not just
characters: a sign that changes text, a sky that changes weather). Front-ends
(ink/articy) emit the `cast` block from the author's character sheet; the
validator rejects an `actor` that references an unknown entity, the same way an
unknown op or staging tag fails the build.

## The `wardrobe` block — an axis the player may change

An axis is normally driven by the script (`actor mara emotion=smile`). A
`wardrobe` block hands one over to the **player**: it declares, per axis, which
values are on offer, what they are called, what they cost, and which story
variable the choice writes into.

```json
"mira": {
  "layers":   [ "/art/mira/body.png", "/art/mira/outfit_{outfit}.png" ],
  "axes":     { "outfit": ["casual", "gown"] },
  "defaults": { "outfit": "casual" },

  "wardrobe": {
    "outfit": {
      "name": "Outfit", "var": "look", "removable": false,
      "items": [
        { "value": "casual", "name": "Everyday coat", "icon": "/art/mira/outfit_casual.png" },
        { "value": "gown",   "name": "Gala gown",     "icon": "/art/mira/outfit_gown.png",
          "currency": "crystals", "price": 20, "rarity": "rare" }
      ]
    }
  }
}
```

The block is keyed by **axis** — the same axis name the layer templates use, so
equipping an item is just a different value substituted into `{outfit}`.

| Slot field | |
|---|---|
| `name` / `icon` | tab label and tab icon; the label defaults to the axis id |
| `var` | the story variable the pick is written back into. Omit it and the slot is wardrobe-only: the player can dress the character, the story never learns |
| `removable` | may the slot be emptied? An unset axis draws no layer, so "take off" costs nothing. Default `true` |
| `items[]` | `value` (required — the axis value), `name`, `icon`, `currency`+`price` (bought through the wallet; ownership is a sku, so it survives reinstalls), `rarity` |

Resolution stays the rule above, with one addition: an axis the `actor` command
left **unset** is filled from what the player has equipped (and from `defaults`
before that), while an axis the command **pinned to a literal** is a costume the
story forces and a try-on cannot override. An axis written as a `{template}`
(`outfit="{look}"`) is variable-driven: it re-reads the variable on every draw,
including live while the sheet is open.

Presence of a `wardrobe` block anywhere in the catalog is also what puts the
always-open wardrobe in the game's quick menu. Opening it *at a story beat* is
the `wardrobe_show` command — a **novel-shell** feature, not an engine one. The
authoring guide, with a working example of both halves, is
[`../howto/wardrobe/`](../howto/wardrobe/).

## Animating a character (in the panel)

A cast entity may carry an `anim` block — named animations the runtime plays
(`kind: "rigged"`; `auto:"true"` loops on show, others fire from the script via
`actor <id> play=<name>`). Each animation has `loop`, `duration`, and `tracks`;
a track drives one property — **`frame`** (swap the layer's sprite by an axis
value, i.e. frame-by-frame) or a transform tween (`scale`/`scalex`/`scaley`/
`rotation`/`alpha`/`x`/`y`/`screen_x`/`screen_y`). See `animation-system.md`.

The **cast editor in the panel** authors all of this without hand-editing JSON:

- **Animations** section — add named animations; set `loop`/`auto`/`duration`;
  **▶ Play** previews the frames live on the stage.
- **Tracks** — pick a property; for `frame`, pick the axis whose values are the
  frames and the target layer, then **↻** rebuilds evenly-timed keys; for a tween,
  set start → end and an easing.
- **⊞ Import** on a frame set — slice an animated **GIF** or a **spritesheet**
  (columns×rows) into per-frame images client-side, uploaded as the axis's values.

The editor round-trips `kind`/`anim`/layer ids losslessly, so re-saving a rigged
character never drops its animation.

## Compatibility

`actor` keeps working without a cast: a plain `sprite_url` (or the
`body_url`/`clothes_url`/`hair_url` layers) draws directly. The cast block is
additive — reach for it when a character has more than one state.
