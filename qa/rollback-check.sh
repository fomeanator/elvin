#!/usr/bin/env bash
# ОТКАТ: ПЛОХАЯ ГЛАВА УЕХАЛА ИГРАЮЩИМ — ВЕРНУТЬ ПРЕЖНЮЮ.
#
# Это сеть безопасности под самой дорогой ошибкой. Гейт публикации ловит битое
# (qa/publish-atomicity-check.sh), но он не умеет ловить ПЛОХОЕ: глава может
# компилироваться, проходить структурную проверку и всё равно оказаться не той.
# Тогда остаётся откат, и мерить надо не «ручка ответила», а три вещи:
#
#   ВЕРНУЛОСЬ ЛИ   по проводу игроку отдаётся ПРЕЖНИЙ байт в байт текст;
#   ОБРАТИМ ЛИ     сам откат тоже попадает в историю. Откат, стирающий то,
#                  от чего откатывались, — это не сеть, а второй обрыв: вернуть
#                  «как было минуту назад» станет уже нечем;
#   ЗАПЕРТ ЛИ      имя файла приходит от клиента, а рядом лежит весь контент.
#                  Путь с «..» обязан быть отвергнут, иначе откат превращается
#                  в запись куда угодно.
#
# АНТИ-ПУСТОТА. Сперва стенд обязан показать, что правка ВООБЩЕ доехала: если
# сервер отдаёт одно и то же всегда, «откат вернул прежнее» проходит само собой.
# Поэтому между версиями проверяется РАЗЛИЧИЕ, и только потом — возврат.
#
#   qa/rollback-check.sh [-bite]
#
# -bite откатывает на снимок, которого нет, и требует отказа: стенд, у которого
# «восстановлено» приходит на любой запрос, не заметил бы и подмены версии.
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
BASE="http://127.0.0.1:$PORT"; ADMIN="roll-$$"
mkdir -p "$W/content/scripts"; echo '{"titles":[]}' > "$W/content/manifest.json"

probe "$PORT" && { echo "порт занят — замер говорил бы с чужим сервером"; exit 2; }
"$W/srv" -addr "127.0.0.1:$PORT" -content "$W/content" -admin-token "$ADMIN" \
  >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 60); do probe "$PORT" && break; sleep 0.2; done
probe "$PORT" || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

REL="scripts/roll-ch01.lvns"
publish() { # $1 = текст главы
  curl -sS -o /dev/null -w '%{http_code}' -X POST "$BASE/v1/admin/agent/publish" \
    -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' \
    -d "{\"id\":\"roll\",\"name\":\"Откат\",\"chapter\":1,\"lvns\":\"$1\"}"
}
served() { curl -fsS "$BASE/content/$REL" 2>/dev/null; }
history() { curl -sS "$BASE/v1/admin/history?file=$REL" -H "Authorization: Bearer $ADMIN"; }
rollback() { # $1 = метка времени; печатает код
  curl -sS -o /dev/null -w '%{http_code}' -X POST "$BASE/v1/admin/rollback" \
    -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' \
    -d "{\"File\":\"$2\",\"TS\":\"$1\"}"
}
stamps() { history | python3 -c 'import json,sys
d = json.load(sys.stdin)
v = d.get("versions") or d.get("history") or d
print(" ".join(str(x.get("ts", x)) if isinstance(x, dict) else str(x) for x in v))' 2>/dev/null; }

fail=0
say() { echo "  $1"; }

code="$(publish 'scene roll\n\nМира: ПЕРВАЯ редакция.\n-> __end\n')"
[ "$code" = "200" ] || { say "✗ первая публикация не прошла ($code)"; exit 1; }
first="$(served)"
code="$(publish 'scene roll\n\nМира: ВТОРАЯ, неудачная.\n-> __end\n')"
[ "$code" = "200" ] || { say "✗ вторая публикация не прошла ($code)"; exit 1; }
second="$(served)"

# Анти-пустота: правка обязана быть ВИДНА, иначе возврат ничего не докажет.
if [ "$first" = "$second" ]; then
  say "✗ по проводу обе редакции одинаковы — сравнивать нечего"; exit 1
fi
say "две редакции по проводу различаются — есть что возвращать"

list="$(stamps)"
[ -n "$list" ] || { say "✗ история пуста — откатывать не с чего"; exit 1; }
ts="$(echo "$list" | tr ' ' '\n' | sort | head -1)"
say "снимков в истории: $(echo "$list" | wc -w | tr -d ' '), берём самый старый"

if [ -n "$BITE" ]; then
  code="$(rollback 9999999999999 "$REL")"
  say "укус: откат на несуществующий снимок → $code (ждём отказ)"
  [ "$code" = "200" ] && { say "✗ СТЕНД ВРЁТ: «восстановлено» приходит на что угодно"; exit 2; }
  say "стенд честный: несуществующая версия отвергается"
  exit 0
fi

code="$(rollback "$ts" "$REL")"
say "откат: $code"
[ "$code" = "200" ] || { say "✗ откат не прошёл"; fail=1; }
back="$(served)"
if [ "$back" = "$first" ]; then say "по проводу вернулась ПЕРВАЯ редакция, байт в байт"
else say "✗ вернулось не то: по проводу не первая редакция"; fail=1; fi

# ОБРАТИМОСТЬ: неудачная редакция обязана остаться в истории.
after="$(stamps)"
say "снимков после отката: $(echo "$after" | wc -w | tr -d ' ')"
if [ "$(echo "$after" | wc -w)" -le "$(echo "$list" | wc -w)" ]; then
  say "✗ откат не сохранил то, от чего откатывались — вернуться назад уже нечем"; fail=1
fi

# ЗАПЕРТ ЛИ ПУТЬ: имя файла приходит от клиента.
esc="$(rollback "$ts" "../../../../tmp/сбежал.json")"
say "откат по пути с «..»: $esc (ждём отказ)"
[ "$esc" = "200" ] && { say "✗ ОТКАТ ПИШЕТ ЗА ПРЕДЕЛЫ КОНТЕНТА"; fail=1; }

[ "$fail" = "0" ] && { echo "ОТКАТ ВОЗВРАЩАЕТ И САМ ОБРАТИМ"; exit 0; }
echo "ОТКАТ НЕ ДЕРЖИТ"; exit 1
