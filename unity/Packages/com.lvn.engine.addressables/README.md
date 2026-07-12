# Elvin — Addressables Loader

`AddressablesAssets` implements the engine's `ILvnAssets` seam over Unity
Addressables: `sprite_url` / audio / text lookups resolve as Addressables
keys, so story art ships in bundles (local or remote) instead of loose files.

## Install

1. The engine:
   `https://github.com/fomeanator/lvn-engine.git`
2. `com.unity.addressables` (Package Manager → Unity Registry).
3. This package:
   `https://github.com/fomeanator/lvn-engine-addressables.git`

The assembly compiles only when Addressables is present (a version define
guards it), so install order never breaks a build.

## Use

```csharp
stage.Assets = new AddressablesAssets();            // bundles only
// or as one level of a fallback chain:
stage.Assets = new ChainAssets()
    .Add(new DirectoryAssets(localDir))             // L1: files on disk
    .Add(new NetworkAssets(server))                 // L2: the content server
    .Add(new AddressablesAssets());                 // L3: Unity bundles
```

Like every `ILvnAssets`, it caches by url and releases through
`Unload`/`UnloadAll`. See `docs/embedding.md` (§ Optional modules) for the
version-define pattern this package follows.
