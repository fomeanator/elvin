# LVN conformance corpus

`.lvn` is played by more than one runtime. This directory is the machine-readable
answer to *"what does playing it correctly mean?"* — declarative cases (a `.lvn`
document plus the effects it must produce) and a table saying which package owns
which op. It belongs to no language: Go, C# and JS all read from here.

Why it exists: the op dictionary had four implementations and a test pinned two of
them (`lvn.KnownOps` ↔ `grammar.json`). The runtimes drifted in silence — the
compiler happily emitted ops that no player implemented, and `LvnPlayer`'s
`default:` branch forwarded an unknown op to a stage that ignored it. The
repository's rule is *"Unknown is an error, never a silent skip"*; this corpus is
how that rule reaches the runtimes.

```
conformance/
  README.md          ← you are here: the contract
  ops-owners.json    ← op → owning package + dispatch site, per runtime
  cases/*.json       ← the corpus
```

## Who runs what

| Runner | Location | Runs |
|---|---|---|
| Op-table guard (Go) | `tools/lvnconv/lvn/conformance_test.go` | `ops-owners.json` vs `KnownOps`, vs the real C# dispatch sites, vs the real JS dispatch sites; plus corpus well-formedness |
| C# runtime (EditMode) | `unity/Packages/com.lvn.engine/Tests/Editor/ConformanceCorpusTests.cs` | every case whose `runtimes` contains `csharp` |
| C# dispatch (EditMode) | `unity/Packages/com.lvn.engine/Tests/Editor/OpDispatchContractTests.cs` | one probe command per op against a BARE engine: flow ops must be consumed, staging ops forwarded verbatim |
| JS playground | `tools/lvn-lang/test/conformance.test.js` | every case whose `runtimes` contains `js` |

The Go guard is the cheap one and needs no Unity and no browser: `cd tools/lvnconv && go test ./...`.

## `ops-owners.json` — the ownership table

One row per op in the reference dictionary (`lvn.KnownOps`). Each row says which
package must contain the handler and where each runtime dispatches it. The
columns are documented inside the file itself.

The table is what makes the split **explicit**. `wardrobe_show` is the living
example: it sits in `KnownOps` and in `grammar.json`, but its handler lives in
`com.lvn.engine.shell` (`NovelApp` → `WardrobeSheet`). A host that installed only
`com.lvn.engine` — the public UPM path from the README — gets a silent no-op on
it. That is allowed; going undeclared is not. Its row says `"owner": "shell"`,
and the Go guard asserts the engine package does **not** claim to handle it.

What goes red, and when:

* add an op to `KnownOps` without a row here → **red** (`TestOpOwnersCoverKnownOps`);
* give it `"owner": "engine"` without a `case` in `LvnPlayer.Advance` or
  `VnStage.ApplyStage` → **red** (`TestEngineOwnedOpsHaveCSharpHandlers`);
