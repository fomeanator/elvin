#!/usr/bin/env bash
# В ИГРУ УЕЗЖАЕТ ИГРА, А НЕ РАБОЧИЙ СТОЛ СТУДИИ.
#
# Каталог контента — это не только скрипты и арт. Рядом копится авторская
# кухня: исходники `.lvns`, из которых компилируются главы; бэкапы манифеста,
# которые оставляет деплой (по полмегабайта штука); присланные архивы с
# ассетами; заметки; черновики редактора («…lvn~»). На живом каталоге это 143
# исходника, девять бэкапов и два архива.
#
# Уезжало всё это в два места, и оба про игрока:
#
#   ИНДЕКС ВЕРСИЙ  сворачивается в общую версию контента, по которой играющий
#                  клиент решает, что мир изменился. Бэкап от деплоя и правка
#                  авторского комментария заставляли ВСЕХ читающих идти за
#                  разницей и перечитывать открытую главу мимо кэша;
#   ОФЛАЙН-НАБОР   экспорт складывал кухню в StreamingAssets — она уезжала в
#                  сборку игры.
#
# Стенд поднимает настоящий сервер, кладёт рядом игру и кухню и спрашивает:
#
#   ТИШИНА     кухня появилась — версия контента не дрогнула;
#   ИНДЕКС     в нём только игровые файлы;
#   ОФЛАЙН     в наборе игра, кухни нет;
#   ИГРА ЦЕЛА  скрипт, картинка, атлас Spine (.atlas.txt), шрифт и каталог
#              перевода — на месте и в индексе, и в наборе;
#   ПАНЕЛЬ     исходник по-прежнему отдаётся по /content/ — редактор читает
#              его именно оттуда, и запретить раздачу значило бы ослепить его.
#
#   qa/game-only-check.sh [-bite]
#
# -bite добавляет ИГРОВОЙ файл: стенд обязан увидеть смену версии. Мерка, не
# замечающая нового арта, не доказывает и тишины на кухне.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go      >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl    >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }
command -v unzip   >/dev/null 2>&1 || { echo "нет unzip — пропускаю"; exit 0; }
command -v python3 >/dev/null 2>&1 || { echo "нет python3 — пропускаю"; exit 0; }

W="$(mktemp -d)"; PID=""
cleanup() { [ -n "$PID" ] && kill "$PID" 2>/dev/null; rm -rf "$W"; }
trap cleanup EXIT

go build -C server -o "$W/lvnserver" . || { echo "сервер не собрался"; exit 1; }

PORT=0
for p in 8201 8203 8205 8207; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

C="$W/content"; mkdir -p "$C/scripts" "$C/bg" "$C/fonts"
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

bad=""; note() { bad="$bad\n  $1"; }
version() { curl -s "$B/v1/content/version" | python3 -c "import json,sys;print(json.load(sys.stdin).get('version',''))"; }
index()   { curl -s "$B/content/asset-versions.json" | python3 -c "
import json,sys
print(' '.join(sorted(json.load(sys.stdin))))"; }
# Кэш версий на сервере — две секунды; замер без запаса сравнивал бы кэш.
settle() { sleep 3; }

# ── Игра ───────────────────────────────────────────────────────────────────
python3 - > "$W/req.json" <<'PY'
import json
print(json.dumps({"id": "proba", "name": "Проба", "chapter": 1,
                  "lvns": "scene p\n\n# заметка автора\nРеплика.\n-> __end\n"}, ensure_ascii=False))
PY
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$B/v1/admin/agent/publish" \
        -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' --data-binary @"$W/req.json")"
[ "$code" = "200" ] || { echo "глава не опубликовалась ($code)"; exit 2; }
printf 'картинка'      > "$C/bg/room.jpg"
printf 'атлас spine'   > "$C/bg/hero.atlas.txt"   # игровой .txt — под нож попасть НЕ должен
printf 'шрифт'         > "$C/fonts/text.ttf"
printf '{"Реплика":"Line"}' > "$C/scripts/proba-ch01.en.json"
settle
V0="$(version)"; IDX0="$(index)"
[ -n "$V0" ] || { echo "сервер не отдал версию контента"; exit 2; }

GAME="scripts/proba-ch01.lvn bg/room.jpg bg/hero.atlas.txt fonts/text.ttf scripts/proba-ch01.en.json"
for f in $GAME; do
  case " $IDX0 " in *" $f "*) ;; *) note "игровой файл не попал в индекс версий: $f";; esac
done

# ── Кухня ──────────────────────────────────────────────────────────────────
cp "$C/manifest.json" "$C/manifest.json.bak-predeploy-014142"
printf 'PKархив с ассетами'  > "$C/kenney_toon-characters.zip"
printf '# заметки студии\n'  > "$C/README-studio.md"
printf 'черновик'            > "$C/scripts/proba-ch01.lvn~"
printf 'слой'                > "$C/bg/room.psd"
if [ -n "$BITE" ]; then printf 'новый арт' > "$C/bg/hall.jpg"; fi
settle
V1="$(version)"; IDX1="$(index)"

if [ -n "$BITE" ]; then
  if [ "$V0" != "$V1" ]; then
    echo "укус чист: новый игровой файл сменил версию контента — мерка это видит"
    exit 0
  fi
  echo "СТЕНД СЛЕП: в каталог добавлен арт, а версия не дрогнула — тишины он бы тоже не доказал"
  exit 2
fi

[ "$V0" = "$V1" ] || note "кухня сменила версию контента — все играющие пошли за разницей"
KITCHEN="manifest.json.bak-predeploy-014142 kenney_toon-characters.zip README-studio.md scripts/proba-ch01.lvn~ bg/room.psd scripts/proba-ch01.lvns"
for f in $KITCHEN; do
  case " $IDX1 " in *" $f "*) note "кухня попала в индекс версий: $f";; esac
done

# ── Офлайновый набор ───────────────────────────────────────────────────────
curl -s -o "$W/off.zip" -X POST "$B/v1/export" -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"name":"Проба","bundleId":"com.stand.probe","offline":true}'
unzip -l "$W/off.zip" >"$W/list.txt" 2>/dev/null || { echo "офлайн-экспорт не распаковался"; exit 2; }
in_zip() { grep -q "StreamingAssets/lvn/content/$1$" "$W/list.txt"; }
for f in $GAME; do in_zip "$f" || note "игровой файл не уехал в офлайновый набор: $f"; done
for f in $KITCHEN; do in_zip "$f" && note "кухня уехала в офлайновый набор: $f"; done

# ── Панель ─────────────────────────────────────────────────────────────────
src_code="$(curl -s -o /dev/null -w '%{http_code}' "$B/content/scripts/proba-ch01.lvns")"
[ "$src_code" = "200" ] || note "исходник больше не отдаётся по проводу ($src_code) — редактор читает его именно оттуда"

echo "  индекс:   $(echo "$IDX1" | wc -w | tr -d ' ') записей — $IDX1"
echo "  кухня:    версия $([ "$V0" = "$V1" ] && echo "та же" || echo "СМЕНИЛАСЬ") после бэкапа, архива, заметки, черновика и .psd"
echo "  офлайн:   $(grep -c "StreamingAssets/lvn/content/" "$W/list.txt" | tr -d ' ') файлов контента в наборе"
echo "  панель:   исходник по /content/ → $src_code"

[ -z "$bad" ] || { echo "РВЁТСЯ:$(printf '%b' "$bad")"; exit 1; }
echo "держит: кухня не в индексе и не в наборе, игра цела вся, редактор своё читает"
