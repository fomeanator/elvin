#!/usr/bin/env bash
# ПРОГРЕСС НА СЕРВЕРЕ: ПЕРЕЕЗД, ПЕРЕЗАПУСК И ДВА УСТРОЙСТВА СРАЗУ.
#
# Цена ошибки здесь та же, что у сейвов: потерянные часы игры не чинятся ни
# патчем, ни извинением. Обещаний три, и проверяются они только живьём:
#
#   ПЕРЕЕЗД      сохранил на одном устройстве — видно на другом;
#   ДОЛГОВЕЧНОСТЬ ответ {saved:true} пережил перезапуск процесса. Сервер пишет на
#                диск ДО ответа именно поэтому: клиент верит и не повторяет;
#   ГОНКА        два устройства пишут одновременно — ни одна подтверждённая
#                запись не пропадает молча.
#
# Соседние проверки (server/hardening_test.go) гоняют всё это ПОСЛЕДОВАТЕЛЬНО
# через httptest. Ровно то, ради чего заведён OCC — «проверил и записал» под
# настоящей параллельностью, — так не проверяется никогда.
#
# КАК ЛОВИТСЯ ПОТЕРЯННОЕ ОБНОВЛЕНИЕ. Счётчик версий для этого не годится: он
# растёт на каждой записи, в том числе на затирающей. Годится только сам
# документ: каждый писатель читает, ДОПИСЫВАЕТ свою метку и пишет обратно. В
# конце меток обязано быть столько же, сколько подтверждённых записей. Меньше —
# значит чьи-то часы игры стёрли и сказали «сохранено».
#
# ПРОВЕРКА ОБЯЗАНА УМЕТЬ ПАДАТЬ, и здесь это видно нагляднее обычного: -bite
# заставляет писателей НЕ слать версию. Это законный устаревший режим, и он
# по устройству теряет обновления. Стенд, не увидевший потери там, не увидел бы
# её нигде.
#
#   qa/state-durability-check.sh [-bite]
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go   >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }

W="$(mktemp -d)"; PID=""
cleanup() { [ -n "$PID" ] && kill "$PID" 2>/dev/null; rm -rf "$W"; }
trap cleanup EXIT

go build -C server -o "$W/lvnserver" . || { echo "сервер не собрался"; exit 1; }

PORT="${LVN_PORT:-8077}"
probe() { curl -fsS -m 1 "http://127.0.0.1:$1/healthz" >/dev/null 2>&1; }
if probe "$PORT"; then
  PORT=0
  for p in 8078 8079 8081 8082; do probe "$p" || { PORT=$p; break; }; done
  [ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }
fi

mkdir -p "$W/content"
echo '{"titles":[]}' > "$W/content/manifest.json"
BASE="http://127.0.0.1:$PORT"
USER="stand__title"

boot() {
  "$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$W/content" >>"$W/server.log" 2>&1 &
  PID=$!
  for _ in $(seq 1 60); do probe "$PORT" && return 0; sleep 0.2; done
  echo "сервер не поднялся:"; tail -5 "$W/server.log"; return 1
}
boot || exit 1

put() { curl -sS -o "$W/o.json" -w '%{http_code}' -X PUT "$BASE/v1/state?user=$USER" \
          -H 'Content-Type: application/json' -d "$1"; }
get() { curl -fsS "$BASE/v1/state?user=$USER"; }

fail=0
say() { echo "  $1"; }

# ── 1. ПЕРЕЕЗД ──────────────────────────────────────────────────────────────
code="$(put '{"vars":{"глава":3,"золото":120},"updatedAt":1}')"
[ "$code" = "200" ] || { say "✗ первое сохранение не прошло ($code)"; fail=1; }
doc="$(get)"
ver="$(printf '%s' "$doc" | python3 -c 'import json,sys;print(json.load(sys.stdin)["_version"])')"
gold="$(printf '%s' "$doc" | python3 -c 'import json,sys;print(json.load(sys.stdin)["vars"]["золото"])')"
say "переезд: другое устройство видит золото=$gold, версия=$ver"
[ "$gold" = "120" ] || { say "✗ прогресс не переехал"; fail=1; }

# ── 2. ДОЛГОВЕЧНОСТЬ ────────────────────────────────────────────────────────
kill "$PID" 2>/dev/null; wait "$PID" 2>/dev/null; PID=""
boot || exit 1
doc="$(get)"
gold2="$(printf '%s' "$doc" | python3 -c 'import json,sys;print(json.load(sys.stdin)["vars"]["золото"])')"
ver2="$(printf '%s' "$doc" | python3 -c 'import json,sys;print(json.load(sys.stdin)["_version"])')"
say "перезапуск: золото=$gold2, версия=$ver2"
{ [ "$gold2" = "120" ] && [ "$ver2" = "$ver" ]; } || { say "✗ подтверждённое сохранение не пережило перезапуск"; fail=1; }

