#!/usr/bin/env bash
# НАГРАДА ЗА РЕКЛАМУ: ЗАРЯДЫ ДЕРЖАТ, И ЧАСТЫЕ ПОПЫТКИ ИХ НЕ ОТОДВИГАЮТ.
#
# У рекламы нет чека, который можно сверить: клиент просто говорит «я посмотрел».
# Значит защита здесь не идемпотентность, а ОГРАНИЧЕНИЕ — заряды на ближайшие
# минуты и дневной потолок на сутки. Проверять надо оба и, главное, их стык.
#
# ОБЕЩАНИЕ С НАЗВАННЫМ СПОСОБОМ СЛОМАТЬСЯ. В самом коде записано: «отсчёт
# перезарядки идёт от ПЕРВОГО показа в цикле, а не от последнего, иначе частые
# просмотры отодвигали бы восстановление бесконечно». Это ровно тот класс, что
# уже ловился у энергии: наивная реализация ведёт отсчёт от последнего события,
# и игрок, часто жмущий кнопку, не дожидается ничего НИКОГДА — а жалоба звучит
# как «у меня кнопка не оживает».
#
# Стенд бьёт по кнопке каждые 100 мс — ровно та нагрузка, при которой наивная
# реализация не восстановит заряды никогда.
#
# ПРОВЕРКА ОБЯЗАНА УМЕТЬ ПАДАТЬ: -bite ставит дневной потолок в единицу и
# требует, чтобы стенд УВИДЕЛ отказ по потолку. Стенд, не замечающий предела,
# отрапортовал бы «заряды держат» на сервере, раздающем без счёта.
#
#   qa/ads-charges-check.sh [-bite]
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

CHARGES=2; RECHARGE=3; CAP=5
[ -n "$BITE" ] && CAP=1
mkdir -p "$W/content"; echo '{"titles":[]}' > "$W/content/manifest.json"
cat > "$W/content/ads.json" <<EOF
{"rewarded": {"currency": "gold", "amount": 10, "daily_cap": $CAP,
              "charges": $CHARGES, "recharge_sec": $RECHARGE}}
EOF

probe "$PORT" && { echo "порт занят — замер говорил бы с чужим сервером"; exit 2; }
"$W/srv" -addr "127.0.0.1:$PORT" -content "$W/content" -auth-dev >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 60); do probe "$PORT" && break; sleep 0.2; done
probe "$PORT" || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

TOK="$(curl -sS -X POST "$BASE/v1/auth/register" -H 'Content-Type: application/json' \
        -d '{"device_id":"ads-device-00000001"}' |
        python3 -c 'import json,sys;print(json.load(sys.stdin)["token"])')"
[ -n "$TOK" ] || { echo "учётка не завелась"; exit 1; }

python3 - "$BASE" "$TOK" "$CHARGES" "$RECHARGE" "$CAP" "${BITE:-}" <<'PY'
import json, sys, time, urllib.request, urllib.error

base, tok = sys.argv[1], sys.argv[2]
charges, recharge, cap = int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5])
bite = bool(sys.argv[6])

def claim():
    req = urllib.request.Request(base + "/v1/ads/reward", method="POST",
                                 data=json.dumps({"placement": "rewarded"}).encode(),
                                 headers={"Authorization": "Bearer " + tok,
                                          "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            return r.status, json.load(r)
    except urllib.error.HTTPError as e:
        try:    return e.code, json.load(e)
        except Exception: return e.code, {}

def gold():
    req = urllib.request.Request(base + "/v1/wallet", headers={"Authorization": "Bearer " + tok})
    with urllib.request.urlopen(req, timeout=5) as r:
        return json.load(r)["balances"].get("gold", 0)

bad = []

# ── 1. Заряды кончаются ровно на объявленном числе ─────────────────────────
granted = 0
for _ in range(charges + 3):
    code, body = claim()
    if code == 200: granted += 1
    elif body.get("error") == "recharging": break
    elif body.get("error") == "daily_cap": break
print("  подряд выдано: %d (объявлено зарядов %d)" % (granted, charges))

if bite:
    # Потолок в единицу: стенд ОБЯЗАН упереться именно в него.
    code, body = claim()
    if body.get("error") != "daily_cap":
        print("  ✗ СТЕНД СЛЕП: при потолке 1 отказа по потолку не увидел (%s)" % body)
        sys.exit(2)
    print("  укус: потолок в 1 виден — стенд предел замечает")
    sys.exit(0)

if granted != charges:
    bad.append("выдано %d при объявленных %d зарядах" % (granted, charges))

# ── 2. ЧАСТЫЕ ПОПЫТКИ НЕ ОТОДВИГАЮТ ПЕРЕЗАРЯДКУ ────────────────────────────
# Бьём каждые 100 мс. Если отсчёт вести от ПОСЛЕДНЕЙ попытки, восстановление
# не наступит никогда.
t0 = time.time()
back_at = None
while time.time() - t0 < recharge * 2 + 2:
    code, _ = claim()
    if code == 200:
        back_at = time.time() - t0
        break
    time.sleep(0.1)

if back_at is None:
    bad.append("за %d с непрерывных попыток заряд не вернулся — отсчёт идёт от последней "
               "попытки, и кнопка не оживёт никогда" % (recharge * 2 + 2))
else:
    print("  заряд вернулся через %.1f с при цикле %d с (бьём каждые 100 мс)" % (back_at, recharge))
    if back_at > recharge * 1.8:
        bad.append("восстановление заняло %.1f с при цикле %d — частые попытки его отодвигают"
                   % (back_at, recharge))

# ── 3. Дневной потолок держится ────────────────────────────────────────────
# ДО ПОТОЛКА НАДО ДОЖИТЬ, А НЕ ДОСТУЧАТЬСЯ. Заряды пускают лишь `charges`
# показов за цикл, поэтому до дневного предела в `cap` нужно примерно
# cap/charges циклов. Первая редакция отводила полторы секунды на девять — и
# отрапортовала «потолок не сработал» там, где до него просто не дошли.
# Ограничение, названное сломанным по нетерпению стенда, — та же ложная
# тревога, что уже случалась этой ночью с кошельком.
need = -(-cap // max(charges, 1))          # сколько циклов нужно, с округлением вверх
deadline = time.time() + recharge * (need + 1) + 6
seen_cap = False
while time.time() < deadline:
    code, body = claim()
    if body.get("error") == "daily_cap":
        seen_cap = True
        break
    time.sleep(0.2)
print("  дневной потолок сработал: %s" % ("да" if seen_cap else "НЕТ"))
if not seen_cap:
    bad.append("за %.0f с потолок в %d так и не сработал" % (recharge * (need + 1) + 6, cap))

total = gold()
print("  начислено всего: %d золота при потолке %d × 10" % (total, cap))
if total > cap * 10:
    bad.append("выдано БОЛЬШЕ дневного потолка: %d при пределе %d" % (total, cap * 10))

for b in bad: print("  ✗ " + b)
sys.exit(1 if bad else 0)
PY
rc=$?
# Укус подводит итог сам: общий баннер успеха здесь означал бы не то, что
# проверено, — и читался бы как «ограничения держат», хотя проверялась
# честность стенда.
[ -n "$BITE" ] && exit "$rc"
[ "$rc" = "0" ] && { echo "ЗАРЯДЫ ДЕРЖАТ, ПОТОЛОК ДЕРЖИТ"; exit 0; }
[ "$rc" = "2" ] && exit 2
echo "ОГРАНИЧЕНИЯ РЕКЛАМЫ НЕ РАБОТАЮТ"; exit 1
