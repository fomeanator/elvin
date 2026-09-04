#!/usr/bin/env bash
# ТАБЛИЦА ЛИДЕРОВ: ЧТО ЗДЕСЬ ЗАЩИЩЕНО, А ЧТО — НЕТ, И ЭТО ЧЕСТНО.
#
# Очки присылает клиент. Сервер не симулирует игру и проверить их НЕ МОЖЕТ —
# значит величина результата не защищена ничем и не может быть. Это граница
# устройства, и называть её надо числом, а не умалчивать: «у нас есть таблица
# лидеров» без этой оговорки читается как обещание честного соревнования.
#
# Защищено при этом ровно то, что защитить можно, и это стоит проверить:
#
#   ЛИЧНОСТЬ   кто подал, берётся из ВХОДА, а не из тела. Иначе один игрок
#              переписывал бы рекорды другого, и таблица теряла бы смысл
#              быстрее, чем от накрученных очков;
#   УЛУЧШЕНИЕ  повторная подача ХУДШЕГО не портит рекорд. Клиент отправляет
#              результат после каждой попытки, и «последний победил» стирал бы
#              достижение при первом же неудачном заходе;
#   КАТАЛОГ    имя доски приходит из адреса и становится ключом хранения.
#              Слаг обязан проверяться, иначе доска пишется куда угодно.
#
# АНТИ-ПУСТОТА: сперва стенд обязан УВИДЕТЬ поданный результат в таблице. Иначе
# «чужой не переписал мой рекорд» проходило бы и на сервере, который не
# принимает ничего.
#
#   qa/leaderboard-check.sh [-bite]
#
# -bite подаёт результат ЛУЧШЕ прежнего и требует, чтобы таблица изменилась:
# стенд, не видящий улучшения, не заметил бы и порчи.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v curl >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }
command -v go   >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }

W="$(mktemp -d)"; PID=""
cleanup() { [ -n "$PID" ] && kill "$PID" 2>/dev/null; rm -rf "$W"; }
trap cleanup EXIT
go build -C server -o "$W/srv" . || { echo "сервер не собрался"; exit 1; }

PORT="${LVN_PORT:-8077}"
probe() { curl -fsS -m 1 "http://127.0.0.1:$1/healthz" >/dev/null 2>&1; }
if probe "$PORT"; then
  PORT=0
  for p in 8078 8079 8081 8082; do probe "$p" || { PORT=$p; break; }; done
  [ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }
fi
BASE="http://127.0.0.1:$PORT"
mkdir -p "$W/content"; echo '{"titles":[]}' > "$W/content/manifest.json"

probe "$PORT" && { echo "порт занят — замер говорил бы с чужим сервером"; exit 2; }
"$W/srv" -addr "127.0.0.1:$PORT" -content "$W/content" -auth-dev >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 60); do probe "$PORT" && break; sleep 0.2; done
probe "$PORT" || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

reg() { curl -sS -X POST "$BASE/v1/auth/register" -H 'Content-Type: application/json' \
        -d "{\"device_id\":\"$1\"}" | python3 -c 'import json,sys;print(json.load(sys.stdin)["token"])'; }
A="$(reg lb-device-первый-0001)"; B="$(reg lb-device-второй-0002)"
[ -n "$A" ] && [ -n "$B" ] || { echo "учётки не завелись"; exit 1; }

BOARD="проба"
submit() { # $1 = токен, $2 = очки, $3 = имя; печатает код
  curl -sS -o /dev/null -w '%{http_code}' -X POST "$BASE/v1/leaderboard/board1" \
    -H "Authorization: Bearer $1" -H 'Content-Type: application/json' \
    -d "{\"score\":$2,\"name\":\"$3\"}"
}
top() { curl -sS "$BASE/v1/leaderboard/board1" -H "Authorization: Bearer $1"; }
score_of() { # $1 = токен, $2 = имя; печатает очки этого имени
  top "$1" | python3 -c '
import json, sys
d = json.load(sys.stdin)
rows = d.get("top") or d.get("entries") or []
name = sys.argv[1]
for r in rows:
    if r.get("name") == name: print(r.get("score")); break
else: print("нет")' "$2"
}

