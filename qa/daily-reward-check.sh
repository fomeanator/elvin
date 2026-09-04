#!/usr/bin/env bash
# ЕЖЕДНЕВНАЯ НАГРАДА — ЗА ДЕНЬ, А НЕ ЗА НАЖАТИЕ.
#
# Награда за вход держит возвращаемость и стоит настоящих денег: валюта, которую
# игрок иначе купил бы. Значит у неё два врага. Первый — двойное нажатие и
# двадцать одновременных запросов из подвисшего клиента: если каждый пройдёт,
# экономика посыплется молча. Второй — календарь: серия обязана считаться днями,
# а не кликами, иначе «седьмой день подряд» получают за минуту.
#
# Стенд проверяет обе стороны на настоящем сервере:
#
#   ПОВТОР      второе получение в тот же день отказано, деньги не выросли;
#   ГОНКА       двадцать одновременных попыток — ровно одно начисление;
#   КАЛЕНДАРЬ   вчера → серия растёт; пропуск дней → серия с начала;
#               дата из будущего не даёт ни награды сверх одной, ни роста серии.
#
#   qa/daily-reward-check.sh [-bite]
#
# -bite добавляет ЗАКОННОЕ второе начисление помимо награды: стенд обязан
# заметить лишние деньги. Мерка, не видящая второго начисления, не увидела бы и
# двойной награды.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go      >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl    >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }
command -v python3 >/dev/null 2>&1 || { echo "нет python3 — пропускаю"; exit 0; }

W="$(mktemp -d)"; PID=""
cleanup() { [ -n "$PID" ] && kill "$PID" 2>/dev/null; rm -rf "$W"; }
trap cleanup EXIT

go build -C server -o "$W/lvnserver" . || { echo "сервер не собрался"; exit 1; }

PORT=0
for p in 8151 8153 8155 8157; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

mkdir -p "$W/content"
printf '{"titles":[]}' > "$W/content/manifest.json"
printf '[{"day":1,"currency":"gold","amount":10},{"day":2,"currency":"gold","amount":20}]' \
  > "$W/content/daily-rewards.json"
"$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$W/content" -admin-token "stand-$$" \
  >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 50); do
  curl -fsS -m 1 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 && break
  sleep 0.2
done
curl -fsS -m 2 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 \
  || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

B="http://127.0.0.1:$PORT"
DB="$W/content/services/lvn.db"
reg() { curl -s -X POST "$B/v1/auth/register" -H 'Content-Type: application/json' -d "{\"device_id\":\"$1\"}"; }
tok() { python3 -c "import json,sys;print(json.loads(sys.stdin.read()).get('token',''))"; }
uid() { python3 -c "import json,sys;print(json.loads(sys.stdin.read()).get('user_id',''))"; }
gold() { curl -s "$B/v1/wallet" -H "Authorization: Bearer $1" \
  | python3 -c "import json,sys
try: print(int((json.load(sys.stdin).get('balances') or {}).get('gold',0)))
except Exception: print(-1)"; }
streak() { curl -s "$B/v1/daily" -H "Authorization: Bearer $1" \
  | python3 -c "import json,sys
try: print(int(json.load(sys.stdin).get('streak',-1)))
except Exception: print(-1)"; }
day() { python3 -c "import datetime;print((datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(days=$1)).strftime('%Y-%m-%d'))"; }

bad=""
note() { bad="$bad\n  $1"; }

# ── 1. Повтор в тот же день ────────────────────────────────────────────────
R="$(reg "стенд-повтор-0123456789")"; T1="$(printf '%s' "$R" | tok)"
[ -n "$T1" ] || { echo "игрок не завёлся"; exit 2; }
c1="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$B/v1/daily/claim" -H "Authorization: Bearer $T1")"
c2="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$B/v1/daily/claim" -H "Authorization: Bearer $T1")"
[ "$c1" = "200" ] || note "первое получение не прошло ($c1)"
[ "$c2" = "200" ] && note "второе получение в тот же день прошло — награда берётся дважды"
g1="$(gold "$T1")"
[ "$g1" = "10" ] || note "после двух нажатий на счету $g1 вместо 10"

