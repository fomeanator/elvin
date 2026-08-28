# LVN conformance corpus

`.lvn` is played by more than one runtime. This directory is the machine-readable
answer to *"what does playing it correctly mean?"* — declarative cases (a `.lvn`
document plus the effects it must produce) and a table saying which package owns
which op. It belongs to no language: Go and C# both read from here.

Why it exists: the op dictionary had four implementations and a test pinned two of
them (`lvn.KnownOps` ↔ `grammar.json`). The runtimes drifted in silence — the
compiler happily emitted ops that no player implemented, and `LvnPlayer`'s
`default:` branch forwarded an unknown op to a stage that ignored it. The
repository's rule is *"Unknown is an error, never a silent skip"*; this corpus is
how that rule reaches the runtimes.

```
conformance/
  README.md          ← you are here: the contract
  ops-owners.json    ← op → owning package + C# dispatch site
  cases/*.json       ← the corpus
```

## Who runs what

| Runner | Location | Runs |
|---|---|---|
| Op-table guard (Go) | `tools/lvnconv/lvn/conformance_test.go` | `ops-owners.json` vs `KnownOps`, vs the real C# dispatch sites, vs the C# `StagingOps.Known` registry; plus corpus well-formedness |
| C# runtime (EditMode) | `unity/Packages/com.lvn.engine/Tests/Editor/ConformanceCorpusTests.cs` | every case whose `runtimes` contains `csharp` |
| C# dispatch (EditMode) | `unity/Packages/com.lvn.engine/Tests/Editor/OpDispatchContractTests.cs` | one probe command per op against a BARE engine: flow ops must be consumed, staging ops forwarded verbatim |
| Browser player (Go, needs node) | `tools/lvnconv/lvn/browser_expr_guard_test.go` | every case whose `runtimes` contains `js`, played by the REAL `panel/public/play/core.js` through `browser-runner.mjs` |

The Go guard is the cheap one and needs no Unity: `cd tools/lvnconv && go test ./...`.

**Set `LVN_REQUIRE_NODE=1`.** Without node the browser rows SKIP instead of
failing — reasonable on a machine that has no node, and a silent hole anywhere
else: the corpus declares 26 `js` cases, and a skipped runner reports the same
green as a passing one. The skip is deliberate; going green while a whole
runtime is unchecked is not. CI and every local run of this repo must set the
variable: `cd tools/lvnconv && LVN_REQUIRE_NODE=1 go test ./...`.

## `ops-owners.json` — the ownership table

One row per op in the reference dictionary (`lvn.KnownOps`). Each row says which
package must contain the handler and where the C# runtime dispatches it. The
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
* add it to `KnownOps` without adding it to the public C# registry
  `Lvn.StagingOps.Known` → **red** (`TestCSharpKnownOpsMirrorKnownOps`; see
  §*The fifth mirror* below).

## Case format

One JSON file per case in `cases/`. Files are read in filename order; the numeric
prefix only groups them for a human reader.

