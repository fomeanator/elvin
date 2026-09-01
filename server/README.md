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

### The `.lvn` write gate

Content is served to players immediately (`Cache-Control: no-store`), so a
`.lvn` uploaded through this route is structurally validated **before** it
reaches the disk — see `lvnguard.go`. Nothing else on the path (Studio's "Save
to app", the importers, a raw `curl`) can bypass it.

* **Rejected — `422`, nothing written, no `.history` entry:** a body that is
  not a `.lvn` document (the runtime's loader needs a JSON object with a
  `script` array), duplicate label ids, a jump to a label that does not exist,
  a command with no `op`, or a host op that omits a field its `ext-grammar.json`
  declares required. The response body lists them: `{"rejected":true,"errors":[…]}`.
* **Written, reported as `warnings` in the `200` body and in the server log:**
  an op the core grammar does not know (a host op registered with
  `LvnOps.Register` is legal content — declaring it in `ext-grammar.json` turns
  the warning into real field/enum checking), unknown fields, dead labels,
  unbalanced `{}` in dialogue.
* **Untouched:** every non-`.lvn` path — art, config JSON, and the `.lvns`
  editable source, which an author saves half-written by design.

Both import endpoints run the same check over the scripts they generate and
return the verdict as `lvn_check` (reporting only — an import also writes art
and the manifest, so it is not failed after the fact).

## Downscaled variants

`GET /content/<name>@2k.png` (or `.jpg`/`.jpeg`) serves `<name>.png` resized
once to fit within 2048×2048 (aspect preserved, never upscaled), cached to
disk next to the source — the same encode-once pattern KTX2 uses. A
source that already fits is served as-is with no variant file written.
Every failure mode 404s, and the Unity client falls back to the original URL.

The client's Spine loader asks for `@2k` variants of atlas pages and
container backgrounds first: Spine region UVs are computed from the atlas
file's `size:` line (normalized 0..1), so a downscaled page renders correctly
without touching the `.atlas` — and a raw 7708×8252 page export drops from
~254 MB of RGBA in VRAM (and hundreds of ms of main-thread PNG decode) to
~17 MB. The full-resolution source stays untouched as the source of truth.

## Notes

- **State is in-memory** in this template — swap `server.state` for a database
  (Postgres, Redis, …) for persistence and multi-instance deploys.
- **The manifest** is served `no-store` so content updates are picked up live;
  static assets under `/content/` are safe to cache (hash or version their urls
  for cache-busting).
- **Auth, entitlements, IAP validation and cloud-save conflict resolution** are
  intentionally out of scope here — they are game-specific. This template gives
  you the content+save spine to build them on.
