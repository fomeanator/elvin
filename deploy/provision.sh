#!/usr/bin/env bash
# One-command LVN server bring-up — runs from YOUR machine, does everything
# over SSH: uploads deploy/, provisions the host (idempotent), cross-builds
# the server binary, ships it, starts the service, health-checks the domain.
#
#   HOST=root@1.2.3.4 DOMAIN=novels.example.com ./deploy/provision.sh
#
# Optional:
#   PORT=8078            loopback port behind nginx
#   ADMIN_TOKEN=...      pin the admin token (else generated server-side)
#   CONTENT_DIR=path     rsync a content tree too (state/analytics excluded)
#
# Re-run any time: provisioning converges, the binary redeploys, content
# re-syncs. CI (deploy-server.yml) automates the binary/deploy part on push.
set -euo pipefail

HOST="${HOST:?set HOST=user@server}"
DOMAIN="${DOMAIN:?set DOMAIN=your.host.name}"
PORT="${PORT:-8078}"
LVN_HOME="${LVN_HOME:-/srv/lvn}"
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"

log() { echo "[provision] $*"; }

log "1/5 upload deploy/"
ssh "$HOST" "mkdir -p $LVN_HOME/deploy"
rsync -az --delete "$HERE/" "$HOST:$LVN_HOME/deploy/"

log "2/5 provision (idempotent)"
ssh "$HOST" "DOMAIN=$DOMAIN PORT=$PORT LVN_HOME=$LVN_HOME ADMIN_TOKEN=${ADMIN_TOKEN:-} bash $LVN_HOME/deploy/setup.sh"

log "3/5 build linux/amd64 binary"
(cd "$REPO/server" && CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -ldflags='-s -w' -o /tmp/lvn-server-linux .)

log "4/5 ship binary + restart"
rsync -az /tmp/lvn-server-linux "$HOST:$LVN_HOME/lvn-server.new"
ssh "$HOST" "install -m 755 -o lvn -g lvn $LVN_HOME/lvn-server.new $LVN_HOME/lvn-server && rm $LVN_HOME/lvn-server.new && systemctl restart lvn"

# Studio web app (authoring IDE + admin UI + playground) for -studio. Built
# from panel/ (npm run deploy → server/website) and shipped to $LVN_HOME/website
# (the server's WorkingDirectory), where the -studio handler serves ./website.
# NON-FATAL: a Studio build hiccup must never take the game API deploy down —
# a missing website only 404s the IDE, the API keeps serving. Set STUDIO=0 to
# skip building it on an API-only box.
if [ "${STUDIO:-1}" = "1" ]; then
  log "4.5/5 build + ship Studio web app (website/)"
  if (cd "$REPO/panel" && npm ci && npm run deploy); then
    rsync -az --delete "$REPO/server/website/" "$HOST:$LVN_HOME/website/"
    ssh "$HOST" "chown -R lvn:lvn $LVN_HOME/website && systemctl restart lvn"
  else
    log "WARN: Studio build failed — API deployed, IDE will 404 until fixed"
  fi
fi

if [ -n "${CONTENT_DIR:-}" ]; then
  log "4.5/5 sync content from $CONTENT_DIR"
  rsync -az --delete \
    --exclude 'state/' --exclude 'services/analytics/' --exclude 'services/users.json' \
    --exclude '.git' --exclude '.gitignore' \
    "$CONTENT_DIR/" "$HOST:$LVN_HOME/content/"
  ssh "$HOST" "chown -R lvn:lvn $LVN_HOME/content && systemctl restart lvn"
fi

log "5/5 health check"
sleep 2
code=$(curl -s -o /dev/null -w '%{http_code}' "https://$DOMAIN/healthz" || true)
if [ "$code" = "200" ]; then
  log "OK — https://$DOMAIN is live"
else
  log "healthz returned '$code' — check: ssh $HOST journalctl -u lvn -n 50"
  exit 1
fi
