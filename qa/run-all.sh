#!/usr/bin/env bash
# qa/run-all.sh — ОДНА команда после изменений: «ничего не сломалось?»
#
#   qa/run-all.sh            ОБА набора: EditMode (юнит+контракт+соук) и
#                            PlayMode (сцена, бут, смоук) — цикл после правок
#   qa/run-all.sh --editmode только EditMode: быстрая итерация, пока пишешь
#   qa/run-all.sh --playmode только PlayMode
#   qa/run-all.sh --device   + сборка dev-APK и смоук на эмуляторе против
#                            локального сервера :8099 (медленно, ~15 мин)
#   qa/run-all.sh --filter "Fixture1;Fixture2"   только выбранные фикстуры
#
# Выход 0 = зелёно. Отчёты в qa/reports/<штамп>-runall/.
#
# ОТКРЫТЫЙ РЕДАКТОР НЕ МЕШАЕТ: TestHost — отдельный проект, и игра, открытая
# в редакторе, батчу не помеха. Мешают только двое: другой batchmode на том же
# TestHost (его ждём) и редактор, открытый НА САМОМ TestHost (тогда выходим с
# объяснением — batchmode такой проект не возьмёт).
set -u -o pipefail

UNITY="/Applications/Unity/Hub/Editor/6000.4.5f1/Unity.app/Contents/MacOS/Unity"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="$REPO_ROOT/qa/reports/$STAMP-runall"
mkdir -p "$OUT"

DEVICE=0
FILTER=""
RUN_EDIT=1
RUN_PLAY=1
while [ $# -gt 0 ]; do
  case "$1" in
    --device) DEVICE=1; shift ;;
    --editmode) RUN_PLAY=0; shift ;;
    --playmode) RUN_EDIT=0; shift ;;
    --filter) FILTER="$2"; shift 2 ;;
    *) echo "неизвестный аргумент: $1"; exit 2 ;;
  esac
done

fail=0
log() { echo "[$(date +%H:%M:%S)] $*"; }

# Другой batchmode на TestHost — ждём его: прогон-в-прогон роняет оба.
waited=0
while pgrep -f "batchmode.*TestHost" >/dev/null 2>&1; do
  [ "$waited" = 0 ] && log "TestHost занят другим прогоном — жду…"
  sleep 5; waited=$((waited + 5))
  if [ "$waited" -ge 1800 ]; then
    echo "FAIL: TestHost занят полчаса — что-то повисло"; exit 1
  fi
done

if [ -f "$REPO_ROOT/unity/TestHost/Temp/UnityLockfile" ] && pgrep -x Unity >/dev/null 2>&1; then
  echo "FAIL: TestHost открыт в редакторе — закрой ЕГО (игру можно не трогать)"; exit 1
fi

# ── 0a. СТРАЖИ ФОРМЫ И ЯЗЫКА (Go) ──────────────────────────────────────────
# Их десятки, и держат они то, чего Unity-прогон не видит вовсе: дубли в C#,
# согласие документации с кодом, единый диалект двух компиляторов, набор команд
# у двух рендереров браузера, читаемость диагностики. Цикл назывался «ничего не
# сломалось?» и при этом их не запускал — можно было увидеть зелёное, не
# проверив ни одного.
#
# LVN_REQUIRE_NODE: на машине с node стражам языка запрещено пропускаться
# молча; без node они честно скипнутся сами.
if command -v go >/dev/null 2>&1; then
  for mod in tools/lvnconv server; do
    # -count=1 обязателен: стражи читают C#, JS и манифесты — файлы, которых
    # кэш go test не видит. Без флага правка в Unity ломает инвариант, а прогон
    # отвечает «ok (cached)»: страж молчит ровно тогда, когда должен кричать.
    log "go test $mod"
    if command -v node >/dev/null 2>&1; then
      (cd "$REPO_ROOT/$mod" && LVN_REQUIRE_NODE=1 go test -count=1 ./... >/dev/null 2>&1) \
        || { log "FAIL: go test $mod — подробности: (cd $mod && go test ./...)"; fail=1; }
    else
      (cd "$REPO_ROOT/$mod" && go test -count=1 ./... >/dev/null 2>&1) \
        || { log "FAIL: go test $mod — подробности: (cd $mod && go test ./...)"; fail=1; }
    fi
  done
else
  log "WARN: go не найден — стражи формы и языка не проверены"
fi

