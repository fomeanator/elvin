#!/usr/bin/env bash
# ВСПЛЕСК РЕАКЦИИ: ВСЕ УЗНАЛИ О ПРАВКЕ ОДНОВРЕМЕННО.
#
# Опрос проверен отдельно (qa/poll-load-check.sh) и дёшев: при тишине это 304
# без тела. Опасен не он, а МОМЕНТ ПРАВКИ. Смена версии — событие, общее для
# всех: клиенты узнают о ней в пределах одного интервала опроса и разом идут за
# разницей, а следом за изменившимся файлом. Пик здесь не размазан по времени,
# он совпадает у всех по построению, и никакое дрожание интервала его не
# сгладит — сглаживать надо саму реакцию.
#
# Стенд повторяет тракт настоящего клиента (NovelApp.OnContentChangedAsync):
#   1. опрос /v1/content/version с честным If-None-Match
#   2. версия сменилась → GET /v1/content/changes?since=<прежняя>
#   3. каталог в разнице → GET /v1/content/manifest (иначе НЕ ходим)
#   4. перечитать открытую главу → GET /content/scripts/<глава>.lvn
#
# ПРЕЖНИЙ ТРАКТ МЕРЯЕТСЯ ТУТ ЖЕ, а не пересчитывается на бумаге: режим -old
# заставляет клиентов делать то, что они делали до разницы — тянуть карту
# версий и манифест целиком. Два числа рядом показывают, что разница купила
# ПОД НАГРУЗКОЙ, а не на одного клиента.
#
# Дерево контента синтетическое, но не игрушечное: полторы тысячи файлов, чтобы
# карта версий имела настоящий вес. На пустом каталоге сравнение было бы про
# ничто.
#
# ПРОВЕРКА ОБЯЗАНА УМЕТЬ ПАДАТЬ: пол на число клиентов, дошедших до реакции.
# Стенд, где никто не отреагировал, отрапортовал бы «пик крошечный» и соврал.
#
#   qa/reaction-burst-check.sh [-old]
set -uo pipefail
cd "$(dirname "$0")/.."
OLD=""; [ "${1:-}" = "-old" ] && OLD=1

command -v go >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }

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

TOKEN="burst-$$"
mkdir -p "$W/content/scripts" "$W/content/art"
echo '{"titles":[]}' > "$W/content/manifest.json"
python3 - "$W/content/art" <<'PY'
import os, sys
d = sys.argv[1]
for i in range(1500):
    open(os.path.join(d, "a%04d.bin" % i), "wb").write(b"x" * 64)
PY
"$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$W/content" -admin-token "$TOKEN" \
  >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 60); do probe "$PORT" && break; sleep 0.2; done
probe "$PORT" || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

mkdir -p "$W/load"
cat > "$W/load/go.mod" <<'EOF'
module burst

go 1.21
EOF
cat > "$W/load/main.go" <<'EOF'
// Повторяет тракт NovelApp.OnContentChangedAsync под нагрузкой.
package main

import (
	"encoding/json"
	"flag"
	"io"
	"net/http"
	"os"
	"sort"
	"sync"
	"time"
)

type out struct {
	Reacted    int     `json:"reacted"`
	Requests   int     `json:"requests"`
	Bytes      int64   `json:"bytes"`
	PeakReqSec int     `json:"peak_req_sec"`
	PeakBytes  int64   `json:"peak_bytes_sec"`
	P99Ms      float64 `json:"p99_ms"`
	Errors     int     `json:"errors"`
}

