#!/usr/bin/env bash
# АДМИНКА ЗАКРЫТА НА ЗАМОК — ПРОВЕРЕНО ПО ПРОВОДУ, А НЕ ПО КОДУ.
#
# За админскими ручками стоит всё, чем живёт игра: публикация глав и манифеста,
# запись любых файлов контента, выдача валюты, чужие сейвы, заказы, учётки
# панели, импорт. Одна ручка, забывшая спросить пропуск, — это чужая казна и
# подменённая глава у всех игроков разом.
#
# СПИСОК РУЧЕК БЕРЁТСЯ ИЗ КОДА, а не переписывается сюда: страж со своим
# списком сторожит список, а не предмет, и новая ручка появилась бы вне
# проверки молча. Здесь `grep` по регистрации маршрутов — что зарегистрировано,
# то и проверяется.
#
# Проверяется три вещи, и каждая отвечает на свой вопрос:
#   ОТКАЗ       ни одна ручка не отвечает данными без пропуска;
#   БЕЗДЕЙСТВИЕ отказ означает «ничего не произошло», а не «сделал и промолчал»;
#   ЗАМОК ЖИВОЙ верный пропуск пускает — иначе «всё закрыто» ничего не значит.
#
#   qa/admin-lock-check.sh [-bite]
#
# -bite шлёт ВЕРНЫЙ пропуск во все запросы: стенд обязан назвать ручки
# открытыми. Мерка, не отличающая открытую дверь от закрытой, бесполезна.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go   >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }

W="$(mktemp -d)"; PID=""
cleanup() { [ -n "$PID" ] && kill "$PID" 2>/dev/null; rm -rf "$W"; }
trap cleanup EXIT

go build -C server -o "$W/lvnserver" . || { echo "сервер не собрался"; exit 1; }

PORT=0
for p in 8121 8123 8125 8127; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

TOKEN="stand-token-$$"
mkdir -p "$W/content/scripts"
printf '{"titles":[]}' > "$W/content/manifest.json"
"$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$W/content" -admin-token "$TOKEN" \
  >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 50); do
  curl -fsS -m 1 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 && break
  sleep 0.2
done
curl -fsS -m 2 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 \
  || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

B="http://127.0.0.1:$PORT"
# Пропуск подставляется только в укусе — в обычном прогоне стучимся с улицы.
AUTH=(); [ -n "$BITE" ] && AUTH=(-H "Authorization: Bearer $TOKEN")

routes="$(grep -rhoE 'mux\.HandleFunc\("/v1/admin/[^"]*"' server/*.go \
          | sed 's/mux.HandleFunc("//; s/"$//' | sort -u)"
count="$(printf '%s\n' "$routes" | wc -l | tr -d ' ')"
[ "$count" -ge 20 ] || { echo "ручек нашлось всего $count — якорь разбора промахнулся"; exit 2; }

open_doors=""
for path in $routes; do
  # Выход из сессии открыт намеренно: уйти можно всегда, и делать ему нечего.
  case "$path" in */session/logout) continue;; esac
  for method in GET POST; do
    code="$(curl -s -o "$W/body" -w '%{http_code}' -m 6 -X "$method" \
            -H 'Content-Type: application/json' -d '{}' "${AUTH[@]}" "$B$path")"
    case "$code" in
      401|403|405|404) ;;                        # отказ — то, что нужно
      *) open_doors="$open_doors\n  $method $path → $code";;
    esac
  done
done

# БЕЗДЕЙСТВИЕ: отказ обязан означать, что ничего не произошло.
curl -s -o /dev/null -m 6 -X POST "${AUTH[@]}" "$B/v1/admin/agent/publish" \
  -H 'Content-Type: application/json' \
  -d '{"id":"probe","name":"Проба","chapter":1,"lvns":"scene p\n\nГолос: строка\n-> __end\n"}'
wrote="$(ls "$W/content/scripts" 2>/dev/null | wc -l | tr -d ' ')"

# ЗАМОК ЖИВОЙ: верный пропуск обязан пускать.
with_token="$(curl -s -o /dev/null -w '%{http_code}' -m 6 \
              -H "Authorization: Bearer $TOKEN" "$B/v1/admin/users")"

if [ -n "$BITE" ]; then
  if [ -n "$open_doors" ] || [ "$wrote" != "0" ]; then
    echo "укус замечен: с верным пропуском стенд видит ручки открытыми — отличать он умеет"
    exit 0
  fi
  echo "СТЕНД СЛЕП: даже с верным пропуском все двери «закрыты» — он ничего не проверяет"
  exit 2
fi

echo "  ручек проверено: $count (по две пробы на каждую)"
echo "  запись без пропуска: файлов в scripts $wrote"
echo "  верный пропуск: /v1/admin/users → $with_token"

fail=0
if [ -n "$open_doors" ]; then
  echo "РВЁТСЯ: ручка отвечает без пропуска:$(printf '%b' "$open_doors")"
  fail=1
fi
[ "$wrote" = "0" ] || { echo "РВЁТСЯ: публикация без пропуска ЗАПИСАЛА главу — отказ был только на словах"; fail=1; }
[ "$with_token" = "200" ] || { echo "РВЁТСЯ: верный пропуск не пускает ($with_token) — замок заклинило, проверка ничего не значит"; fail=1; }

# ОБХОДЫ: пустой и чужой пропуск, другая схема, свой заголовок, запрос в строке.
for probe in "Authorization: Bearer " "Authorization: Bearer чужой" \
             "Authorization: bearer $TOKEN" "X-Admin-Token: $TOKEN"; do
  code="$(curl -s -o /dev/null -w '%{http_code}' -m 6 -H "$probe" "$B/v1/admin/users")"
  [ "$code" = "401" ] || [ "$code" = "403" ] || {
    echo "РВЁТСЯ: обход «${probe%%:*}» дал $code"; fail=1; }
done
code="$(curl -s -o /dev/null -w '%{http_code}' -m 6 "$B/v1/admin/users?token=$TOKEN")"
[ "$code" = "401" ] || [ "$code" = "403" ] || { echo "РВЁТСЯ: пропуск принят из строки запроса ($code)"; fail=1; }

# ПОДБОР ПАРОЛЯ: попытки обязаны упереться в потолок, а не идти вечно.
throttled=0
for _ in $(seq 1 12); do
  c="$(curl -s -o /dev/null -w '%{http_code}' -m 6 -X POST "$B/v1/admin/session/login" \
       -H 'Content-Type: application/json' -d '{"login":"admin","password":"неверный"}')"
  [ "$c" = "429" ] && throttled=1
done
[ "$throttled" = "1" ] || { echo "РВЁТСЯ: двенадцать неверных паролей подряд и ни одного отказа по частоте"; fail=1; }

# Пропуск не обязан светиться в журнале сервера.
if grep -q "$TOKEN" "$W/server.log" 2>/dev/null; then
  echo "РВЁТСЯ: пропуск попал в журнал сервера — он утечёт с логами"
  fail=1
fi

[ "$fail" = "0" ] || exit 1
echo "держит: без пропуска ни одна ручка не отвечает и ничего не пишет; подбор упирается в потолок; пропуск в журнал не попадает"
