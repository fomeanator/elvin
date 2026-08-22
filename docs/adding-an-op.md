# Adding an op to the language

An op lives in **six independent implementations**. That used to make every
addition a small tragedy — somebody always forgot a place, and the miss stayed
invisible until content hit it in a player's hands. It is not a tragedy any
more: every place has a test that goes red when you skip it, so this is a
checklist, not an act of memory.

The rule that makes it work: **start with the ownership table and the
conformance case, then let the failing tests walk you through the rest.**

---

## Before you add anything

Three cheaper answers first. Each new op is paid for six times, forever.

| Instead of an op | Use | When |
|---|---|---|
| Game-specific behaviour | `ext <name> k=v` + `LvnOps.Register` in the host | It belongs to one game, not to the language |
| Sugar over existing ops | A source-only construct (`move`, `play`, `voice`, `defanim`) | It lowers into ops that already exist — the runtime never sees it |
| A whole subsystem (video, networking) | `ext` with a documented contract | "One op" is hiding weeks of per-platform work |

An op earns its place when **every runtime must understand it** and it cannot
be expressed by lowering. If in doubt, ship it as `ext` and promote it later
when a second host needs it.

---

## The checklist

Work top to bottom. The right-hand column is what fails if you stop early.

| # | Where | What | Goes red |
|---|---|---|---|
| 1 | `conformance/ops-owners.json` | A row: `owner` (`engine`\|`shell`) and the C# dispatch site (`csharp`) | `TestOpOwnersCoverKnownOps` |
| 2 | `tools/lvnconv/lvn/validate.go` | `KnownOps`. Add to `OpFields` **only** if the field set is closed; to `EnumValues` only for closed value sets | `TestOpFieldsMatchGrammarClosedSet`, `TestEnumValuesMatchGrammar` |
| 3 | `tools/lvn-lang/src/grammar.json` | The op, `op_fields`, `op_docs`; then `npm run gen` in `tools/lvn-lang` | `TestKnownOpsMatchGrammar`, `grammar.test.js`, and the CI regen-diff step |
| 4 | `unity/…/Runtime/StagingOps.cs` | `Known` — the public C# registry | `TestCSharpKnownOpsMirrorKnownOps`, `CameraRigTests.StagingOpsMatchTheSharedOpTable` |
| 5 | `unity/…/Runtime/LvnPlayer.cs` **or** `VnStage.ApplyStage` | The actual behaviour. Flow ops get a `case` in the player; staging ops are forwarded to the stage. Match what row 1 declared | `TestEngineOwnedOpsHaveCSharpHandlers`, `OpDispatchContractTests.OpDispatchesWhereTheTableSaysItDoes` |
| 6 | `tools/lvnconv/internal/lvns/convert.go` | `KnownOps` + the parse/lowering, so authors can write it in `.lvns` | Without it the line silently becomes **dialogue text** — see the note below |
| 7 | `unity/…/Editor/LvnsCompiler.cs` | The same, in the C# port — or declare it in `UnsupportedSourceOps` with a reason | `TestUnityCompilerKnownOpsMirrorsSource` |
| 8 | `tools/lvnconv/importer/decompile.go` | Emit it back when decompiling `.lvn` → `.lvns` | `VerifyLvnsRoundTrip`; `lvnconv resync-lvns` starts reporting drift |
| 9 | `conformance/cases/NN-<topic>.json` | The behavioural contract | `TestConformanceCasesWellFormed`, plus the C# EditMode runner |
| 11 | `howto/CAPABILITIES.md` §1 | A row in the op catalog | `TestCapabilitiesOpCatalogMatchesKnownOps`, `TestCapabilitiesHasNoSelfContradiction` |
| 12 | A gated example | Use it in some `howto/*/*.lvns` | `TestDocumentedConstructsHaveAWitnessExample` |

### The one step with no guard, and why it bites hardest

Step 7 is the dangerous one. A word that is **not** in the `.lvns` vocabulary
does not error — it falls into the narration branch and **prints itself on
screen as a line of dialogue**. That is how `input var=…` once rendered as
visible text in the Unity import path. There is no test that can catch "this
word should have been a command" in general, so this step is on you.

Step 8 has the same failure mode, which is exactly why `TestUnityCompilerKnownOpsMirrorsSource`
demands that every `.lvns` construct be either implemented **or explicitly
declared unimplemented**. "Not implemented" is an allowed answer; silence is not.

---

## Order of work

1. **Row in `ops-owners.json` + the conformance case.** Write the contract
   before the code: the case says what the op must *do*, in both runtimes.
2. **Run the Go guards** (`cd tools/lvnconv && go test ./lvn/`). They will now
   fail, and each failure names the next file to touch.
3. **Follow the failures** through steps 2–9.
4. **Docs and the example** (11–12) — the doc gate treats an undocumented op
   and a documented-but-unused one as the same defect.
5. **Prove the guard bites.** Temporarily break one step, watch the right test
   go red, revert. A guard nobody has seen fail is a guard nobody trusts.

Full local verification (Unity is the slow one, run it last):

```sh
cd tools/lvnconv && gofmt -l . && go vet ./... && go test ./...
cd tools/lvn-lang && node --test
cd panel && npm run lint && npm test
# the 0-warning gate over every authored example
for f in howto/*/*.lvns examples/*.lvns; do lvnconv convert -i "$f" -o /tmp/x.lvn && lvnconv validate /tmp/x.lvn; done
# and the C# side
Unity -batchmode -nographics -projectPath unity/TestHost -runTests -testPlatform EditMode …
```

---

## Why six

For the curious, and so nobody "simplifies" this by deleting a mirror:

| Implementation | Exists because |
|---|---|
| `lvn.KnownOps` (Go) | The reference. Everything else is checked against it |
| `grammar.json` | The IDE and the VS Code extension need it without running Go |
| `StagingOps.Known` (C#) | Public UPM API since 0.1.0 — hosts ask it "is this a real op?" |
| `LvnPlayer` / `VnStage` | The reference runtime |
| `core.js` / `app.js` | The browser player: no Unity, no Go |
| `LvnsCompiler.cs` | Compiles `.lvns` inside Unity, so dropping a file into `Assets/` works with no toolchain |

They cannot be collapsed into one — they run in different places, with
different languages and no shared runtime. What *can* be guaranteed is that
they never disagree silently, and that is what the table plus the guards buy.

`TestGuardBitesOnADriftedOp` re-proves the whole mechanism on every CI run by
running the guards against a doctored dictionary — so the guards themselves
cannot rot.
