# Changelog

## [Unreleased]

- Extracted from `com.lvn.engine` (`Runtime/Spine/`) into a standalone
  optional package. Same assembly name (`Lvn.Engine.Spine`), same GUIDs, same
  behaviour — projects that had the module inside the engine upgrade by just
  adding this package. The engine's `LvnSpineBridge` seam and the
  `kind:"spine"` staging path are unchanged.
