#!/usr/bin/env bash
# РЕЖИМ БЕЗ ПРОВЕРОК ОБЯЗАН КРИЧАТЬ О СЕБЕ ПРИ ЗАПУСКЕ.
#
# У сервера есть два флага, снимающих доверие ЦЕЛИКОМ:
#
#   -iap-dev    любой чек принимается, и ОДИН И ТОТ ЖЕ можно предъявлять
#               сколько угодно раз (замерено: 500 + 500 + 500 = 1500);
#   -auth-dev   любая строка становится личностью, причём одна и та же — всегда
#               одним и тем же игроком: узнал чужую строку — стал этим человеком.
#
# Оба стартовали МОЛЧА, притом что про меньший риск (-wallet-earn) сервер
# говорил. Хуже: -auth-dev в том же блоке уже участвовал, но лишь чтобы
# приглушить соседнее замечание.
#
# ЧТО ИМЕННО ПРОВЕРЯЕТСЯ ЗДЕСЬ, а не в стражe исходников: что предупреждение
# ДОХОДИТ ДО ОПЕРАТОРА. Наличие log.Printf в коде и строка в терминале — разные
# вещи: между ними стоят условие, порядок инициализации и вывод.
#
# И ОБРАТНАЯ СТОРОНА: без флагов этих строк быть НЕ ДОЛЖНО. Предупреждение,
# которое горит всегда, через неделю перестают читать — и оно перестаёт быть
# предупреждением.
#
#   qa/dev-flags-check.sh
set -uo pipefail
cd "$(dirname "$0")/.."

command -v go   >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }

W="$(mktemp -d)"; PID=""
cleanup() { [ -n "$PID" ] && kill "$PID" 2>/dev/null; rm -rf "$W"; }
trap cleanup EXIT
go build -C server -o "$W/srv" . || { echo "сервер не собрался"; exit 1; }

PORT="${LVN_PORT:-8077}"
probe() { curl -fsS -m 1 "http://127.0.0.1:$1/healthz" >/dev/null 2>&1; }
if probe "$PORT"; then
  PORT=0
  for p in 8078 8079 8081 8082; do probe "$p" || { PORT=$p; break; }; done
  [ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }
fi
mkdir -p "$W/content"; echo '{"titles":[]}' > "$W/content/manifest.json"

run() { # $@ = флаги; печатает лог в $W/out
  probe "$PORT" && { echo "порт $PORT занят — замер говорил бы с чужим сервером"; exit 2; }
  "$W/srv" -addr "127.0.0.1:$PORT" -content "$W/content" "$@" >"$W/out" 2>&1 &
  PID=$!
  for _ in $(seq 1 60); do probe "$PORT" && break; sleep 0.2; done
  probe "$PORT" || { echo "сервер не поднялся:"; tail -5 "$W/out"; exit 1; }
  kill "$PID" 2>/dev/null; wait "$PID" 2>/dev/null; PID=""
  for _ in $(seq 1 50); do probe "$PORT" || return 0; sleep 0.2; done
  echo "порт не умер — на нём чужой сервер"; exit 2
}

fail=0
run -iap-dev -auth-dev
iap=$(grep -c -- "-iap-dev is ON" "$W/out" || true);  iap=${iap:-0}
aut=$(grep -c -- "-auth-dev is ON" "$W/out" || true); aut=${aut:-0}
echo "  с флагами:  про iap-dev $iap, про auth-dev $aut (ждём по одному)"
[ "$iap" -ge 1 ] || { echo "  ✗ -iap-dev включён и молчит: любой чек проходит и повторяется"; fail=1; }
[ "$aut" -ge 1 ] || { echo "  ✗ -auth-dev включён и молчит: любая строка становится личностью"; fail=1; }

run
iap=$(grep -c -- "-iap-dev is ON" "$W/out" || true);  iap=${iap:-0}
aut=$(grep -c -- "-auth-dev is ON" "$W/out" || true); aut=${aut:-0}
echo "  без флагов: про iap-dev $iap, про auth-dev $aut (ждём ноль)"
[ "$iap" = "0" ] && [ "$aut" = "0" ] || {
  echo "  ✗ предупреждение горит без флага — такое перестают читать через неделю"; fail=1; }

[ "$fail" = "0" ] && { echo "РЕЖИМЫ БЕЗ ПРОВЕРОК КРИЧАТ, ОСТАЛЬНОЕ МОЛЧИТ"; exit 0; }
echo "ПРЕДУПРЕЖДЕНИЯ НЕ РАБОТАЮТ"; exit 1
