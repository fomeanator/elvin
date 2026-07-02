# Releasing the engine

Projects built on LVN reference the engine as a UPM git dependency pinned to a
release tag (`…?path=/unity/Packages/com.lvn.engine#vX.Y.Z` — the export
writes the pin automatically). A release therefore can never break an existing
project: owners upgrade by bumping one tag in `Packages/manifest.json`, and
only when they choose to.

## Compatibility contract (what a release must keep)

Within a major version:

- **C# API** of `com.lvn.engine` — public types/members stay; removals go
  through `[Obsolete]` for at least one minor release.
- **Script formats** — `.lvn` ops/fields and `.lvns` grammar only grow. The
  SSOT is `tools/lvn-lang/src/grammar.json`; two-sided parity tests
  (`lvn/grammar_sync_test.go`, lvn-lang node tests) fail CI on drift. Unknown
  fields are tolerated by every reader (Json.NET / forward-compatible
  samplers), so newer content degrades gracefully on older runtimes.
- **Player saves** — schema-versioned (`LvnSaveSlot.CurrentVersion`): older
  saves migrate up in `LvnSaveStore.Migrate`; saves from a newer build are
  hidden, never misread or destroyed.
- **Server protocol** — `/v1/state` merges field-level with OCC versioning;
  legacy (unversioned) clients keep working.

Breaking any of these = major version bump, with migration notes in the
CHANGELOG.

## Release steps

1. Move the `[Unreleased]` section of
   `unity/Packages/com.lvn.engine/CHANGELOG.md` under the new version.
2. Bump `version` in `unity/Packages/com.lvn.engine/package.json`.
3. Commit, merge to `main`, tag `vX.Y.Z`, push the tag
   (`git push origin vX.Y.Z`).
4. CI must be green (Go, grammar parity, panel, Unity EditMode on TestHost).

The export server derives the pin from the engine version the template points
at, so from the moment `main` carries the new version, fresh exports pin to
the new tag — existing exports stay on theirs.

## Upgrading a project (owner's side)

Edit `Packages/manifest.json`: change `#vX.Y.Z` to the new tag, reopen the
project. Unity re-resolves the package; saves and content need no action.
