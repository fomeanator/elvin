#!/usr/bin/env bash
# ПОКА АВТОР РАБОТАЕТ, ИГРОКИ ИГРАЮТ.
#
# Студия и игра живут на одном сервере. Автор в это же время делает самые
# тяжёлые вещи, какие вообще умеет движок: выгружает готовый Unity-проект
# (архив со всем контентом), перезаливает арт целиком (после чего сервер обязан
# пересчитать хэши всего дерева) и публикует главы пачкой. Каждая из них
# читает диск, жмёт, считает sha256 — и всё это на тех же ядрах, на которых
# сервер отдаёт главы играющим.
#
# Обещание простое: player не должен этого заметить. Стенд меряет время ответа
# игроку в покое и во время каждой из трёх работ:
#
#   ЭКСПОРТ        архив проекта уезжает автору;
#   ПЕРЕЗАЛИВКА    у всех файлов новое время правки, версия пересчитывается,
#                  и двадцать клиентов спрашивают её разом;
#   ПУБЛИКАЦИЯ     тридцать глав подряд, каждая с записью манифеста.
#
#   qa/author-load-check.sh [-bite]
#
# -bite меряет заведомо медленный запрос (скачивание с ограниченной скоростью):
# стенд обязан назвать это ухудшением. Мерка, не видящая медленного ответа, не
# доказывает и быстрых.
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
for p in 8211 8213 8215 8217; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

C="$W/content"; mkdir -p "$C/bg" "$C/scripts"
printf '{"titles":[{"id":"p","name":"П","seasons":[{"chapters":[]}]}]}' > "$C/manifest.json"
# Арт настоящего порядка: 300 файлов по 200 КБ, случайные (жмутся плохо — как
# и настоящие jpeg, иначе замер мерил бы скорость нулей). Меньше — и работа
# автора заканчивается раньше, чем игрок успевает попасть в её окно.
python3 - "$C" <<'PY'
import os, random, sys
random.seed(7)
d = os.path.join(sys.argv[1], "bg")
for i in range(300):
    open(os.path.join(d, "art%03d.jpg" % i), "wb").write(random.randbytes(200 * 1024))
PY
content_mb=$(python3 -c "
import os,sys
d=os.path.join('$C','bg')
print(sum(os.path.getsize(os.path.join(d,f)) for f in os.listdir(d))//1048576)")

TOKEN="stand-$$"
"$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$C" -admin-token "$TOKEN" >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 50); do
  curl -fsS -m 1 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 && break
  sleep 0.2
done
curl -fsS -m 2 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 \
  || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }
B="http://127.0.0.1:$PORT"

bad=""; note() { bad="$bad\n  $1"; }

# Один «игрок»: спросить каталог и забрать картинку — то, что делает клиент
# постоянно. Печатает медиану и максимум в миллисекундах.
player() { # $1 = сколько кругов → "медиана максимум"
  local n="${1:-20}" rate=""
  # Укус: ответ по каплям. Кругов берём меньше — медленный он и есть медленный,
  # а стенд не должен идти минуты, чтобы это заметить.
  [ -n "$BITE" ] && { rate="--limit-rate 200k"; n=2; }
  for _ in $(seq 1 "$n"); do
    curl -s -o /dev/null -w '%{time_total}\n' $rate "$B/v1/content/manifest"
    curl -s -o /dev/null -w '%{time_total}\n' $rate "$B/content/bg/art001.jpg"
  done | python3 -c "
import sys
v = sorted(float(x) * 1000 for x in sys.stdin if x.strip())
print(f'{v[len(v)//2]:.1f} {v[-1]:.1f}' if v else '0 0')"
}

read -r base_med base_max < <(player 20)
[ -n "$base_med" ] || { echo "мерка не сняла фоновый уровень"; exit 2; }

