#!/usr/bin/env bash
# qa/prebuild.sh — ВОРОТА ПЕРЕД СБОРКОЙ: собирать APK можно, только когда это зелено.
#
#   qa/prebuild.sh            стражи Go, EditMode, PlayMode С ГРАФИКОЙ, кадры
#                             панелей и сверка с эталонами
#   qa/prebuild.sh --quick    без PlayMode (когда правили только сервер/Go)
#
# Зачем отдельно от run-all: run-all — «ничего не сломалось?» после правки,
# а здесь — «можно ли это отдавать людям». Разница в двух вещах. Кадры
# панелей (LVN_TEST_SHOTS) пишутся ВСЕГДА и остаются в отчёте — перед сборкой
# на них смотрят глазами. PlayMode в run-all уже идёт с графикой; здесь
# LVN_PREBUILD дополнительно запрещает пропуск новых проверок раскладки,
# а отсутствующий обязательный эталон всегда считается ошибкой.
#
# Эталоны лежат в unity/Packages/com.lvn.engine/Tests/Runtime/Golden/. Ушёл
# кадр — тест падает и кладёт <имя>-diff.png рядом с кадрами; если так и
# задумано, эталон переписывают ОТДЕЛЬНЫМ коммитом:
#   LVN_GOLDEN_WRITE=1 qa/run-all.sh --playmode --filter "DownloadHudPanelTests"
#
# Стенд ui-lab (кадры главной с настоящим артом) сюда не входит: арт облика
# живёт в контенте, не в репо. Его сверяют руками по readme-shots/stage/.
set -u -o pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STAMP="$(date +%Y%m%d-%H%M%S)"
SHOTS="$REPO_ROOT/qa/reports/$STAMP-prebuild-shots"
mkdir -p "$SHOTS"

# Без массива: пустой массив под set -u в bash 3.2 (macOS, наш раннер) —
# «unbound variable», на этом уже спотыкался admin-lock-check.sh.
MODE=""
[ "${1:-}" = "--quick" ] && MODE="--editmode"

echo "[prebuild] кадры панелей и разницы с эталонами: $SHOTS"
# shellcheck disable=SC2086  # MODE — один флаг или пусто, разбиение по словам и нужно
LVN_PREBUILD=1 LVN_TEST_SHOTS="$SHOTS" bash "$REPO_ROOT/qa/run-all.sh" $MODE
status=$?

shots=$(ls "$SHOTS" 2>/dev/null | wc -l | tr -d ' ')
if [ "$status" -ne 0 ]; then
  echo "[prebuild] КРАСНО — собирать нельзя. Кадров: $shots; разницы (*-diff.png) там же, если ушли эталоны."
  exit "$status"
fi
echo "[prebuild] зелено — можно собирать. Кадров для глаз: $shots в $SHOTS"
