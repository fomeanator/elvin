#!/usr/bin/env bash
# qa/story-walk.sh — сюжетный бот (быстрая версия D2, без AltTester):
# устанавливает APK, бутит до меню, тапом входит в первую карточку и
# «читает» историю серией тапов. Прогресс доказывается диффом скриншотов
# (сцена обязана меняться), здоровье — отсутствием крэшей и живым процессом.
#
#   qa/story-walk.sh [путь/к/apk] [--server URL] [--taps N] [--keep-emulator]
#
# По умолчанию: dev-APK + 30 тапов. Для локального сервера нужен dev-APK
# (LVN_BUILD_DEV=1) — см. qa/monkey.sh --server.
set -u -o pipefail

SDK="${ANDROID_SDK_ROOT:-$HOME/Library/Android/sdk}"
ADB="$SDK/platform-tools/adb"
EMU="$SDK/emulator/emulator"
PKG="com.ominis.timeromance"
ACTIVITY="com.unity3d.player.UnityPlayerGameActivity"
PORT=5560
SERIAL="emulator-$PORT"
AVD="Pixel_3a_API_34_extension_level_7_arm64-v8a"

APK="$HOME/ominis/builds/timeromance-qa-dev.apk"
TAPS=30
SERVER_OVERRIDE=""
KEEP_EMU=0
while [ $# -gt 0 ]; do
  case "$1" in
    --taps) TAPS="$2"; shift 2 ;;
    --server) SERVER_OVERRIDE="$2"; shift 2 ;;
    --keep-emulator) KEEP_EMU=1; shift ;;
    *) APK="$1"; shift ;;
  esac
done

[ -f "$APK" ] || { echo "FAIL: APK не найден: $APK"; exit 1; }
STAMP="$(date +%Y%m%d-%H%M%S)"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$REPO_ROOT/qa/reports/$STAMP-storywalk"
mkdir -p "$OUT"
FAIL_COUNT=0
STARTED_EMU=0

log() { echo "[$(date +%H:%M:%S)] $*" | tee -a "$OUT/report.txt"; }
fail() { FAIL_COUNT=$((FAIL_COUNT + 1)); log "FAIL: $1"; }
cleanup() { [ "$STARTED_EMU" = 1 ] && [ "$KEEP_EMU" = 0 ] && "$ADB" -s "$SERIAL" emu kill >/dev/null 2>&1 || true; }
trap cleanup EXIT

# ── эмулятор (те же грабли, что в monkey.sh) ────────────────────────────────
if ! "$ADB" -s "$SERIAL" shell true >/dev/null 2>&1; then
  log "Поднимаю эмулятор ${AVD}..."
  "$EMU" -avd "$AVD" -no-window -no-audio -no-boot-anim -no-snapshot \
    -gpu host -feature -Vulkan -port "$PORT" >"$OUT/emulator.log" 2>&1 &
  STARTED_EMU=1
fi
deadline=$((SECONDS + 240))
until [ "$("$ADB" -s "$SERIAL" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ]; do
  [ $SECONDS -ge $deadline ] && { fail "эмулятор не загрузился"; exit 1; }
  sleep 2
done
"$ADB" -s "$SERIAL" shell settings put secure immersive_mode_confirmations confirmed >/dev/null 2>&1 || true

# экранные координаты (тапы в долях, чтобы не зависеть от AVD);
# awk вместо printf/bc — русская локаль printf ждёт запятую в числах
read -r W H < <("$ADB" -s "$SERIAL" shell wm size | sed -E 's/.*: *([0-9]+)x([0-9]+).*/\1 \2/' | tr -d '\r')
tapf() {
  "$ADB" -s "$SERIAL" shell input tap \
    "$(awk -v a="$W" -v f="$1" 'BEGIN{printf "%d", a*f}')" \
    "$(awk -v a="$H" -v f="$2" 'BEGIN{printf "%d", a*f}')"
}

log "Устанавливаю APK…"
"$ADB" -s "$SERIAL" install -r -g "$APK" >>"$OUT/report.txt" 2>&1 || { fail "install"; exit 1; }
"$ADB" -s "$SERIAL" logcat -c 2>/dev/null || true
if [ -n "$SERVER_OVERRIDE" ]; then
  case "$SERVER_OVERRIDE" in *127.0.0.1*|*localhost*)
    p="$(echo "$SERVER_OVERRIDE" | sed -E 's|.*:([0-9]+).*|\1|')"
    "$ADB" -s "$SERIAL" reverse "tcp:$p" "tcp:$p" >/dev/null 2>&1 || fail "adb reverse";;
  esac
  "$ADB" -s "$SERIAL" shell am start -n "$PKG/$ACTIVITY" -e lvn_server "$SERVER_OVERRIDE" >/dev/null 2>&1
else
  "$ADB" -s "$SERIAL" shell am start -n "$PKG/$ACTIVITY" >/dev/null 2>&1
fi

# бут до «veil handed off»
deadline=$((SECONDS + 120))
until "$ADB" -s "$SERIAL" logcat -d -s Unity 2>/dev/null | grep -q "veil handed off"; do
  [ $SECONDS -ge $deadline ] && { fail "бут не завершился"; break; }
  sleep 2
done
log "Бут завершён; жму «Начать/Продолжить» и вхожу в главу…"
sleep 3
tapf 0.5 0.87   # «Начать» велком-экрана (первый запуск; в хабе — пустота, безвредно)
sleep 2
tapf 0.5 0.79   # «Продолжить» карусели / карточка хаба
sleep 2
tapf 0.5 0.79   # повтор: если первый тап закрывал велком, этот входит в главу
sleep 8         # полный прелоуд главы (с локального сервера — быстрый)

# ── чтение: TAPS тапов, скриншот на каждом 3-м, прогресс = уникальные кадры ─
declare -a HASHES=()
uniq_count() { printf '%s\n' "${HASHES[@]}" | sort -u | wc -l | tr -d ' '; }
for i in $(seq 1 "$TAPS"); do
  tapf 0.5 0.60   # центр сцены: advance реплики; на экране выбора — верхняя опция
  sleep 1
  if [ $((i % 3)) = 0 ]; then
    "$ADB" -s "$SERIAL" exec-out screencap -p >"$OUT/walk-$i.png" 2>/dev/null || true
    HASHES+=("$(md5 -q "$OUT/walk-$i.png" 2>/dev/null || echo "x$i")")
  fi
done
log "Тапов: $TAPS, уникальных кадров: $(uniq_count)/${#HASHES[@]}"

# ── вердикт ─────────────────────────────────────────────────────────────────
"$ADB" -s "$SERIAL" logcat -d >"$OUT/logcat.txt" 2>/dev/null || true
grep -qE "FATAL EXCEPTION|Force finishing activity|ANR in $PKG" "$OUT/logcat.txt" \
  && fail "крэш-сигнатуры в logcat"
"$ADB" -s "$SERIAL" shell pidof "$PKG" >/dev/null 2>&1 || fail "процесс мёртв"
[ "$(uniq_count)" -ge 3 ] || fail "сцена не движется (уникальных кадров < 3) — бот застрял"

if [ "$FAIL_COUNT" = 0 ]; then
  log "PASS: бот прошёл $TAPS тапов сюжета, сцена движется, процесс жив."
  exit 0
else
  log "ИТОГ: $FAIL_COUNT провал(ов)"
  exit 1
fi