func main() {
	base := flag.String("base", "", "")
	n := flag.Int("clients", 120, "")
	every := flag.Duration("every", 500*time.Millisecond, "")
	dur := flag.Duration("for", 10*time.Second, "")
	oldWay := flag.Bool("old", false, "прежний тракт: карта версий + манифест целиком")
	chapter := flag.String("chapter", "/content/scripts/burst-ch01.lvn", "")
	flag.Parse()

	var mu sync.Mutex
	o := out{}
	lat := []float64{}
	reqSec := map[int64]int{}
	byteSec := map[int64]int64{}
	start := time.Now()

	get := func(cl *http.Client, url, etag string) (int, string, []byte) {
		t0 := time.Now()
		req, _ := http.NewRequest("GET", url, nil)
		if etag != "" {
			req.Header.Set("If-None-Match", etag)
		}
		resp, err := cl.Do(req)
		if err != nil {
			mu.Lock()
			o.Errors++
			mu.Unlock()
			return 0, "", nil
		}
		body, _ := io.ReadAll(resp.Body)
		resp.Body.Close()
		sec := int64(time.Since(start) / time.Second)
		mu.Lock()
		o.Requests++
		o.Bytes += int64(len(body))
		reqSec[sec]++
		byteSec[sec] += int64(len(body))
		lat = append(lat, float64(time.Since(t0).Microseconds())/1000)
		mu.Unlock()
		return resp.StatusCode, resp.Header.Get("ETag"), body
	}

	var wg sync.WaitGroup
	for i := 0; i < *n; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			time.Sleep(time.Duration(i) * *every / time.Duration(*n))
			cl := &http.Client{Timeout: 20 * time.Second}
			etag, prev, done := "", "", false
			for time.Since(start) < *dur {
				code, e, body := get(cl, *base+"/v1/content/version", etag)
				if code == 200 && etag != "" && e != etag && !done {
					done = true
					mu.Lock()
					o.Reacted++
					mu.Unlock()
					if *oldWay {
						// Так было до разницы: карта версий и каталог целиком.
						get(cl, *base+"/content/asset-versions.json", "")
						get(cl, *base+"/v1/content/manifest", "")
					} else {
						_, _, d := get(cl, *base+"/v1/content/changes?since="+prev, "")
						var delta struct {
							Changed map[string]string `json:"changed"`
							Full    bool              `json:"full"`
						}
						_ = json.Unmarshal(d, &delta)
						if delta.Full {
							get(cl, *base+"/content/asset-versions.json", "")
						}
						if _, ok := delta.Changed["manifest.json"]; ok || delta.Full {
							get(cl, *base+"/v1/content/manifest", "")
						}
					}
					// Открытую главу перечитывают в обоих трактах.
					get(cl, *base+*chapter, "")
				}
				if code == 200 && body != nil {
					var v struct {
						Version string `json:"version"`
					}
					if json.Unmarshal(body, &v) == nil && v.Version != "" {
						prev = v.Version
					}
				}
				if e != "" {
					etag = e
				}
				time.Sleep(*every)
			}
		}(i)
	}
	wg.Wait()

	sort.Float64s(lat)
	if len(lat) > 0 {
		o.P99Ms = lat[len(lat)*99/100]
	}
	for _, c := range reqSec {
		if c > o.PeakReqSec {
			o.PeakReqSec = c
		}
	}
	for _, b := range byteSec {
		if b > o.PeakBytes {
			o.PeakBytes = b
		}
	}
	_ = json.NewEncoder(os.Stdout).Encode(o)
}
EOF
( cd "$W/load" && go build -o "$W/burstbin" . ) || { echo "клиент не собрался"; exit 1; }

pub() {
  curl -sS -o /dev/null -X POST "http://127.0.0.1:$PORT/v1/admin/agent/publish" \
    -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"id\":\"burst\",\"name\":\"Всплеск\",\"chapter\":1,\"lvns\":\"scene burst\\n\\nМира: Редакция $1.\\n-> __end\\n\"}"
}
pub 1

settle() {
  local prev="" cur="" stable=0
  for _ in $(seq 1 80); do
    cur="$(curl -fsS "http://127.0.0.1:$PORT/v1/content/version" 2>/dev/null)"
    if [ -n "$cur" ] && [ "$cur" = "$prev" ]; then
      stable=$((stable+1)); [ "$stable" -ge 6 ] && return 0
    else stable=0; fi
    prev="$cur"; python3 -c "import time;time.sleep(0.5)"
  done
  echo "версия не устоялась"; return 1
}
settle || exit 1

echo "карта версий: $(curl -sS "http://127.0.0.1:$PORT/content/asset-versions.json" | wc -c | tr -d ' ') байт"
echo "манифест:     $(curl -sS "http://127.0.0.1:$PORT/v1/content/manifest" | wc -c | tr -d ' ') байт"

CLIENTS="${LVN_CLIENTS:-120}"
( sleep 4; pub 2 ) &
FLAGS=""; [ -n "$OLD" ] && FLAGS="-old"
"$W/burstbin" -base "http://127.0.0.1:$PORT" -clients "$CLIENTS" -for 10s $FLAGS \
  > "$W/res.json" 2>"$W/err.txt"
[ -s "$W/res.json" ] || { echo "нагрузка не отработала:"; tail -3 "$W/err.txt"; exit 1; }

python3 - "$W/res.json" "$CLIENTS" "${OLD:-}" <<'PY'
import json, sys
r = json.load(open(sys.argv[1])); clients = int(sys.argv[2]); old = bool(sys.argv[3])
print("тракт: %s, клиентов %d" % ("ПРЕЖНИЙ (каталог целиком)" if old else "разница", clients))
print("  отреагировали:       %d" % r["reacted"])
print("  запросов всего:      %d" % r["requests"])
print("  трафик всего:        %.1f МБ" % (r["bytes"] / 1048576))
print("  ПИК запросов в сек:  %d" % r["peak_req_sec"])
print("  ПИК байт в сек:      %.1f МБ" % (r["peak_bytes_sec"] / 1048576))
print("  задержка p99:        %.1f мс" % r["p99_ms"])
print("  ошибок:              %d" % r["errors"])

bad = []
if r["reacted"] < clients * 0.9:
    bad.append("отреагировали лишь %d из %d — «пик мал» означало бы, что всплеска не было"
               % (r["reacted"], clients))
if r["errors"] > 0:
    bad.append("сервер отдал %d ошибок под всплеском" % r["errors"])
for b in bad: print("  ✗ " + b)
sys.exit(1 if bad else 0)
PY
