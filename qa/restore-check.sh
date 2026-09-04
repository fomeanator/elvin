#!/usr/bin/env bash
# СНИМОК НЕ ПРОСТО ДЕЛАЕТСЯ — ИЗ НЕГО ВСТАЁТ ЖИВОЙ СЕРВЕР.
#
# `deploy/backup.sh` увозит то, что нельзя пересобрать: аккаунты, кошельки,
# сейвы игроков, таблицы результатов, учётки панели. Он проверяет архив на
# читаемость (`tar -t`) и базу на целостность — но ни одна проверка не
# отвечает на вопрос, ради которого бэкап существует: **встанет ли из него
# сервер и найдут ли себя игроки**. В самом скрипте так и написано:
# «восстановление — руками и осознанно», то есть путь не пройден ни разу.
#
# Стенд проходит его целиком:
#
#   1. поднимает сервер и заводит на нём то, что теряют при пожаре:
#      аккаунт по device_id, кошелёк с историей, сейв игрока;
#   2. снимает бэкап ТЕМ ЖЕ скриптом, что на проде;
#   3. разворачивает архив в ЧИСТЫЙ каталог (контент кладётся из выкладки —
#      его снимок и не везёт, он пересобирается импортом);
#   4. поднимает второй сервер и спрашивает у него то же самое.
#
# Совпасть обязаны три вещи: тот же device_id даёт ТОТ ЖЕ user id, кошелёк
# помнит баланс и историю, сейв читается прежним ключом.
#
#   qa/restore-check.sh [-bite]
#
# -bite разворачивает ПУСТОЙ каталог вместо снимка: проверки обязаны
# покраснеть. Стенд, который зеленеет и без данных, ничего не проверяет.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go      >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl    >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }
command -v python3 >/dev/null 2>&1 || { echo "нет python3 — пропускаю"; exit 0; }

W="$(mktemp -d)"; PIDS=""
cleanup() { for p in $PIDS; do kill "$p" 2>/dev/null; done; rm -rf "$W"; }
trap cleanup EXIT

go build -C server -o "$W/lvnserver" . || { echo "сервер не собрался"; exit 1; }

free_port() {
  for p in "$@"; do
    curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { echo "$p"; return; }
  done
  echo 0
}
serve() { # $1 = корень контента, $2 = порт
  "$W/lvnserver" -addr "127.0.0.1:$2" -content "$1" -admin-token "stand-$$" \
    >"$W/server-$2.log" 2>&1 &
  PIDS="$PIDS $!"
  for _ in $(seq 1 50); do
    curl -fsS -m 1 "http://127.0.0.1:$2/healthz" >/dev/null 2>&1 && return 0
    sleep 0.2
  done
  echo "  сервер на $2 не поднялся:"; tail -3 "$W/server-$2.log"; return 1
}
jget() { python3 -c "import json,sys;print(json.load(sys.stdin).get('$1',''))"; }

PA="$(free_port 8101 8103 8105 8107)"; PB="$(free_port 8102 8104 8106 8108)"
{ [ "$PA" = "0" ] || [ "$PB" = "0" ]; } && { echo "порты заняты — пропускаю"; exit 0; }

# ── 1. Живой сервер с данными, которые нельзя пересобрать ───────────────────
mkdir -p "$W/live/content"
printf '{"titles":[{"id":"probe","name":"Проба"}]}' > "$W/live/content/manifest.json"
serve "$W/live/content" "$PA" || exit 2

DEV="stand-device-0123456789abcdef"
REG="$(curl -s -X POST "http://127.0.0.1:$PA/v1/auth/register" \
       -H 'Content-Type: application/json' -d "{\"device_id\":\"$DEV\"}")"
USERID="$(printf '%s' "$REG" | jget user_id)"
TOKEN="$(printf '%s' "$REG" | jget token)"
[ -n "$USERID" ] || { echo "аккаунт не завёлся — стенду не на чем стоять"; exit 2; }

curl -s -o /dev/null -X POST "http://127.0.0.1:$PA/v1/wallet/earn" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"currency":"gold","amount":150,"reason":"stand","op_id":"stand-op-1"}'
curl -s -o /dev/null -X PUT "http://127.0.0.1:$PA/v1/state?user=$USERID" \
  -H "X-State-Key: stand-key-1" -H 'Content-Type: application/json' \
  -d '{"version":0,"blob":{"chapter":"probe-ch1","step":42}}'

