#!/usr/bin/env bash
# РОЛИ В ПАНЕЛИ: СМОТРЯЩИЙ НЕ МЕНЯЕТ, РЕДАКТОР НЕ РАСПОРЯЖАЕТСЯ ЛЮДЬМИ.
#
# Панель правит ЖИВОЙ контент: манифест, тексты, ассеты — то, что через секунду
# увидят играющие. Ролей намеренно три, и разница между ними — не удобство:
#
#   viewer  только смотреть
#   editor  содержание: сцены, тексты, ассеты
#   owner   всё, включая управление людьми
#
# ЧТО ЗДЕСЬ ЛЕГКО ПОТЕРЯТЬ. Право по умолчанию выводится ИЗ МЕТОДА запроса:
# GET и HEAD — смотрящему, остальное — редактору. Правило удобное и хрупкое:
# любое опасное действие, сделанное через GET, автоматически достаётся
# СМОТРЯЩЕМУ. Явно названное право во всём сервере встречается ровно один раз —
# у публикации, требующей владельца.
#
# Поэтому проверяется не «есть ли роли», а МАТРИЦА: кто что может на живом
# сервере, с настоящим входом по паролю и настоящей сессией.
#
# АНТИ-ПУСТОТА. Стенд сперва обязан показать, что каждая роль вообще ВОШЛА и
# что-то может: иначе «смотрящему отказали» проходило бы и на сервере, который
# отказывает всем. Поэтому у каждой роли есть строка, которая обязана быть 200.
#
#   qa/admin-roles-check.sh [-bite]
#
# -bite заводит смотрящего с ролью редактора и требует, чтобы стенд УВИДЕЛ
# разницу: не видящий её не заметил бы и настоящей потери прав.
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

VIEWER_ROLE="viewer"
[ -n "$BITE" ] && VIEWER_ROLE="editor"

# Учётки заводятся флагом: сервер их создаёт и выходит.
for spec in "хозяин:пароль-хозяина:owner" "редактор:пароль-редактора:editor" "смотрящий:пароль-смотрящего:$VIEWER_ROLE"; do
  "$W/srv" -content "$W/content" -admin-user "$spec" >>"$W/setup.log" 2>&1 ||
    { echo "не завелась учётка $spec"; tail -3 "$W/setup.log"; exit 1; }
done

probe "$PORT" && { echo "порт занят — замер говорил бы с чужим сервером"; exit 2; }
"$W/srv" -addr "127.0.0.1:$PORT" -content "$W/content" >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 60); do probe "$PORT" && break; sleep 0.2; done
probe "$PORT" || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

login() { # $1 = логин, $2 = пароль, $3 = файл для печенья
  curl -sS -o /dev/null -c "$3" -X POST "$BASE/v1/admin/session/login" \
    -H 'Content-Type: application/json' \
    -d "{\"login\":\"$1\",\"password\":\"$2\"}" -w '%{http_code}'
}
as() { # $1 = печенье, остальное — аргументы curl; печатает код
  local jar="$1"; shift
  curl -sS -o /dev/null -b "$jar" -w '%{http_code}' "$@"
}

for who in хозяин редактор смотрящий; do :; done
code="$(login хозяин пароль-хозяина "$W/owner.jar")";    [ "$code" = "200" ] || { echo "хозяин не вошёл ($code)"; exit 1; }
code="$(login редактор пароль-редактора "$W/editor.jar")"; [ "$code" = "200" ] || { echo "редактор не вошёл ($code)"; exit 1; }
code="$(login смотрящий пароль-смотрящего "$W/viewer.jar")"; [ "$code" = "200" ] || { echo "смотрящий не вошёл ($code)"; exit 1; }

READ="$BASE/v1/admin/orders"
WRITE_ARGS=(-X PUT "$BASE/v1/admin/manifest" -H 'Content-Type: application/json' -d '{"titles":[]}')
PEOPLE_ARGS=(-X POST "$BASE/v1/admin/people" -H 'Content-Type: application/json' -d '{"login":"новичок","password":"пароль-новичка-длинный","role":"viewer"}')

v_read="$(as "$W/viewer.jar" "$READ")"
v_write="$(as "$W/viewer.jar" "${WRITE_ARGS[@]}")"
e_write="$(as "$W/editor.jar" "${WRITE_ARGS[@]}")"
e_people="$(as "$W/editor.jar" "${PEOPLE_ARGS[@]}")"
o_people="$(as "$W/owner.jar" "${PEOPLE_ARGS[@]}")"

echo "  смотрящий читает:        $v_read (ждём 200)"
echo "  смотрящий пишет:         $v_write (ждём отказ)"
echo "  редактор пишет:          $e_write (ждём 200)"
echo "  редактор заводит людей:  $e_people (ждём отказ)"
echo "  хозяин заводит людей:    $o_people (ждём 200)"

if [ -n "$BITE" ]; then
  if [ "$v_write" = "$e_write" ]; then
    echo "  укус: «смотрящий» с правами редактора пишет так же ($v_write) — стенд разницу ловит"
    exit 0
  fi
  echo "  ✗ СТЕНД СЛЕП: роль подменили, а поведение осталось прежним"
  exit 2
fi

# ОТКАЗ ОБЯЗАН БЫТЬ ОТКАЗОМ, А НЕ ОТСУТСТВИЕМ. 404 значит «нет такого пути», и
# засчитывать его за «не пустили» — значит проверять опечатку в адресе вместо
# прав. Поймано первым же прогоном: путь записи был неверный, оба получили 404,
# и у смотрящего это прошло бы как защита.
refused() { [ "$1" = "401" ] || [ "$1" = "403" ]; }

fail=0
# Анти-пустота: каждая роль обязана что-то МОЧЬ, иначе отказы ничего не значат.
[ "$v_read"   = "200" ] || { echo "  ✗ смотрящий не может даже читать — отказы ниже бессмысленны"; fail=1; }
[ "$e_write"  = "200" ] || { echo "  ✗ редактор не может править содержание — это не роль, а запрет"; fail=1; }
[ "$o_people" = "200" ] || { echo "  ✗ хозяин не может завести человека — роль владельца пуста"; fail=1; }
# Собственно разграничение.
refused "$v_write"  || { echo "  ✗ смотрящему ответили $v_write, а ждали 401/403 — это не запрет"; fail=1; }
refused "$e_people" || { echo "  ✗ редактору ответили $e_people, а ждали 401/403 — это не запрет"; fail=1; }

[ "$fail" = "0" ] && { echo "РОЛИ РАЗГРАНИЧЕНЫ"; exit 0; }
echo "РОЛИ НЕ ДЕРЖАТ"; exit 1