# ── 0. Go-сервер для PlayMode-смоука (BootSmokeTests поднимает его сам) ─────
mkdir -p "$REPO_ROOT/qa/bin"
if command -v go >/dev/null 2>&1; then
  go build -o "$REPO_ROOT/qa/bin/lvnserver-test" "$REPO_ROOT/server" \
    || { log "WARN: go build сервера не удался — PlayMode-смоук скипнется"; }
fi

report_platform() { # $1 = имя, $2 = xml
python3 - "$2" "$1" <<'PY'
import sys, xml.etree.ElementTree as ET
try:
    r = ET.parse(sys.argv[1]).getroot()
except Exception as e:
    print(f"  {sys.argv[2]}: нет результатов ({e})"); sys.exit(1)
total, passed, failed = r.get('total'), r.get('passed'), r.get('failed')
print(f"  {sys.argv[2]}: {passed}/{total} passed, {failed} failed")
for tc in r.iter('test-case'):
    if tc.get('result') not in (None, 'Passed', 'Skipped'):
        print("   ", tc.get('result'), tc.get('fullname'))
sys.exit(0 if failed == '0' else 1)
PY
}

# ── 1. EditMode: вся пирамида (юнит + контракт + соук) ──────────────────────
if [ "$RUN_EDIT" = 1 ]; then
log "EditMode-прогон…"
args=(-batchmode -nographics -projectPath "$REPO_ROOT/unity/TestHost"
      -runTests -testPlatform EditMode
      -testResults "$OUT/editmode.xml" -logFile "$OUT/editmode.log")
[ -n "$FILTER" ] && args+=(-testFilter "$FILTER")
"$UNITY" "${args[@]}" >/dev/null 2>&1
report_platform editmode "$OUT/editmode.xml" || fail=1
fi

# ── 1b. PlayMode: интеграция (сцена, бут NovelApp против живого сервера) ─────
# НЕ опционально и не «когда вспомним»: EditMode не поднимает ни сцену, ни
# UI-панель, поэтому целый класс регрессий виден только здесь. Красный
# PlayMode-тест простоял незамеченным ровно потому, что цикл его не гонял.
if [ "$RUN_PLAY" = 1 ]; then
log "PlayMode-прогон…"
args=(-batchmode -nographics -projectPath "$REPO_ROOT/unity/TestHost"
      -runTests -testPlatform PlayMode
      -testResults "$OUT/playmode.xml" -logFile "$OUT/playmode.log")
[ -n "$FILTER" ] && args+=(-testFilter "$FILTER")
"$UNITY" "${args[@]}" >/dev/null 2>&1
report_platform playmode "$OUT/playmode.xml" || fail=1
fi

# ── 2. Девайс-смоук (опционально) ───────────────────────────────────────────
if [ "$DEVICE" = 1 ]; then
  APK="${LVN_QA_APK:-$REPO_ROOT/qa/bin/sandbox-qa-dev.apk}"
  if [ ! -f "$APK" ]; then
    log "Собираю dev-APK (LVN_BUILD_DEV=1)…"
    LVN_BUILD_OUT="$APK" LVN_BUILD_DEV=1 \
      "$UNITY" -batchmode -nographics -projectPath "$REPO_ROOT/sandbox" \
        -buildTarget Android -executeMethod Lvn.EditorTools.CliBuild.Android \
        -quit -logFile "$OUT/apk-build.log" >/dev/null 2>&1 || { log "FAIL: сборка APK"; fail=1; }
  fi
  if [ -f "$APK" ]; then
    log "Поднимаю тестовый сервер :8099…"
    go build -o "$OUT/lvnserver" "$REPO_ROOT/server" || { log "FAIL: go build server"; fail=1; }
    "$OUT/lvnserver" -addr :8099 -content "$REPO_ROOT/server/content" >"$OUT/server.log" 2>&1 &
    SRV=$!
    trap '[ -n "${SRV:-}" ] && kill $SRV 2>/dev/null' EXIT
    sleep 1
    log "Смоук APK на эмуляторе…"
    "$REPO_ROOT/qa/monkey.sh" "$APK" --server http://127.0.0.1:8099 \
      | tee "$OUT/device-smoke.log" | tail -3 || fail=1
    kill $SRV 2>/dev/null; SRV=""
  fi
fi

if [ "$fail" = 0 ]; then log "RUN-ALL PASS — отчёты: $OUT"; else log "RUN-ALL FAIL — отчёты: $OUT"; fi
exit $fail
