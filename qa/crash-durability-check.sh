#!/usr/bin/env bash
# ЖЁСТКОЕ ВЫКЛЮЧЕНИЕ НЕ РВЁТ КОШЕЛЁК.
#
# Сервер не всегда останавливают вежливо. Кончается память, перезагружают
# хостинг, роняет OOM-killer, кто-то делает kill -9 в разгар распродажи. В этот
# момент идут ДЕНЬГИ игроков: начисления, траты, покупки — и каждая из них это
# несколько записей в базе (баланс, метка идемпотентности, строка журнала).
#
# Обещание простое и жёсткое: после подъёма ни одной ПОЛУОПЕРАЦИИ. Либо деньги
# списаны и записаны в журнал, либо не случилось ничего. Промежуточного «списали,
# но не записали» быть не должно: по журналу считают выплаты авторам, и
# разошедшийся баланс потом не восстановить ничем.
#
# Стенд гоняет настоящий сервер и убивает его НЕВЕЖЛИВО (SIGKILL) прямо посреди
# потока операций, потом поднимает заново на том же каталоге:
#
#   СОГЛАСОВАННОСТЬ  баланс равен сумме журнала — до копейки;
#   ВЫЖИВШИЕ         операции, на которые клиент получил ответ, на месте;
#   БАЗА ЖИВА        после подъёма сервер отвечает и принимает новые операции;
#   ПОВТОР           клиент, не получивший ответа, повторяет с той же меткой —
#                    и деньги не удваиваются.
#
#   qa/crash-durability-check.sh [-bite]
#
# -bite сверяет баланс с ЗАВЕДОМО НЕВЕРНОЙ суммой: стенд обязан покраснеть.
# Мерка, которая не отличает сходящийся баланс от расходящегося, ничего не
# проверяет.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go      >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl    >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }
command -v python3 >/dev/null 2>&1 || { echo "нет python3 — пропускаю"; exit 0; }

W="$(mktemp -d)"; PID=""
cleanup() { [ -n "$PID" ] && kill -9 "$PID" 2>/dev/null; rm -rf "$W"; }
trap cleanup EXIT

go build -C server -o "$W/lvnserver" . || { echo "сервер не собрался"; exit 1; }

PORT=0
for p in 8251 8253 8255 8257; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

C="$W/content"; mkdir -p "$C"
printf '{"titles":[]}' > "$C/manifest.json"
TOKEN="stand-$$"
B="http://127.0.0.1:$PORT"

start() {
  "$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$C" -admin-token "$TOKEN" >>"$W/server.log" 2>&1 &
  PID=$!
  # Снимаем задание с учёта оболочки: иначе она сама печатает «Killed: 9»
  # в общий вывод, и цикл прогона принимает это сообщение за вердикт стенда.
  disown "$PID" 2>/dev/null || true
  for _ in $(seq 1 60); do
    curl -fsS -m 1 "$B/healthz" >/dev/null 2>&1 && return 0
    sleep 0.2
  done
  return 1
}

start || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

bad=""; note() { bad="$bad\n  $1"; }
field() { python3 -c "import json,sys;print(json.loads(sys.stdin.read()).get('$1',''))"; }

REG="$(curl -s -X POST "$B/v1/auth/register" -H 'Content-Type: application/json' \
       -d '{"device_id":"стенд-краха-0123456789ab"}')"
TOK="$(printf '%s' "$REG" | field token)"
[ -n "$TOK" ] || { echo "игрок не завёлся"; exit 2; }

# ── Поток операций, который прервёт SIGKILL ────────────────────────────────
# Каждая операция со своей меткой: клиент, не получивший ответа, повторит её
# позже — и повтор не должен удвоить деньги.
ops=400
acked="$W/acked.txt"; : > "$acked"
(
  for i in $(seq 1 $ops); do
    code="$(curl -s -o /dev/null -m 2 -w '%{http_code}' -X POST "$B/v1/wallet/earn" \
      -H "Authorization: Bearer $TOK" -H 'Content-Type: application/json' \
      -d "{\"currency\":\"золото\",\"amount\":10,\"reason\":\"стенд\",\"op_id\":\"кр-$i\"}" 2>/dev/null)"
    [ "$code" = "200" ] && echo "кр-$i" >> "$acked"
  done
) &
FLOW=$!

# Убиваем НЕВЕЖЛИВО В РАЗГАРЕ: не даём дописать, не даём закрыть базу. Число
# операций взято с запасом, чтобы на быстрой машине удар пришёлся В СЕРЕДИНУ
# потока, а не после него: замер, где сервер умер уже на тишине, ничего не
# проверяет — и стенд об этом скажет вслух.
sleep 0.9
kill -9 "$PID" 2>/dev/null
killed_at="$(wc -l < "$acked" | tr -d ' ')"
[ "$killed_at" -lt "$ops" ] || note "удар пришёлся ПОСЛЕ потока ($killed_at из $ops) — крах не пересёкся с записью, замер пустой"
wait $FLOW 2>/dev/null
PID=""