before_gold="$(curl -s "http://127.0.0.1:$PA/v1/wallet" -H "Authorization: Bearer $TOKEN" \
  | python3 -c "import json,sys;print((json.load(sys.stdin).get('balances') or {}).get('gold',0))")"
echo "  завели: аккаунт $USERID, кошелёк $before_gold, сейв на шаге 42"

# ── 2. Снимок ТЕМ ЖЕ скриптом, что на проде ────────────────────────────────
LVN_HOME="$W/live" CONTENT="$W/live/content" DEST="$W/backups" KEEP=2 \
  bash deploy/backup.sh >"$W/backup.log" 2>&1 \
  || { echo "снимок не снялся:"; tail -3 "$W/backup.log"; exit 1; }
ARCHIVE="$(ls -1t "$W/backups"/*.tar.gz 2>/dev/null | head -1)"
[ -n "$ARCHIVE" ] || { echo "архива нет"; exit 1; }
echo "  снимок: $(basename "$ARCHIVE"), файлов внутри $(tar -tzf "$ARCHIVE" | wc -l | tr -d ' ')"

# ── 3. Разворот в ЧИСТЫЙ каталог ───────────────────────────────────────────
mkdir -p "$W/restored/content"
if [ -z "$BITE" ]; then
  tar -xzf "$ARCHIVE" -C "$W/restored/content"
fi
# Контент снимок не везёт — он пересобирается выкладкой. Кладём как при
# настоящем восстановлении: свежий контент плюс данные из снимка.
cp "$W/live/content/manifest.json" "$W/restored/content/manifest.json"
serve "$W/restored/content" "$PB" || exit 2

# ── 4. Спрашиваем у поднятого то же самое ──────────────────────────────────
REG2="$(curl -s -X POST "http://127.0.0.1:$PB/v1/auth/register" \
        -H 'Content-Type: application/json' -d "{\"device_id\":\"$DEV\"}")"
USER2="$(printf '%s' "$REG2" | jget user_id)"
TOKEN2="$(printf '%s' "$REG2" | jget token)"
gold2="$(curl -s "http://127.0.0.1:$PB/v1/wallet" -H "Authorization: Bearer $TOKEN2" \
  | python3 -c "import json,sys;print((json.load(sys.stdin).get('balances') or {}).get('gold',0))" 2>/dev/null || echo 0)"
save2="$(curl -s "http://127.0.0.1:$PB/v1/state?user=$USERID" -H "X-State-Key: stand-key-1" \
  | python3 -c "import json,sys;print((json.load(sys.stdin).get('blob') or {}).get('step',''))" 2>/dev/null || echo "")"

echo "  из снимка: аккаунт $USER2, кошелёк $gold2, сейв на шаге ${save2:-—}"

same_user=$([ "$USERID" = "$USER2" ] && echo 1 || echo 0)
same_gold=$([ "$gold2" = "$before_gold" ] && echo 1 || echo 0)
same_save=$([ "$save2" = "42" ] && echo 1 || echo 0)

if [ -n "$BITE" ]; then
  if [ "$same_user$same_gold$same_save" = "111" ]; then
    echo "СТЕНД СЛЕП: без снимка всё «совпало» — он не проверяет ничего"
    exit 2
  fi
  echo "укус чист: без снимка проверки краснеют (аккаунт=$same_user, кошелёк=$same_gold, сейв=$same_save)"
  exit 0
fi

fail=0
[ "$same_user" = "1" ] || { echo "РВЁТСЯ: тот же device_id получил ДРУГОЙ аккаунт — игрок не найдёт себя"; fail=1; }
[ "$same_gold" = "1" ] || { echo "РВЁТСЯ: кошелёк не восстановился ($gold2 вместо $before_gold) — покупки потеряны"; fail=1; }
[ "$same_save" = "1" ] || { echo "РВЁТСЯ: сейв не читается прежним ключом — прогресс потерян"; fail=1; }
[ "$fail" = "0" ] || exit 1
echo "держит: из снимка встал сервер, игрок нашёл себя, кошелёк и сейв целы"
