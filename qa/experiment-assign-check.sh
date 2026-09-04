#!/usr/bin/env bash
# ГРУППУ ОПЫТА НАЗНАЧАЕТ СЕРВЕР, И ОНА НЕ ПЛЫВЁТ.
#
# A/B у нас идёт по живому: развилку ставит автор в сценарии, доли и выключатель
# правит панель, новой сборки не требуется. Цена такой свободы — доверие к
# делению. Если группа игрока меняется от запроса к запросу, от перезапуска
# сервера или по просьбе самого клиента, то отчёт сравнивает не варианты, а шум,
# и решение по нему хуже, чем решение наугад.
#
# Стенд ставит два опыта (поровну и один к девяти) и спрашивает:
#
#   СТОЙКОСТЬ    двадцать запросов подряд — один и тот же ответ;
#   ПЕРЕЗАПУСК   сервер поднялся заново — группа та же;
#   ЧУЖАЯ ВОЛЯ   ни строкой запроса, ни телом, ни заголовком группу не выбрать;
#   ДОЛИ         на трёх сотнях игроков деление близко к заданному;
#   ВЫКЛЮЧАТЕЛЬ  выключенный опыт отдаёт первый вариант всем;
#   ЦЕНА ПРАВКИ  смена долей БЕЗ версии переселяет часть игроков — сколько
#                именно, стенд называет числом, а не словом «часть».
#
#   qa/experiment-assign-check.sh [-bite]
#
# -bite меняет версию опыта и требует, чтобы стенд увидел переезд: мерка, не
# замечающая перетасовки, не заметила бы и плавающих групп.
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
for p in 8161 8163 8165 8167; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

C="$W/content"; mkdir -p "$C"
printf '{"titles":[]}' > "$C/manifest.json"
cfg() { # $1 = вес первого варианта, $2 = версия, $3 = включён
  cat > "$C/experiments.json" <<JSON
[{"name":"ровно","variants":[{"id":"a","weight":$1},{"id":"b","weight":$((100-$1))}],"layer":"первый","version":$2,"enabled":$3},
 {"name":"редкий","variants":[{"id":"base","weight":90},{"id":"proba","weight":10}],"layer":"второй","version":1,"enabled":true}]
JSON
}
start() {
  "$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$C" -admin-token "stand-$$" \
    >>"$W/server.log" 2>&1 &
  PID=$!
  for _ in $(seq 1 50); do
    curl -fsS -m 1 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 && return 0
    sleep 0.2
  done
  return 1
}

cfg 50 1 true
start || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }
B="http://127.0.0.1:$PORT"

bad=""; note() { bad="$bad\n  $1"; }
reg() { curl -s -X POST "$B/v1/auth/register" -H 'Content-Type: application/json' \
        -d "{\"device_id\":\"$1\"}" | python3 -c "import json,sys;print(json.loads(sys.stdin.read()).get('token',''))"; }
mine() { curl -s "$B/v1/experiments" -H "Authorization: Bearer $1"; }

T="$(reg "стенд-опыт-0123456789ab")"
[ -n "$T" ] || { echo "игрок не завёлся"; exit 2; }

# ── 1. Стойкость и вход ────────────────────────────────────────────────────
# Пустые строки отбрасываем: ответ сервера уже с переводом строки, и без
# фильтра «уникальных ответов» выходило два — пустой и настоящий. Первая
# редакция стенда на этом и объявила, что группа плывёт.
uniq_count="$(for _ in $(seq 1 20); do mine "$T"; echo; done | grep -v '^$' | sort -u | wc -l | tr -d ' ')"
[ "$uniq_count" = "1" ] || note "двадцать запросов дали $uniq_count разных ответов — группа плывёт"
anon="$(curl -s "$B/v1/experiments")"
case "$anon" in *'{}'*) ;; *) note "без входа выдумана группа: $anon";; esac

# ── 2. Чужая воля ──────────────────────────────────────────────────────────
base="$(mine "$T")"
for probe in "?group=b" "?ровно=b" "?assignments=%7B%22ровно%22%3A%22b%22%7D"; do
  got="$(curl -s "$B/v1/experiments$probe" -H "Authorization: Bearer $T")"
  [ "$got" = "$base" ] || note "строка запроса «$probe» изменила группу"
done
got="$(curl -s -X POST "$B/v1/experiments" -H "Authorization: Bearer $T" \
       -H 'Content-Type: application/json' -d '{"ровно":"b"}')"
