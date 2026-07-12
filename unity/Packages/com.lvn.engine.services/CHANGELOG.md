# Changelog

## [0.9.0] — 2026-07-12

- Version-lockstep release with `com.lvn.engine` 0.9.0 (install every
  package from the same tag).

## [0.8.0] — 2026-07-12

- Extracted from `com.lvn.engine` (`Runtime/Services/`, 9 files) into a
  standalone package. New assembly `Lvn.Engine.Services` (the classes were
  previously compiled into the core `Lvn.Engine` assembly); file GUIDs
  unchanged (git renames), behaviour unchanged. The core engine no longer
  references any service — stories play fully offline without this package.
