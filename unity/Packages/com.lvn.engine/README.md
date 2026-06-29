# Elvin (Unity runtime)

Elvin plays narrative games written as plain text. Drop a `.lvns` script into
`Assets/` — it compiles automatically — and the runtime plays it as a real game:
dialogue, branching choices, characters with emotions, stats, animation,
save/load — with no dialogue or branching system to build yourself. Use the
drop-in `VnStage`, or, for a custom skin, the runtime owns flow control and you
own rendering through one interface (`ILvnStage`). (`Elvin` = how you say `LVN` —
the `.lvn` format it plays.)

## Install

**Package Manager → Add package from git URL:**

```
https://github.com/fomeanator/unity-lvn-vn-engine.git?path=/unity/Packages/com.lvn.engine
```

Requires `com.unity.nuget.newtonsoft-json` (declared as a dependency; Package
Manager pulls it automatically).

## Use

```csharp
using Lvn;

var doc    = LvnDocument.Parse(lvnJsonText);   // from a TextAsset or download
var player = new LvnPlayer(doc, myStage);      // myStage : ILvnStage
player.Advance();                              // run to the first pause
```

Implement `ILvnStage` on whatever renders your game:

```csharp
public void ShowSay(string who, string text, string style) { /* draw the line */ }
public void ShowChoice(IReadOnlyList<LvnOption> options)    { /* draw the buttons */ }
public void ApplyStage(JObject command)                    { /* bg/actor/fade/… */ }
public void OnEnd()                                        { /* finale */ }
```

Drive it by alternating with the player:

- `Advance()` runs commands until a **say**, a **choice**, or the **end**;
- after a say, call `Advance()` again on the player's tap;
- after a choice, call `Choose(option.Index)` then `Advance()`.

Flow control — `goto`, `if`, `choice`, `call`/`return` tunnels — and the
variable bag (`set`/`inc`, exposed as `player.Vars`) are handled for you.
`player.Index`, `player.Vars` and `player.CallStack` are the autosave snapshot;
`player.Restore(...)` puts a player back. Both structured `cond` and string
`expr` conditions (`courage >= 2 && !lied`) evaluate out of the box via
`LvnExpression`; set `player.ExprEvaluator` only to plug in a different dialect.

## Sample

Import **Hello LVN** from the package's Samples, drop `HelloLvnRunner` on a
GameObject, assign `hello.lvn.txt`, and press Play — the story prints to the
Console and advances on click. It is a complete, minimal `ILvnStage`.

## Authoring

Drop a `.lvns` file into `Assets/` — the built-in ScriptedImporter compiles it to
a playable asset automatically (no external tool). For CI, Ink, or articy, the
standalone `lvnconv` transcoder also produces `.lvn` and validates it. The full
language and engine limits are documented in the repo `howto/` folder.

## Scope (v0.4)

A full runtime: interpreter (flow, vars, subroutines, expressions via
`LvnExpression`, autosave), the cast/compositor (layered parametric sprites),
the animation engine (channels, easing, yoyo, queue), effect modules
(fade/dim/flash/tint/blur/camera/particles/audio), the reactive HUD, save/load,
and the novel-shell — plus the in-Unity `.lvns` importer. See the repo root
README and `howto/CAPABILITIES.md` for exactly what is and isn't supported.
