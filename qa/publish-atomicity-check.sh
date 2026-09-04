#!/usr/bin/env bash
# ОТКАЗ ПУБЛИКАЦИИ НЕ СМЕЕТ ПОРТИТЬ УЖЕ ОПУБЛИКОВАННОЕ.
#
# Справка самого API обещает дословно: «Структурная ошибка — кодом 422, и в этом
# случае НИЧЕГО не записано: прежняя версия игры цела». Обещание сильное и
# ровно то, ради которого гейт существует: у игроков в этот момент идёт игра, и
# неудачная попытка автора не должна её задеть.
#
# Половина «отказал ли гейт» проверяется тестами сервера. Здесь проверяется
# ВТОРАЯ, которую не проверял никто: что после отказа на диске и на проводе
# лежит побайтово то же самое, что лежало до него.
#
# Стенд поднимает НАСТОЯЩИЙ сервер и говорит с ним по HTTP. Слепок снимается с
# трёх сторон сразу, потому что порознь каждая врёт:
#   дерево контента   хеши всех файлов — ловит запись мимо ожидаемого пути
#   манифест          отдельно — он правится другим кодом, чем скрипты
#   провод            GET /content/… — то, что реально увидит игрок
#
# ПРОВЕРКА ОБЯЗАНА УМЕТЬ ПАДАТЬ. Сравнение слепков, промахнувшееся мимо
# каталога, «совпадает» всегда. Поэтому -bite публикует ПРАВИЛЬНУЮ правку вместо
# битой и требует, чтобы слепок ИЗМЕНИЛСЯ: стенд, не замечающий настоящей
# записи, не заметил бы и порчи.
#
#   qa/publish-atomicity-check.sh [-bite]
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go   >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }

W="$(mktemp -d)"
PID=""
cleanup() { [ -n "$PID" ] && kill "$PID" 2>/dev/null; rm -rf "$W"; }
trap cleanup EXIT

go build -C server -o "$W/lvnserver" . || { echo "сервер не собрался"; exit 1; }

PORT="${LVN_PORT:-8077}"
if curl -fsS -m 1 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1; then
  PORT=0
  for p in 8078 8079 8081 8082; do
    curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
  done
  [ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }
  echo "порт 8077 занят, беру $PORT"
fi

TOKEN="stand-token-$$"
mkdir -p "$W/content/scripts"
echo '{"titles":[]}' > "$W/content/manifest.json"
"$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$W/content" -admin-token "$TOKEN" \
  >"$W/server.log" 2>&1 &
PID=$!

for _ in $(seq 1 50); do
  curl -fsS -m 1 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 && break
  sleep 0.2
done
curl -fsS -m 2 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 \
  || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

publish() { # $1 = файл с телом запроса; печатает код ответа
  curl -sS -o "$W/resp.json" -w '%{http_code}' \
    -X POST "http://127.0.0.1:$PORT/v1/admin/agent/publish" \
    -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d @"$1"
}

snapshot() { # хеши дерева + манифест + то, что отдаёт провод
  ( cd "$W/content" && find . -type f | LC_ALL=C sort | xargs shasum 2>/dev/null )
  curl -fsS "http://127.0.0.1:$PORT/content/scripts/stand-ch01.lvn" 2>/dev/null | shasum
  curl -fsS "http://127.0.0.1:$PORT/v1/content/manifest" 2>/dev/null | shasum
}

python3 - "$W/good.json" <<'PY'
import json, sys
json.dump({"id": "stand", "name": "Стенд", "chapter": 1,
           "lvns": "scene stand\n\nМира: Первая редакция.\n-> __end\n"},
          open(sys.argv[1], "w"), ensure_ascii=False)
PY
code="$(publish "$W/good.json")"
[ "$code" = "200" ] || { echo "первая публикация не прошла ($code): $(cat "$W/resp.json")"; exit 1; }
python3 -c "
import json,sys
r=json.load(open('$W/resp.json'))
w = r.get('warnings') or []
print('  опубликовано: ok=%s, команд=%s, предупреждений=%s' % (r.get('ok'), r.get('commands'), len(w)))
for x in w: print('    ', x)
# Мерить надо на чистом входе: предупреждение в эталонной публикации означало бы,
# что стенд сравнивает слепки заведомо подпорченной главы.
sys.exit(0 if r.get('ok') and not w else 1)
" || exit 1

snapshot > "$W/before.txt"
before_files=$(wc -l < "$W/before.txt" | tr -d ' ')
[ "$before_files" -ge 3 ] || { echo "слепок пуст ($before_files строк) — сравнивать нечего"; exit 2; }

if [ -n "$BITE" ]; then
  # УКУС: правильная правка. Слепок ОБЯЗАН измениться.
  python3 - "$W/second.json" <<'PY'
import json, sys
json.dump({"id": "stand", "name": "Стенд", "chapter": 1,
           "lvns": "scene stand\n\nМира: ВТОРАЯ редакция, совсем другая.\n-> __end\n"},
          open(sys.argv[1], "w"), ensure_ascii=False)
PY
  code="$(publish "$W/second.json")"
  echo "УКУС: правильная правка → код $code"
else
  # Структурно битая глава: опечатка в имени команды. Синтаксис безупречен,
  # строка молча становится репликой — то самое, что не должно доехать.
  python3 - "$W/bad.json" <<'PY'
import json, sys
json.dump({"id": "stand", "name": "Стенд", "chapter": 1,
           "lvns": "scene stand\n\nactr Мира pose=idle\nМира: Битая редакция.\n-> __end\n"},
          open(sys.argv[1], "w"), ensure_ascii=False)
PY
  code="$(publish "$W/bad.json")"
  echo "битая публикация → код $code (ждём 422)"
  [ "$code" = "422" ] || {
    echo "ГЕЙТ НЕ ОТКАЗАЛ: $(cat "$W/resp.json")"; exit 1; }
fi

snapshot > "$W/after.txt"

if [ -n "$BITE" ]; then
  if cmp -s "$W/before.txt" "$W/after.txt"; then
    echo "СТЕНД СЛЕП: настоящая публикация не изменила слепок — он не заметил бы и порчи"; exit 2
  fi
  echo "стенд честный: настоящая запись слепком видна"; exit 0
fi

if ! cmp -s "$W/before.txt" "$W/after.txt"; then
  echo "ОТКАЗ ИСПОРТИЛ ОПУБЛИКОВАННОЕ — расхождение:"
  diff "$W/before.txt" "$W/after.txt" | sed 's/^/  /'
  exit 1
fi

leftovers=$(find "$W/content/scripts" -name ".publish-*" | wc -l | tr -d ' ')
echo "слепок совпал построчно ($before_files строк), временных файлов осталось: $leftovers"
[ "$leftovers" = "0" ] || { echo "ВРЕМЕННЫЙ ФАЙЛ ОСТАЛСЯ В ЖИВОМ КАТАЛОГЕ"; exit 1; }
echo "ОТКАЗ ЧИСТ — прежняя версия игры цела"
