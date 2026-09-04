#!/usr/bin/env bash
# ЭКСПОРТ ОТДАЁТ ИГРУ, А НЕ ЧУЖИЕ ДАННЫЕ.
#
# «Экспорт» — это кнопка, после которой у автора на диске лежит готовый
# Unity-проект его игры. Внутрь уезжает весь каталог контента, а рядом с
# контентом на сервере живут кошельки, сейвы и учётки панели: один неверный
# фильтр — и казна игроков оказывается в архиве, который автор перешлёт кому
# угодно.
#
# Стенд кладёт в каталог и содержимое игры, и приватные файлы с меткой внутри,
# а потом просит экспорт трижды:
#
#   ЗАМОК     без пропуска экспорт не отдают;
#   ОНЛАЙН    проект собран, адрес сервера вшит, контента в нём нет
#             (игра берёт его с сервера), чужих данных нет;
#   ОФЛАЙН    контент лежит в StreamingAssets вместе с индексом версий,
#             в проекте поднят флаг офлайна — и снова ни одной метки.
#
#   qa/export-check.sh [-bite]
#
# -bite кладёт метку в ПУБЛИЧНУЮ картинку: стенд обязан найти её в офлайн-
# архиве. Мерка, не находящая метку там, где она заведомо есть, не доказывает
# и её отсутствия в остальных местах.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go    >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl  >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }
command -v unzip >/dev/null 2>&1 || { echo "нет unzip — пропускаю"; exit 0; }

W="$(mktemp -d)"; PID=""
cleanup() { [ -n "$PID" ] && kill "$PID" 2>/dev/null; rm -rf "$W"; }
trap cleanup EXIT

go build -C server -o "$W/lvnserver" . || { echo "сервер не собрался"; exit 1; }

PORT=0
for p in 8171 8173 8175 8177; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

MARK="ТАЙНА-$$"
C="$W/content"
mkdir -p "$C/bg" "$C/scripts" "$C/services/wallet" "$C/state" "$C/.history"
printf '{"titles":[{"id":"p","name":"П","chapters":[]}]}' > "$C/manifest.json"
printf '{"scene":"t","script":[{"op":"say","text":"строка"}]}' > "$C/scripts/ch.lvn"
printf '{"balances":{"gold":999},"note":"%s"}' "$MARK" > "$C/services/wallet/u_1.json"
printf '{"blob":{"secret":"%s"}}' "$MARK" > "$C/state/u_1.json"
printf 'старое %s' "$MARK" > "$C/.history/old.lvn"
printf '{"admins":["%s"]}' "$MARK" > "$C/admin-users.json"
if [ -n "$BITE" ]; then
  printf 'картинка с меткой %s' "$MARK" > "$C/bg/room.jpg"
else
  printf 'картинка' > "$C/bg/room.jpg"
fi

TOKEN="stand-token-$$"
"$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$C" -admin-token "$TOKEN" \
  >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 50); do
  curl -fsS -m 1 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 && break
  sleep 0.2
done
curl -fsS -m 2 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 \
  || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

B="http://127.0.0.1:$PORT"
bad=""; note() { bad="$bad\n  $1"; }

# ── Замок ──────────────────────────────────────────────────────────────────
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$B/v1/export" \
        -H 'Content-Type: application/json' -d '{"name":"Кража"}')"
[ "$code" = "401" ] || [ "$code" = "403" ] || note "экспорт отдан без пропуска ($code)"

ask() { # $1 = файл, $2 = тело запроса
  curl -s -o "$1" -w '%{http_code}' -X POST "$B/v1/export" \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "$2"
}
unpack() { rm -rf "$2"; mkdir -p "$2"; unzip -q "$1" -d "$2" 2>/dev/null; }
marks_in() { grep -rl "$MARK" "$1" 2>/dev/null | wc -l | tr -d ' '; }
players_in() { unzip -l "$1" 2>/dev/null | grep -icE "content/services/|content/state/|admin-users|\.history/"; }

# ── Онлайн ─────────────────────────────────────────────────────────────────
c1="$(ask "$W/online.zip" '{"name":"Проба","bundleId":"com.stand.probe"}')"
[ "$c1" = "200" ] || note "онлайн-экспорт не отдан ($c1)"
unpack "$W/online.zip" "$W/on"
for want in "ProjectSettings/ProjectSettings.asset" "Packages/manifest.json"; do
  [ -n "$(find "$W/on" -path "*/$want" | head -1)" ] || note "в проекте нет $want"
done
boot="$(find "$W/on" -name Boot.cs | head -1)"
[ -n "$boot" ] || note "в проекте нет Boot.cs"
[ -z "$boot" ] || grep -q "127.0.0.1:$PORT" "$boot" || note "адрес сервера не вшит в Boot.cs"
[ -z "$boot" ] || grep -q "OfflineBundled = false" "$boot" || note "онлайн-проект объявлен офлайновым"
on_marks="$(marks_in "$W/on")"
on_players="$(players_in "$W/online.zip")"

# ── Офлайн ─────────────────────────────────────────────────────────────────
c2="$(ask "$W/offline.zip" '{"name":"Проба","bundleId":"com.stand.probe","offline":true}')"
[ "$c2" = "200" ] || note "офлайн-экспорт не отдан ($c2)"
unpack "$W/offline.zip" "$W/off"
sa="$(find "$W/off" -path "*StreamingAssets*" -type f | wc -l | tr -d ' ')"
[ "$sa" -ge 3 ] || note "в офлайн-проекте всего $sa файлов контента — игра без сети не поедет"
[ -n "$(find "$W/off" -name asset-versions.json | head -1)" ] || note "в офлайн-проекте нет индекса версий"
boot2="$(find "$W/off" -name Boot.cs | head -1)"
[ -z "$boot2" ] || grep -q "OfflineBundled = true" "$boot2" || note "офлайн-проект не объявлен офлайновым"
off_marks="$(marks_in "$W/off")"
off_players="$(players_in "$W/offline.zip")"

if [ -n "$BITE" ]; then
  if [ "$off_marks" -gt 0 ]; then
    echo "укус чист: метку в публичной картинке стенд нашёл ($off_marks файл(ов)) — искать он умеет"
    exit 0
  fi
  echo "СТЕНД СЛЕП: метка лежала в открытом файле игры и не нашлась — «чужого нет» ничего не значило бы"
  exit 2
fi

echo "  замок:  без пропуска $code"
echo "  онлайн: $(unzip -l "$W/online.zip" | tail -1 | awk '{print $2}') файлов, меток $on_marks, файлов игроков $on_players"
echo "  офлайн: контента в StreamingAssets $sa, меток $off_marks, файлов игроков $off_players"

[ "$on_marks" = "0" ] || note "в онлайн-проекте нашлись чужие данные ($on_marks файлов с меткой)"
[ "$off_marks" = "0" ] || note "в офлайн-проекте нашлись чужие данные ($off_marks файлов с меткой)"
[ "$on_players" = "0" ] || note "в онлайн-архив попали пути с данными игроков"
[ "$off_players" = "0" ] || note "в офлайн-архив попали пути с данными игроков"

[ -z "$bad" ] || { echo "РВЁТСЯ:$(printf '%b' "$bad")"; exit 1; }
echo "держит: экспорт закрыт пропуском, проект собран, контент на месте, чужих данных нет"
