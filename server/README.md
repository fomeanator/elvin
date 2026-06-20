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

## Notes

- **State is in-memory** in this template — swap `server.state` for a database
  (Postgres, Redis, …) for persistence and multi-instance deploys.
- **The manifest** is served `no-store` so content updates are picked up live;
  static assets under `/content/` are safe to cache (hash or version their urls
  for cache-busting).
- **Auth, entitlements, IAP validation and cloud-save conflict resolution** are
  intentionally out of scope here — they are game-specific. This template gives
  you the content+save spine to build them on.
