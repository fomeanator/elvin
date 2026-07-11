# Embedding the LVN engine

`com.lvn.engine` is a **library first**: the exported project template is just
the thinnest possible host (a 50-line Boot). Any Unity game can embed the
engine at one of three levels, and extend it from C# without forking.

## The three entry levels

### Level 0 — the script engine only (`LvnPlayer` + `ILvnStage`)

No engine UI at all. You implement `ILvnStage` (four methods: `ShowSay`,
`ShowChoice`, `ApplyStage`, `OnEnd`) and render however you like — your own
UI, a text console, a Discord bot. The player owns flow control, variables,
saves-shaped snapshots (`Save`/`Restore`), rollback history and localization.

```csharp
var doc = LvnDocument.Parse(lvnJson);
var player = new LvnPlayer(doc, myStage);   // myStage : ILvnStage
player.Advance();                            // …then Advance/Choose on input
```

### Level 1 — the stage (`VnStage`)

The full dialogue presentation — typewriter, choices, actors (layered, boned,
Spine), FX, audio, quick menu, saves — on your GameObject:

```csharp
var stage = go.AddComponent<VnStage>();      // go also carries a UIDocument
stage.Assets  = myAssets;                    // ILvnAssets — YOUR asset pipeline
stage.Catalog = new SpriteCatalog(manifest.sprites);
stage.ApplyTheme(VnThemeBuilder.From(manifest.ui));
stage.Play(lvnJson);
```

Pluggable seams: `ILvnAssets` (where content comes from), `VnTheme` (how it
looks), `Strings` (language, hot-swappable), `SeedVars`, `CrossChapterLoader`.

### Level 2 — the shell (`NovelApp`)

Carousel, chapter flow, resume, settings, language picker — the whole app.
Configure through the manifest (`ui`, `languages`, themes) and a few fields
(`ServerUrl`, `OfflineBundled`, `Locale`, `StateKey`).

## Extending from C# (the "engine doesn't cover X" valves)

### Custom script ops — `LvnOps`

The main valve. The script says what happens; YOUR code says how:

```csharp
LvnOps.Register("minigame", (cmd, ctx) => {
    ctx.Hold();                              // pause the story here
    MyMinigame.Run((string)cmd["kind"], won => {
        ctx.Vars["won"] = won;               // same store set/if read
        if (!won) ctx.GoTo("failed");
        ctx.Resume();                        // continue the story
    });
});
```

Authors write it as `ext minigame kind="lockpick"` in `.lvns` (compiles to
`{op:"minigame", …}`). Without `Hold()` the op is fire-and-forget. The
validator flags unknown ops as a warning (they may be yours), never an error.
Custom ops are not replayed by save/restore visual rebuilds — persist your
own state in `ctx.Vars` (it IS saved) or your own systems.

### Declaring your ops — `ext-grammar.json`

Undeclared, a host op is a stranger to the toolchain: the validator warns
"unknown op" (so the 0-warnings gate fails) and the editor can't complete or
document it. Declare it once and it becomes a first-class citizen everywhere
— *without* touching compilation or the core grammar:

```json
{
  "ops": {
    "minigame": {
      "doc": "Runs a host mini-game; the story waits for Resume().",
      "fields": ["difficulty", "timeout"],
      "required": ["id"],
      "enums": { "difficulty": ["easy", "normal", "hard"] },
      "snippet": "ext minigame id=\"river\" difficulty=normal"
    }
  }
}
```

Put it beside your scripts or one directory up (`content/ext-grammar.json`
covers `content/scripts/*.lvn`); `lvnconv validate` auto-detects it (or take
`-ext-grammar <file>`), the panel editor picks it up from `/content/`, the
playground compiler accepts it as `lvnsCompile(src, extGrammarJSON)`, and the
MCP `lvns_check` takes an `ext_grammar` argument. A declared op then validates
like a built-in: unknown/typo'd fields and out-of-set enum values warn, a
missing `required` field is an error. Redeclaring a core op, unknown JSON
keys, or an enum on an undeclared field are declaration errors — the same
"unknown is an error" rule the language keeps. Full example:
[`examples/ext-grammar.json`](../examples/ext-grammar.json).

### Menu items — `StageMenu.AddMenuItem`

```csharp
StageMenu.AddMenuItem("Достижения", stage => MyAchievements.Show());
```

Appears in the quick menu between Settings and Exit; label is yours (localize
it yourself), the callback gets the running stage.

### Events

- `LvnPlayer.OnSay` — every rendered line (who, text, style).
- `VnStage.Saved` — after any successful save (slot name): cloud sync,
  achievements, analytics.
- `VnStage.ExitRequested` / `RequestExit()` — the menu's Exit, or trigger it.
- `VnStage.ChromeHiddenChanged` — the long-press art view (mirror your HUD).

### Meta-progress stores

- `LvnGalleryStore` — CG unlocks (per-title, survives deleted saves). The
  engine unlocks on matching `bg`s; a host can read `Unlocked(titleId)` for
  its own gallery screen, `Unlock` from custom ops, or `Clear` on "reset
  progress". Feed `VnStage.Gallery` yourself when you bypass `NovelApp`.
- `LvnSaveStore` — save slots (List/Get/Put/Delete, thumbnails, schema
  migration) if you build your own save UI.

### Drawing over the engine

The stage renders in a `UIDocument`; your own `UIDocument` with a higher
`sortingOrder` draws above it. Set `stage.InputBlocked = true` while your
overlay owns the screen so tap-to-advance sleeps.

### Optional modules (the version-define pattern)

Heavy integrations ship as assemblies that compile only when their package is
present — `Lvn.Engine.Spine` (spine-unity) and `Lvn.Engine.Addressables` are
the references. Follow the same pattern for yours: an `.asmdef` with a
`versionDefines` entry and a `[RuntimeInitializeOnLoadMethod]` hookup.

## API stability

Everything shown here is the supported surface and follows the compatibility
contract in `releasing.md`: within a major version it only grows. Types not
mentioned here (and everything `internal`) may change between minor versions.
