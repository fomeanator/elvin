#!/usr/bin/env bash
# qa/monkey.sh — автоматический смоук APK на headless-эмуляторе:
# бут → установка → запуск → ожидание готовности → скриншот → adb monkey →
# греп крэшей → отчёт в qa/reports/<штамп>/ . Выход 0 = зелёный.
#
# Использование:
#   qa/monkey.sh [путь/к/apk] [--events N] [--avd NAME] [--keep-emulator] [--no-monkey]
# По умолчанию: свежий APK из ~/ominis/builds/timeromance-demo.apk, 500 событий.
set -u -o pipefail

SDK="${ANDROID_SDK_ROOT:-$HOME/Library/Android/sdk}"
ADB="$SDK/platform-tools/adb"
EMU="$SDK/emulator/emulator"
PKG="com.ominis.timeromance"
ACTIVITY="com.unity3d.player.UnityPlayerGameActivity"
PORT=5560
SERIAL="emulator-$PORT"

APK="$HOME/ominis/builds/timeromance-demo.apk"
AVD="Pixel_3a_API_34_extension_level_7_arm64-v8a"
EVENTS=500
SEED=20260719
KEEP_EMU=0
RUN_MONKEY=1
BOOT_TIMEOUT=240   # сек на холодный бут эмулятора
READY_TIMEOUT=120  # сек на готовность приложения (маркер в logcat)

while [ $# -gt 0 ]; do
  case "$1" in
    --events) EVENTS="$2"; shift 2 ;;
    --avd) AVD="$2"; shift 2 ;;
    --keep-emulator) KEEP_EMU=1; shift ;;
    --no-monkey) RUN_MONKEY=0; shift ;;
    *) APK="$1"; shift ;;
  esac
done

[ -f "$APK" ] || { echo "FAIL: APK не найден: $APK"; exit 1; }
[ -x "$ADB" ] || { echo "FAIL: adb не найден: $ADB"; exit 1; }

STAMP="$(date +%Y%m%d-%H%M%S)"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$REPO_ROOT/qa/reports/$STAMP"
mkdir -p "$OUT"
REPORT="$OUT/report.txt"
STARTED_EMU=0
FAIL_COUNT=0
FAIL_LIST=""

log() { echo "[$(date +%H:%M:%S)] $*" | tee -a "$REPORT"; }
fail() { FAIL_COUNT=$((FAIL_COUNT + 1)); FAIL_LIST="$FAIL_LIST | $1"; log "FAIL: $1"; }

