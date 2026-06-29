# The articy:draft `.adpd` binary format — a field guide

*An open, honest reverse-engineering of articy:draft's project files, so others
don't have to start from a blank hex editor. Part of [lvnconv](../README.md).*

articy:draft saves its project as proprietary binary partitions (`ADPD8`). There
is no public specification. This document writes down everything we verified
against a real project (the "Советское воспитание" novel, articy:draft 3.x,
~25k flow objects) — what the bytes mean, what you can pull out of them today,
and — just as importantly — **what you cannot**, and why.

> **TL;DR**
> - The body is a flat stream of typed property entries
>   `<seq:u16><propid:u16><tag:u8><value>`. Strings, GUIDs, numbers, all inline.
> - Text, speakers, variables, instructions and cast extract straight from the
>   bytes ([`extract.py`](../internal/adpd/extract.py)).
> - **The branching flow graph is recoverable too.** Connections are objects whose
>   `propid 0x02` is a 4-element ordinal list `[src_fragment, dst_fragment,
>   src_pin, dst_pin]` — one directed edge. Collect them all and you have the whole
>   graph: linear runs and choices alike ([`decode.py`](../internal/adpd/decode.py),
>   verified — 26,339 nodes / 27,707 edges / 1,211 choice points on the test
>   project, 84% reachable from the entry points).
>
> *(An earlier revision of this guide concluded the graph was not recoverable.
> That was wrong — it is. The encoding is documented in §6.)*

---

## 1. Project layout on disk

