# Elvin — Novel Shell

The ready-made **novel application** over the
[Elvin engine](../com.lvn.engine): add `NovelApp` to a GameObject with a
`UIDocument`, point it at a content server, press Play — boot screen, title
browse (carousel, or the hub → collections → detail flow), name input,
chapter loading with streamed assets, the running story, and the full product
layer:

- **Monetization** — store (sections/bundles), skin & pack shops, chapter
  energy with a live refill HUD, popups/paywall seams.
- **Meta** — wardrobe (axes + wallet SKUs, live preview), CG gallery,
  profile, leaderboards, daily rewards, settings, auth (device +
  Google/Apple via the engine's services).
- **Flow events** — `ChapterStarted` / `ChapterFinished`, save/resume,
  cross-chapter loads.

Everything is themed from the content manifest (`ui.*` + design tokens) — the
same build renders any novel the server ships.

## Who needs it (and who doesn't)

- Shipping a **stand-alone novel app** (a Liminal-style streaming library)?
  This package is the app — you write content, not UI code.
- **Embedding** the engine inside your own game with your own menus? Skip
  it. The core (`VnStage`, or `LvnPlayer` + your `ILvnStage`) plays stories
  without any of this; see `docs/embedding.md`.

## Install

1. The engine:
   `https://github.com/fomeanator/unity-lvn-vn-engine.git?path=/unity/Packages/com.lvn.engine`
2. This package:
   `https://github.com/fomeanator/unity-lvn-vn-engine.git?path=/unity/Packages/com.lvn.engine.shell`

Server-side features (wallet, IAP, ads, daily, leaderboards) degrade
gracefully when the backend doesn't ship them — screens hide or show empty
states rather than break.
