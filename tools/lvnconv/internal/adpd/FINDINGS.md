# Reverse-engineering articy:draft `.adpd` — working findings

> Living log of the binary-format reverse engineering. Verified facts vs open
> questions are tagged. Test corpus: the "Советское воспитание" project
> (articy:draft 3.x project, ~25k flow objects). Everything below was checked
> against the real `Flow` / `Entities` / `Global_Variables` partitions.

## Project layout (on disk, after unrar)

```
<Project>/
  ProjectInfo.aph              # XML: <ProjectInfo Version="1"> project metadata, GUIDs
  Assets.sqlite                # SQLite: asset metadata
  Partitions/
    RootPartition(<guid>).adpd
    'Flow'-TypedPartition(<guid>).adpd          # the story graph (largest)
    'Entities'-TypedPartition(<guid>).adpd       # characters / actors
    'Global_Variables'-TypedPartition(<guid>).adpd
    'Locations'-TypedPartition(<guid>).adpd
    'Template_Design'-TypedPartition(<guid>).adpd  # the SCHEMA / metamodel
    'Settings' / 'Journeys' / 'Documents' / 'Export_Rulesets' ...
  Assets/Partition(<guid>)/    # extracted art (jpg/png), Cyrillic filenames OK
  Sessions/                    # tiny, edit-session bookkeeping
```

`ProjectInfo.aph` is plain XML — DisplayName, project GUIDs, timestamps. Easy.

## ADPD8 container [VERIFIED]

Each `*.adpd` partition:

```
offset 0   : "ADPD8" magic (5 bytes) + 0x00 0x00 0x00 padding to 8
offset 8   : idx_off : uint64 LE  — start of the tail "index" region
offset 16  : uint32  — counter (role unconfirmed; e.g. Entities=653, Flow varies)
offset 20  : uint32  — counter (role unconfirmed; Entities=228)
offset 24  : body begins (serialized object stream)
[24 .. idx_off)   : BODY  — the objects
[idx_off .. EOF)  : TAIL  — a large table of uint32 offsets into the body
```

Tail table: in `Flow` it is ~1.47 MB ≈ 368k uint32, of which ~99.98% are valid
offsets that point back into the body. Strongly suspected to be the
serialized **reference graph** (object→object pointers by file offset). Decoding
it is the open problem for flow connections (see below).

## Primitive encoding [VERIFIED]

- **String**: tag byte `0x12`, then `uint32 LE` byte-length, then UTF-8 bytes.
  ```
  12 <len:u32> <utf8...>
  ```
  Cyrillic is stored inline as UTF-8 (bytes 0xD0–0xD3…), which is why plain
  `strings(1)` misses it — scan for the `0x12` tag instead, or for valid UTF-8
  Cyrillic runs `(?:[\xd0-\xd3][\x80-\xbf]){2,}`.
- **GUID**: stored as a 36-char ASCII string via the same `0x12` form (NOT as a
  16-byte binary GUID — searched, absent). Object Ids and cross-references are
  all GUID strings.
- **Value wrapper**: every GUID *value* is preceded by the identical 8 bytes
  `04 00 00 00 01 00 3a 00` then the `12 24 00 00 00` string marker (0x24=36).
  So `0x3a`-ish bytes are a value/type wrapper, NOT a per-property role tag —
  property identity is positional/schema-driven, not in the wrapper.
- Other structural tag bytes seen wrapping values: `0xf1 0xfd 0xfe 0xfc 0xfa`.
  Grammar of these not fully pinned yet.

## Object records — DialogueFragment [VERIFIED shape]

Walking the `Flow` body as a token stream (strings + raw gaps), dialogue
fragments serialize in a regular order:

```
'Text'                          # property-name string (schema key)
  …raw…
'BackgroundColor'
  …raw…
'DisplayNameMultiLanguageText'
  <speaker caption>             # e.g. "Автор", "Главный герой", "Игрок"
  …raw…
'Entity'                        # marks the speaker reference
  …raw…
@<fragment-guid>                # the fragment's OWN unique Id (see note)
  …raw…
<HTML text>                     # the line, as articy rich-text HTML
  [optional] <instruction>      # e.g. "Wardrobe.mainCh_Clothes = 11;"
```

- The `@<guid>` is the fragment's **own Id**, proven unique: 4 different "Автор"
  fragments carry 4 distinct GUIDs while sharing the caption.