Unpack the project (it's often shipped as a `.rar`/`.zip`). You get:

```
<Project>/
  ProjectInfo.aph              # plain XML: <ProjectInfo Version="1"> — name, GUIDs, timestamps
  Assets.sqlite                # SQLite — asset metadata
  Partitions/
    RootPartition(<guid>).adpd
    'Flow'-TypedPartition(<guid>).adpd            # the story graph (largest file)
    'Entities'-TypedPartition(<guid>).adpd        # characters / actors
    'Global_Variables'-TypedPartition(<guid>).adpd
    'Locations'-TypedPartition(<guid>).adpd
    'Template_Design'-TypedPartition(<guid>).adpd # user templates only (see §6)
    'Settings' / 'Journeys' / 'Documents' / 'Export_Rulesets' ...
  Assets/Partition(<guid>)/    # the actual art — loose jpg/png, already usable
  Sessions/                    # tiny edit-session bookkeeping
```

`ProjectInfo.aph` is XML — read it with any parser. Everything in `Partitions/`
is the binary `ADPD8` format described below.

## 2. The `ADPD8` container

Every `*.adpd` partition starts with the same header (all integers little-endian):

| Offset | Type     | Meaning                                                        |
|-------:|----------|---------------------------------------------------------------|
| 0      | char[5]  | magic `"ADPD8"`                                               |
| 5      | byte[3]  | padding `00 00 00`                                            |
| 8      | uint64   | `idx_off` — start of the tail index region                   |
| 16     | uint32   | counter (role unconfirmed; varies per partition)             |
| 20     | uint32   | counter (role unconfirmed)                                    |
| 24     | …        | **body** — the serialised object stream begins here          |

So the file is three regions:

```
[ 0 .. 24 )         header
[ 24 .. idx_off )   BODY   — objects
[ idx_off .. EOF )  TAIL   — a flat index (see §5)
```

```python
magic   = data[:5]                       # b"ADPD8"
idx_off = struct.unpack_from('<Q', data, 8)[0]
body, tail = data[24:idx_off], data[idx_off:]
```

## 3. Primitive encoding

**Strings** — tag `0x12`, a `uint32` byte-length, then UTF-8:

```
12 <len:uint32 LE> <utf-8 bytes>
```

```python
def read_string(buf, o):
    if buf[o] == 0x12:
        ln = struct.unpack_from('<I', buf, o + 1)[0]
        return buf[o+5 : o+5+ln].decode('utf-8'), o + 5 + ln
```

Cyrillic (and any non-ASCII) is stored as plain inline UTF-8. That's why
`strings(1)` shows **nothing** for a Russian project — its bytes (`0xD0–0xD3 …`)
aren't ASCII-printable. Scan for the `0x12` tag instead, or for UTF-8 runs:
`(?:[\xd0-\xd3][\x80-\xbf]){2,}`.

**GUIDs** are stored as 36-char ASCII strings via that same `0x12` form — *not*
as 16-byte binary GUIDs (we searched; absent). Object Ids and every
cross-reference are GUID strings.

**Value wrappers** — a GUID *value* is always preceded by the identical bytes
`04 00 00 00 01 00 3a 00` then its `12 24 00 00 00` string marker (`0x24` = 36).
This wrapper is a value/type marker, **not** a per-property role tag — see §6.
Other structural tag bytes seen around values: `0xf1 0xfd 0xfe 0xfc 0xfa`.

## 4. Object records (the readable part)

Walk the body as a stream of `0x12` strings with the raw gaps between them and a
clear, regular shape appears. A **DialogueFragment** serialises as:

```
'Text'                          ← a schema property-name, stored as a string
   …raw bytes…
'BackgroundColor'
   …raw bytes…
'DisplayNameMultiLanguageText'
   <speaker caption>            ← e.g. "Автор", "Главный герой", "Игрок"
   …raw bytes…
'Entity'                        ← marks the speaker reference
   …raw bytes…
<fragment-guid>                 ← the fragment's OWN unique Id
   …raw bytes…
<html text>                     ← the line, as articy rich-text HTML
   [optional] <instruction>     ← e.g. "Wardrobe.mainCh_Clothes = 11;"
```

Notes proven on the data:

- The fragment GUID is its **own Id** and is unique — four different "Автор"
  lines carry four distinct GUIDs while sharing the caption.
- **Speaker** is the embedded caption (`DisplayNameMultiLanguageText`), not a
  speaker-GUID inside the fragment.
- **Text** is HTML:
  `<html><head><style>…</style></head><body><p id="s0"><span id="s1">…</span></p></body></html>`.
  Strip the tags → the plain line.
- Trailing `X = Y;` / `X += N` strings are pin **instructions** (variable writes).

`Entities` records decode the same way: `DisplayName`, `TechnicalName`
(Marina/Vadim/…), `OriginalSource` (a `file:///…` path to the art),
`PreviewImageAsset`, a template GUID. `Global_Variables` decodes to namespaced
variable names (Wardrobe, Music, Scene, …).

## 5. The tail index

The tail (`[idx_off .. EOF)`) is a large array — on the test project's `Flow`,
~368k `uint32`, ~99.98% of which are valid offsets back into the body. It looks
like a connection table at first, but it is a **per-field offset index**: its
entries are `(offset, 0x01000000)` pairs whose offsets sit only 1–3 bytes apart,
i.e. they point at individual serialised fields (a fast partial-load index), not
at object→object edges.

## 6. The property-entry grammar and the flow graph

The body is **not** a bag of strings — it is a flat stream of typed property
entries. Once you read it as such, the branching falls out.

### 6.1 Entries

```
<seq:uint16> <propid:uint16> <tag:uint8> <value>
```

- `seq` — 1 for a scalar property; 1,2,3,… for successive elements of a list.
- `propid` — the numeric property id (the schema's field; names are not stored).
- `tag` → value: `0x12` string (§3), `0xf6/0xf7` float64 (8 bytes),
  `0xfa 0xfb 0xfc 0xfd 0xfe` uint32 (4 bytes), `0xee` packed colour (4 bytes).

Bytes that don't form a plausible entry are object/array framing — skip them one
at a time; the entries themselves are unambiguous, so a resync scanner recovers
the whole stream without needing the (unshipped) object schema.

### 6.2 The propids that matter

| propid | tag        | meaning                                              |
|-------:|------------|------------------------------------------------------|
| `0x3a` | str        | the object's **GUID Id** (also a `0xfa` companion)   |
| `0x200`| str        | **Text** — the line, as rich-text HTML               |
| `0x100`| str        | **speaker caption** (display name)                   |
| `0x0c` | ref(`0xfe`)| **parent** local ordinal                             |
| `0x39` | ref(`0xfe`)| **self / pin** local ordinal                         |
| `0x02` | ref(`0xfe`)| **connection** — a 4-element ordinal list (below)    |

Objects carry small **local numeric ordinals** (not the GUIDs) and reference each
other by ordinal. A DialogueFragment owns a handful of pin objects; a connection
is its own object.

### 6.3 Connections = the edges

A connection object's `propid 0x02` is four consecutive `0xfe` ref entries:

```
[ source_fragment_ordinal, target_fragment_ordinal, source_pin, target_pin ]
```

That is exactly one directed edge `source → target`. Collect every 0x02 list and
you have the **complete flow graph**:

- out-degree 1 → a linear continuation;
- out-degree ≥ 2 → a **choice** (the targets are the options).

Worked proof on the test project — the first branch found decodes to the actual
choice in the script:

```
FROM  Автор: «Воспоминание о пробуждении на лавке в каком-то парке…»
  ->  Игрок: «И…»
  ->  Игрок: «И мне стыдно за свой поступок.»
```

Mapping a fragment's ordinal back to its text: a fragment record ends with its
GUID (`0x3a`); its ordinal is the modal `parent` (`0x0c`) of the pin entries
that precede the GUID. Reference implementation:
[`decode.py`](../internal/adpd/decode.py).

```sh
python3 internal/adpd/decode.py "<Project>"
# → nodes / edges / out-degree histogram + sample decoded branches
```

### 6.4 Two-stage pipeline: reconstruct the model, reuse the converter

Rather than mapping every propid by hand, reconstruct the articy **object model**
from the binary and emit it in the *same shape the JSON export uses*, then run it
through the existing converter — one back-end for both paths:

```
.adpd  →  model.py  →  export.json  →  lvnconv convert -f articy  →  .lvn
```

The native Go path (`internal/adpd`) builds, per object: `DialogueFragment` for
text nodes, `Instruction`/`Condition` for logic nodes (§6.5), `Hub` for the rest,
`Entity` per speaker, a synthetic `Dialogue` wrapping the start, and the global
variables. Choices fall out where a node has >1 outgoing connection. (The Python
[`model.py`](../internal/adpd/model.py) is a simpler reference that types only
fragments/hubs — the Go path is the complete one.)

This is built into `lvnconv` natively (Go: `internal/adpd`) — point it at the
project directory and it does both stages:

```sh
lvnconv convert <project-dir> -o novel.lvn            # the WHOLE novel (all chapters)
lvnconv convert <project-dir> -start 122 -max 40 -o branch.lvn   # one chapter
```

With no `-start`, every flow node is emitted: all in-degree-0 chapter roots plus
one entry per otherwise-unreachable pocket are fanned out from a synthetic
"chapters" hub, so nothing is dropped. On the test project the full novel decodes
to **say 24374 · choice 934 · if 297 · set 2299** and validates. Of 24,422 text
fragments, **24,387 (99.86%) transfer**; the only 35 that don't are *disconnected
author notes* (team comments, scene-category labels, testing TODOs) with no
in-flow connections — not story. (This project has **no `Jump` nodes** — the
pockets are sub-scenes reached through container nesting, surfaced by the hub.)

Verified end-to-end on the test project — `lvnconv validate` → *OK, 534 commands*;
the decoded chapter contains 67 lines and 12 choices including
`["И…", "И мне стыдно за свой поступок."]`. The Python reference
([`model.py`](../internal/adpd/model.py)) emits the same export JSON if you'd
rather inspect the intermediate model:

```sh
python3 internal/adpd/model.py "<Project>" --start <ordinal> -o export.json
lvnconv convert -i export.json -f articy -dialogue chapter -o chapter.lvn
```

### 6.5 Node types and per-node logic — decoded via object framing

The variable logic (Instruction/Condition nodes) binds too, once you stop
stream-skipping and read the **object framing**. The body is a flat sequence of
length-prefixed objects:

```
<bodyLen:uint32> <u16> <u8 C> <u8 typecode> 00 00 00      (11-byte header)
```

`bodyLen` counts the bytes after the uint32 up to the next object; objects are
flat siblings, so `o += 4 + bodyLen` walks them all (covers ~99% of the body).
The `(C, typecode)` pair is the kind:

| (C, type) | kind |
|-----------|------|
| (1, 4)  | DialogueFragment content (has Text 0x200) |
| (2, 6)  | speaker ref (caption 0x100) |
| (1, 17) | **Instruction / Condition** (0x03 / 0x79) |
| (3, 11) | Connection (edge, 0x02) |

A flow node's ordinal is its self `0x39` (logic nodes) or the parent `0x0c` its
text/speaker children point at (fragments). With clean boundaries **every** logic
node lands in the edge set (2,058/2,058). So:

- Instruction (`X = N;`) → `set`/`inc`;
- Condition (`{ns}.{var} == …`, refs resolved `\{\d+:([^}]+)\}` → `$1`) → `if`
  with two pins split by source pin.

`internal/adpd` does this natively. End-to-end on the test project a chapter
decodes to **say 2171 · set 701 · choice 285 · if 14 · goto 294** and validates.
Branching, choices **and** variable logic all come straight from the `.adpd`. The
JSON export is still useful for the last edges (a few name-less variable refs,
per-`Dialogue` chapter boundaries, and the Jump-entered sub-dialogues).

### 6.6 Localization (`-localize`)

articy text fields are multi-language containers, and each fragment has a stable
GUID. `lvnconv convert <project> -localize -o ch.lvn` uses that: it emits a
**language-independent `.lvn`** where every line/option is a `text_id` (the
fragment GUID) plus a sidecar **catalog** `ch.<lang>.json` mapping `text_id → string`
(`<lang>` from the project's Settings, e.g. `ru`):

```
ch.lvn:        { "op":"say", "who":"Автор", "text_id":"fdeae8a0-…" }
ch.ru.json:    { "fdeae8a0-…": "Прохлада летнего утра бодрит…" }
ch.en.json:    { "fdeae8a0-…": "The morning chill is bracing…" }   ← add later
```

The flow/choices/logic are identical across languages — a translator only fills
another catalog against the same keys. The engine resolves text through a
swappable catalog (`LvnPlayer.Strings`; the shell loads `<script>.<locale>.json`
for its `Locale`): a line uses its `text_id` key, or — for inline-authored lines —
the **source string itself** as the key (gettext/Ren'Py style), falling back to
the source when a translation is missing. On the test project `-localize` yields
24,374 `text_id` lines + a 20,396-string `ru` catalog.

The authoring IDE has a **Translate workbench** (🌐 Languages): it lists every
source line from the compiled chapter, lets you fill a target language, and saves
`<script>.<lang>.json` (keyed by source line) — the same catalog the engine reads.

## 7. What you *can* do straight from `.adpd`

Reliably, with no articy schema:

- **All dialogue** — speaker + text per line, in file order (`extract.py`).
- **The branching graph** — edges + choices (`decode.py`, §6).
- **Variables** (`Global_Variables`) and inline **instructions** (set/inc).
- **Cast** (`Entities`) — names, technical names, art paths.
- **Art** — already loose files under `Assets/Partition(<guid>)/`.

That's enough to reconstruct a playable branching script — modulo per-pin
conditions and `Jump` targets (§6.4), which you layer in from the JSON export
if you have it.

Reference tools — [`extract.py`](../internal/adpd/extract.py) (content) and
[`decode.py`](../internal/adpd/decode.py) (graph):

```sh
python3 internal/adpd/extract.py "<Project>"   # → 23891 lines, speakers, script
python3 internal/adpd/decode.py  "<Project>"   # → 26339 nodes / 27707 edges / branches
```

## 8. Status & contributions

| Area | State |
|------|-------|
| `ADPD8` container, strings, GUIDs | documented, verified |
| Property-entry grammar (`<seq><propid><tag><value>`) | documented, verified |
| DialogueFragment / Entity / variable records | documented, verified |
| Content extraction (text/cast/vars/instructions) | working — `extract.py` |
| **Flow connection graph (edges, choices)** | **decoded — `decode.py`, verified** |
| **`.adpd` → articy model → `.lvn`** | **native in `lvnconv` (`-f adpd`); `model.py` reference; validates** |
| Object framing (length-prefixed) + node types `(C,type)` | **decoded — §6.5, verified** |
| Instructions → `set`, Conditions → `if` (variable logic) | **decoded & wired — `internal/adpd`, validates** |
| Whole-novel emit (all chapters + pockets) | **done — no `-start`, fans out from a chapters hub** |
| Long var names (truncated `…` in 0x03) | **fixed — read the full GUID form in 0x79 + var map** |
| Exact chapter/scene entry ordering | approximate — surfaced via a synthetic hub (no content lost) |

Corrections and additions welcome — especially mapping the remaining propids
(pin conditions, Jump targets) and confirming the encoding across other
articy:draft versions. Findings log:
[`../internal/adpd/FINDINGS.md`](../internal/adpd/FINDINGS.md).

*Verified against articy:draft 3.x. The format may differ on other versions.*