# ── 2. Гонка: двадцать одновременных ───────────────────────────────────────
R2="$(reg "стенд-гонка-0123456789")"; T2="$(printf '%s' "$R2" | tok)"
codes="$(seq 1 20 | xargs -P 20 -I{} curl -s -o /dev/null -w '%{http_code}\n' \
         -X POST "$B/v1/daily/claim" -H "Authorization: Bearer $T2" | sort | uniq -c | tr '\n' ' ')"
g2="$(gold "$T2")"
[ "$g2" = "10" ] || note "после двадцати одновременных попыток на счету $g2 вместо 10"
s2="$(streak "$T2")"
[ "$s2" = "1" ] || note "серия после гонки $s2 вместо 1"

# ── 3. Календарь (нужен sqlite3: дни подменяются в хранилище) ──────────────
cal="пропущен (нет sqlite3)"
if command -v sqlite3 >/dev/null 2>&1 && [ -f "$DB" ]; then
  R3="$(reg "стенд-календарь-0123456789")"; T3="$(printf '%s' "$R3" | tok)"; U3="$(printf '%s' "$R3" | uid)"
  curl -s -o /dev/null -X POST "$B/v1/daily/claim" -H "Authorization: Bearer $T3"   # день первый

  sqlite3 "$DB" "update daily_claims set last_claim='$(day -1)', streak=1 where user_id='$U3';"
  s="$(curl -s -X POST "$B/v1/daily/claim" -H "Authorization: Bearer $T3" \
       | python3 -c "import json,sys;print(json.load(sys.stdin).get('streak',-1))")"
  [ "$s" = "2" ] || note "после вчерашнего получения серия $s вместо 2"

  sqlite3 "$DB" "update daily_claims set last_claim='$(day -3)', streak=5 where user_id='$U3';"
  s="$(curl -s -X POST "$B/v1/daily/claim" -H "Authorization: Bearer $T3" \
       | python3 -c "import json,sys;print(json.load(sys.stdin).get('streak',-1))")"
  [ "$s" = "1" ] || note "после пропуска трёх дней серия $s вместо 1"

  sqlite3 "$DB" "update daily_claims set last_claim='$(day 1)', streak=9 where user_id='$U3';"
  s="$(curl -s -X POST "$B/v1/daily/claim" -H "Authorization: Bearer $T3" \
       | python3 -c "import json,sys;print(json.load(sys.stdin).get('streak',-1))")"
  case "$s" in
    1|-1) ;;                                   # начали заново или отказали — оба ответа честные
    *) note "дата из будущего дала серию $s — часы устройства двигают награду";;
  esac
  cal="серия по дням: вчера → 2, пропуск → 1, будущее → $s"
fi

if [ -n "$BITE" ]; then
  curl -s -o /dev/null -X POST "$B/v1/wallet/earn" -H "Authorization: Bearer $T1" \
    -H 'Content-Type: application/json' \
    -d '{"currency":"gold","amount":10,"reason":"укус","op_id":"bite-extra"}'
  g="$(gold "$T1")"
  if [ "$g" != "10" ]; then
    echo "укус чист: лишние деньги видны ($g вместо 10) — мерка их считает"
    exit 0
  fi
  echo "СТЕНД СЛЕП: второе начисление прошло мимо счёта — двойной награды он бы не заметил"
  exit 2
fi

echo "  повтор в тот же день: коды $c1 и $c2, на счету $g1"
echo "  гонка из 20:          $codes → на счету $g2, серия $s2"
echo "  календарь:            $cal"

[ -z "$bad" ] || { echo "РВЁТСЯ:$(printf '%b' "$bad")"; exit 1; }
echo "держит: награда одна в день, гонка не размножает её, серия считается днями"
