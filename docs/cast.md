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
`ActorLayer` (stacks the resolved sprites).

## Creating entities

Authors create an entity by adding a `cast` entry — no code, no engine change.
The same shape describes anything visual that varies by named state (not just
characters: a sign that changes text, a sky that changes weather). Front-ends
(ink/articy) emit the `cast` block from the author's character sheet; the
validator rejects an `actor` that references an unknown entity, the same way an
unknown op or staging tag fails the build.

## Compatibility

`actor` keeps working without a cast: a plain `sprite_url` (or the
`body_url`/`clothes_url`/`hair_url` layers) draws directly. The cast block is
additive — reach for it when a character has more than one state.
