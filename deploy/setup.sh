#!/usr/bin/env bash
# LVN server provisioning — runs ON the target host (Debian/Ubuntu), as root.
# Idempotent: safe to re-run any time; every step converges to the same state.
#
#   DOMAIN=novels.example.com ADMIN_TOKEN=... PORT=8078 bash setup.sh
#
# What it converges:
#   • base: timezone, 2G swap, ufw (22/80/443), fail2ban, unattended-upgrades
#   • runtime: `lvn` system user, ${LVN_HOME} layout, systemd unit
#   • front: nginx site rendered from the template, Let's Encrypt certificate
#     (webroot flow; issued on first run, renewed by certbot.timer)
#   • encoder: basis_universal CLI (KTX2 texture variants), built once
#
# The LVN binary itself is DEPLOYED separately (CI/deploy.sh uploads
# ${LVN_HOME}/lvn-server and restarts the unit) — provisioning and releases
# stay independent.
set -euo pipefail

DOMAIN="${DOMAIN:?set DOMAIN=your.host.name}"
PORT="${PORT:-8078}"
LVN_HOME="${LVN_HOME:-/srv/lvn}"
ADMIN_TOKEN="${ADMIN_TOKEN:-}"

export DEBIAN_FRONTEND=noninteractive
log() { echo "[lvn-setup] $*"; }

# ── base system ─────────────────────────────────────────────────────────────
log "packages"
apt-get update -qq
apt-get install -y -qq nginx certbot rsync ufw fail2ban unattended-upgrades \
  git build-essential cmake curl >/dev/null

timedatectl set-timezone "${TZ_NAME:-Europe/Moscow}" || true

if ! swapon --show --noheadings | grep -q .; then
  log "swap 2G"
  fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile >/dev/null && swapon /swapfile
  grep -q '/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi

# ── OS tuned for one hot Go process ─────────────────────────────────────────
log "sysctl + journald caps"
cat > /etc/sysctl.d/90-lvn.conf <<'SYS'
# Bursty small-connection profile (game clients polling + art fetches).
net.core.somaxconn = 4096
net.ipv4.tcp_fastopen = 3
net.ipv4.tcp_tw_reuse = 1
fs.file-max = 200000
# Swap is a safety net, not working memory — prefer keeping the Go heap hot.
vm.swappiness = 10
SYS
sysctl --system >/dev/null
mkdir -p /etc/systemd/journald.conf.d
printf '[Journal]\nSystemMaxUse=200M\n' > /etc/systemd/journald.conf.d/90-lvn.conf
systemctl restart systemd-journald || true
mkdir -p /var/cache/nginx/lvn && chown -R www-data:www-data /var/cache/nginx
cp "$(cd "$(dirname "$0")" && pwd)/nginx/tuning.conf" /etc/nginx/conf.d/lvn-tuning.conf

log "firewall"
ufw default deny incoming >/dev/null
ufw default allow outgoing >/dev/null
ufw allow 22/tcp >/dev/null; ufw allow 80/tcp >/dev/null; ufw allow 443/tcp >/dev/null
ufw --force enable >/dev/null
systemctl enable --now fail2ban >/dev/null 2>&1 || true

# ── runtime user + layout ───────────────────────────────────────────────────
id -u lvn >/dev/null 2>&1 || useradd --system --home "$LVN_HOME" --shell /usr/sbin/nologin lvn
mkdir -p "$LVN_HOME/content"
touch "$LVN_HOME/lvn.env"
chmod 600 "$LVN_HOME/lvn.env"
# Keep the Go runtime inside the unit's memory fence: GOMEMLIMIT makes the GC
# tighten up near the limit instead of tripping MemoryMax.
grep -q '^GOMEMLIMIT=' "$LVN_HOME/lvn.env" || echo "GOMEMLIMIT=1100MiB" >> "$LVN_HOME/lvn.env"
if [ -n "$ADMIN_TOKEN" ]; then
  grep -q '^ADMIN_TOKEN=' "$LVN_HOME/lvn.env" \
    && sed -i "s|^ADMIN_TOKEN=.*|ADMIN_TOKEN=$ADMIN_TOKEN|" "$LVN_HOME/lvn.env" \
    || echo "ADMIN_TOKEN=$ADMIN_TOKEN" >> "$LVN_HOME/lvn.env"
elif ! grep -q '^ADMIN_TOKEN=' "$LVN_HOME/lvn.env"; then
  echo "ADMIN_TOKEN=$(head -c 24 /dev/urandom | base64 | tr -dc A-Za-z0-9 | head -c 32)" >> "$LVN_HOME/lvn.env"
  log "generated ADMIN_TOKEN (see $LVN_HOME/lvn.env)"
fi
chown -R lvn:lvn "$LVN_HOME"

# ── systemd unit ────────────────────────────────────────────────────────────
log "systemd unit"
HERE="$(cd "$(dirname "$0")" && pwd)"
sed -e "s|\${PORT}|$PORT|g" -e "s|\${LVN_HOME}|$LVN_HOME|g" \
    -e "s|\${ADMIN_TOKEN_REF}|\${ADMIN_TOKEN}|g" \
    "$HERE/lvn.service.template" > /etc/systemd/system/lvn.service
systemctl daemon-reload
systemctl enable lvn >/dev/null 2>&1 || true

# ── nginx + certificate ─────────────────────────────────────────────────────
log "nginx site for $DOMAIN"
mkdir -p /var/www/certbot
# Bootstrap order matters: nginx can't load an ssl server block before the
# certificate exists. First render an HTTP-only site (ACME + proxy), issue the
# cert through it, then render the full TLS site.
if [ ! -e "/etc/letsencrypt/live/$DOMAIN/fullchain.pem" ]; then
  cat > /etc/nginx/sites-available/lvn.conf <<BOOT
server {
    listen 80;
    server_name $DOMAIN;
    location /.well-known/acme-challenge/ { root /var/www/certbot; }
    location / {
        proxy_pass http://127.0.0.1:$PORT;
        proxy_set_header Host \$host;
    }
}
BOOT
  ln -sf /etc/nginx/sites-available/lvn.conf /etc/nginx/sites-enabled/lvn.conf
  rm -f /etc/nginx/sites-enabled/default
  nginx -t && systemctl reload-or-restart nginx
  log "issuing certificate"
  certbot certonly --webroot -w /var/www/certbot -d "$DOMAIN" \
    --non-interactive --agree-tos --register-unsafely-without-email
fi
sed -e "s|\${DOMAIN}|$DOMAIN|g" -e "s|\${PORT}|$PORT|g" \
    "$HERE/nginx/lvn.conf.template" > /etc/nginx/sites-available/lvn.conf
ln -sf /etc/nginx/sites-available/lvn.conf /etc/nginx/sites-enabled/lvn.conf
rm -f /etc/nginx/sites-enabled/default
nginx -t && systemctl reload-or-restart nginx
systemctl enable --now certbot.timer >/dev/null 2>&1 || true

# ── basisu (KTX2 encoder) ───────────────────────────────────────────────────
if ! command -v basisu >/dev/null; then
  log "building basis_universal (one-time)"
  tmp=$(mktemp -d)
  git clone --depth 1 https://github.com/BinomialLLC/basis_universal.git "$tmp" >/dev/null 2>&1
  (cd "$tmp" && cmake -DCMAKE_BUILD_TYPE=Release . >/dev/null && make -j"$(nproc)" basisu >/dev/null)
  install -m 755 "$tmp/bin/basisu" /usr/local/bin/basisu
  rm -rf "$tmp"
fi

log "done. Deploy the binary to $LVN_HOME/lvn-server and: systemctl restart lvn"
