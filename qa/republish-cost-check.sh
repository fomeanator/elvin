#!/usr/bin/env bash
# ПУБЛИКАЦИЯ, НИЧЕГО НЕ ИЗМЕНИВШАЯ, НИЧЕГО НЕ СТОИТ.
#
# Обещание «качается только изменившееся» держится на двух опорах, и обе
# невидимы из кода.
#
# Первая — компилятор. Если один и тот же исходник даёт разные байты от
# запуска к запуску (порядок обхода таблиц, путь сборочной машины, метка
# времени внутри), то перевыпуск игры меняет ВСЕ главы разом, и правка одной
# реплики прилетает игроку как полная перекачка.
#
# Вторая — каталог. Клиент умеет дешёвый путь «каталог тот же — за ним не
# ходим», но им распоряжается сервер: любая запись manifest.json двигает rev,
# rev входит в общую версию контента, а смена версии заставляет КАЖДОГО
# играющего забрать каталог (в живой студии это 436 КБ), перечитать открытую
# главу мимо кэша и пересобрать фигуры на сцене.
#
# Стенд спрашивает по проводу:
#
#   ДЕТЕРМИНИЗМ    пять компиляций из двух разных каталогов — один хэш;
#   ХОЛОСТОЕ       переиздание тех же глав тем же текстом: версия контента не
#                  дрогнула, разница пуста, rev на месте, история не выросла;
#   ПРАВКА         правка одной реплики меняет её скрипт — и НЕ каталог;
#   НОВОЕ ВИДНО    новая глава и переименование каталог менять ОБЯЗАНЫ.
#
#   qa/republish-cost-check.sh [-bite]
#
# -bite меняет одно слово в тексте перед «холостым» переизданием: стенд обязан
# увидеть изменение. Мерка, не замечающая правку, не доказывает и тишины.
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
go build -C tools/lvnconv -o "$W/lvnconv" . || { echo "компилятор не собрался"; exit 1; }

bad=""; note() { bad="$bad\n  $1"; }

# ── 1. Детерминизм компилятора ─────────────────────────────────────────────
# Разные каталоги — чтобы поймать абсолютный путь, уехавший внутрь .lvn;
# разные процессы — чтобы поймать порядок обхода таблиц (Go тасует его на
# каждом запуске).
SRC="howto/every-command/every-command.lvns"
[ -f "$SRC" ] || SRC="examples/hello.lvns"
mkdir -p "$W/дом-один" "$W/дом-два"
cp -R "$(dirname "$SRC")" "$W/дом-один/" 2>/dev/null
cp -R "$(dirname "$SRC")" "$W/дом-два/" 2>/dev/null
base="$(basename "$(dirname "$SRC")")/$(basename "$SRC")"
for d in дом-один дом-два; do
  for i in 1 2 3; do
    "$W/lvnconv" convert -i "$W/$d/$base" -o "$W/c-$d-$i.lvn" >/dev/null 2>&1
  done
done
hashes="$(shasum -a 256 "$W"/c-*.lvn 2>/dev/null | awk '{print $1}' | sort -u | wc -l | tr -d ' ')"
built="$(ls "$W"/c-*.lvn 2>/dev/null | wc -l | tr -d ' ')"
[ "$built" -ge 6 ] || note "скомпилировалось только $built из 6 — детерминизм проверить нечем"
[ "$hashes" = "1" ] || note "шесть компиляций одного исходника дали $hashes разных файла — перевыпуск переливает игру целиком"

# ── Сервер ─────────────────────────────────────────────────────────────────
PORT=0
for p in 8181 8183 8185 8187; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

C="$W/content"; mkdir -p "$C"
printf '{"titles":[]}' > "$C/manifest.json"
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

pub() { # $1 = номер главы, $2 = текст реплики, $3 = имя игры (не обязательно)
  python3 - "$1" "$2" "${3:-Проба}" > "$W/req.json" <<'PY'
import json, sys
n, txt, name = int(sys.argv[1]), sys.argv[2], sys.argv[3]
lvns = f"scene proba{n}\n\n{txt}\n- Дальше -> k\n\n:k\nКонец главы {n}.\n-> __end\n"
print(json.dumps({"id": "proba", "name": name, "chapter": n, "lvns": lvns}, ensure_ascii=False))
PY
  curl -s -o /dev/null -w '%{http_code}' -X POST "$B/v1/admin/agent/publish" \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    --data-binary @"$W/req.json"
}
version() { curl -s "$B/v1/content/version" | python3 -c "import json,sys;print(json.load(sys.stdin).get('version',''))"; }
rev()     { python3 -c "import json;print(json.load(open('$C/manifest.json')).get('rev',0))"; }
snaps()   { ls "$C/.history/manifest.json" 2>/dev/null | wc -l | tr -d ' '; }
changed() { # $1 = версия, от которой считать → список изменившихся файлов
  curl -s "$B/v1/content/changes?since=$1" | python3 -c "
import json,sys
d = json.load(sys.stdin)
if d.get('full'): print('ВСЁ')
else: print(' '.join(sorted(d.get('changed') or {})) or '-')"
}
# Окно кэша версий на сервере — полсекунды; берём с запасом, иначе замер
# сравнивает не состояния, а кэш.
settle() { sleep 2; }

