# Elvin — Spine Integration

Spine skeletal animation for the [Elvin narrative-game engine](../com.lvn.engine):
a `kind: "spine"` catalog entity renders as a live skeleton — downloaded,
parsed off the main thread, mesh-built hidden, faded in, MRU-cached and
screen-fitted — while the story script stays plain `actor id=… play=…` text.

This package is the **driver** behind the engine's `LvnSpineBridge` seam. The
core engine carries no Spine dependency and works fully without this package;
with it (plus the official Spine runtime) skeleton entities come alive.

## Install

1. The engine (if not already):
   `https://github.com/fomeanator/unity-lvn-vn-engine.git?path=/unity/Packages/com.lvn.engine`
2. The official spine-unity runtime (Esoteric Software), per their docs —
   e.g. the spine-unity UPM package for your Spine version.
3. This package:
   `https://github.com/fomeanator/unity-lvn-vn-engine.git?path=/unity/Packages/com.lvn.engine.spine`

The assembly compiles only when `com.esotericsoftware.spine.spine-unity` is
present (a version define guards it), so install order never breaks a build.

> **Licensing:** Spine runtimes require a Spine editor license in production.
> That obligation lives entirely in THIS package + the Spine runtime — the
> engine core stays MIT-only and license-free.

## What's inside

- `LvnSpineBootstrap` — hooks the engine's `LvnSpineBridge` delegates at load:
  runtime skeleton builds from downloaded json/atlas/page textures (multi-page
  atlases supported), off-main-thread parse (`Prepare`), named-animation play.
- `LvnSpineFader` — short default fade for show/hide plus the GPU warm pulse
  that pre-compiles the pipeline so a first show never hitches.
- `LvnSpineFit` — fits and rescales the skeleton container to the actor slot
  (`scale` / `fit` from the catalog entry, live refit on placement changes).

Authoring stays in the engine: see `docs/cast.md` (spine entities in the
catalog) and `docs/animation-system.md` (§ Spine) in the repository root.
