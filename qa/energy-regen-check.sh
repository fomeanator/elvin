#!/usr/bin/env bash
# ЭНЕРГИЯ ВОССТАНАВЛИВАЕТСЯ ПО ЧАСАМ — И ЧАСТЫЙ ОПРОС ЕЁ НЕ ТОРОПИТ И НЕ КРАДЁТ.
#
# Энергия глав — монетизационный шлюз: её нельзя заработать, только ждать или
# купить. Значит ошибка в ритме бьёт по игроку деньгами, в какую бы сторону ни
# промахнулась.
#
# Опасность здесь несимметрична и потому её обычно видят наполовину. «Опрос
# ускоряет» ищут все. А есть вторая, тише и хуже: если начисление ставит якорь
# в «сейчас», то каждый запрос СБРАСЫВАЕТ недосчитанные секунды. Игрок,
# у которого открыт экран с обратным отсчётом, не получает энергию НИКОГДА —
# и жалоба будет звучать как «у меня просто не идёт».
#
# Один опыт ловит обе стороны: тратим до нуля и опрашиваем часто, засекая
# моменты, когда прибавляется единица. Ритм обязан остаться ровным.
#
# Правила восстановления берутся из services/energy.json с горячей
# перезагрузкой — стенд кладёт своё окно в две секунды вместо часов, поэтому
# замер идёт секунды, а не полдня.
#
# ПРОВЕРКА ОБЯЗАНА УМЕТЬ ПАДАТЬ: -bite убирает правила совсем и требует, чтобы
# стенд НЕ нашёл восстановления. Стенд, находящий ритм там, где его нет, врёт и
# в другую сторону.
#
#   qa/energy-regen-check.sh [-bite]
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
BASE="http://127.0.0.1:$PORT"

mkdir -p "$W/content/services"
echo '{"titles":[]}' > "$W/content/manifest.json"
INTERVAL=2
if [ -z "$BITE" ]; then
  cat > "$W/content/services/energy.json" <<EOF
{"energy": {"cap": 3, "interval_seconds": $INTERVAL, "start": 3}}
EOF
fi

# Порт обязан быть свободен ДО старта: иначе замер говорил бы с чужим сервером.
probe "$PORT" && { echo "порт $PORT занят"; exit 2; }
"$W/srv" -addr "127.0.0.1:$PORT" -content "$W/content" -auth-dev >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 60); do probe "$PORT" && break; sleep 0.2; done
probe "$PORT" || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

TOK="$(curl -sS -X POST "$BASE/v1/auth/register" -H 'Content-Type: application/json' \
        -d '{"device_id":"energy-device-00001"}' |
        python3 -c 'import json,sys;print(json.load(sys.stdin)["token"])')"
[ -n "$TOK" ] || { echo "учётка не завелась"; exit 1; }

bal() { curl -sS "$BASE/v1/wallet" -H "Authorization: Bearer $TOK" |
        python3 -c 'import json,sys;print(json.load(sys.stdin)["balances"].get("energy",0))'; }
spend() { curl -sS -o /dev/null -X POST "$BASE/v1/wallet/spend" -H "Authorization: Bearer $TOK" \
            -H 'Content-Type: application/json' \
            -d "{\"currency\":\"energy\",\"amount\":$1,\"reason\":\"глава\"}"; }

start="$(bal)"
echo "  старт нового игрока: $start"
if [ -n "$BITE" ]; then
  [ "$start" = "0" ] || { echo "  ✗ без правил энергия откуда-то взялась ($start)"; exit 2; }
fi

fail=0
if [ -z "$BITE" ]; then
  [ "$start" = "3" ] || { echo "  ✗ стартовое значение не из правил"; fail=1; }
fi

spend 3 >/dev/null 2>&1
echo "  потрачено до: $(bal)"

# ЧАСТЫЙ ОПРОС: каждые 100 мс. Ровно та нагрузка, при которой наивная
# реализация не начислит НИКОГДА.
python3 - "$BASE" "$TOK" "$INTERVAL" "${BITE:-}" <<'PY'
import json, sys, time, urllib.request

base, tok, interval, bite = sys.argv[1], sys.argv[2], int(sys.argv[3]), bool(sys.argv[4])

def energy():
    req = urllib.request.Request(base + "/v1/wallet", headers={"Authorization": "Bearer " + tok})
    with urllib.request.urlopen(req, timeout=5) as r:
        return json.load(r)["balances"].get("energy", 0)

# ЧАСЫ ПУСКАЕТ НЕ ТРАТА, А СЛЕДУЮЩИЙ ЗАПРОС. При трате баланс ещё на потолке,
# якорь припаркован; заводит его первое обращение, увидевшее баланс НИЖЕ
# потолка. Значит отсчёт обязан начинаться тем же запросом, что и часы, — иначе
# первый шаг выйдет короче окна на время запуска замера. Поймано прогоном:
# «1,4 с при окне 2 с», притом что следующие шаги были 1,9 и 2,1.
last = energy()      # этот запрос и заводит часы
t0 = time.time()
seen = {}
deadline = t0 + interval * 4 + 2
while time.time() < deadline:
    b = energy()
    if b > last:
        seen[b] = time.time() - t0
        last = b
    time.sleep(0.1)

if bite:
    if seen:
        print("  ✗ СТЕНД ВРЁТ: без правил нашлось восстановление " + str(seen))
        sys.exit(2)
    print("  укус: правил нет — восстановления нет, как и должно")
    sys.exit(0)

if not seen:
    print("  ✗ ЭНЕРГИЯ НЕ ВОССТАНАВЛИВАЕТСЯ ПРИ ЧАСТОМ ОПРОСЕ — каждый запрос "
          "сбрасывает недосчитанные секунды")
    sys.exit(1)

marks = sorted(seen.items())
print("  моменты прибавки: " + ", ".join("+%d на %.1f с" % (b, t) for b, t in marks))

bad = []
prev = 0.0
for i, (b, t) in enumerate(marks, start=1):
    step = t - prev
    if not (interval * 0.7 <= step <= interval * 1.6):
        bad.append("шаг до +%d занял %.1f с при окне %d с" % (b, step, interval))
    prev = t
if len(marks) < 3:
    bad.append("за %d с пришло лишь %d прибавки — ритм рвётся" % (interval * 4 + 2, len(marks)))
if marks and marks[-1][0] > 3:
    bad.append("перевалило за потолок: %d при потолке 3" % marks[-1][0])

for m in bad:
    print("  ✗ " + m)
sys.exit(1 if bad else 0)
PY
rc=$?
[ "$rc" = "0" ] || fail=1
[ -n "$BITE" ] && exit "$rc"

# Потолок держится: ждём ещё окно и требуем, чтобы не выросло.
python3 -c "import time;time.sleep($INTERVAL + 1)"
top="$(bal)"
echo "  через окно после потолка: $top"
[ "$top" = "3" ] || { echo "  ✗ энергия перевалила потолок ($top)"; fail=1; }

[ "$fail" = "0" ] && { echo "РИТМ РОВНЫЙ, ПОТОЛОК ДЕРЖИТСЯ"; exit 0; }
echo "РИТМ НАРУШЕН"; exit 1
