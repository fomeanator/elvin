# Extension plugin (template)

The complete anatomy of an Elvin plugin in three small files:

| File | What it shows |
|---|---|
| `LvnMinigamePlugin.cs` | `LvnOps.Register` (custom `ext …` ops with `Hold`/`Resume` flow control + the shared variable store) and `StageMenu.AddMenuItem` — registered once via `[RuntimeInitializeOnLoadMethod]`, no scene wiring. |
| `ext-grammar.json` | The toolchain declaration: with it, `lvnconv validate`, the panel editor and the playground treat your ops like built-ins (field/enum/required checks, completion, hover docs) — scripts keep the zero-warnings gate. |
| `story.lvns.txt` | A playable scene using the ops (rename to `.lvns` to compile). |

## Try it

1. Import this sample (Package Manager → Elvin → Samples).
2. Rename `story.lvns.txt` → `story.lvns` — the in-Unity importer compiles it.
3. Point a `VnStage` at it (or use the **Hello Elvin** sample's component and
   set its script path) and press Play: the story pauses at the river, the
   "mini-game" resolves, the win/lose branch plays.
4. Copy `ext-grammar.json` next to your scripts (or `content/ext-grammar.json`
   on the server) — validation and IDE completion light up everywhere:
   `lvnconv validate` auto-detects it, the panel fetches it, MCP `lvns_check`
   accepts it as `ext_grammar`.

## Turning this into your own UPM package

A plugin is an ordinary UPM package that depends on the engine — the
first-party reference is `com.lvn.engine.spine` (this repo), which wraps the
optional Spine runtime behind the engine's `LvnSpineBridge` seam. Skeleton:

```
com.yourstudio.elvin.minigames/
  package.json
  Runtime/
    YourStudio.Elvin.Minigames.asmdef
    LvnMinigamePlugin.cs        ← this file, renamed to your namespace
    ext-grammar.json            ← ship the declaration with the code
```

`package.json`:

```json
{
  "name": "com.yourstudio.elvin.minigames",
  "version": "1.0.0",
  "displayName": "My Elvin Mini-games",
  "unity": "2022.3",
  "dependencies": { "com.lvn.engine": "0.7.0" }
}
```

`Runtime/YourStudio.Elvin.Minigames.asmdef`:

```json
{
  "name": "YourStudio.Elvin.Minigames",
  "references": ["Lvn.Engine", "Lvn.Engine.UI"],
  "autoReferenced": true
}
```

Wrapping a THIRD-PARTY library (an ads SDK, a haptics plugin, Spine)? Add a
`versionDefines` entry so your assembly compiles only when that package is
present — see `com.lvn.engine.spine`'s asmdef and `docs/embedding.md`
(§ Optional modules) for the pattern. Install for users is then one line:

```
https://github.com/you/your-repo.git?path=/com.yourstudio.elvin.minigames
```
