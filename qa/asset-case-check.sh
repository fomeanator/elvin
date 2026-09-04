#!/usr/bin/env bash
# ИМЯ ФАЙЛА, КОТОРОЕ ПРОЩАЕТ МАШИНА АВТОРА, НЕ ДОЛЖНО ЛОМАТЬСЯ У ИГРОКА.
#
# Контент собирают на маке (и на Windows), а раздаёт его Linux. У автора
# `Room.png` спокойно открывает `room.png` — файловая система прощает регистр;
# на сервере игры то же имя даёт 404, и вместо фона игрок видит пустоту. Это не
# теория: ровно так дважды уезжали живые главы.
#
# Стенд ставит обе машины рядом и говорит с НАСТОЯЩИМ сервером по HTTP:
#
#   том автора    обычный каталог (регистр не значим)
#   том игрока    образ «Case-sensitive APFS» — Linux в миниатюре
#
# На каждом поднимается сервер, в оба публикуется ОДНА И ТА ЖЕ глава со
# ссылкой, разошедшейся регистром. Замеряются две вещи: сказал ли гейт про
# расхождение при публикации и что отдаёт провод по этому адресу.
#
# ПРОВЕРКА ОБЯЗАНА УМЕТЬ МОЛЧАТЬ. Стенд, который «находит расхождение» всегда,
# не стоит ничего: -bite публикует главу с ТОЧНЫМИ ссылками и требует тишины.
#
#   qa/asset-case-check.sh [-bite]
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go   >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }

W="$(mktemp -d)"
VOL=""; PIDS=""
cleanup() {
  for p in $PIDS; do kill "$p" 2>/dev/null; done
  [ -n "$VOL" ] && hdiutil detach "$VOL" -quiet 2>/dev/null
  rm -rf "$W"
}
trap cleanup EXIT

go build -C server -o "$W/lvnserver" . || { echo "сервер не собрался"; exit 1; }

# ── два тома ────────────────────────────────────────────────────────────────
# Каталог автора есть всегда. «Том игрока» на маке делается образом; на Linux
# им служит обычный каталог — там регистр значим и без образа.
AUTHOR="$W/author"
PLAYER=""
case "$(uname -s)" in
  Darwin)
    if hdiutil create -size 32m -fs "Case-sensitive APFS" -volname LVNCASE -quiet "$W/case.dmg" \
       && hdiutil attach "$W/case.dmg" -mountpoint "$W/casevol" -nobrowse -quiet; then
      VOL="$W/casevol"; PLAYER="$VOL/player"
    else
      echo "не удалось создать регистрозависимый том — половину замера пропускаю"
    fi
    ;;
  *) PLAYER="$W/player" ;;
esac

art() { # $1 = корень контента: точные имена, как они лягут на прод
  mkdir -p "$1/content/bg" "$1/content/scripts"
  printf '{"titles":[]}' > "$1/content/manifest.json"
  python3 - "$1/content/bg/room.png" <<'PY'
import struct, sys, zlib
def chunk(t, d):
    c = t + d
    return struct.pack(">I", len(d)) + c + struct.pack(">I", zlib.crc32(c) & 0xffffffff)
rows = b"".join(b"\x00" + bytes((40, 60, 90)) * 1600 for _ in range(1200))
open(sys.argv[1], "wb").write(
    b"\x89PNG\r\n\x1a\n"
    + chunk(b"IHDR", struct.pack(">IIBBBBB", 1600, 1200, 8, 2, 0, 0, 0))
    + chunk(b"IDAT", zlib.compress(rows))
    + chunk(b"IEND", b""))
PY
}

free_port() { # печатает свободный порт из списка
  for p in "$@"; do
    curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { echo "$p"; return 0; }
  done
  echo 0
}

serve() { # $1 = корень; $2 = порт; поднимает сервер и ждёт healthz
  "$W/lvnserver" -addr "127.0.0.1:$2" -content "$1/content" -admin-token "stand-$$" \
    >"$W/server-$2.log" 2>&1 &
  PIDS="$PIDS $!"
  for _ in $(seq 1 50); do
    curl -fsS -m 1 "http://127.0.0.1:$2/healthz" >/dev/null 2>&1 && return 0
    sleep 0.2
  done
  echo "  сервер на $2 не поднялся:"; tail -3 "$W/server-$2.log"; return 1
}

# Ссылка главы: с расхождением регистра или точная (укус).
REF="/content/bg/Room.png"; [ -n "$BITE" ] && REF="/content/bg/room.png"
python3 - "$W/pub.json" "$REF" <<'PY'
import json, sys
json.dump({"id": "stand", "name": "Стенд", "chapter": 1,
           "lvns": "scene stand\n\nbg %s\nРассказчик: Кадр.\n-> __end\n" % sys.argv[2]},
          open(sys.argv[1], "w"), ensure_ascii=False)
PY

probe() { # $1 = ярлык; $2 = корень; $3 = порт → печатает строку замера
  local label="$1" root="$2" port="$3"
  art "$root"
  serve "$root" "$port" || return 2
  local code said wire
  code="$(curl -sS -o "$W/resp-$port.json" -w '%{http_code}' \
    -X POST "http://127.0.0.1:$port/v1/admin/agent/publish" \
    -H "Authorization: Bearer stand-$$" -H "Content-Type: application/json" -d @"$W/pub.json")"
  said="$(python3 - "$W/resp-$port.json" <<'PY'
import json, sys
w = json.load(open(sys.argv[1])).get("warnings") or []
hit = [x for x in w if "имя файла разошлось" in x or "файла нет" in x]
print(hit[0] if hit else "")
PY
)"
  wire="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port$REF")"
  echo "  $label: публикация $code, провод $wire, гейт: ${said:-молчит}"
  [ -n "$said" ] && echo "SAID" >> "$W/said-$port"
  return 0
}

PA="$(free_port 8077 8078 8079 8081)"; PP="$(free_port 8082 8083 8084 8085)"
[ "$PA" = "0" ] || [ "$PP" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

echo "ссылка главы: $REF"
probe "том автора  (регистр не значим)" "$AUTHOR" "$PA" || exit 2
if [ -n "$PLAYER" ]; then
  probe "том игрока  (регистр значим) " "$PLAYER" "$PP" || exit 2
fi

author_said=$([ -f "$W/said-$PA" ] && echo 1 || echo "")
player_said=$([ -f "$W/said-$PP" ] && echo 1 || echo "")

if [ -n "$BITE" ]; then
  if [ -n "$author_said" ] || [ -n "$player_said" ]; then
    echo "СТЕНД ВРЁТ: точная ссылка вызвала жалобу — такому замеру верить нельзя"; exit 2
  fi
  echo "укус чист: на точных именах гейт молчит, значит его «нашёл» что-то значит"
  exit 0
fi

if [ -z "$author_said" ]; then
  echo "РВЁТСЯ: на машине автора гейт промолчал — расхождение имени всплывёт только у игрока"
  exit 1
fi
if [ -n "$PLAYER" ] && [ -z "$player_said" ]; then
  echo "РВЁТСЯ: на регистрозависимом томе гейт промолчал"; exit 1
fi
echo "держит: расхождение имени названо ДО выкладки, на обеих машинах одинаково"
