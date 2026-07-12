# Changelog

## [0.9.0] — 2026-07-12

- Version-lockstep release with `com.lvn.engine` 0.9.0 (install every
  package from the same tag).

## [0.8.0] — 2026-07-12

- Extracted from `com.lvn.engine` (`Runtime/UI/Screens/`, 26 files) into a
  standalone package: the whole novel-shell — NovelApp/NovelShell,
  carousel + hub browse, and every product screen (store, wardrobe,
  skin/pack shops, gallery, profile, leaderboards, daily, settings, popups,
  auth, HUD). New assembly `Lvn.Engine.Shell`; file GUIDs unchanged (git
  renames), behaviour unchanged. The tiny `UiColor`/`ScreenFx` helpers moved
  DOWN into the engine's UI core (namespace `Lvn.UI`) — they were
  general-purpose utilities that only happened to live beside the screens.