fail=0
say() { echo "  $1"; }

submit "$A" 100 "Первый" >/dev/null
got="$(score_of "$A" Первый)"
say "первый подал 100 → в таблице: $got"
[ "$got" = "100" ] || { say "✗ результат не доехал до таблицы — мерить нечего"; exit 1; }

if [ -n "$BITE" ]; then
  submit "$A" 500 "Первый" >/dev/null
  now="$(score_of "$A" Первый)"
  say "укус: подали 500 (лучше) → в таблице: $now"
  [ "$now" = "500" ] && { say "стенд честный: улучшение он видит"; exit 0; }
  say "✗ СТЕНД СЛЕП: лучший результат не изменил таблицу"; exit 2
fi

# 1. Только улучшение.
submit "$A" 10 "Первый" >/dev/null
after="$(score_of "$A" Первый)"
say "тот же игрок подал 10 (хуже) → в таблице: $after (ждём 100)"
[ "$after" = "100" ] || { say "✗ ХУДШИЙ РЕЗУЛЬТАТ СТЁР РЕКОРД"; fail=1; }

# 2. Личность из входа: второй игрок не переписывает первого.
submit "$B" 5 "Первый" >/dev/null
mine="$(score_of "$A" Первый)"
say "второй игрок подал 5 под тем же ИМЕНЕМ → у первого: $mine (ждём 100)"
[ "$mine" = "100" ] || { say "✗ ЧУЖОЙ ПЕРЕПИСАЛ ЧУЖОЙ РЕКОРД — личность берётся из тела"; fail=1; }

# 3. Имя доски не выводит за каталог — И ОТКАЗ ДОЛЖЕН БЫТЬ ОТ СЕРВЕРА.
#
# Путь с «..» нормализует сам маршрутизатор, и 404 от него ничего не говорит о
# проверке слага: так выглядела бы и дыра, прикрытая случайностью. Поэтому
# рядом — имя, которое ДОХОДИТ до обработчика и обязано быть отвергнуто им
# самим кодом 400.
esc="$(curl -sS -o /dev/null -w '%{http_code}' -X POST "$BASE/v1/leaderboard/../../сбежал" \
        -H "Authorization: Bearer $A" -H 'Content-Type: application/json' -d '{"score":1}')"
say "путь с «..» (нормализует маршрутизатор): $esc"
[ "$esc" = "200" ] && { say "✗ ДОСКА ПИШЕТСЯ ЗА ПРЕДЕЛЫ КАТАЛОГА"; fail=1; }

slug="$(curl -sS -o /dev/null -w '%{http_code}' -X POST "$BASE/v1/leaderboard/НЕ-СЛАГ" \
        -H "Authorization: Bearer $A" -H 'Content-Type: application/json' -d '{"score":1}')"
say "имя не по слагу (доходит до сервера): $slug (ждём 400)"
[ "$slug" = "400" ] || { say "✗ имя доски не проверяется САМИМ сервером (ответ $slug)"; fail=1; }

# 4. ГРАНИЦА, А НЕ ДЕФЕКТ: величину очков не проверяет никто.
submit "$A" 9223372036854775807 "Первый" >/dev/null
huge="$(score_of "$A" Первый)"
say "подан предельный int64 → в таблице: $huge"
say "     ↑ граница устройства: сервер не симулирует игру и проверить очки НЕ МОЖЕТ"

[ "$fail" = "0" ] && { echo "ЛИЧНОСТЬ И РЕКОРД ЗАЩИЩЕНЫ, ВЕЛИЧИНА — НЕТ И НЕ МОЖЕТ"; exit 0; }
echo "ТАБЛИЦА ЛИДЕРОВ НЕ ДЕРЖИТ"; exit 1