cleanup() {
  if [ "$STARTED_EMU" = 1 ] && [ "$KEEP_EMU" = 0 ]; then
    "$ADB" -s "$SERIAL" emu kill >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

log "APK: $APK ($(du -h "$APK" | cut -f1))"
log "Отчёт: $OUT"

# ── 1. Эмулятор: переиспользовать живой или поднять headless ────────────────
if ! "$ADB" -s "$SERIAL" shell true >/dev/null 2>&1; then
  log "Поднимаю эмулятор $AVD (headless)…"
  # -gpu host: дефолтный для -no-window SwiftShader вешает Unity 6.
  # -feature -Vulkan: с Vulkan Unity 6 виснет до первого лога (Auto API
  # пробует Vulkan первым); без него движок откатывается на GLES3 и живёт.
  "$EMU" -avd "$AVD" -no-window -no-audio -no-boot-anim -no-snapshot \
    -gpu host -feature -Vulkan -port "$PORT" >"$OUT/emulator.log" 2>&1 &
  STARTED_EMU=1
fi

deadline=$((SECONDS + BOOT_TIMEOUT))
until [ "$("$ADB" -s "$SERIAL" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ]; do
  [ $SECONDS -ge $deadline ] && { fail "эмулятор не загрузился за ${BOOT_TIMEOUT}с"; exit 1; }
  sleep 2
done
log "Эмулятор загружен (Android $("$ADB" -s "$SERIAL" shell getprop ro.build.version.release | tr -d '\r'))"

# Стабильность прогона: убрать анимации, разбудить экран.
# immersive_mode_confirmations: оверлей "Viewing full screen" крадёт фокус,
# Unity ловит APP_CMD_LOST_FOCUS и паузит цикл ДО первой сцены — вечный чёрный экран.
"$ADB" -s "$SERIAL" shell settings put secure immersive_mode_confirmations confirmed >/dev/null 2>&1 || true
for k in window_animation_scale transition_animation_scale animator_duration_scale; do
  "$ADB" -s "$SERIAL" shell settings put global "$k" 0 >/dev/null 2>&1 || true
done
"$ADB" -s "$SERIAL" shell input keyevent KEYCODE_WAKEUP >/dev/null 2>&1 || true

# ── 2. Установка и запуск ───────────────────────────────────────────────────
log "Устанавливаю APK…"
if ! "$ADB" -s "$SERIAL" install -r -g "$APK" >>"$REPORT" 2>&1; then
  fail "adb install провалился"; exit 1
fi

"$ADB" -s "$SERIAL" logcat -c 2>/dev/null || true
"$ADB" -s "$SERIAL" logcat -c -b crash 2>/dev/null || true
log "Запускаю ${PKG}..."
"$ADB" -s "$SERIAL" shell am start -n "$PKG/$ACTIVITY" >>"$REPORT" 2>&1

# ── 3. Ожидание готовности по logcat-маркеру ────────────────────────────────
# Готовность = NovelApp снял бут-вуаль (NovelApp.cs: "veil handed off — app boot done").
# Промежуточные фазы бута идут строками "[lvn-boot] +NNNms <фаза>".
READY_RE="veil handed off"
PROGRESS_RE="\[lvn-boot\]|\[sandbox\]|\[novelapp\]"
CRASH_RE="FATAL EXCEPTION|Force finishing activity|ANR in $PKG|beginning of crash"

ready=""
deadline=$((SECONDS + READY_TIMEOUT))
while [ $SECONDS -lt $deadline ]; do
  snap="$("$ADB" -s "$SERIAL" logcat -d -s Unity 2>/dev/null)"
  full="$("$ADB" -s "$SERIAL" logcat -d -v brief 2>/dev/null | grep -E "$PKG" || true)"
  if echo "$full" | grep -qE "$CRASH_RE"; then
    fail "крэш во время бута"; break
  fi
  if echo "$snap" | grep -qE "$READY_RE"; then
    ready="$(echo "$snap" | grep -E "$PROGRESS_RE" | tail -8)"
    break
  fi
  sleep 2
done
if [ -n "$ready" ]; then
  log "Бут завершён, фазы:"
  echo "$ready" | tee -a "$REPORT"
  sleep 8  # дать сцене дорисоваться перед скриншотом
elif [ "$FAIL_COUNT" -eq 0 ]; then
  fail "маркер готовности не появился за ${READY_TIMEOUT}с"
  progress="$("$ADB" -s "$SERIAL" logcat -d -s Unity 2>/dev/null | grep -E "$PROGRESS_RE" | tail -5)"
  if [ -n "$progress" ]; then
    log "…но бут шёл, последние фазы:"; echo "$progress" | tee -a "$REPORT"
  else
    log "…Unity-логов нет вообще (движок не стартовал)"
  fi
fi

"$ADB" -s "$SERIAL" exec-out screencap -p >"$OUT/01-boot.png" 2>/dev/null || true
log "Скриншот бута: 01-boot.png ($(du -h "$OUT/01-boot.png" 2>/dev/null | cut -f1))"

# ── 4. Monkey ───────────────────────────────────────────────────────────────
if [ "$RUN_MONKEY" = 1 ] && [ "$FAIL_COUNT" -eq 0 ]; then
  log "Monkey: $EVENTS событий, seed=${SEED}..."
  if ! "$ADB" -s "$SERIAL" shell monkey -p "$PKG" -s "$SEED" \
      --throttle 150 --ignore-timeouts --pct-syskeys 0 \
      "$EVENTS" >"$OUT/monkey.log" 2>&1; then
    fail "monkey завершился с ошибкой (см. monkey.log)"
  fi
  grep -E "// CRASH|// NOT RESPONDING|aborted" "$OUT/monkey.log" >/dev/null 2>&1 \
    && fail "monkey поймал крэш/ANR (см. monkey.log)"
  "$ADB" -s "$SERIAL" exec-out screencap -p >"$OUT/02-after-monkey.png" 2>/dev/null || true
  log "Monkey завершён, скриншот: 02-after-monkey.png"
fi

# ── 5. Сбор логов и вердикт ─────────────────────────────────────────────────
"$ADB" -s "$SERIAL" logcat -d >"$OUT/logcat.txt" 2>/dev/null || true
"$ADB" -s "$SERIAL" logcat -d -b crash >"$OUT/logcat-crash.txt" 2>/dev/null || true

if grep -qE "$CRASH_RE" "$OUT/logcat.txt" "$OUT/logcat-crash.txt" 2>/dev/null; then
  fail "в logcat есть крэш-сигнатуры:"
  grep -hE "$CRASH_RE" "$OUT/logcat.txt" "$OUT/logcat-crash.txt" | head -5 | tee -a "$REPORT"
fi
if ! "$ADB" -s "$SERIAL" shell pidof "$PKG" >/dev/null 2>&1; then
  fail "процесс $PKG мёртв в конце прогона"
fi

if [ "$FAIL_COUNT" -eq 0 ]; then
  log "PASS: бут чистый, monkey $EVENTS событий пережит, процесс жив."
  exit 0
else
  log "ИТОГ: $FAIL_COUNT провал(ов):$FAIL_LIST"
  exit 1
fi
