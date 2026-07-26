# 👗 Wardrobe — how a dressing scene is actually authored

> "The mechanic is there, but how do I *drive* it from the script?"

Short answer: **one line of script**, `wardrobe_show char="mira"`. Everything
else — which outfits exist, what they cost, which story variable they set — is
data on the character, not script.

This folder is the whole mechanic in two files:

| File | Half |
|---|---|
| [`manifest.json`](manifest.json) | the **catalog**: `sprites.<id>.wardrobe` — slots, items, prices, the story variable each slot drives |
| [`wardrobe.lvns`](wardrobe.lvns) | the **moment**: staging, `wardrobe_show`, and branching on what the player picked |

```sh
lvnconv convert  -i wardrobe.lvns -o wardrobe.lvn
lvnconv validate wardrobe.lvn        # OK: 40 command(s), 0 warning(s)
```

---

## The three parts

```
   manifest.json                 .lvns                     runtime
 ┌────────────────┐        ┌────────────────┐        ┌──────────────────┐
 │ sprites.mira   │        │ look = "casual"│        │ actor re-dresses │
 │  .wardrobe     │        │ actor mira     │        │ live while the   │
 │   outfit ──────┼── var ─┤   outfit=      │        │ player browses   │
 │     items[]    │  "look"│   "{look}"     │        │                  │
 │     price      │        │ wardrobe_show  │───────►│ bottom sheet     │
 │     var:"look" │        │   char="mira"  │        │ ▲ confirm        │
 └────────────────┘        │ if look == …   │◄───────┴─ writes `look`   │
                           └────────────────┘         back into state
```

1. **Catalog** — a `wardrobe` block on the cast entity. Keyed by **axis**
   (`outfit`, `hair`, `armor`…): the same axis the character's layer templates
   use, so equipping an item just changes which layer file is drawn.
2. **Moment** — `wardrobe_show char="<entity id>"`. The story **holds** until the
   player confirms or collapses the sheet.
3. **Result** — the slot's `"var"`. On confirm the sheet writes the picked axis
   value into that story variable, so the script can branch on it.

---

## The catalog (`manifest.json` → `sprites.<id>.wardrobe`)

```json
"wardrobe": {
  "outfit": {
    "name": "Outfit",
    "var": "look",
    "removable": false,
    "items": [
      { "value": "casual", "name": "Everyday coat", "icon": "/content/art/mira/outfit_casual.png" },
      { "value": "gown",   "name": "Gala gown",     "icon": "/content/art/mira/outfit_gown.png",
        "currency": "crystals", "price": 20, "rarity": "rare" }
    ]
  }
}
```

**Slot** (the tab) — one per axis:

| Field | Meaning |
|---|---|
| *(the key)* | the **axis** it dresses — must be an axis the entity's layers use (`outfit_{outfit}.png`) |
| `name` | tab label; defaults to the axis id |
| `icon` | tab icon (content url); optional |
| `var` | the story variable the pick is written into. **Omit it** and the slot is wardrobe-only — the player can still dress the character, the story just never learns about it |
| `removable` | may the slot be emptied? An unset axis draws no layer, so "take off" is free. Default `true` |
| `items` | the axis values offered |

**Item** — one axis value:

| Field | Meaning |
|---|---|
| `value` | the axis value written into the axis (and into `var`) — **required** |
| `name` | display name; defaults to the value |
| `icon` | card art; a layer png works fine |
| `currency` + `price` | makes the item **bought**, through the product wallet. Ownership is a wallet sku (`wardrobe:<entity>:<axis>:<value>`), so it survives reinstalls |
| `rarity` | tint key into `ui.wardrobe.rarity_colors` |

⚠ **A cast entity cannot be defined in `.lvns`** — and neither can its wardrobe.
The catalog lives in `manifest.json` (`sprites`) or in a `.lvn`'s `cast` block;
the panel's cast editor writes the same shape. See [`../../docs/cast.md`](../../docs/cast.md).

---

## Driving it from the script

### Open the sheet

```
wardrobe_show char="mira"
```

`char` is a **cast entity id** — anything with a `wardrobe` block can be dressed,
protagonist or not. The story holds while the sheet is up.

The sheet has **no preview pane**: the live actor on stage *is* the mirror. Stage
the character first; if you forget, the shell stages them for you, but they walk
in mid-beat.

### Wear a variable, or force a costume

```
actor mira center outfit="{look}"    // variable-driven: follows the player's pick, live
actor mira center outfit="armor"     // story-forced: the try-on cannot override it
actor mira center                    // unset: the player's equipped value fills in
```

Those three lines are the whole authoring vocabulary:

- **`{braces}`** — a template re-read from the variable on every draw. While the
  sheet is open the preview overrides it, so the character re-dresses as the
  player scrolls.
- **a literal** — a costume the story pinned. A try-on preview does **not**
  override it. Use it for "she is in uniform because she is on duty".
- **unset** — filled by what the player has equipped, and by the entity's
  `defaults` before that.

### Read the result