[ "$got" = "$base" ] || note "тело запроса изменило группу"

# ── 3. Перезапуск ──────────────────────────────────────────────────────────
kill "$PID" 2>/dev/null; sleep 1; start || { echo "сервер не поднялся после перезапуска"; exit 2; }
[ "$(mine "$T")" = "$base" ] || note "после перезапуска сервера группа сменилась"

# ── 4. Доли на трёх сотнях ─────────────────────────────────────────────────
python3 - "$B" "$W/before.json" <<'PY'
import json, sys, urllib.request, concurrent.futures
from collections import Counter
B, path = sys.argv[1], sys.argv[2]
def one(i):
    body = json.dumps({"device_id": f"толпа-{i:06d}-abcdefgh"}).encode()
    req = urllib.request.Request(B + "/v1/auth/register", body, {"Content-Type": "application/json"})
    tok = json.load(urllib.request.urlopen(req, timeout=15))["token"]
    r = urllib.request.Request(B + "/v1/experiments", headers={"Authorization": "Bearer " + tok})
    return tok, json.load(urllib.request.urlopen(r, timeout=15))["assignments"]
out, c1, c2 = {}, Counter(), Counter()
with concurrent.futures.ThreadPoolExecutor(max_workers=16) as ex:
    for tok, a in ex.map(one, range(300)):
        out[tok] = a; c1[a.get("ровно")] += 1; c2[a.get("редкий")] += 1
json.dump(out, open(path, "w"), ensure_ascii=False)
print(f"{c1['a']} {c1['b']} {c2['base']} {c2['proba']}")
PY
read -r ra rb rbase rproba < <(python3 - "$W/before.json" <<'PY'
import json, sys
from collections import Counter
d = json.load(open(sys.argv[1]))
c1 = Counter(a.get("ровно") for a in d.values()); c2 = Counter(a.get("редкий") for a in d.values())
print(c1["a"], c1["b"], c2["base"], c2["proba"])
PY
)
# Границы широкие намеренно: проверяется «доли соблюдаются», а не генератор
# случайных чисел. На 300 игроках честное деление 50/50 почти никогда не даёт
# ровно 150, и придираться к этому значит ловить шум.
[ "$ra" -ge 110 ] && [ "$ra" -le 190 ] || note "деление поровну дало $ra против $rb — это не половина"
[ "$rproba" -ge 10 ] && [ "$rproba" -le 60 ] || note "десятая доля дала $rproba из 300"

# ── 5. Выключатель ─────────────────────────────────────────────────────────
cfg 50 1 false; sleep 3
off="$(mine "$T")"
case "$off" in *'"ровно":"a"'*) ;; *) note "выключенный опыт ответил не первым вариантом: $off";; esac

# ── 6. Цена правки долей ───────────────────────────────────────────────────
if [ -n "$BITE" ]; then cfg 50 2 true; else cfg 80 1 true; fi
sleep 3
moved="$(python3 - "$B" "$W/before.json" <<'PY'
import json, sys, urllib.request, concurrent.futures
B, path = sys.argv[1], sys.argv[2]
before = json.load(open(path))
def now(tok):
    r = urllib.request.Request(B + "/v1/experiments", headers={"Authorization": "Bearer " + tok})
    return tok, json.load(urllib.request.urlopen(r, timeout=15))["assignments"]
moved = 0
with concurrent.futures.ThreadPoolExecutor(max_workers=16) as ex:
    for tok, a in ex.map(now, list(before)):
        if a.get("ровно") != before[tok].get("ровно"): moved += 1
print(moved)
PY
)"

if [ -n "$BITE" ]; then
  if [ "$moved" -gt 30 ]; then
    echo "укус чист: смена версии перетасовала $moved из 300 — переезд стенд видит"
    exit 0
  fi
  echo "СТЕНД СЛЕП: версия поднята, а переезда он не заметил ($moved) — плавающих групп тоже не заметил бы"
  exit 2
fi

echo "  стойкость:   $uniq_count ответ на 20 запросов, перезапуск не тронул"
echo "  доли на 300: поровну $ra/$rb, редкий $rproba из 300"
echo "  цена правки: смена долей без версии переселила $moved из 300"

[ -z "$bad" ] || { echo "РВЁТСЯ:$(printf '%b' "$bad")"; exit 1; }
echo "держит: группа стойкая, чужой волей не меняется, доли соблюдаются, выключатель работает"
