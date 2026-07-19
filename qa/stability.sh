#!/usr/bin/env bash
# qa/stability.sh — ловля флейков: «раз на 10 отваливается, а я не замечаю».
#
#   qa/stability.sh [N] [--filter "Fixture1;Fixture2"]
#
# Гоняет EditMode-набор N раз подряд (по умолчанию 5). Каждый прогон трясёт
# соук-бот НОВЫМИ сидами (LVN_SOAK_SEED_BASE=1000*i), так что N прогонов —
# это 3N разных случайных прохождений каждого скрипта. В конце — таблица:
# тест → в скольких прогонах падал. Падение «иногда» = флейк, «всегда» = баг.
# Выход 0 = все N прогонов зелёные. Отчёты в qa/reports/<штамп>-stability/.
set -u -o pipefail

UNITY="/Applications/Unity/Hub/Editor/6000.4.5f1/Unity.app/Contents/MacOS/Unity"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="$REPO_ROOT/qa/reports/$STAMP-stability"
mkdir -p "$OUT"

N=5
FILTER=""
while [ $# -gt 0 ]; do
  case "$1" in
    --filter) FILTER="$2"; shift 2 ;;
    *) N="$1"; shift ;;
  esac
done

if pgrep -x Unity >/dev/null 2>&1; then
  echo "FAIL: редактор Unity открыт — batchmode-прогон невозможен"; exit 1
fi

for i in $(seq 1 "$N"); do
  echo "[$(date +%H:%M:%S)] прогон $i/$N (seed base $((1000 * i)))…"
  args=(-batchmode -nographics -projectPath "$REPO_ROOT/unity/TestHost"
        -runTests -testPlatform EditMode
        -testResults "$OUT/run-$i.xml" -logFile "$OUT/run-$i.log")
  [ -n "$FILTER" ] && args+=(-testFilter "$FILTER")
  LVN_SOAK_SEED_BASE=$((1000 * i)) "$UNITY" "${args[@]}" >/dev/null 2>&1 || true
done

python3 - "$OUT" "$N" <<'PY'
import glob, os, sys, xml.etree.ElementTree as ET
out, n = sys.argv[1], int(sys.argv[2])
runs, fails = 0, {}
for path in sorted(glob.glob(os.path.join(out, "run-*.xml"))):
    try:
        root = ET.parse(path).getroot()
    except Exception:
        print(f"  {os.path.basename(path)}: XML не родился (прогон умер) — считаю прогон провальным")
        fails.setdefault("<прогон без результатов>", []).append(os.path.basename(path))
        continue
    runs += 1
    for tc in root.iter("test-case"):
        if tc.get("result") not in (None, "Passed", "Skipped"):
            fails.setdefault(tc.get("fullname"), []).append(os.path.basename(path))
print(f"\nСтабильность: {runs}/{n} прогонов дали результаты")
if not fails:
    print("Флейков нет: все тесты зелёные во всех прогонах.")
    sys.exit(0)
print(f"{'тест':70} падений")
for name, where in sorted(fails.items(), key=lambda kv: -len(kv[1])):
    kind = "ВСЕГДА (баг)" if len(where) == runs and runs == n else "иногда (ФЛЕЙК)"
    print(f"  {name:68} {len(where)}/{n} — {kind} [{', '.join(where)}]")
sys.exit(1)
PY
rc=$?
echo "Отчёты: $OUT"
exit $rc
