#!/usr/bin/env bash
# ЧУЖОЙ КОШЕЛЁК НЕ ОТКРЫТЬ, И СЕБЕ НЕ ДОРИСОВАТЬ.
#
# Кошелёк — единственное место в движке, где лежат настоящие деньги игрока:
# купленная валюта, наряды, ключи глав. Ошибка здесь не «неудобно», а «за меня
# потратили» или «мне бесплатно начислили», и обе стороны узнают о ней из
# отчёта по выручке, а не из бага.
#
# Стенд заводит ДВУХ игроков, кладёт деньги одному и пробует добраться до них
# от имени другого — теми способами, которыми это пробуют на самом деле:
#
#   чужой номер в запросе      /v1/wallet?user=<чужой>
#   чужой номер в заголовке    X-User-Id
#   чужой номер в теле         spend/earn с полем user
#   склейка пропуска           номер чужого + секрет своего
#   огрызки пропуска           обрезанный, с пустым секретом
#   поддельная покупка         чек, которого не было
#
#   qa/wallet-isolation-check.sh [-bite]
#
# -bite делает ЗАКОННОЕ начисление и требует, чтобы стенд увидел изменение
# баланса: мерка, не отличающая пришедшие деньги от непришедших, ничего не
# доказывает и в остальных строках.
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
for p in 8141 8143 8145 8147; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

mkdir -p "$W/content"
printf '{"titles":[]}' > "$W/content/manifest.json"
printf '{"gold_100":{"currency":"gold","amount":100,"title":"100","price":"$0.99"}}' \
  > "$W/content/iap-catalog.json"
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
reg() { curl -s -X POST "$B/v1/auth/register" -H 'Content-Type: application/json' \
        -d "{\"device_id\":\"$1\"}"; }
field() { python3 -c "import json,sys;print(json.loads(sys.stdin.read()).get('$1',''))"; }
gold() { # $1 = пропуск → сколько золота видно этим пропуском
  curl -s "$B/v1/wallet" -H "Authorization: Bearer $1" \
  | python3 -c "import json,sys
try: print(int((json.load(sys.stdin).get('balances') or {}).get('gold', 0)))
except Exception: print(-1)"
}

A="$(reg "стенд-игрок-A-0123456789")"; TA="$(printf '%s' "$A" | field token)"; UA="$(printf '%s' "$A" | field user_id)"
Bp="$(reg "стенд-игрок-B-0123456789")"; TB="$(printf '%s' "$Bp" | field token)"; UB="$(printf '%s' "$Bp" | field user_id)"
[ -n "$TA" ] && [ -n "$TB" ] || { echo "игроки не завелись — стенду не на чем стоять"; exit 2; }

curl -s -o /dev/null -X POST "$B/v1/wallet/earn" -H "Authorization: Bearer $TB" \
  -H 'Content-Type: application/json' \
  -d '{"currency":"gold","amount":500,"reason":"стенд","op_id":"b-500"}'
b_before="$(gold "$TB")"
a_before="$(gold "$TA")"
[ "$b_before" = "500" ] || { echo "у второго игрока не появились деньги ($b_before) — проверять нечего"; exit 2; }

bad=""
note() { bad="$bad\n  $1"; }

# 1. Чужой номер в запросе и в заголовке.
seen="$(curl -s "$B/v1/wallet?user=$UB" -H "Authorization: Bearer $TA" \
        | python3 -c "import json,sys;print(int((json.load(sys.stdin).get('balances') or {}).get('gold',0)))")"
[ "$seen" = "0" ] || note "через ?user= видно чужие $seen"
seen="$(curl -s "$B/v1/wallet" -H "Authorization: Bearer $TA" -H "X-User-Id: $UB" \
        | python3 -c "import json,sys;print(int((json.load(sys.stdin).get('balances') or {}).get('gold',0)))")"
[ "$seen" = "0" ] || note "через X-User-Id видно чужие $seen"

# 2. Склейка и огрызки пропуска.
SECA="${TA#*.}"
for probe in "$UB.$SECA" "${TA%?}" "$UB." "$UB" "чужой"; do
  code="$(curl -s -o /dev/null -w '%{http_code}' "$B/v1/wallet" -H "Authorization: Bearer $probe")"
  [ "$code" = "401" ] || note "пропуск «${probe:0:24}…» принят с кодом $code"
done

# 3. Трата и начисление с чужим номером в теле.
curl -s -o /dev/null -X POST "$B/v1/wallet/spend" -H "Authorization: Bearer $TA" \
  -H 'Content-Type: application/json' \
  -d "{\"currency\":\"gold\",\"amount\":100,\"reason\":\"чужое\",\"op_id\":\"steal-1\",\"user\":\"$UB\",\"user_id\":\"$UB\"}"
curl -s -o /dev/null -X POST "$B/v1/wallet/earn" -H "Authorization: Bearer $TA" \
  -H 'Content-Type: application/json' \
  -d "{\"currency\":\"gold\",\"amount\":7,\"reason\":\"своё\",\"op_id\":\"a-7\",\"user\":\"$UB\"}"

# 4. Покупка, которой не было.
for pf in gplay appstore google ""; do
  curl -s -o /dev/null -X POST "$B/v1/iap/verify" -H "Authorization: Bearer $TA" \
    -H 'Content-Type: application/json' \
    -d "{\"sku\":\"gold_100\",\"platform\":\"$pf\",\"receipt\":\"{\\\"orderId\\\":\\\"подделка\\\"}\"}"
done

if [ -n "$BITE" ]; then
  curl -s -o /dev/null -X POST "$B/v1/wallet/earn" -H "Authorization: Bearer $TB" \
    -H 'Content-Type: application/json' \
    -d '{"currency":"gold","amount":11,"reason":"укус","op_id":"bite-11"}'
fi

b_after="$(gold "$TB")"
a_after="$(gold "$TA")"

if [ -n "$BITE" ]; then
  if [ "$b_after" != "$b_before" ]; then
    echo "укус чист: законное начисление видно ($b_before → $b_after) — мерка считает деньги"
    exit 0
  fi
  echo "СТЕНД СЛЕП: деньги пришли, а он не заметил — «чужое не тронуто» ничего не значило бы"
  exit 2
fi

echo "  второй игрок: было $b_before, стало $b_after"
echo "  первый игрок: было $a_before, стало $a_after (законное начисление себе: 7)"

fail=0
[ "$b_after" = "$b_before" ] || { echo "РВЁТСЯ: чужой кошелёк изменился ($b_before → $b_after)"; fail=1; }
[ "$a_after" = "7" ] || { echo "РВЁТСЯ: у первого игрока $a_after вместо 7 — начисление ушло не туда или подделка прошла"; fail=1; }
[ -z "$bad" ] || { echo "РВЁТСЯ: доступ к чужому кошельку:$(printf '%b' "$bad")"; fail=1; }
[ "$fail" = "0" ] || exit 1
echo "держит: чужое не прочитать и не потратить, пропуск не склеить, покупка без чека не проходит"