# ── Подъём на том же каталоге ──────────────────────────────────────────────
start || { echo "после SIGKILL сервер НЕ ПОДНЯЛСЯ:"; tail -8 "$W/server.log"; exit 1; }

wallet="$(curl -s "$B/v1/wallet" -H "Authorization: Bearer $TOK")"
balance="$(printf '%s' "$wallet" | python3 -c "
import json,sys
try: print(int((json.load(sys.stdin).get('balances') or {}).get('золото',0)))
except Exception: print(-1)")"
# ЖУРНАЛ БЕРЁМ ИЗ БАЗЫ, А НЕ ИЗ ОТВЕТА. Ответ игроку несёт последние сто
# записей (walletHistoryView) — на ста двадцати операциях сверка «баланс равен
# журналу» показала бы расхождение в двести монет, которого нет. Первая
# редакция стенда на этом и объявила полуоперацию; проверять надо полный
# журнал, а он живёт в базе.
DB="$C/services/lvn.db"
if command -v sqlite3 >/dev/null 2>&1 && [ -f "$DB" ]; then
  ledger="$(sqlite3 "$DB" "select coalesce(sum(amount),0) from wallet_ledger where currency='золото';")"
  ledger_src="база"
else
  ledger="$(printf '%s' "$wallet" | python3 -c "
import json,sys
try:
    d=json.load(sys.stdin)
    print(sum(int(e.get('amount',0)) for e in (d.get('history') or []) if e.get('currency')=='золото'))
except Exception: print(-1)")"
  ledger_src="ответ (последние сто)"
fi
acked_n="$(wc -l < "$acked" | tr -d ' ')"

[ -n "$BITE" ] && balance=$((balance + 10))   # укус: заведомо неверная сумма

# ── 1. Согласованность: баланс равен сумме журнала ─────────────────────────
if [ "$ledger_src" = "база" ]; then
  [ "$balance" = "$ledger" ] || note "баланс $balance не сходится с журналом $ledger — есть полуоперация"
else
  # Без sqlite3 полный журнал не прочитать: сверяем только то, что видно, и
  # говорим об этом вслух — молчаливо ослабленная проверка хуже пропущенной.
  [ "$ledger" -le "$balance" ] || note "журнал ($ledger) больше баланса ($balance) — деньги записаны, но не начислены"
fi

# ── 2. Подтверждённое не пропало ───────────────────────────────────────────
# Журнал показывает последние сто записей, поэтому сверяем по балансу: каждая
# подтверждённая операция это ровно десять монет.
want=$((acked_n * 10))
[ "$balance" -ge "$want" ] || note "подтверждено $acked_n операций ($want монет), а на счету $balance — ответ был ложью"

# ── 3. База жива: новая операция проходит ──────────────────────────────────
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$B/v1/wallet/earn" \
  -H "Authorization: Bearer $TOK" -H 'Content-Type: application/json' \
  -d '{"currency":"золото","amount":7,"reason":"после подъёма","op_id":"после-краха"}')"
[ "$code" = "200" ] || note "после подъёма сервер не принимает операции ($code)"

# ── 4. Повтор с той же меткой не удваивает ─────────────────────────────────
before="$(curl -s "$B/v1/wallet" -H "Authorization: Bearer $TOK" | python3 -c "
import json,sys;print(int((json.load(sys.stdin).get('balances') or {}).get('золото',0)))")"
for i in $(seq 1 $ops); do
  curl -s -o /dev/null -m 2 -X POST "$B/v1/wallet/earn" \
    -H "Authorization: Bearer $TOK" -H 'Content-Type: application/json' \
    -d "{\"currency\":\"золото\",\"amount\":10,\"reason\":\"повтор\",\"op_id\":\"кр-$i\"}" 2>/dev/null
done
after="$(curl -s "$B/v1/wallet" -H "Authorization: Bearer $TOK" | python3 -c "
import json,sys;print(int((json.load(sys.stdin).get('balances') or {}).get('золото',0)))")"
# Повтор ЗАКОННО дописывает те операции, которые не успели пройти до SIGKILL:
# всего их ops, значит потолок известен точно.
top=$((ops * 10 + 7))
[ "$after" -le "$top" ] || note "повтор удвоил деньги: было $before, стало $after при потолке $top"

if [ -n "$BITE" ]; then
  if [ -n "$bad" ]; then
    echo "укус чист: подменённую сумму стенд поймал$(printf '%b' "$bad")"
    exit 0
  fi
  echo "СТЕНД СЛЕП: баланс подменён, а сверка этого не заметила"
  exit 2
fi

echo "  до SIGKILL подтверждено: $killed_at операций, всего подтверждено $acked_n"
echo "  после подъёма: баланс $balance, журнал $ledger (источник: $ledger_src), новая операция $code"
echo "  повтор всех $ops меток: $before → $after (потолок $top)"

[ -z "$bad" ] || { echo "РВЁТСЯ:$(printf '%b' "$bad")"; exit 1; }
echo "держит: полуопераций нет, подтверждённое на месте, база жива, повтор не удваивает"
