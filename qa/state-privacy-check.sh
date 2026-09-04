#!/usr/bin/env bash
# ЧУЖОЙ НЕ ПРОЧИТАЕТ И НЕ ПЕРЕПИШЕТ МОЙ ПРОГРЕСС.
#
# Сейвы в облаке — это часы игры, и адрес у них угадываемый: имя блоба
# складывается из идентификатора игрока и титула. Значит вся защита держится на
# КЛЮЧЕ, который знает только устройство игрока.
#
# Договор в коде назван «доверие при первом обращении» (TOFU):
#
#   блоб НЕ занят   чтение открыто; запись С ключом ЗАНИМАЕТ блоб;
#                   запись без ключа оставляет его открытым (старые клиенты);
#   блоб занят      и чтение, и запись требуют совпадающий ключ.
#
# Проверяется здесь именно вторая строка — и не тем, что «отказ пришёл», а тем,
# что отказ НИЧЕГО НЕ ИСПОРТИЛ: неудачная попытка чужого не должна ни отдать
# ему документ, ни сдвинуть версию, ни затереть данные.
#
# ЦЕНА TOFU ЗАМЕРЯЕТСЯ ТУТ ЖЕ, а не замалчивается: блоб, который никто ещё не
# занял, занимает ПЕРВЫЙ пришедший с ключом. Для старой записи, оставшейся с
# дозамочных времён, это значит, что её может присвоить посторонний, и хозяин
# окажется заперт снаружи. Это свойство выбранной модели, а не дефект, — но
# число у него должно быть, а не умолчание.
#
# ПРОВЕРКА ОБЯЗАНА УМЕТЬ ПАДАТЬ: сперва стенд ДОКАЗЫВАЕТ, что верный ключ
# открывает документ. Иначе «чужому отказали» проходило бы и на сервере,
# который не отдаёт никому и ничего.
#
#   qa/state-privacy-check.sh [-bite]
#
# -bite не занимает блоб вовсе и требует, чтобы чтение БЕЗ ключа прошло: стенд,
# у которого всё всегда закрыто, не отличил бы защиту от поломки.
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

probe "$PORT" && { echo "порт занят — замер говорил бы с чужим сервером"; exit 2; }
"$W/srv" -addr "127.0.0.1:$PORT" -content "$W/content" >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 60); do probe "$PORT" && break; sleep 0.2; done
probe "$PORT" || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

USER="privacy__title"
MINE="ключ-хозяина"; THEIRS="ключ-чужого"

put() { # $1 = ключ ("" = без ключа), $2 = тело; печатает код
  if [ -z "$1" ]; then
    curl -sS -o /dev/null -w '%{http_code}' -X PUT "$BASE/v1/state?user=$USER" \
      -H 'Content-Type: application/json' -d "$2"
  else
    curl -sS -o /dev/null -w '%{http_code}' -X PUT "$BASE/v1/state?user=$USER" \
      -H 'Content-Type: application/json' -H "X-State-Key: $1" -d "$2"
  fi
}
get() { # $1 = ключ ("" = без ключа); печатает код и пишет тело в $W/body
  if [ -z "$1" ]; then
    curl -sS -o "$W/body" -w '%{http_code}' "$BASE/v1/state?user=$USER"
  else
    curl -sS -o "$W/body" -w '%{http_code}' -H "X-State-Key: $1" "$BASE/v1/state?user=$USER"
  fi
}
gold() { python3 -c 'import json,sys
try: print(json.load(open(sys.argv[1], encoding="utf-8"))["vars"]["золото"])
except Exception: print("нет")' "$W/body"; }

fail=0
say() { echo "  $1"; }

if [ -n "$BITE" ]; then
  # Блоб НЕ занимаем: пишем без ключа. Чтение без ключа обязано пройти —
  # иначе стенд не отличает защиту от общей поломки.
  code="$(put "" '{"vars":{"золото":10},"updatedAt":1}')"
  [ "$code" = "200" ] || { say "✗ запись без ключа не прошла ($code) — стенд сломан, а не строг"; exit 2; }
  code="$(get "")"
  say "укус: незанятый блоб, чтение без ключа → $code (ждём 200)"
  [ "$code" = "200" ] || { say "✗ СТЕНД ВРЁТ: закрыто даже там, где замка нет"; exit 2; }
  say "стенд честный: без замка документ отдаётся, значит отказ выше — это замок"
  exit 0
fi

# 1. Хозяин занимает блоб своим ключом.
code="$(put "$MINE" '{"vars":{"золото":100},"updatedAt":1}')"
[ "$code" = "200" ] || { say "✗ хозяин не смог записать ($code)"; exit 1; }

# АНТИ-ПУСТОТА: сперва доказываем, что верный ключ ОТДАЁТ документ.
code="$(get "$MINE")"; mine_gold="$(gold)"
say "хозяин со своим ключом: $code, золото $mine_gold"
{ [ "$code" = "200" ] && [ "$mine_gold" = "100" ]; } || {
  say "✗ верный ключ не открывает документ — «чужому отказали» ничего не докажет"; fail=1; }

# 2. Чужой читает — без ключа и с неверным.
code="$(get "")";        say "чужой без ключа:      $code (ждём отказ)"
[ "$code" = "200" ] && { say "✗ ЧУЖОЙ ПРОЧИТАЛ ПРОГРЕСС БЕЗ КЛЮЧА"; fail=1; }
code="$(get "$THEIRS")"; say "чужой с чужим ключом: $code (ждём отказ)"
[ "$code" = "200" ] && { say "✗ ЧУЖОЙ ПРОЧИТАЛ ПРОГРЕСС ПО СВОЕМУ КЛЮЧУ"; fail=1; }

# 3. Чужой пишет — и главное, ничего не портит.
code="$(put "$THEIRS" '{"vars":{"золото":0},"updatedAt":9}')"
say "чужой пишет:          $code (ждём отказ)"
[ "$code" = "200" ] && { say "✗ ЧУЖОЙ ПЕРЕЗАПИСАЛ ПРОГРЕСС"; fail=1; }
get "$MINE" >/dev/null; after="$(gold)"
say "после чужой попытки у хозяина: золото $after"
[ "$after" = "100" ] || { say "✗ отказ ИСПОРТИЛ документ: было 100, стало $after"; fail=1; }

# 4. Цена TOFU — замеряется, а не замалчивается.
OTHER="legacy__title"
curl -sS -o /dev/null -X PUT "$BASE/v1/state?user=$OTHER" -H 'Content-Type: application/json' \
  -d '{"vars":{"золото":50},"updatedAt":1}'
grab="$(curl -sS -o /dev/null -w '%{http_code}' -X PUT "$BASE/v1/state?user=$OTHER" \
        -H 'Content-Type: application/json' -H "X-State-Key: $THEIRS" \
        -d '{"vars":{"золото":50},"updatedAt":2}')"
locked="$(curl -sS -o /dev/null -w '%{http_code}' "$BASE/v1/state?user=$OTHER")"
say "незанятый блоб: посторонний занял ($grab), хозяин без ключа теперь получает $locked"
[ "$grab" = "200" ] && [ "$locked" != "200" ] &&
  say "     ↑ цена доверия при первом обращении: дозамочную запись присваивает первый с ключом"

[ "$fail" = "0" ] && { echo "ПРОГРЕСС ЗАКРЫТ ОТ ЧУЖИХ"; exit 0; }
echo "ПРОГРЕСС ОТКРЫТ ЧУЖИМ"; exit 1
