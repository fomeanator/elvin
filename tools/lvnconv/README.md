# lvnconv — the narrative transcoder

"ffmpeg for visual novels." Compile a script in any supported authoring format
to `.lvn`, the container the runtime plays, and validate it.

```sh
lvnconv convert  -i <in> [-o <out.lvn>] [-f ink|articy|adpd] [-dialogue <name>] [-autostage] [-localize]
lvnconv import   <project-dir> -id <id> -name <name> [-content <dir>] [-localize]
lvnconv validate <in.lvn> [-strict]
lvnconv probe    <in.lvn>
```

## Commands

- **convert** — compile a source to `.lvn` (stdout if `-o` omitted). Format is
  inferred from the extension (`.ink` → Ink, `.json` → articy export, dir/`.adpd`
  → articy binary) or forced with `-f`. `-dialogue` selects which articy Dialogue
  to compile. For `.adpd`: `-autostage` emits a `bg` per scene marker and an
  `actor` per speaking character; `-localize` moves text into a
  `<out>.<lang>.json` catalog (see **Localization**).
- **import** — the one-shot pipeline behind the IDE's "Import articy" button:
  an extracted `.adpd` project in, a playable title out (compiled + auto-staged
  `.lvn`, resolved & matted art, a manifest title entry) written into a content
  root. `-localize` also writes the string catalog beside the script.
- **validate** — structural checks any build should gate on: unknown op,
  dangling jump targets, duplicate labels. `-strict` also fails on lint
  warnings (labels never targeted).
- **probe** — a one-line summary (op counts) of a `.lvn`.

## Localization

Every fragment carries a reimport-stable id (its articy GUID), stamped onto the
`say`/option as `id`. `-localize` lifts each line's text out into a catalog
(`text_id → string`) written as `<script>.<lang>.json` and leaves a `text_id`
reference in the `.lvn`. The flow, choices and logic are language-independent, so
translating a novel is just shipping more catalogs against the same keys. The
runtime loads `<script>.<locale>.json` per locale; lines with no catalog entry
fall back to their inline text. Staging (`-autostage`) runs first on the inline
text, so scene markers still become backgrounds.

## Front-ends

| Format | Input | Notes |
|---|---|---|
| Ink | `.ink` | A play-testable subset; staging on `# tag:` lines. Knots→labels, diverts→goto, `*`/`+` choices, tunnels, visit counts, text alternatives. |
| articy:draft | `.json` (export) | DialogueFragment→say, Hub/multi-pin→choice, Jump→goto, Condition→if, Instruction→set/inc. |
| articy:draft | `.adpd` (binary project dir) | Reads the raw binary project — no JSON export needed. Reconstructs the articy model (text, speakers, edges, choices) and runs it through the same back-end. `lvnconv convert <project-dir> -o ch.lvn` (`-start <ordinal>`, `-max <N>`). Format reverse-engineered in [`docs/articy-adpd-format.md`](docs/articy-adpd-format.md). |

Both compile to the same `.lvn` — see [`../../docs/lvn-format.md`](../../docs/lvn-format.md)
and the shared [`../../docs/staging-tags.md`](../../docs/staging-tags.md). Add a
new format by adding a front-end under `internal/`; the validator and runtime
are unchanged.

### articy:draft `.adpd` binary projects

articy's source `.json` is an **export**. If all you have is the raw binary
project (`.adpd` partitions), see the open field guide
[`docs/articy-adpd-format.md`](docs/articy-adpd-format.md) — a full
reverse-engineering of the `ADPD8` format. Text, speakers, variables,
instructions and cast extract directly from the bytes
([`internal/adpd/extract.py`](internal/adpd/extract.py)), and the **branching flow
graph** decodes too — connections are `propid 0x02` ordinal lists
`[src, dst, src_pin, dst_pin]` ([`internal/adpd/decode.py`](internal/adpd/decode.py),
verified: 26k nodes / 27k edges / 1.2k choice points on a real project).
Instructions (`set`/`inc`) and per-pin conditions (`if`/then/else) reconstruct
from the object framing too, and every otherwise-unreachable pocket (entered by
`Jump`/nesting) is surfaced through a synthetic chapter hub so nothing is
dropped. A native Go decoder ([`internal/adpd`](internal/adpd)) does all of this;
the Python files are the reverse-engineering PoCs it was ported from.

## Build

```sh
go build -o lvnconv .
go test ./...
```
