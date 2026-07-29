# Server-loaded 3D sets

`bg3d` uses the same content model as sprites: the script stores a stable id,
the server manifest maps that id to replaceable files, and the client keeps a
versioned disk cache for offline replay.

## Authoring loop

1. Put each set root prefab in `sandbox/Assets/Resources/Sets/`:
   `forest.prefab`, `apartment.prefab`, and so on.
2. Build the platform bundles:

   ```sh
   "/Applications/Unity/Hub/Editor/6000.4.5f1/Unity.app/Contents/MacOS/Unity" \
     -batchmode -quit -projectPath ./sandbox -buildTarget Android \
     -executeMethod Lvn3DSetBundleBuilder.BuildAndroid
   ```

3. Publish the generated `server/content/sets/*.android.bundle` files and the
   updated `server/content/manifest.json`.
4. Reference only the id in Elvin Script:

   ```lvns
   bg3d id=forest x=0 y=2 z=-8 pitch=4 yaw=12 fov=55
   actor id=hero position=center
   ```

The exporter creates one bundle per set. Replacing `forest` therefore does not
force the player to redownload `apartment`, and Android never receives desktop
or iOS data.

## Manifest contract

```json
{
  "sets3d": {
    "forest": {
      "fallback_resource": "Sets/forest",
      "platforms": {
        "android": {
          "url": "/content/sets/forest.android.bundle",
          "asset": "forest",
          "hash": "15169177010123bdc8fcfbc4dfd3995d",
          "bytes": 5094238
        }
      }
    }
  }
}
```

Platform keys are `android`, `ios`, `windows`, `macos`, `linux`, and `webgl`.
`default` is an optional catch-all. `hash` is the Unity AssetBundle hash emitted
by the exporter; the server's `asset-versions.json` supplies the byte-level
content version used by the disk cache.

`fallback_resource` is optional. It is used when the manifest has no matching
platform, the network is unavailable and the bundle has never been cached, or
Unity rejects the remote bundle. Keeping the same prefab in Resources makes
development and first-launch offline behavior deterministic; product builds
can omit it once remote-only delivery is desired.

## Runtime and layering

The runtime opens a cached bundle from a file rather than copying the entire
bundle into a second `byte[]`. The active set owns a lease; replacing it
instantiates the new prefab first, destroys the old instance, then unloads the
old bundle. Repeating the current id is only a camera cut and does not reload.

The composition order is fixed:

```text
3D set camera → RenderTexture → background RawImage → 2D actor Canvas → dialogue
```

The set camera is never allowed to render directly to the display. The Canvas
camera is also forced after other scene cameras, so an imported demo camera
cannot clear the framebuffer over already-loaded characters.

AssetBundles are platform-specific. Never serve an Android bundle to iOS or
desktop, and never put executable scripts in content bundles: components must
already exist in the shipped player.
