# Changelog

## [Unreleased]

- Extracted from `com.lvn.engine` (`Runtime/Services/`, 9 files) into a
  standalone package. New assembly `Lvn.Engine.Services` (the classes were
  previously compiled into the core `Lvn.Engine` assembly); file GUIDs
  unchanged (git renames), behaviour unchanged. The core engine no longer
  references any service — stories play fully offline without this package.