CHAPTERS=6
for n in $(seq 1 $CHAPTERS); do
  code="$(pub "$n" "Реплика главы $n.")"
  [ "$code" = "200" ] || note "глава $n не опубликовалась ($code)"
done
settle
V0="$(version)"; R0="$(rev)"; S0="$(snaps)"
[ -n "$V0" ] || { echo "сервер не отдал версию контента"; exit 2; }

# ── 2. Холостое переиздание ────────────────────────────────────────────────
TEXT="Реплика главы"
[ -n "$BITE" ] && TEXT="Реплика ПОПРАВЛЕННАЯ главы"
for n in $(seq 1 $CHAPTERS); do pub "$n" "$TEXT $n." >/dev/null; done
settle
V_idle="$(version)"; R1="$(rev)"; S1="$(snaps)"
idle_changed="$(changed "$V0")"

if [ -n "$BITE" ]; then
  if [ "$V0" != "$V_idle" ] && [ "$idle_changed" != "-" ]; then
    echo "укус чист: правку текста стенд увидел (изменилось: $idle_changed)"
    exit 0
  fi
  echo "СТЕНД СЛЕП: текст глав переписан, а он не заметил (версия та же, разница «${idle_changed}»)"
  exit 2
fi

[ "$V0" = "$V_idle" ] || note "холостое переиздание сменило версию контента — все играющие пойдут за каталогом"
[ "$idle_changed" = "-" ] || note "холостое переиздание объявило изменившимся: $idle_changed"
[ "$R0" = "$R1" ] || note "холостое переиздание сдвинуло rev $R0 → $R1"
[ "$S0" = "$S1" ] || note "холостое переиздание добавило снимков в историю ($S0 → $S1)"

# ── 2б. Панель сохраняет каталог, ничего не тронув ─────────────────────────
# Второй вход в ту же дверь: редактор открыл каталог и нажал «Сохранить». Тело
# то же самое, что на диске, — записью это быть не должно.
VP="$(version)"; RP="$(rev)"
put_code="$(curl -s -o "$W/put.json" -w '%{http_code}' -X PUT \
  "$B/v1/admin/assets/manifest.json" -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' --data-binary @"$C/manifest.json")"
settle
[ "$put_code" = "200" ] || note "сохранение своей же копии каталога отклонено ($put_code)"
RP2="$(rev)"; VP2="$(version)"
[ "$RP2" = "$RP" ] || note "холостое сохранение из панели сдвинуло rev $RP → $RP2"
[ "$VP2" = "$VP" ] || note "холостое сохранение из панели сменило версию контента"
V1="$VP2"

# ── 3. Правка одной реплики: скрипт — да, каталог — нет ────────────────────
pub 3 "Реплика главы 3, поправленная." >/dev/null
settle
edit_changed="$(changed "$V1")"
case "$edit_changed" in
  *manifest.json*) note "правка реплики потянула каталог: $edit_changed";;
  *proba-ch03.lvn*) ;;
  *) note "правка реплики не доехала до индекса версий: «${edit_changed}»";;
esac
V2="$(version)"

# ── 4. Новое игрок обязан увидеть ──────────────────────────────────────────
pub 7 "Новая глава." >/dev/null
settle
new_changed="$(changed "$V2")"
case "$new_changed" in
  *manifest.json*) ;;
  *) note "новая глава каталог не изменила ($new_changed) — игрок её не увидит";;
esac
V3="$(version)"
pub 7 "Новая глава." "Проба другая" >/dev/null
settle
ren_changed="$(changed "$V3")"
case "$ren_changed" in
  *manifest.json*) ;;
  *) note "переименование игры каталог не изменило ($ren_changed)";;
esac

MAN_BYTES="$(wc -c < "$C/manifest.json" | tr -d ' ')"
echo "  компилятор:  6 сборок из 2 каталогов → $hashes хэш"
echo "  панель:      сохранение без правок → $put_code, rev $RP → $RP2"
echo "  холостое:    версия $([ "$V0" = "$V_idle" ] && echo "та же" || echo "СМЕНИЛАСЬ"), разница «${idle_changed}», rev $R0 → $R1, снимков $S0 → $S1"
echo "  правка одной: «${edit_changed}» (каталога здесь нет — игрок не качает лишние $MAN_BYTES Б)"
echo "  новое видно: глава → «${new_changed}», переименование → «${ren_changed}»"

[ -z "$bad" ] || { echo "РВЁТСЯ:$(printf '%b' "$bad")"; exit 1; }
echo "держит: одинаковый исходник — одинаковые байты; переиздание без правок молчит; правка тянет только свой скрипт; новое видно"
