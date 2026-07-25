#!/usr/bin/env bash
# Сборка деплой-артефактов Time Romance НА ДЕВ-МАШИНЕ и раскладка их в
# деплой-репо (timeromance-api). Прод-бокс слаб (go build уводит его в
# своп-шторм, npm тем более — выяснено опытным путём), поэтому конвейер
# такой: этот скрипт собирает всё здесь → артефакты коммитятся в api-репо →
# push в main → gitlab-runner НА боксе раскладывает готовое (deploy-local.sh).
#
#   deploy/build-artifacts.sh                      # → ../timeromance/api
#   deploy/build-artifacts.sh /path/to/api-repo
#   COMMIT=1 deploy/build-artifacts.sh             # + git commit в api-репо
#
# Кладёт: bin/lvn-server-linux-amd64, bin/lvnconv-linux-amd64 (нужен на
# проде для resync-lvns/детект-инструментов), website/ (Studio-панель,
# включает свежий lvns.wasm — тот же компилятор, что в CLI), VERSION.
set -euo pipefail

HERE="$(cd "$(dirname "$0")/.." && pwd)"   # корень движка
API_REPO="${1:-$HERE/../timeromance/api}"
API_REPO="$(cd "$API_REPO" && pwd)"
log() { echo "[build-artifacts] $*"; }

[ -f "$API_REPO/deploy-local.sh" ] || { echo "не похоже на api-репо: $API_REPO"; exit 1; }

ENGINE_SHA="$(git -C "$HERE" rev-parse --short HEAD)"
if [ -n "$(git -C "$HERE" status --porcelain -- tools server panel 2>/dev/null)" ]; then
  log "ВНИМАНИЕ: в движке незакоммиченные изменения — артефакт будет помечен ${ENGINE_SHA}-dirty"
  ENGINE_SHA="${ENGINE_SHA}-dirty"
fi

log "движок @ $ENGINE_SHA"

# ── 1. Серверный бинарь (linux/amd64, стрипнутый — как ждёт deploy-local) ──
log "go build server → bin/lvn-server-linux-amd64"
mkdir -p "$API_REPO/bin"
(cd "$HERE/server" && CGO_ENABLED=0 GOOS=linux GOARCH=amd64 \
  go build -trimpath -ldflags='-s -w' -o "$API_REPO/bin/lvn-server-linux-amd64" .)

# ── 2. lvnconv CLI (resync-lvns, detect и прочие серверные операции) ───────
log "go build lvnconv → bin/lvnconv-linux-amd64"
(cd "$HERE/tools/lvnconv" && CGO_ENABLED=0 GOOS=linux GOARCH=amd64 \
  go build -trimpath -ldflags='-s -w' -o "$API_REPO/bin/lvnconv-linux-amd64" .)

# ── 3. Studio-панель (npm run deploy пересобирает и wasm-компилятор) ───────
log "panel build (npm run deploy)"
(cd "$HERE/panel" && npm run deploy >/dev/null)
rsync -a --delete "$HERE/server/website/" "$API_REPO/website/"

# ── 4. Штамп версии — трассируемость «какой движок задеплоен» ──────────────
printf 'engine=%s\nbuilt=%s\n' "$ENGINE_SHA" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$API_REPO/VERSION"

log "готово:"
ls -la "$API_REPO/bin" | awk 'NR>1 {print "  " $5 "\t" $9}'
du -sh "$API_REPO/website" | awk '{print "  " $1 "\twebsite/"}'
cat "$API_REPO/VERSION" | sed 's/^/  /'

# ── 5. Опциональный коммит в api-репо ──────────────────────────────────────
if [ "${COMMIT:-0}" = "1" ]; then
  (cd "$API_REPO" && git add bin website VERSION && \
   git commit -m "Артефакты движка @ ${ENGINE_SHA}" && \
   log "закоммичено в api-репо — git push origin main запустит автодеплой")
fi