- **Speaker** is identified by the embedded caption (`DisplayNameMultiLanguageText`),
  not by a speaker-GUID inside the fragment.
- **Text** value is HTML: `<html><head><style>…</style></head><body><p id="s0">`
  `<span id="s1">…line…</span></p></body></html>`. Strip tags → plain line.
- Trailing `X = Y;` / `X += N` strings are pin **instructions** (variable writes).

## Content extraction — WORKS [VERIFIED on real data]

A token-stream scan of `Flow` already recovers the full script content:

- **23,891 dialogue records** with speaker + clean text, in file order.
- Cast by line count: Главный герой 7301, Автор 6806, Timur 1584, Lyuba 1292,
  Andrey 1140, Daniil 1043, Игрок 973, …
- **1,667 instructions** (set/inc) on variables (`Wardrobe.*`, `Music.*`, `Scene.*`).
- The opening reads as coherent prose in file order.

`Global_Variables` decodes to namespaced variable names (Wardrobe, Music, Scene…).
`Entities` decodes to cast: DisplayName, TechnicalName (Marina/Vadim/Mother…),
`OriginalSource` (file:/// path to the art), template GUID.

Reference extractor: `extract.py` in this folder (PoC; to be ported to Go).

## The flow connection graph — SOLVED

The branching graph IS recoverable. The body is a flat stream of typed property
entries `<seq:u16><propid:u16><tag:u8><value>` (tags: 0x12 str, 0xf6/0xf7 f64,
0xfa–0xfe u32, 0xee colour). Objects carry small **local numeric ordinals** and
reference each other by ordinal (not GUID — that's why ≈1 GUID/fragment and the
earlier "no GUID edges" dead-end).

Key propids (names not stored; these are the numeric field ids):
- `0x3a` str = object **GUID Id**
- `0x200` str = **Text** (HTML)   ·   `0x100` str = **speaker caption**
- `0x0c` ref = **parent** ordinal   ·   `0x39` ref = **self/pin** ordinal
- **`0x02` ref ×4 = a CONNECTION** = `[src_frag, dst_frag, src_pin, dst_pin]` → one edge

Collect every `0x02` 4-list → the full directed graph. out-degree 1 = linear,
≥2 = choice. **Verified on the test project: 26,339 nodes, 27,707 edges,
out-degree {1:25110, 2:1082, 3:108, 4:18, 5:2, 8:1} = 1,211 choice points, 84%
reachable from the 18 entry roots.** First decoded branch matches the real script:

```
FROM Автор: «Воспоминание о пробуждении на лавке в каком-то парке…»
  -> Игрок: «И…»
  -> Игрок: «И мне стыдно за свой поступок.»
```

Fragment ordinal ↔ text: a fragment ends with its GUID (`0x3a`); its ordinal is
the modal `parent` (`0x0c`) of the pin entries just before the GUID.

Reference: `decode.py` (graph) + `extract.py` (content), both verified live.

More propids, now known:
- `0x00` ref = the object's **own global ordinal** (unique 0,1,2,…; the authoritative
  self-id — the `0x02` edge endpoints live in this ordinal space). Prefer this over
  the "modal parent of pins" heuristic for fragment ordinals.
- `0x0c` ref = **parent/owner** ordinal (container ids recur with high counts —
  e.g. one parent referenced by 931 children).

### Still open: sub-dialogue / Jump entry (the 16% unreachable)
The 3,966 unreachable nodes are NOT hidden/encrypted — they're internally
connected (4,126 intra-edges) and sit in the same weak component as the main
flow. They simply have **no `0x02` flow-edge entering them from the reachable
side** (verified: 0 reachable→unreachable 0x02 edges). Their entry is articy's
**nesting / `Jump`** semantics — and it is NOT a plain scalar reference either
(verified: excluding self/conn/parent/pin, no propid points a scalar ref into the
pockets). So the pocket entry is structural (Dialogue/FlowFragment input-pin
invocation or a Jump object), not a single field.

**Right way to close it (per design): reconstruct the full articy object model
first, then convert.** I.e. `.adpd → {objects: Type+Properties+InputPins/
OutputPins/Connections/Target, GlobalVariables} → internal/articy/convert.go →
.lvn`. Rebuilding every object with all its reference properties captures Jump /
nesting inherently, and `convert.go` already knows the flow semantics
(Jump→goto, Condition→if, Hub/multi-pin→choice). The JSON export produces that
same model — so the `.adpd` front-end's job is to emit it from the binary.

