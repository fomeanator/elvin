# Deploying an LVN server

The engine ships as ONE static Go binary — a production host is a bare
Linux box (Debian/Ubuntu), nginx in front for TLS, systemd for lifecycle.
No containers: on the small VPS this stack targets, the OS should be
serving the Go process, not daemons (resource fencing is done with the
same cgroups via the systemd unit). State that needs a real database
(wallets, analytics at scale) points at a managed Postgres — nothing
else runs on the box.

## One command, from your machine

```sh
HOST=root@1.2.3.4 DOMAIN=novels.example.com \
CONTENT_DIR=./server/content \
./deploy/provision.sh
```

Everything happens over SSH and is **idempotent** — run it again after
changing anything:

1. uploads `deploy/` and runs `setup.sh` on the host: swap, sysctl, ufw
   (22/80/443), fail2ban, journald caps, the `lvn` system user, the
   systemd unit (auto-restart + memory fences), nginx site (gzip, file
   cache, `/content/` micro-cache) and a Let's Encrypt certificate with
   auto-renewal, plus a one-time `basisu` build for KTX2 texture variants;
2. cross-builds the server (`GOOS=linux`), ships it, restarts the unit;
3. optionally syncs a content tree (runtime state excluded);
4. health-checks `https://DOMAIN/healthz`.

The admin token is generated on the host into `/srv/lvn/lvn.env` on first
provision (or pass `ADMIN_TOKEN=...` to pin it). Novels are then imported
remotely — no shell needed:

```sh
curl -X POST https://novels.example.com/v1/admin/import-bundle \
  -H "Authorization: Bearer $TOKEN" \
  -F articy=@Cold.rar -F backgrounds=@bg.zip -F heroine=@heroine.zip \
  -F characters=@chars.zip -F vars=@vars.xlsx \
  -F id=cold -F name="Холодный ветер перемен"
```

## Continuous deployment

`.github/workflows/deploy-server.yml` runs the same `provision.sh` on
every push to `main` that touches server code. Fork setup: add the
`DEPLOY_SSH_KEY` secret (a private key authorized on your host) and the
`DEPLOY_HOST` / `DEPLOY_DOMAIN` repository variables.
