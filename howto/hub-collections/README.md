# 🏛 Hub & collections — hub layout, three novel types

Demonstrates the **hub layout** (instead of a carousel) and how three content
"types" — **Expeditions / Dates / Reality Storyline** — are NOT three systems but one
novel + a `type` field + unlock conditions on `global.*` flags. One engine;
each novel has its own visuals and content — all driven by manifest data.

## The full chain

1. **Hub** (`ui.browse.layout = "hub"`) — game title + collection tiles.
2. Tap a collection → **card list**; tap a card → **detail view** (image +
   text + "Play").
3. "Play" on an expedition **spends 1 energy** (`cost`) — not enough → popup → store.
4. A regular novel plays (`exp_victoria.lvns`) with branching and a condition.
5. **The finale sets `global.*` flags** — `exp_victoria_done`, `date_victoria`,
   `reality_beat_2`.
6. The date and the reality beat are gated by these flags in the manifest (`unlock`) — as
   soon as the flag is set, their cards stop being locked.

The expedition script is `exp_victoria.lvns` (compiles, 0 warnings). The flag inside
the expedition (`daring`) is local; the finale flags (`global.*`) are shared per player.

## Manifest (excerpt)

```json
{
  "ui": { "browse": { "layout": "hub", "title": "My Story Hub", "subtitle": "Choose…" } },

  "collections": [
    { "id": "expeditions", "name": "Expeditions", "type": "expedition",
      "card": { "image": "/content/cards/exp.jpg", "desc": "Time travel" },
      "titles": ["exp_victoria"] },
    { "id": "dates", "name": "Dates", "type": "date",
      "card": { "image": "/content/cards/dates.jpg", "desc": "Romance" },
      "titles": ["date_victoria"] },
    { "id": "reality", "name": "Reality Storyline", "type": "reality",
      "card": { "image": "/content/cards/reality.jpg", "desc": "What's happening back home" },
      "titles": ["reality_2"] }
  ],

  "titles": [
    { "id": "exp_victoria", "type": "expedition",
      "card": { "image": "/content/cards/exp_victoria.jpg", "desc": "A ball at Victoria's court." },
      "cost": { "currency": "energy", "amount": 1 },
      "seasons": [ { "chapters": [ { "id": "exp_victoria", "script_url": "/content/scripts/exp_victoria.lvn" } ] } ] },

    { "id": "date_victoria", "type": "date",
      "unlock": "global.exp_victoria_done",
      "locked_hint": "Finish the expedition with Victoria",
      "card": { "image": "/content/cards/date_victoria.jpg", "desc": "A date with Victoria." },
      "seasons": [ { "chapters": [ { "id": "date_victoria", "script_url": "/content/scripts/date_victoria.lvn" } ] } ] },

    { "id": "reality_2", "type": "reality",
      "unlock": "global.reality_beat_2",
      "card": { "image": "/content/cards/reality_2.jpg", "desc": "Beat 2." },
      "seasons": [ { "chapters": [ { "id": "reality_2", "script_url": "/content/scripts/reality_2.lvn" } ] } ] }
  ]
}
```

## What is engine here, and what is data

| Engine (shared, one firmware) | Data (each game has its own) |
|---|---|
| the hub renders **any** `collections` | which collections, names, art |
| `type` is a free-form tag, the engine never reads it | `expedition`/`date`/anything |
| `unlock` is an expression over `global.*` | which flag gates the card |
| `cost` charges the wallet | 1 energy / free |
| the finale sets a flag via `set key="global.…"` | which flags, what they unlock |
| screen theme from `ui.browse` | colors, art, texts, shape |

A different novel = different `collections`/`type`/`unlock`/`cost` + a different `ui.browse`
→ different visuals and content, **same firmware**. Expeditions/Dates/Reality
are product data, not the engine.

## Themes

`ui.browse.theme` swaps the whole look in one word — not just the palette but the
*cut*: corner radius, the glowing edge on panels, heading case and tracking, and
the atmosphere behind the content.

```json
"ui": { "browse": { "layout": "hub", "theme": "cyber" } }
```

| theme | look |
|---|---|
| `midnight` (default) | warm plum neutral, rose accent, round pills, no backdrop |
| `cyber` | cold navy, cyan accent, chamfered corners, grid + scanlines + vignette |

The individual `*_color` fields still work and override the theme one value at a
time — the theme is the fallback, not a lock.

Themes carry **no asset files**: the backdrop is generated in code and every icon
is drawn as a vector path, so a theme costs nothing to ship and can't go missing
from a build. That also fixes a class of bug you cannot see coming — icons used
to be Unicode glyphs, and a phone whose font lacked one drew an empty box.

## Build and check

```
lvnconv convert  -i exp_victoria.lvns -o exp_victoria.lvn
lvnconv validate exp_victoria.lvn        # OK: 32 command(s), 0 warning(s)
```
