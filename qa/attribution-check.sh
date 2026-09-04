#!/usr/bin/env bash
# ПОЛУЧАТЕЛЯ ВЫПЛАТЫ НАЗЫВАЕТ СЕРВЕР, А НЕ КЛИЕНТ.
#
# Доверие здесь разделено осознанно, и в самом коде это записано так: клиент
# говорит, В КАКОМ ТИТУЛЕ он играет (этого не знает больше никто), сервер
# говорит, ЧЕЙ это титул. Вторая половина — деньги автора, и клиент не должен
# уметь назвать своим плательщиком кого угодно.
#
# Проверяется поэтому не «есть ли поле author», а три разных вопроса:
#
#   ПОДМЕНА   клиент присылает в теле СВОЙ author — он обязан быть выброшен,
#             а в журнале обязан оказаться автор из манифеста;
#   НЕЗНАКОМЫЙ титул без объявленного автора атрибутируется ПУСТЫМ автором.
#             Пустота — честный ответ; догадка была бы чужими деньгами;
#   ГОРЯЧАЯ СМЕНА автор титула в манифесте меняется живьём (панель правит его
#             на ходу), и следующая трата обязана уехать НОВОМУ автору.
#
# ПОЧЕМУ ЗДЕСЬ ЛЕГКО ОБМАНУТЬСЯ. Если бы сервер писал в журнал пустого автора
# ВСЕГДА, проверка подмены прошла бы сама собой: подставленного имени нет,
# значит «держит». Поэтому стенд сперва обязан УВИДЕТЬ настоящего автора —
# и только потом судить о подмене.
#
#   qa/attribution-check.sh [-bite]
#
# -bite убирает автора из манифеста и требует, чтобы стенд отчитался ПУСТЫМ:
# стенд, который всегда видит имя, не заметил бы и его пропажи.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go   >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }

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
BASE="http://127.0.0.1:$PORT"; ADMIN="attr-$$"

mkdir -p "$W/content"
AUTHOR1="автор-первый"
[ -n "$BITE" ] && AUTHOR1=""
cat > "$W/content/manifest.json" <<EOF
{"titles": [{"id": "titleA", "author": "$AUTHOR1"},
            {"id": "titleB", "author": "автор-второй"}]}
EOF

probe "$PORT" && { echo "порт занят — замер говорил бы с чужим сервером"; exit 2; }
"$W/srv" -addr "127.0.0.1:$PORT" -content "$W/content" -auth-dev -admin-token "$ADMIN" \
  >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 60); do probe "$PORT" && break; sleep 0.2; done
probe "$PORT" || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

TOK="$(curl -sS -X POST "$BASE/v1/auth/register" -H 'Content-Type: application/json' \
        -d '{"device_id":"attr-device-0000001"}' |
        python3 -c 'import json,sys;print(json.load(sys.stdin)["token"])')"
[ -n "$TOK" ] || { echo "учётка не завелась"; exit 1; }

api() { curl -sS -o /dev/null -X POST "$BASE/v1/wallet/$1" -H "Authorization: Bearer $TOK" \
          -H 'Content-Type: application/json' -d "$2"; }
# author в журнале виден только у трат С АРТИКУЛОМ — их и шлём.
spend() { api spend "$2"; }
orders() { curl -sS "$BASE/v1/admin/orders" -H "Authorization: Bearer $ADMIN"; }

api earn '{"currency":"gold","amount":1000,"reason":"стенд"}'

# 1. ПОДМЕНА: клиент называет себя получателем прямо в теле запроса.
spend x '{"currency":"gold","amount":10,"reason":"покупка","sku":"item.one","title":"titleA","author":"злоумышленник"}'
# 2. Незнакомый титул.
spend x '{"currency":"gold","amount":10,"reason":"покупка","sku":"item.two","title":"нет-такого-титула"}'

python3 - "$W/orders1.json" <<'PY' > /dev/null
PY
orders > "$W/orders1.json"

# 3. ГОРЯЧАЯ СМЕНА автора. Индекс перечитывает манифест по mtime/size с полом
#    в 2 секунды — ждём заведомо дольше, иначе замерили бы кэш, а не смену.
cat > "$W/content/manifest.json" <<'EOF'
{"titles": [{"id": "titleA", "author": "автор-третий"},
            {"id": "titleB", "author": "автор-второй"}]}
EOF
python3 -c "import time; time.sleep(3)"
spend x '{"currency":"gold","amount":10,"reason":"покупка","sku":"item.three","title":"titleA"}'
orders > "$W/orders2.json"

python3 - "$W/orders1.json" "$W/orders2.json" "${BITE:-}" <<'PY'
import json, sys
first, second, bite = sys.argv[1], sys.argv[2], bool(sys.argv[3])

def by_sku(path):
    d = json.load(open(path, encoding="utf-8"))
    return {o.get("sku"): o for o in d.get("orders") or []}

o1, o2 = by_sku(first), by_sku(second)
bad = []

one = o1.get("item.one")
if one is None:
    print("  ✗ трата не попала в журнал — мерить нечего"); sys.exit(1)
author_one = one.get("author", "")
print("  титул с объявленным автором → в журнале: %r" % author_one)

if bite:
    if author_one != "":
        print("  ✗ СТЕНД ВРЁТ: автора в манифесте нет, а в журнале %r" % author_one)
        sys.exit(2)
    print("  укус: автора нет — журнал честно пуст, стенд разницу видит")
    sys.exit(0)

# Сперва анти-пустота: стенд обязан УВИДЕТЬ настоящего автора, иначе проверка
# подмены проходит сама собой.
if author_one != "автор-первый":
    bad.append("вместо автора из манифеста записано %r — проверка подмены "
               "была бы бессмысленной" % author_one)
if author_one == "злоумышленник":
    bad.append("КЛИЕНТ НАЗНАЧИЛ ПОЛУЧАТЕЛЯ САМ — поле author из тела запроса дошло до журнала")

two = o2.get("item.two") or o1.get("item.two")
if two is None:
    bad.append("трата по незнакомому титулу не попала в журнал")
else:
    a2 = two.get("author", "")
    print("  незнакомый титул → в журнале: %r (ждём пусто)" % a2)
    if a2 != "":
        bad.append("незнакомому титулу придуман автор %r — это чужие деньги" % a2)

three = o2.get("item.three")
if three is None:
    bad.append("трата после смены автора не попала в журнал")
else:
    a3 = three.get("author", "")
    print("  после смены автора в манифесте → в журнале: %r" % a3)
    if a3 != "автор-третий":
        bad.append("смена автора не доехала: в журнале %r вместо нового" % a3)

for b in bad: print("  ✗ " + b)
sys.exit(1 if bad else 0)
PY
rc=$?
[ -n "$BITE" ] && exit "$rc"
[ "$rc" = "0" ] && { echo "ПОЛУЧАТЕЛЯ НАЗЫВАЕТ СЕРВЕР"; exit 0; }
echo "АТРИБУЦИЯ НЕ ДЕРЖИТ"; exit 1