```jsonc
{
  "id": "choice-expr-gate",          // stable id, quoted in failures
  "title": "one line: what must hold",
  "why": "why it matters — the failure a reader would otherwise ship",
  "runtimes": ["csharp"],            // which runtimes MUST pass this case
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

## One runtime, on purpose

There used to be a second player: the browser playground's JS core. It was a
deliberately partial implementation — flow and text in full, staging forwarded to
whatever the web renderer drew — and it charged every new op a second
implementation plus a column in this table. It has been deleted; `runtimes` keeps
its shape because the field is how a case says who must pass it, and a second
runtime (a server-side player, another engine) may well arrive later.

The corpus itself is unchanged by that: it describes the LANGUAGE, not any one
player. Every divergence the JS player once caused is listed below — closed, and
worth reading before adding a runtime, because they are the mistakes a second
implementation actually makes.

## Adding a case

1. Write `cases/NN-topic.json`. Fill in `why` — a case whose failure a reader
   can't interpret gets deleted the first time it's inconvenient.
2. List only the runtimes that genuinely must pass it. If a runtime can't, say so
   in `why` and leave it out of `runtimes` — never weaken the expectation to make
   everyone green.
3. `cd tools/lvnconv && go test ./lvn/` — the well-formedness guard checks the
   schema, that every op used is a real op, and that the script validates without
   errors.
4. The Unity EditMode suite runs the behavioural half.

## Known runtime divergences

None outstanding. Every entry this table once carried is closed and pinned by a
case; the JS player that caused them is gone, but the cases stay — they are the
list of mistakes a SECOND runtime makes, and the next one will make them too.
What the table held, and where it went:

| Was | Resolution | Pinned by |
|---|---|---|
| `if cond={key,op,value}` (structured) ignored in JS → always took `else` | JS evaluates it, mirroring `LvnPlayer.EvalCond`. The importer emits this form, so every imported condition used to play differently in the browser. | case 17 |
| `set default=true` overwrote in JS | initialise-only in both — a chapter-entry default must not stomp progress carried in from an earlier chapter or a save. | case 18 |
| `requires_stat` with no `min`: threshold 0 (C#) vs 1 (JS) | 0 in both. | case 21 |
| `requires_min` (the name the importer writes) ignored by JS | honoured in both. | case 21 |
| `if` false with no `else`: C# ENDED THE CHAPTER, JS fell through | falls through in both, as the cheatsheet and language reference promise. The compiler always emits `else`, so this only ever reached hand-written `.lvn` — but `.lvn` is advertised as a container any tool may write. | case 22 |
| `{unset_var}`: literal `{key}` (C#) vs `0` (JS) | literal in both — missing data has to be visible. Ink defaults still hold inside conditions, and the case asserts both halves so one cannot be fixed by breaking the other. | case 23 |
| `inc by="<expr>"`: stepped by 1 (C#) vs evaluated (JS) | `by` is a number in both, per the documented contract; a non-numeric `by` is no longer silently wrong — the validator warns and tells the author to compute it with `set` first. | validator |

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

### The fifth mirror: `Lvn.StagingOps.Known` — closed

`Lvn.StagingOps.Known` (`com.lvn.engine/Runtime/StagingOps.cs`) is a public
`HashSet<string>` whose doc comment claimed it "mirrors the Go validator's
registry". It held **24 of the 30** ops — `anim`, `input`, `load`, `save`, `text`
and `wardrobe_show` were missing — and `CameraRigTests.cs` asserted
`Known.Count == 24`, so the drift was *frozen by a test*: the assertion stayed
green on the bug and would have gone red on the fix. Nothing in the runtime
dispatches on the set (only tests read it), which is why a six-op hole had no
symptom; a host that asks it "is this a real op?" got a false negative six times
out of thirty.

Both halves are fixed. The set holds the whole dictionary, its doc comment now
states what it actually means (*is this name part of the language, or is it
mine?* — flow ops included, so "staging" in the type name is a legacy label, not
a claim about which ops reach the stage), and the count assertion is gone,
replaced by two diffs against the source of truth:

* `TestCSharpKnownOpsMirrorKnownOps` (Go, this file's neighbourhood) reads the
  literal initializer out of `StagingOps.cs` — the same source-scraping trick the
  dispatch checks use — and diffs it against `KnownOps`, in both directions. An
  op added to the language and not to the mirror is red before any editor opens;
  a stale op left behind in the mirror is red too.
* `CameraRigTests.StagingOpsMatchTheSharedOpTable` (C#, EditMode) diffs the same
  set against `ops-owners.json`, ignoring itself when `/conformance` is absent
  (the UPM install). A short presence-only probe test is the floor there.

`TestGuardBitesOnADriftedOp` proves the Go half bites: it runs the check against a
doctored dictionary (an op the mirror lacks) and a doctored mirror (an op the
language lacks) and fails if either goes unreported.

The type keeps its name: it has been public API since 0.1.0 and `CONTRIBUTING.md`
names it as the C# half of "a new op must be registered". Deleting it was the
alternative — nothing in the engine dispatches on it — but a host asking "is this
op mine?" in `ApplyStage` is a real question, and the answer belongs in the
engine, correct, rather than nowhere.