## Two-stage pipeline — WORKING (`model.py`)

`model.py` reconstructs the articy model from `.adpd` and emits it in the
**JSON-export shape**, then the existing back-end converts it:

```
.adpd  →  model.py  →  export.json  →  lvnconv convert -f articy  →  .lvn
```

Verified end-to-end on the test project (start = story opening, ordinal 102):
- `lvnconv validate` → **OK: 534 commands, 6 warnings** (minor fall-through labels
  from synthetic hubs).
- `probe` → 67 say, **12 choice**, 31 label, 13 goto — a real branching chapter,
  including the decoded player choice
  `["И…", "И мне стыдно за свой поступок."]`.

How the model is built (per object, keyed by the edge ordinal = modal `0x0c`
parent of the pins before the GUID):
- fragment (has text 0x200) → `DialogueFragment` {Id=GUID, Text, Speaker=caption,
  OutputPins/Connections→successor GUIDs};
- text-less flow node (Hub/container) → `Hub` with a synthetic Id, so flow routes
  through it; leaves connect to the synthetic `Dialogue` exit;
- speakers → `Entity` {Id=caption, DisplayName=caption};
- one synthetic `Dialogue` whose input pin → the chosen start;
- `GlobalVariables` from the Global_Variables partition.

Choices fall out automatically (a node with >1 outgoing connection →
`convert.go` emits `choice`).

## Node types & per-node logic — SOLVED via object framing

An earlier revision concluded the logic couldn't be bound with heuristics. That
was right about heuristics and wrong about the format: the body **is** cleanly
delimited — by a length-prefixed object framing. Decoding that gives exact object
boundaries, and the logic binds 100%.

### Object framing [VERIFIED]
The body is a flat sequence of length-prefixed objects. Each object header:

```
<bodyLen:uint32> <u16 B> <u8 C> <u8 typecode> 00 00 00      (11 bytes)
```

`bodyLen` counts the bytes **after the uint32** up to the next object; the body
`[hdr+11 : hdr+4+bodyLen]` is a flat run of property entries. Objects are **flat
siblings** (a fragment's text/speaker/pins/connections are separate objects whose
parent `0x0c` points back at it) — so walking by `o += 4 + bodyLen` visits every
object. Verified: the chain from the first header covers **99%** of the body
(183,525 objects), e.g. the fragment header `71 01 00 00 …` (bodyLen 369) lands
exactly on the next header 369 bytes on.

### Object kinds by (C, typecode) [VERIFIED by content correlation]
| (C, type) | n      | kind                                    |
|-----------|--------|-----------------------------------------|
| (1, 4)    | 24,445 | **DialogueFragment** content (has Text 0x200) |
| (2, 6)    | 24,950 | **speaker** ref (caption 0x100, string) |
| (1, 17)   | 2,058  | **Instruction / Condition** node (0x03 / 0x79) |
| (3, 11)   | 27,865 | **Connection** (edge, 0x02)             |
| (3, 4), (1, 5), (7, 16) | … | pins / structural |

A flow node's edge ordinal = its **self `0x39`** (logic nodes) or the **parent
`0x0c`** its text/speaker children point at (dialogue fragments). With clean
boundaries every logic node's 0x39 lands in the edge set — **2,058 / 2,058**.

### Result
- Instruction (assignment) → `Instruction` model → `set`/`inc`.
- Condition (`==`/`<`/`>`) → `Condition` model with two pins (split by source
  pin) → `if`/then/else.
- Variable refs `{nsGuid:NsName}.{varGuid:VarName}` → `NsName.VarName`.

End-to-end on the test project (native Go `internal/adpd`): a chapter decodes to
**say 2171 · set 701 · choice 285 · if 14 · goto 294**, `lvnconv validate` → OK.
The branching, the choices, **and the variable logic** all come straight from the
`.adpd`.

Still minor: a few condition RHS use a name-less ref `{guid}` (no inline name) so
they don't fully resolve; per-`Dialogue` chapter boundaries (currently a BFS cap);
the sub-dialogue/Jump pocket entry (§ "the 16% unreachable").