```
if look == "gown" -> gala
```

A numeric-looking value is written back as a **number** (`3`), anything else as a
**string** (`"gown"`) — compare the way your catalog spells it.

### The always-open wardrobe

The shell adds a quick-menu **Wardrobe** entry automatically as soon as *any*
entity has a `wardrobe` block (turn it off with `ui.wardrobe.show_menu_item:
false`). That surface is a *collection*, not a shop: it lists only outfits that
have crossed the player's path — worn by an actor on stage, offered by a story
wardrobe moment, or bought (`ui.wardrobe.collection_only: false` turns it back
into the full catalog). A story `wardrobe_show` always shows the author's full
catalog for that beat, and marks everything in it as seen.

---

## Where the wardrobe comes from in an **imported** novel

If the novel arrives from articy (`import-articy/`), nobody writes
`wardrobe_show` by hand — the importer substitutes it. Honest picture of what
happens, because every piece of it is a convention you must follow in the source
material:

**1. The catalog comes from the spreadsheet, not from the panel.**
The «Гардероб» sheet of the variables `.xlsx` is the source of truth. Each row:

| Column | Becomes |
|---|---|
| «Переменная» (`Wardrobe.mainCh_Hair = 11;`) | the slot's `var`, and which slot the row belongs to |
| «Значение» | the item's `value` (`0` means "undressed" and makes an NPC slot removable) |
| «Описание» | the item's `name`; a leading `[premium]` tag prices it (`crystals`/20/`rare` by default) |
| «Тех.название» | **presence check only** — an empty cell means "no art shipped" and the row is dropped. The `icon` itself is composed as `/content/art/<character tech name>_<hair\|clothes>_<value>.png` |
| «Не добавлять в гардероб» = 1 | the row is dropped |

Grouping is by variable: `Wardrobe.mainCh_Hair` / `Wardrobe.mainCh_Clothes` are
the protagonist's `hair` / `outfit` slots, and every other `Wardrobe.<StoryName>`
is that character's `outfit` slot — matched by **story name**, not tech name. All
of those names are template fields, not hardcoded; see
`server/content/import-templates/README.md`.

**2. The moment comes from a flag block in articy.**
The importer looks for the span between `Open.Wardrobe = true` and
`Open.Wardrobe = false` (again: `wardrobe.flag_key` in the template). Inside that
span it deletes the hand-built picker — the "choose your dress" lines, the
choices, the labels, the control flow, and the `set Wardrobe.mainCh_*` ops — and
puts in its place:

```
actor <protagonist> center
wardrobe_show char="<protagonist>"
```

Everything else inside the span (backgrounds, audio, other `set`s, other actors)
is preserved in order. [`../import-articy/after-import.lvns`](../import-articy/after-import.lvns)
is exactly this substitution on a real beat, line by line.

**Gotchas that bite in real projects:**

- **An unterminated block is left completely alone.** If the `= false` marker is
  missing (or landed in another chapter), you get the raw articy picker in the
  shipped novel and no warning. Symptom: a dressing scene with text choices.
- **The picker's branch bodies are removed as dead code.** The linearizer appends
  choice branches at the script tail; once their choice is gone they are
  unreachable, so the importer drops them. They used to survive and read as
  "broken wardrobe choices" at the end of every chapter.
- **The importer only ever opens the sheet for the protagonist.** An NPC can have
  a full catalog built from `Wardrobe.<Name>` rows and still never be dressed by
  the story — only through the always-open wardrobe. To dress an NPC in a scene
  you hand-edit the chapter's `.lvns` and add `wardrobe_show char="Felix"`
  yourself. That edit **survives re-import** (the three-way merge keeps
  hand-edited files); see [`../import-articy/README.md`](../import-articy/README.md).
- **Outfits follow their variable automatically.** The importer stamps
  `outfit="{Wardrobe.<Name>}"` (and the protagonist's `hair=`) onto every `actor`
  op that left the slot unset, so a costume change made anywhere in the story is
  visible everywhere afterwards. An `actor` op where you wrote an explicit outfit
  keeps it.
- **A hand-edited catalog does not survive a re-import.** Scripts are protected
  by the three-way merge; `manifest.json` cast entities are **replaced whole** by
  id. If you retune the wardrobe in the panel's cast editor and re-import, the
  spreadsheet wins. Fix the `.xlsx`, not the manifest.

---

## Checklist — "is my wardrobe wired?"

1. The character has a `wardrobe` block, and every slot key is an axis that
   appears in a layer template (`outfit_{outfit}.png`).
2. Every item `value` is listed in the entity's `axes` for that axis — otherwise
   the outfit is never preloaded and pops in late.
3. The slot's `var` is a variable the script seeds *before* the sheet opens.
4. `wardrobe_show char=` names the same entity id the `actor` op stages.
5. `convert` + `validate` → `0 warning(s)`.
6. The host ships **`com.lvn.engine.shell`**. On a bare `com.lvn.engine` the op is
   a silent no-op — deliberate, and declared in `conformance/ops-owners.json`.
