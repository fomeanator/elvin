# Embedding the engine in your own game

The engine is a library (`com.lvn.engine`, UPM). The exported template is just
the thinnest possible host. Full reference of the seams: `docs/embedding.md` (EN).

## Three entry levels

1. **Script only** — `LvnPlayer` + your own `ILvnStage` (4 methods): you draw
   the dialogue however you like, the engine drives the story/variables/saves.
2. **Scene** — `VnStage` on your own GameObject: the whole presentation
   (typewriter, choices, actors, bones, Spine, menu, saves). Your own assets —
   via `ILvnAssets`.
3. **The whole shell** — `NovelApp` (package `com.lvn.engine.shell`, pulls in
   `com.lvn.engine.services`): carousel/hub, chapters, resume, stores, settings.

## "The engine lacks X" — the valves

### Your own script commands — `LvnOps`

```csharp
LvnOps.Register("minigame", (cmd, ctx) => {
    ctx.Hold();                              // the story waits
    MyMinigame.Run((string)cmd["kind"], won => {
        ctx.Vars["won"] = won;               // same store as set/if
        if (!won) ctx.GoTo("failed");
        ctx.Resume();                        // the story continues
    });
});
```

In `.lvns` the author writes: `ext minigame kind="lockpick"`. Without `Hold()`
it is fire-and-forget. The validator emits a warning (not an error) for an
unknown op — it might be yours.

### Your own menu items

```csharp
StageMenu.AddMenuItem("Achievements", stage => MyAchievements.Show());
```

### Events

- `LvnPlayer.OnSay` — every line;
- `VnStage.Saved` — after every save (cloud sync, achievements);
- `NovelApp.ChapterStarted / ChapterFinished` — chapter lifecycle;
- `VnStage.ExitRequested`, `ChromeHiddenChanged`.

### Drawing on top

Your own `UIDocument` with a higher `sortingOrder` renders above the engine;
while your screen is up, set `stage.InputBlocked = true`.

### Web view (in-app browser)

The engine does **not** link a web-view library — only the `Lvn.Services.LvnWebView`
seam: the engine calls `LvnWebView.Open(url)` (the "how to pay from Russia"
banner in the store, ToS/Policy), and the host plugs in an implementation via a
hook variable.

```csharp
LvnWebView.Opener = url => { web.LoadURL(url); web.SetVisibility(true); return true; };
```

Without the hook, `Open` opens the system browser (`Application.OpenURL`) — a
safe default with zero dependencies. Ready-made wiring for gree/unity-webview
is the **Web view (gree adapter)** sample (Package Manager ▸ LVN Engine ▸ Samples):
drop the plugin into `Assets/`, import the sample — it registers the seam itself.

### Optional modules

Heavy integrations live in separate assemblies gated by a version define
(examples: `com.lvn.engine.spine`, `com.lvn.engine.addressables`): if the
third-party package is present, the module compiles; if not, the engine stays
clean. The shell and services are separate packages too (`com.lvn.engine.shell`,
`com.lvn.engine.services`): an embedding game with its own UI does not need
them at all.

## The contract

Everything listed above is supported surface: within a major version it only
grows (see `docs/releasing.md`). `internal` and anything unmentioned may change.