# ── 1. Экспорт ─────────────────────────────────────────────────────────────
exp_started=$(python3 -c "import time;print(time.time())")
curl -s -o "$W/export.zip" -X POST "$B/v1/export" -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"name":"Проба","bundleId":"com.stand.probe","offline":true}' &
EXP=$!
sleep 0.4
read -r exp_med exp_max < <(player 20)
wait $EXP
exp_took=$(python3 -c "import time;print(int((time.time()-$exp_started)*1000))")
zip_mb=$(python3 -c "
import os
p='$W/export.zip'
print(os.path.getsize(p)//1048576 if os.path.exists(p) else 0)")
[ "$zip_mb" -ge 1 ] || note "экспорт отдал $zip_mb МБ — тяжёлой работы не было, замер пустой"
[ "$exp_took" -ge 200 ] || note "экспорт занял $exp_took мс — игрок мерился уже по спокойному серверу"

# ── 2. Перезаливка арта и пересчёт версий ──────────────────────────────────
find "$C" -type f -exec touch {} +
seq 1 20 | xargs -P 20 -I{} curl -s -o /dev/null -w '%{time_total}\n' "$B/v1/content/version" > "$W/ver" &
VER=$!
read -r ver_med ver_max < <(player 20)
wait $VER
poll=$(python3 -c "
v=sorted(float(x)*1000 for x in open('$W/ver') if x.strip())
print(f'{v[len(v)//2]:.0f}/{v[-1]:.0f}' if v else '—')")

# ── 3. Публикация тридцати глав ────────────────────────────────────────────
(
  for n in $(seq 1 30); do
    python3 - "$n" > "$W/req.json" <<'PY'
import json, sys
n = int(sys.argv[1])
print(json.dumps({"id": "proba", "name": "Проба", "chapter": n,
                  "lvns": f"scene p{n}\n\nРеплика главы {n}.\n-> __end\n"}, ensure_ascii=False))
PY
    curl -s -o /dev/null -X POST "$B/v1/admin/agent/publish" -H "Authorization: Bearer $TOKEN" \
      -H 'Content-Type: application/json' --data-binary @"$W/req.json"
  done
) &
PUB=$!
sleep 0.3
read -r pub_med pub_max < <(player 20)
wait $PUB
chapters=$(python3 -c "
import json
m = json.load(open('$C/manifest.json'))
print(max((len(t.get('seasons',[{}])[0].get('chapters',[])) for t in m['titles']), default=0))")
[ "$chapters" -ge 30 ] || note "опубликовано $chapters глав из 30 — тяжёлой работы не было, замер пустой"

worst() { python3 -c "
print(max($1, $2, $3))"; }
worst_med=$(worst "$exp_med" "$ver_med" "$pub_med")
worst_max=$(worst "$exp_max" "$ver_max" "$pub_max")

if [ -n "$BITE" ]; then
  slow_seen=$(python3 -c "print(1 if $worst_med > 100 else 0)")
  if [ "$slow_seen" = "1" ]; then
    echo "укус чист: медленный ответ мерка увидела (медиана $worst_med мс) — замедление она отличает"
    exit 0
  fi
  echo "СТЕНД СЛЕП: ответы шли по каплям, а мерка показала $worst_med мс — быстрым ответам её верить нельзя"
  exit 2
fi

echo "  контента $content_mb МБ; в покое медиана $base_med мс, максимум $base_max мс"
echo "  экспорт ($zip_mb МБ, шёл $exp_took мс): медиана $exp_med мс, максимум $exp_max мс"
echo "  перезаливка + пересчёт:     медиана $ver_med мс, максимум $ver_max мс (сам опрос версии $poll мс)"
echo "  публикация $chapters глав:        медиана $pub_med мс, максимум $pub_max мс"

# Пороги намеренно щедрые: проверяется «игрок не заметил», а не скорость этой
# машины. Заметным считается ответ дольше четверти секунды в среднем или
# полутора секунд в худшем случае.
over=$(python3 -c "
print(1 if $worst_med > 250 else 0)")
[ "$over" = "0" ] || note "во время работы автора медиана ответа игроку $worst_med мс"
over=$(python3 -c "
print(1 if $worst_max > 1500 else 0)")
[ "$over" = "0" ] || note "во время работы автора worst ответ игроку $worst_max мс"

[ -z "$bad" ] || { echo "РВЁТСЯ:$(printf '%b' "$bad")"; exit 1; }
echo "держит: ни экспорт, ни перезаливка, ни публикация пачкой не задели играющих"
