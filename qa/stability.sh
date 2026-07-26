#!/usr/bin/env bash
# qa/stability.sh — ловля флейков: «раз на 10 отваливается, а я не замечаю».
#
#   qa/stability.sh [N] [--filter "Fixture1;Fixture2"] [--seed-base N]
#
# Гоняет EditMode-набор N раз подряд (по умолчанию 5). Каждый прогон трясёт
# соук-бот НОВЫМИ сидами (LVN_SOAK_SEED_BASE), так что N прогонов — это 3N
# разных случайных прохождений каждого скрипта. В конце — таблица: тест → в
# скольких прогонах падал. Падение «иногда» = флейк, «всегда» = баг.
# Выход 0 = все N прогонов зелёные. Отчёты в qa/reports/<штамп>-stability/.
#
# ЧТО ИМЕННО ПРИБИВАЕТ СИД. LVN_SOAK_SEED_BASE управляет ВЫБОРАМИ бота
# (SoakBotTests.SoakOne: `new System.Random(seed)` на выбор опции). Броски
# самого контента — rand()/chance() в выражениях — идут из отдельного потока
# Lvn.LvnExpression.Random. С 2026-07-26 этот поток инстансный, сеемый и
# сохраняемый (LvnRandom, снапшот несёт его в RngState), но соук его пока НЕ
# фиксирует: пока в SoakBotTests.SoakOne нет строки
#
#     LvnExpression.Random = new LvnRandom((ulong)seed);
#
# один и тот же сид на скрипте с rand() даёт разные прогоны, и таблица ниже
# не отличает флейк от «контент выбросил другое число». Отсюда --seed-base:
# упавший прогон переигрывается его же сидом, а не «примерно похожим».
set -u -o pipefail

UNITY="/Applications/Unity/Hub/Editor/6000.4.5f1/Unity.app/Contents/MacOS/Unity"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
N=5
FILTER=""
SEED_BASE=""   # пусто = штатная лестница 1000, 2000, 3000…
while [ $# -gt 0 ]; do
  case "$1" in
    --filter) FILTER="$2"; shift 2 ;;
    --seed-base) SEED_BASE="$2"; shift 2 ;;
    *) N="$1"; shift ;;
  esac
done

# Путь к редактору захардкожен выше и переживает апгрейды Unity ровно до
# первого. Молчаливая смерть batchmode здесь читалась как «ноль флейков»,
# поэтому проверяем ДО прогона и говорим прямо.
if [ ! -x "$UNITY" ]; then
  echo "FAIL: редактор не найден: $UNITY"
  echo "      поправь путь UNITY в qa/stability.sh (ls /Applications/Unity/Hub/Editor/)"
  exit 1
fi

if pgrep -x Unity >/dev/null 2>&1; then
  echo "FAIL: редактор Unity открыт — batchmode-прогон невозможен"; exit 1
fi

# Папку заводим ПОСЛЕ проверок — иначе каждый отбитый запуск («редактор
# открыт») оставлял пустой отчёт. Штамп секундной точности, а переигровка
# сида идёт сразу за ночным прогоном: два запуска в одну секунду делили папку,
# и агрегатор считал ЧУЖИЕ run-*.xml («3/1 прогонов дали результаты»).
STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="$REPO_ROOT/qa/reports/$STAMP-stability"
dup=2
while [ -e "$OUT" ]; do OUT="$REPO_ROOT/qa/reports/$STAMP-stability-$dup"; dup=$((dup + 1)); done
mkdir -p "$OUT"

# Сид прогона i: штатно 1000*i, а с --seed-base <S> — S, S+1000, S+2000…
# так что `qa/stability.sh 1 --seed-base 3000` — это ТОЧНО тот прогон, что
# упал третьим в ночной пятёрке.
seed_of() {
  if [ -n "$SEED_BASE" ]; then echo $((SEED_BASE + 1000 * ($1 - 1)));
  else echo $((1000 * $1)); fi
}

: > "$OUT/seeds.txt"
for i in $(seq 1 "$N"); do
  seed="$(seed_of "$i")"
  echo "run-$i.xml $seed" >> "$OUT/seeds.txt"
  echo "[$(date +%H:%M:%S)] прогон $i/$N (seed base $seed)…"
  args=(-batchmode -nographics -projectPath "$REPO_ROOT/unity/TestHost"
        -runTests -testPlatform EditMode
        -testResults "$OUT/run-$i.xml" -logFile "$OUT/run-$i.log")
  [ -n "$FILTER" ] && args+=(-testFilter "$FILTER")
  LVN_SOAK_SEED_BASE="$seed" "$UNITY" "${args[@]}" >/dev/null 2>&1 || true
done

python3 - "$OUT" "$N" "$FILTER" <<'PY'
import os, sys, xml.etree.ElementTree as ET
out, n, filt = sys.argv[1], int(sys.argv[2]), sys.argv[3]
# Читаем ИМЕННО прогоны этого запуска (seeds.txt), а не всё, что лежит в
# папке: список заодно несёт сид каждого прогона для переигровки.
seeds = {}
with open(os.path.join(out, "seeds.txt")) as f:
    for line in f:
        name, seed = line.split()
        seeds[name] = seed
runs, fails = 0, {}
for name in seeds:
    path = os.path.join(out, name)
    try:
        root = ET.parse(path).getroot()
    except Exception:
        print(f"  {name}: XML не родился (прогон умер) — считаю прогон провальным")
        fails.setdefault("<прогон без результатов>", []).append(name)
        continue
    runs += 1
    for tc in root.iter("test-case"):
        if tc.get("result") not in (None, "Passed", "Skipped"):
            fails.setdefault(tc.get("fullname"), []).append(name)
print(f"\nСтабильность: {runs}/{n} прогонов дали результаты")
# Недостающий прогон — это НЕ «нет флейков». glob() по отсутствующим файлам
# просто не итерируется, поэтому при упавшем Unity (например, после апгрейда
# редактора: путь к нему захардкожен выше) агрегатор раньше печатал «0/5» и
# выходил с кодом 0 — ночная ловля флейков навсегда зелёная и бесполезная.
if runs < n:
    print(f"ПРОВАЛ: {n - runs} прогон(ов) не дали результатов — Unity не запустился "
          f"или умер. Проверь путь к редактору в qa/stability.sh и логи {out}/run-*.log")
    sys.exit(1)
if not fails:
    print("Флейков нет: все тесты зелёные во всех прогонах.")
    sys.exit(0)
print(f"{'тест':70} падений")
for name, where in sorted(fails.items(), key=lambda kv: -len(kv[1])):
    kind = "ВСЕГДА (баг)" if len(where) == runs and runs == n else "иногда (ФЛЕЙК)"
    print(f"  {name:68} {len(where)}/{n} — {kind} [{', '.join(where)}]")
# Флейк без способа его переиграть — просто плохая новость. Печатаем готовую
# команду с сидом ИМЕННО того прогона, который упал. Фильтруем по КЛАССУ:
# имя параметризованного случая ("Soak(duel.lvn)") Unity разбирает как
# регулярку, и скобки в ней сами себя не матчат.
print("\nПереиграть упавший прогон:")
for name, where in sorted(fails.items(), key=lambda kv: -len(kv[1])):
    seed = seeds.get(where[0])
    if not seed:
        continue
    cls = name.split("(")[0].rsplit(".", 1)[0]
    f = filt if filt else cls
    print(f'  qa/stability.sh 1 --seed-base {seed} --filter "{f}"   # {name}')
sys.exit(1)
PY
rc=$?
echo "Отчёты: $OUT"
exit $rc
