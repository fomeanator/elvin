# lvnconv — the narrative transcoder

"ffmpeg for visual novels." Compile a script in any supported authoring format
to `.lvn`, the container the runtime plays, and validate it.

```sh
lvnconv convert  -i <in> [-o <out.lvn>] [-f ink|articy] [-dialogue <name>]
lvnconv validate <in.lvn> [-strict]
lvnconv probe    <in.lvn>
```

## Commands

- **convert** — compile a source to `.lvn` (stdout if `-o` omitted). Format is
  inferred from the extension (`.ink` → Ink, `.json` → articy export) or forced
  with `-f`. `-dialogue` selects which articy Dialogue to compile.
- **validate** — structural checks any build should gate on: unknown op,
  dangling jump targets, duplicate labels. `-strict` also fails on lint
  warnings (labels never targeted).
- **probe** — a one-line summary (op counts) of a `.lvn`.

## Front-ends

| Format | Input | Notes |
|---|---|---|
| Ink | `.ink` | A play-testable subset; staging on `# tag:` lines. Knots→labels, diverts→goto, `*`/`+` choices, tunnels, visit counts, text alternatives. |
| articy:draft | `.json` (export) | DialogueFragment→say, Hub/multi-pin→choice, Jump→goto, Condition→if, Instruction→set/inc. |

Both compile to the same `.lvn` — see [`../../docs/lvn-format.md`](../../docs/lvn-format.md)
and the shared [`../../docs/staging-tags.md`](../../docs/staging-tags.md). Add a
new format by adding a front-end under `internal/`; the validator and runtime
are unchanged.

## Build

```sh
go build -o lvnconv .
go test ./...
```
