# LVN server template

A minimal, dependency-free Go backend for an LVN game. It serves content and
player saves and nothing you don't need; grow it into your own service.

```sh
go run . -content ./content -addr :8000 -admin-token secret
```

| Route | Method | Purpose |
|---|---|---|
| `/healthz` | GET | liveness |
| `/v1/content/manifest` | GET | content manifest (`content/manifest.json`; empty if absent) |
| `/content/<path>` | GET | static `.lvn`, art, audio from the content dir |
| `/content/<path>.astc` | GET | on-demand GPU-native transcode of `<path>.png`/`.jpg` (see **ASTC transcoding**) |
| `/content/<path>@2k.<ext>` | GET | on-demand 2048-capped downscale of `<path>.<ext>` (see **Downscaled variants**) |
| `/v1/state?user=<id>` | GET/PUT | per-player save (JSON) |
| `/v1/admin/assets/<path>` | PUT | upload an asset/script (Bearer admin token) |

## Pipeline

The admin route mirrors `lvnconv`: compile a script, push it, the client picks
it up.

```sh
lvnconv convert -i chapter.ink -o chapter.lvn
curl -X PUT -H "Authorization: Bearer secret" \
     --data-binary @chapter.lvn \
     http://localhost:8000/v1/admin/assets/scripts/chapter.lvn
```

## Downscaled variants

`GET /content/<name>@2k.png` (or `.jpg`/`.jpeg`) serves `<name>.png` resized
once to fit within 2048×2048 (aspect preserved, never upscaled), cached to
disk next to the source — the same encode-once pattern as ASTC below. A
source that already fits is served as-is with no variant file written.
Every failure mode 404s, and the Unity client falls back to the original URL.

The client's Spine loader asks for `@2k` variants of atlas pages and
container backgrounds first: Spine region UVs are computed from the atlas
file's `size:` line (normalized 0..1), so a downscaled page renders correctly
without touching the `.atlas` — and a raw 7708×8252 page export drops from
~254 MB of RGBA in VRAM (and hundreds of ms of main-thread PNG decode) to
~17 MB. The full-resolution source stays untouched as the source of truth.

## ASTC transcoding

The Unity client (`ContentLoader`/`CachingAssets`) requests a texture as
`<url>.astc` first when its GPU supports ASTC-compressed textures, falling
back to the normal `.png`/`.jpg` untouched if that 404s. This server answers
that request by transcoding on demand: `<path>.astc` → looks for a sibling
`<path>.png`/`.jpg`/`.jpeg`, runs it through the `astcenc` CLI (ARM's
reference encoder, 6x6 blocks, sRGB profile), and writes the result next to
the source — so every later request for that file is a plain static-file hit,
the same "encode once, cache to disk forever" pattern `ContentLoader` already
uses client-side for its own disk cache.

**Why this matters and PNG/JPEG recompression (`lvnconv optimize`) doesn't
cover it**: recompressing to a smaller PNG/JPEG (or WebP, which this project
deliberately doesn't use — Unity has no built-in decoder for it, and it
wouldn't help here anyway) only shrinks the WIRE/DISK footprint. Once decoded,
a texture is full RGBA in VRAM regardless of source format. ASTC is a
GPU-native block format — the GPU samples the compressed bytes directly — so
it's the one encoding that actually cuts RUNTIME VRAM (4–8× at 6x6 blocks),
which matters most for the biggest assets (a Spine export can run
7000×8000+ px).

**Install `astcenc`** (not vendored — this transcoder just needs it on PATH;
if it's missing, every `.astc` request 404s and clients silently keep using
PNG/JPG, so this is safe to skip entirely):

```sh
npm install -g astcenc   # prebuilt binaries for darwin-arm64/x64, linux-x64
```

**Known limitation**: transcodes of different files run concurrently with no
global cap (same-file requests ARE single-flighted, so a burst for one
still-missing `.astc` never spawns duplicate encodes). `astcenc` is fast
(seconds even for a 7708×8252 source) and already multi-threads internally,
but a server preloading many still-uncached textures at once will burn real
CPU during that cold-cache window. Fine for a small deployment; a
high-traffic one should add a semaphore around `astcTranscoder.transcode` or
pre-warm the `.astc` cache at deploy time.

## Notes

- **State is in-memory** in this template — swap `server.state` for a database
  (Postgres, Redis, …) for persistence and multi-instance deploys.
- **The manifest** is served `no-store` so content updates are picked up live;
  static assets under `/content/` are safe to cache (hash or version their urls
  for cache-busting).
- **Auth, entitlements, IAP validation and cloud-save conflict resolution** are
  intentionally out of scope here — they are game-specific. This template gives
  you the content+save spine to build them on.
