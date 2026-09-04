#!/usr/bin/env bash
# ОБОРВАННАЯ ЗАКАЧКА ПРОДОЛЖАЕТСЯ С МЕСТА — И НЕ ОТРАВЛЯЕТ КЭШ.
#
# Докачка просит «Range: bytes=N-» и дописывает хвост к недокачанному куску.
# Пока файл на сервере не меняется, это чистая экономия трафика. Но автор
# перезаливает арт прямо во время игры (в этом вся идея живого обновления), и
# тогда к голове ПРЕЖНЕЙ редакции приклеивается хвост НОВОЙ — файл, которого
# никогда не существовало, оседает в кэше как настоящий.
#
# Страховка по sha256 из индекса версий сюда не достаёт: производные варианты
# (@2k, .ktx2) в индекс намеренно не входят — их там ноль, замерено на живом
# сервере. А качается именно производное.
#
# Лечится условием самому серверу: «дай хвост, ЕСЛИ файл тот же» (If-Range).
# Условию нужен СИЛЬНЫЙ валидатор, поэтому сервер обязан ставить ETag —
# http.FileServer сам его не ставит.
#
# Стенд проверяет три вещи на НАСТОЯЩЕМ сервере:
#   1) ETag есть — иначе условие поставить не на что;
#   2) без условия склейка двух редакций действительно случается;
#   3) с условием сервер отвечает целым файлом вместо хвоста.
#
#   qa/resume-integrity-check.sh [-bite]
#
# -bite не подменяет файл: тогда условие обязано пропустить докачку (206).
# Стенд, который всегда получает 200, проверяет не докачку, а её отсутствие.
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
for p in 8093 8094 8095 8096; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

mkdir -p "$W/content/bg"
printf '{"titles":[]}' > "$W/content/manifest.json"
python3 -c "open('$W/content/bg/pic.jpg','wb').write(b'A'*300000)"

"$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$W/content" -admin-token "stand-$$" \
  >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 50); do
  curl -fsS -m 1 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 && break
  sleep 0.2
done
curl -fsS -m 2 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 \
  || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

URL="http://127.0.0.1:$PORT/content/bg/pic.jpg"
ETAG="$(curl -s -D - -o /dev/null "$URL" | grep -i '^etag:' | cut -d' ' -f2- | tr -d '\r')"
if [ -z "$ETAG" ]; then
  echo "РВЁТСЯ: сервер не ставит ETag — условной докачке не на что опереться"
  exit 1
fi
echo "ETag: $ETAG"

HALF=120000
curl -s -r 0-$((HALF-1)) "$URL" -o "$W/head.bin" >/dev/null
[ "$(wc -c < "$W/head.bin" | tr -d ' ')" = "$HALF" ] || { echo "первый кусок не пришёл"; exit 1; }

if [ -z "$BITE" ]; then
  # Автор перезалил файл, пока игрок был в разрыве связи.
  python3 -c "open('$W/content/bg/pic.jpg','wb').write(b'B'*300000)"
  sleep 1.1   # время правки должно отличаться от прежнего
fi

# 1. Как было БЕЗ условия — ради замера, а не ради работы.
code_plain="$(curl -s -r $HALF- "$URL" -o "$W/tail_plain.bin" -w '%{http_code}')"
cat "$W/head.bin" "$W/tail_plain.bin" > "$W/spliced.bin"
mix="$(python3 - "$W/spliced.bin" <<'PY'
import sys
d = open(sys.argv[1], "rb").read()
print(f"{len(d)} байт: A={d.count(b'A')}, B={d.count(b'B')}")
PY
)"

# 2. Как стало С условием.
code_cond="$(curl -s -r $HALF- -H "If-Range: $ETAG" "$URL" -o "$W/tail_cond.bin" -w '%{http_code}')"
size_cond="$(wc -c < "$W/tail_cond.bin" | tr -d ' ')"

echo "  без условия: код $code_plain, склейка — $mix"
echo "  с условием:  код $code_cond, получено $size_cond байт"

if [ -n "$BITE" ]; then
  if [ "$code_cond" = "206" ] && [ "$size_cond" = "180000" ]; then
    echo "укус чист: файл не менялся — условие пропустило докачку хвостом"
    exit 0
  fi
  echo "СТЕНД ВРЁТ: без подмены условие всё равно тянет файл целиком — это не докачка"
  exit 2
fi

fail=0
case "$mix" in
  *"A=120000, B=180000"*) ;;  # склейка воспроизведена — есть от чего защищаться
  *) echo "  стенд не воспроизвёл склейку: $mix"; fail=1;;
esac
if [ "$code_cond" != "200" ] || [ "$size_cond" != "300000" ]; then
  echo "РВЁТСЯ: подменённый файл всё равно отдан хвостом ($code_cond, $size_cond б) — склейка доедет до игрока"
  fail=1
fi
[ "$fail" = "0" ] || exit 1
echo "держит: подмена между заходами ловится сервером — вместо хвоста приходит целый файл"
