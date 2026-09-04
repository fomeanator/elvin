#!/usr/bin/env bash
# ЧУЖИЕ ДЕНЬГИ И ЧУЖОЙ ПРОГРЕСС НЕ СКАЧАТЬ ПО ПРОВОДУ.
#
# Каталог контента раздаётся статикой — и внутри него, ради удобства работы,
# лежит то, что раздавать нельзя: база с аккаунтами и хэшами паролей, кошельки,
# сейвы игроков, учётки панели, история правок и неопубликованный черновик
# манифеста. Правило «эти пути не отдаём» записано одной функцией; цена ошибки
# в ней — чужая казна одним GET-запросом.
#
# Здесь проверяется не функция, а ПРОВОД: настоящий сервер, живые файлы с
# меткой внутри, и десяток способов до них добраться — прямо, другим регистром,
# через точку-сегмент, через `..`, через двойной слэш, через процентное
# кодирование и по цепочке редиректов.
#
# Стенд обязан уметь и обратное: публичная картинка ДОЛЖНА отдаваться. Сервер,
# который запрещает всё, проходит проверку «ничего не утекло» и не годится ни
# на что.
#
#   qa/private-paths-check.sh [-bite]
#
# -bite кладёт метку в ПУБЛИЧНЫЙ файл: мерка обязана её найти. Иначе «утечек
# нет» означает лишь то, что искать она не умеет.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go   >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }

W="$(mktemp -d)"; PID=""
cleanup() { [ -n "$PID" ] && kill "$PID" 2>/dev/null; rm -rf "$W"; }
trap cleanup EXIT

go build -C server -o "$W/lvnserver" . || { echo "сервер не собрался"; exit 1; }

PORT=0
for p in 8111 8112 8113 8114; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

C="$W/content"
mkdir -p "$C/services/wallet" "$C/state" "$C/.history" "$C/bg"
MARK="ТАЙНА-$$"
printf '{"titles":[{"id":"p","name":"P"}]}' > "$C/manifest.json"
# Форматы настоящие: сервер поднимается только на разбираемых файлах, а стенд
# на непонятных данных не проверил бы ничего (проверено — падал при старте).
printf '{}' > "$C/services/users.json"
printf '{"balances":{"gold":777},"note":"%s"}' "$MARK" > "$C/services/wallet/u_1.json"
printf '{"blob":{"secret":"%s"}}' "$MARK" > "$C/state/u_1.json"
printf 'старая правка %s' "$MARK" > "$C/.history/old.lvn"
printf '{"titles":[],"note":"%s"}' "$MARK" > "$C/manifest.draft.json"
printf 'входящее %s' "$MARK" > "$C/bg/x.jpg.incoming"
printf '{"admins":["%s"]}' "$MARK" > "$C/admin-users.json"
# КОПИИ ЗАКРЫТОГО ФАЙЛА. Их заводят руками перед правкой ролей, их оставляет
# редактор и деплой — и до 04.09 сам admin-users.json отвечал 404, а его
# бэкап отдавался двумя сотнями вместе с хэшами паролей.
printf '{"admins":["%s"]}' "$MARK" > "$C/admin-users.json.bak-20260904"
printf '{"admins":["%s"]}' "$MARK" > "$C/admin-users.json~"
printf '{"titles":[],"note":"%s"}' "$MARK" > "$C/manifest.draft.json.bak"
if [ -n "$BITE" ]; then
  printf 'публичная картинка с меткой %s' "$MARK" > "$C/bg/room.jpg"
else
  printf 'публичная картинка' > "$C/bg/room.jpg"
fi

"$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$C" -admin-token "stand-$$" \
  >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 50); do
  curl -fsS -m 1 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 && break
  sleep 0.2
done
curl -fsS -m 2 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 \
  || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

B="http://127.0.0.1:$PORT"
leaks=0
try() { # путь → печатает код и ловит метку в теле (по цепочке редиректов тоже)
  local path="$1" code body
  code="$(curl -sL -o "$W/out.bin" -w '%{http_code}' --path-as-is "$B$path")"
  if grep -q "$MARK" "$W/out.bin" 2>/dev/null; then
    printf '  УТЕЧКА %-46s код %s\n' "$path" "$code"
    leaks=$((leaks + 1))
  fi
}

tried=0
for path in \
  /content/services/lvn.db /content/services/users.json \
  /content/services/wallet/u_1.json /content/state/u_1.json \
  /content/.history/old.lvn /content/manifest.draft.json \
  /content/bg/x.jpg.incoming /content/admin-users.json \
  /content/admin-users.json.bak-20260904 /content/admin-users.json~ \
  /content/manifest.draft.json.bak /content/ADMIN-USERS.JSON.BAK-20260904 \
  /content/SERVICES/wallet/u_1.json /content/State/u_1.json \
  /content/./services/wallet/u_1.json /content/bg/../state/u_1.json \
  //content//state//u_1.json '/content/services%2fwallet%2fu_1.json' \
  '/content/%2e%2e/backups/x.tar.gz' '/content/.%2fstate/u_1.json' \
  '/content/state/u_1.json?v=1' '/content/state/./u_1.json' \
  /content/bg/room.jpg
do
  try "$path"
  tried=$((tried + 1))
done
# Последним стоит ПУБЛИЧНЫЙ путь — он и делает мерку проверяемой: при -bite
# метка лежит именно в нём, и не найти её значит не уметь искать вообще.

# Публичное обязано отдаваться — иначе «утечек нет» ничего не значит.
pub_code="$(curl -s -o "$W/pub.bin" -w '%{http_code}' "$B/content/bg/room.jpg")"
pub_ok=0
grep -q "публичная картинка" "$W/pub.bin" 2>/dev/null && [ "$pub_code" = "200" ] && pub_ok=1

if [ -n "$BITE" ]; then
  if [ "$leaks" -gt 0 ]; then
    echo "укус замечен: метку в публичном файле мерка нашла ($leaks) — искать она умеет"
    exit 0
  fi
  echo "СТЕНД СЛЕП: метка лежала в открытом файле и не нашлась — «утечек нет» ничего не значило бы"
  exit 2
fi

[ "$pub_ok" = "1" ] || { echo "РВЁТСЯ: публичная картинка не отдаётся (код $pub_code) — сервер запрещает лишнее"; exit 1; }
if [ "$leaks" -gt 0 ]; then
  echo "РВЁТСЯ: приватное ушло по проводу ($leaks путей) — это чужие деньги и чужой прогресс"
  exit 1
fi
echo "держит: $tried путей (обходы к приватному плюс публичный) — ни одного ответа с данными; публичное отдаётся"