# ── 3. УСТАРЕВШАЯ ЗАПИСЬ ────────────────────────────────────────────────────
code="$(put "{\"vars\":{\"золото\":200},\"updatedAt\":2,\"_version\":$ver}")"
[ "$code" = "200" ] || { say "✗ свежая запись отвергнута ($code)"; fail=1; }
code="$(put "{\"vars\":{\"золото\":1},\"updatedAt\":3,\"_version\":$ver}")"
say "устаревшая запись со второго устройства: код $code (ждём 409)"
[ "$code" = "409" ] || { say "✗ устаревшая запись затёрла бы чужой прогресс"; fail=1; }
now="$(get | python3 -c 'import json,sys;print(json.load(sys.stdin)["vars"]["золото"])')"
[ "$now" = "200" ] || { say "✗ после отказа документ испорчен (золото=$now)"; fail=1; }

# ── 4. ГОНКА ────────────────────────────────────────────────────────────────
mkdir -p "$W/race"
cat > "$W/race/go.mod" <<'EOF'
module race

go 1.21
EOF
cat > "$W/race/main.go" <<'EOF'
// Каждый писатель читает документ, ДОПИСЫВАЕТ свою метку и пишет обратно.
// Потерянное обновление видно только так: версии растут и на затирающей записи.
package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"sync"
	"time"
)

func main() {
	base := flag.String("base", "", "")
	user := flag.String("user", "", "")
	n := flag.Int("writers", 40, "")
	legacy := flag.Bool("legacy", false, "не слать _version (устаревший режим)")
	flag.Parse()

	var mu sync.Mutex
	acked, conflicts := 0, 0
	var wg sync.WaitGroup
	for i := 0; i < *n; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			cl := &http.Client{Timeout: 10 * time.Second}
			url := *base + "/v1/state?user=" + *user
			for try := 0; try < 200; try++ {
				resp, err := cl.Get(url)
				if err != nil {
					return
				}
				raw, _ := io.ReadAll(resp.Body)
				resp.Body.Close()
				var doc map[string]any
				if json.Unmarshal(raw, &doc) != nil {
					return
				}
				marks, _ := doc["marks"].(map[string]any)
				if marks == nil {
					marks = map[string]any{}
				}
				marks[fmt.Sprintf("w%03d", i)] = true
				doc["marks"] = marks
				if *legacy {
					delete(doc, "_version")
				}
				body, _ := json.Marshal(doc)
				req, _ := http.NewRequest("PUT", url, bytes.NewReader(body))
				req.Header.Set("Content-Type", "application/json")
				pr, err := cl.Do(req)
				if err != nil {
					return
				}
				io.Copy(io.Discard, pr.Body)
				pr.Body.Close()
				if pr.StatusCode == 200 {
					mu.Lock()
					acked++
					mu.Unlock()
					return
				}
				if pr.StatusCode == 409 {
					mu.Lock()
					conflicts++
					mu.Unlock()
					continue // перечитать и слить — ровно ради этого 409 и нужен
				}
				return
			}
		}(i)
	}
	wg.Wait()
	json.NewEncoder(os.Stdout).Encode(map[string]int{"acked": acked, "conflicts": conflicts})
}
EOF
( cd "$W/race" && go build -o "$W/racebin" . ) || { echo "гонщик не собрался"; exit 1; }

WRITERS="${LVN_WRITERS:-40}"
LEG=""; [ -n "$BITE" ] && LEG="-legacy"
"$W/racebin" -base "$BASE" -user "$USER" -writers "$WRITERS" $LEG > "$W/race.json"

marks="$(get | python3 -c 'import json,sys;print(len(json.load(sys.stdin).get("marks") or {}))')"
acked="$(python3 -c 'import json;print(json.load(open("'"$W"'/race.json"))["acked"])')"
conf="$(python3 -c 'import json;print(json.load(open("'"$W"'/race.json"))["conflicts"])')"
say "гонка: писателей $WRITERS, подтверждено $acked, отказов 409 — $conf, меток в документе $marks"

if [ -n "$BITE" ]; then
  if [ "$marks" -lt "$acked" ]; then
    say "укус: устаревший режим потерял $((acked - marks)) подтверждённых записей — стенд потерю ВИДИТ"
    exit 0
  fi
  say "✗ СТЕНД СЛЕП: в режиме без версий потери не нашлось, а она там по устройству"
  exit 2
fi

[ "$acked" -ge "$WRITERS" ] || { say "✗ подтверждено лишь $acked из $WRITERS — гонка не отработала"; fail=1; }
[ "$conf" -ge 1 ] || { say "✗ ни одного 409 — писатели не пересеклись, гонки не было"; fail=1; }
[ "$marks" = "$acked" ] || { say "✗ ПОТЕРЯНО $((acked - marks)) подтверждённых записей"; fail=1; }

[ "$fail" = "0" ] && { echo "ПРОГРЕСС ЦЕЛ — переезд, перезапуск и гонка"; exit 0; }
echo "ПРОГРЕСС ТЕРЯЕТСЯ"; exit 1