* claim `"csharp": "player"` for an op the player actually forwards to the stage
  (or vice versa) → **red** (`OpDispatchContractTests`, C# side, behavioural);
* change what the playground implements without updating the `js` column →
  **red** (`TestJsDispatchMatchesTable`).

## Case format

One JSON file per case in `cases/`. Files are read in filename order; the numeric
prefix only groups them for a human reader.

```jsonc
{
  "id": "choice-expr-gate",          // stable id, quoted in failures
  "title": "one line: what must hold",
  "why": "why it matters — the failure a reader would otherwise ship",
  "runtimes": ["csharp", "js"],      // which runtimes MUST pass this case
  "picks":  [0, {"timeout": true}],  // consumed in order at each choice stop
  "inputs": ["Аня"],                 // consumed in order at each input stop
  "max_steps": 500,                  // optional runaway guard (default 500)
  "doc": { "scene": "conformance", "script": [ /* the .lvn */ ] },
  "expect": { /* see below */ }
}
```

### Driving is implicit

There is no step machine to re-implement per runtime. A runner advances until the
next stop and reacts by kind:

* **say** — record it, advance;
* **choice** — record the captions *that were presented*, then consume the next
  entry of `picks`. An integer picks the **n-th presented option** (so a hidden
  option shifts the numbering — that is the point); `{"timeout": true}` fires the
  `timeout_goto` branch;
* **input** — record prompt/default/max, then consume the next entry of `inputs`
  and submit it;
* **wait** — record the duration, then release it;
* **end** — stop.

Running out of `picks`/`inputs` while a stop is open is a case-authoring error and
fails loudly.

### `expect` fields

All optional; each is asserted only when present.

| Field | Meaning |
|---|---|
| `stops` | The ordered stop trace — the spine of a case. See forms below. |
| `vars` | Subset of the final variable bag. Numbers compare by value (`2` == `2.0`). |
| `expr_true` / `expr_false` | Expressions evaluated against the final variables by the runtime's own evaluator. The way to assert ink-style defaults (`unseen == 0`) without a variable existing. |
| `stage` | Ordered staging commands the presentation layer received, each matched as a **subset** of the actual fields. |
| `scene` | Final scene reduction: `bg`, `visible` (set of actor ids), `actors` (per-id field subset). |
| `labels` | Ordered ids of the `label` commands the cursor executed — the route, not just the destination. |

`stops` entries, short and long form:

```jsonc
{ "say": "Текст." }
{ "say":   { "who": "Аня", "text": "Текст.", "style": "whisper" } }
{ "choice": ["Первый", "Второй"] }
{ "choice": { "options": ["Первый"], "timeout": 3 } }
{ "input":  { "prompt": "Введи имя", "default": "Гость", "max": 12 } }
{ "wait":   { "ms": 250 } }
{ "end": true }
```

A `say` immediately followed by a `choice` is ONE beat in the engine (the line and
its options appear together), but it produces **two** stops — a `say` then a
`choice`. Both runtimes normalise to that.

Absent/empty `who` compares equal to `null` — narration is narration in both
runtimes.

## Partial runtimes

The playground player (`panel/public/play/core.js` + `app.js`) is a deliberately
partial implementation: flow and text in full, staging forwarded to whatever the
web renderer happens to draw. That is not a defect to fix, it is a scope. It is
kept honest in **data**, never in runner code:

* `ops-owners.json` `js` column — `player` / `renderer` / `none`. The Go guard
  pins it against the actual `case` labels in `core.js` and `app.js`, so the
  playground cannot quietly gain or lose an op.
* per-case `runtimes` — a case the playground is not expected to pass simply does
  not list `js`. Runners filter on this field and nothing else.

Two consequences the Go guard enforces, so a case can't be written into a trap:

* a case listing `js` may only use ops whose `js` column is `player` or `renderer`;
* a case listing `js` may only assert `stage` entries for ops whose `js` column is
  `renderer` (the C# player also forwards `wait`/`input`/`preload`/`load` to the
  stage; the JS player consumes them itself, so those never appear in its stage log);
* `scene` and `labels` are not observable in the playground — a case asserting
  either must not list `js`.

## Adding a case

1. Write `cases/NN-topic.json`. Fill in `why` — a case whose failure a reader
   can't interpret gets deleted the first time it's inconvenient.
2. List only the runtimes that genuinely must pass it. If a runtime can't, say so
   in `why` and leave it out of `runtimes` — never weaken the expectation to make
   everyone green.
3. `cd tools/lvnconv && go test ./lvn/` — the well-formedness guard checks the
   schema, that every op used is a real op, and that the script validates without
   errors.
4. `cd tools/lvn-lang && node --test` for the JS half; the Unity EditMode suite
   for the C# half.

## Known runtime divergences

Facts, not aspirations — each is why some case above is single-runtime. Fixing any
of them is a product decision, not a test change.

| Behaviour | C# engine | JS playground |
|---|---|---|
| `if cond={key,op,value}` (structured) | evaluated | always false → always takes `else` |
| `set default=true` | initialise-only | plain overwrite |
| `requires_stat` with no `min` | threshold 0 (option shown) | threshold 1 |
| `requires_min` (the importer's field name) | honoured | ignored (`min` only) |
| `if` with no `else` and a false condition | ends the chapter | falls through to the next command |
| `{unset_var}` in text | renders literal `{unset_var}` | renders `0` |
| `inc by="<expression>"` | not evaluated → steps by 1 | evaluated |

### Closed since this table was written

The expression layer was a whole divergence class of its own, now pinned by cases
19 and 20 (both run in **both** runtimes, so these cannot silently reopen):

| Was | Now |
|---|---|
| The browser evaluator held **8 of the 26** built-ins and threw `unknown function` on the rest — every list/map recipe worked in the app and died in the playground | all 26 present, semantics matched case-for-case against `LvnExpression.cs` |
| `rand(n)` was exclusive of `n`; `rand()` always returned 0; `min`/`max` read every argument; `has` worked on lists only | inclusive `rand`, float `rand()`, first-two-args `min`/`max` (mirroring the engine, including its limitation), `has` on lists, maps and strings |
| Dotted stat names (`Way.Moral`, `Relationships.Ivan` — what the articy importer emits by the hundred) were a **hard parse error**; `{Way.Moral}` rendered as literal text | dotted keys nest on write and resolve by member access on read, as `SetVarPath`/`GetVarPath` do; `[i]` indexing too |
| `int()` existed only in the browser — content using it passed there and threw in Unity | removed; the built-in set is closed on both sides |

Storage shape is deliberately **not** part of the contract: case 20 asserts
read-back (interpolation, `if`, `expr_true`), not a flat `"Way.Moral"` key, because
a dotted key nests. Still missing in the browser evaluator: map literals
(`{a: 1}`), which `LvnExpression` parses — no authored content uses them, and a
case would be red on arrival.

One more mirror, deliberately **not** pinned here because pinning it would be red on
arrival: `Lvn.StagingOps.Known` (`com.lvn.engine/Runtime/StagingOps.cs`) is a public
`HashSet<string>` whose doc comment says it "mirrors the Go validator's registry".
It holds 24 of the 30 ops — `anim`, `input`, `load`, `save`, `text` and
`wardrobe_show` are missing — and `CameraRigTests.cs` asserts the count is 24, so
the drift is frozen by a test. Nothing in the runtime dispatches on it (only tests
read it), which is why the gap has no visible symptom yet; a host that asks it
"is this a real op?" gets a false negative six times out of thirty. Whoever fixes
it should add the row to `TestOpOwnersCoverKnownOps`'s neighbourhood in
`tools/lvnconv/lvn/conformance_test.go` so it can never drift again.
