#!/usr/bin/env bash
# «ИГР НЕТ» И «У МЕНЯ БЕДА» — РАЗНЫЕ ОТВЕТЫ.
#
# Манифест — единственная точка правды о том, какие новеллы есть у игрока.
# Сервер, который не смог его прочитать, отвечал ПУСТЫМ КАТАЛОГОМ и кодом 200:
# «свежая установка ещё ничего не опубликовала» и «выкладка сломалась»
# выглядели с провода одинаково. Клиент такой ответ принимал — и затирал
# офлайновую копию, то есть игрок оставался без библиотеки и без сети тоже
# (замерено живым прогоном, CatalogGuardTests).
#
# Здесь проверяются три состояния настоящего сервера:
#
#   манифест на месте     → 200 и каталог
#   файла нет вовсе       → 200 и пустой каталог (законно: публиковать нечего)
#   файл есть, не читается → 503 (наша беда, а не «игр нет»)
#
#   qa/empty-manifest-check.sh [-bite]
#
# -bite возвращает права файлу и требует, чтобы сервер снова отдал каталог:
# стенд, который получает 503 всегда, проверяет не различение, а поломку.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go   >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }
[ "$(id -u)" = "0" ] && { echo "прогон от root: снятие прав ничего не значит — пропускаю"; exit 0; }

W="$(mktemp -d)"; PID=""
cleanup() {
  [ -n "$PID" ] && kill "$PID" 2>/dev/null
  chmod -R u+rwX "$W" 2>/dev/null
  rm -rf "$W"
}
trap cleanup EXIT

go build -C server -o "$W/lvnserver" . || { echo "сервер не собрался"; exit 1; }

PORT=0
for p in 8097 8098 8099 8100; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

mkdir -p "$W/content"
M="$W/content/manifest.json"
printf '{"titles":[{"id":"probe","name":"Проба"}]}' > "$M"

"$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$W/content" -admin-token "stand-$$" \
  >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 50); do
  curl -fsS -m 1 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 && break
  sleep 0.2
done
curl -fsS -m 2 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 \
  || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

ask() { # печатает «код тело»
  local code body
  body="$(curl -s -o "$W/body.json" -w '%{http_code}' "http://127.0.0.1:$PORT/v1/content/manifest")"
  code="$body"
  printf '%s %s' "$code" "$(head -c 60 "$W/body.json")"
}

ok="$(ask)"; echo "  манифест на месте:      $ok"

mv "$M" "$M.away"
gone="$(ask)"; echo "  файла нет:              $gone"
mv "$M.away" "$M"

chmod 000 "$M"
locked="$(ask)"; echo "  файл есть, не читается: $locked"
chmod 644 "$M"

if [ -n "$BITE" ]; then
  back="$(ask)"
  echo "  права вернули:          $back"
  case "$back" in
    200*probe*) echo "укус чист: сервер снова отдаёт каталог — 503 был про беду, а не про себя"; exit 0;;
    *) echo "СТЕНД ВРЁТ: после возврата прав каталог не вернулся — проверять нечего"; exit 2;;
  esac
fi

fail=0
case "$ok" in     200*probe*) ;; *) echo "  живой манифест не отдан: $ok"; fail=1;; esac
case "$gone" in   200*'"titles":[]'*) ;; *) echo "  без файла ожидался пустой каталог, получено: $gone"; fail=1;; esac
case "$locked" in
  503*) ;;
  200*) echo "РВЁТСЯ: нечитаемый манифест отдан как ПУСТОЙ КАТАЛОГ ($locked) — игрок увидит пустую библиотеку"; fail=1;;
  *)    echo "  неожиданный ответ на нечитаемый файл: $locked"; fail=1;;
esac
[ "$fail" = "0" ] || exit 1
echo "держит: пустой каталог только когда публиковать нечего; нечитаемый манифест — 503"
