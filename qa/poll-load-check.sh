#!/usr/bin/env bash
# ОПРОС РАЗ В 500 МС: ЧТО ОН СТОИТ СЕРВЕРУ И ЧТО ЗА НЕГО ПОКУПАЮТ.
#
# Требование звучало так: пусть клиенты спрашивают раз в 500 мс, «но так чтобы
# не убить сервак». В этом требовании два вопроса, и отвечать надо на оба:
#   ЦЕНА      выдержит ли сервер такую частоту;
#   ПОКУПКА   станет ли правка видна быстрее, чем при прежних двух секундах.
#
# Второй вопрос важнее и обычно не задаётся. Версия контента считается из кэша
# с TTL (server/main.go, verCacheTTL): много клиентов делят один обход дерева,
# и это защищает от шторма. Но тот же кэш кладёт ПОЛ на скорость реакции: чаще
# TTL правка появиться не может, сколько ни спрашивай. Опрос вчетверо чаще без
# смены TTL покупает нули — это надо ЗАМЕРИТЬ, а не вывести из константы.
#
# Стенд поднимает настоящий сервер, держит N клиентов с честным If-None-Match,
# публикует правку в известный момент и меряет:
#   доля 304          при тишине опрос обязан быть пустым
#   байт на клиента   во что обходится минута опроса
#   задержка правки   от публикации до первого не-304 у клиента
#   пик после правки  все клиенты узнают об изменении почти разом
#
# ПРОВЕРКА ОБЯЗАНА УМЕТЬ ПАДАТЬ. Стенд, не создавший нагрузки, отрапортует
# «сервер жив» и соврёт. Поэтому: пол на число запросов, пол на число клиентов,
# заметивших правку, а -bite НЕ публикует ничего и требует, чтобы правку не
# заметил НИКТО — стенд, находящий изменение там, где его не было, врёт и в
# другую сторону.
#
#   qa/poll-load-check.sh [-bite]
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

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
  echo "порт занят, беру $PORT"
fi

TOKEN="stand-$$"
mkdir -p "$W/content/scripts"
echo '{"titles":[]}' > "$W/content/manifest.json"
"$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$W/content" -admin-token "$TOKEN" \
  >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 50); do probe "$PORT" && break; sleep 0.2; done
probe "$PORT" || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

mkdir -p "$W/load"
cat > "$W/load/go.mod" <<'EOF'
module load

go 1.21
EOF
cat > "$W/load/main.go" <<'EOF'
// Нагрузочный клиент: N опрашивающих с честным ETag, как это делает ContentSync.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"sync"
	"time"
)

type result struct {
	Requests   int     `json:"requests"`
	NotMod     int     `json:"not_modified"`
	Bytes      int64   `json:"bytes"`
	Saw        int     `json:"saw_change"`
	FirstSeeMs []int64 `json:"first_see_ms"`
	P99Ms      float64 `json:"p99_ms"`
	PeakPerSec int     `json:"peak_per_sec"`
}

func main() {
	base := flag.String("base", "", "адрес сервера")
	n := flag.Int("clients", 100, "сколько клиентов")
	every := flag.Duration("every", 500*time.Millisecond, "интервал опроса")
	dur := flag.Duration("for", 8*time.Second, "сколько держать нагрузку")
	changeAtMs := flag.Int64("change-at-ms", -1, "когда ожидается правка, мс от старта (-1 = не ждать)")
	flag.Parse()

	var mu sync.Mutex
	res := result{}
	lat := make([]float64, 0, 8192)
	perSec := map[int64]int{}
	start := time.Now()

	var wg sync.WaitGroup
	for i := 0; i < *n; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			// Фазы разъезжаются сами: клиенты запускаются не разом, и это
			// ближе к жизни, чем идеальный такт.
			time.Sleep(time.Duration(i) * *every / time.Duration(*n))
			cl := &http.Client{Timeout: 5 * time.Second}
			etag, seen := "", false
			for time.Since(start) < *dur {
				t0 := time.Now()
				req, _ := http.NewRequest("GET", *base+"/v1/content/version", nil)
				if etag != "" {
					req.Header.Set("If-None-Match", etag)
				}
				resp, err := cl.Do(req)
				if err != nil {
					time.Sleep(*every)
					continue
				}
				body, _ := io.ReadAll(resp.Body)
				resp.Body.Close()
				ms := float64(time.Since(t0).Microseconds()) / 1000

				mu.Lock()
				res.Requests++
				res.Bytes += int64(len(body))
				lat = append(lat, ms)
				perSec[int64(time.Since(start)/time.Second)]++
				if resp.StatusCode == http.StatusNotModified {
					res.NotMod++
				} else if etag != "" && resp.Header.Get("ETag") != etag && !seen {
					// Версия сменилась под играющим — вот момент, ради которого
					// весь опрос и заведён.
					seen = true
					res.Saw++
					res.FirstSeeMs = append(res.FirstSeeMs, time.Since(start).Milliseconds()-*changeAtMs)
				}
				if e := resp.Header.Get("ETag"); e != "" {
					etag = e
				}
				mu.Unlock()
				time.Sleep(*every)
			}
		}(i)
	}
	wg.Wait()

	for i := 0; i < len(lat); i++ {
		for j := i + 1; j < len(lat); j++ {
			if lat[j] < lat[i] {
				lat[i], lat[j] = lat[j], lat[i]
			}
		}
	}
	if len(lat) > 0 {
		res.P99Ms = lat[len(lat)*99/100]
	}
	for _, c := range perSec {
		if c > res.PeakPerSec {
			res.PeakPerSec = c
		}
	}
	_ = json.NewEncoder(os.Stdout).Encode(res)
	fmt.Fprintln(os.Stderr, "готово")
}
EOF

CLIENTS="${LVN_CLIENTS:-120}"
EVERY="500ms"; DUR="8s"; CHANGE_MS=4000

python3 - "$W/edit.json" <<'PY'
import json, sys
json.dump({"id": "load", "name": "Нагрузка", "chapter": 1,
           "lvns": "scene load\n\nМира: Правка под игрой.\n-> __end\n"},
          open(sys.argv[1], "w"), ensure_ascii=False)
PY

# Первая публикация ДО нагрузки: клиентам нужна отправная версия.
curl -sS -o /dev/null -X POST "http://127.0.0.1:$PORT/v1/admin/agent/publish" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"id":"load","name":"Нагрузка","chapter":1,"lvns":"scene load\n\nМира: Первая.\n-> __end\n"}'

( cd "$W/load" && go build -o "$W/loadbin" . ) || { echo "нагрузочный клиент не собрался"; exit 1; }

# БАЗОВАЯ ВЕРСИЯ ОБЯЗАНА УСТОЯТЬСЯ ДО НАЧАЛА ЗАМЕРА. Версия считается из кэша с
# TTL, поэтому установочная публикация становится видна не сразу. Стартуй
# нагрузка раньше — клиенты поймали бы ХВОСТ установки и приняли его за правку,
# которую мы публикуем в середине. Ровно это и случилось при первом прогоне:
# стенд отрапортовал «задержка 257 мс» там, где сервер отвечает за 2 секунды.
settle() {
  local prev="" cur="" stable=0
  for _ in $(seq 1 60); do
    cur="$(curl -fsS "http://127.0.0.1:$PORT/v1/content/version" 2>/dev/null)"
    if [ -n "$cur" ] && [ "$cur" = "$prev" ]; then
      stable=$((stable+1)); [ "$stable" -ge 6 ] && return 0
    else
      stable=0
    fi
    prev="$cur"
    python3 -c "import time; time.sleep(0.5)"
  done
  echo "версия не устоялась — замер был бы про установку, а не про правку"; return 1
}
settle || exit 1

if [ -z "$BITE" ]; then
  ( sleep 4; curl -sS -o /dev/null -X POST "http://127.0.0.1:$PORT/v1/admin/agent/publish" \
      -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
      -d @"$W/edit.json" ) &
else
  CHANGE_MS=-1
fi

"$W/loadbin" -base "http://127.0.0.1:$PORT" -clients "$CLIENTS" \
   -every "$EVERY" -for "$DUR" -change-at-ms "$CHANGE_MS" > "$W/res.json" 2>"$W/load.err"
[ -s "$W/res.json" ] || { echo "нагрузка не отработала:"; tail -5 "$W/load.err"; exit 1; }

# ХУДШИЙ СЛУЧАЙ МЕРЯЕТСЯ ОТДЕЛЬНО И ДЕТЕРМИНИРОВАННО.
# Нагрузка выше даёт СЛУЧАЙНУЮ точку внутри окна кэша: правка попадает в него
# когда попадёт, и «медиана 900 мс» — это про везение, а не про договор. Здесь
# публикация идёт сразу ПОСЛЕ обновления кэша, то есть в самой дальней от него
# точке. Это и есть то, что автор обязан заложить, обещая «правка доедет».
if [ -z "$BITE" ]; then
  settle >/dev/null 2>&1
  # МОЛЧИМ дольше TTL, чтобы запись кэша протухла; следующий запрос заставит
  # пересчёт, и публикация ляжет в САМУЮ ДАЛЬНЮЮ от него точку. Без этой паузы
  # «свежий» запрос вернул бы запись возрастом почти в целый TTL, и худший
  # случай вышел бы приукрашенным — поймано прогоном: 1463 мс вместо 2065.
  python3 -c "import time; time.sleep(3.0)"
  v0="$(curl -fsS "http://127.0.0.1:$PORT/v1/content/version")"
  t0="$(python3 -c 'import time;print(time.time())')"
  curl -sS -o /dev/null -X POST "http://127.0.0.1:$PORT/v1/admin/agent/publish" \
    -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{"id":"load","name":"Нагрузка","chapter":1,"lvns":"scene load\n\nМира: Худший случай.\n-> __end\n"}'
  worst="?"
  for _ in $(seq 1 120); do
    v="$(curl -fsS "http://127.0.0.1:$PORT/v1/content/version")"
    if [ "$v" != "$v0" ]; then
      worst="$(python3 -c "import time;print(int((time.time()-$t0)*1000))")"; break
    fi
    python3 -c "import time;time.sleep(0.05)"
  done
  echo "  худший случай:       $worst мс от публикации до видимости (опрос тут 50 мс)"
  echo "$worst" > "$W/worst.txt"
fi

python3 - "$W/res.json" "$CLIENTS" "${BITE:-}" <<'PY'
import json, sys
r = json.load(open(sys.argv[1])); clients = int(sys.argv[2]); bite = bool(sys.argv[3])
req, nm = r["requests"], r["not_modified"]
print("клиентов: %d, опрос каждые 500 мс, 8 с" % clients)
print("  запросов:            %d  (%.0f/с)" % (req, req / 8))
print("  из них 304 без тела: %d  (%.1f%%)" % (nm, 100.0 * nm / max(req, 1)))
print("  трафик всего:        %d байт  → %.0f байт на клиента в минуту"
      % (r["bytes"], r["bytes"] / clients * 60 / 8))
print("  задержка ответа p99: %.1f мс" % r["p99_ms"])
print("  пик запросов в сек:  %d" % r["peak_per_sec"])

bad = []
if req < clients * 8:
    bad.append("нагрузки почти не было (%d запросов) — «сервер жив» ничего не значит" % req)

if bite:
    if r["saw_change"] != 0:
        bad.append("СТЕНД ВРЁТ: %d клиентов «увидели» правку, которой не публиковали" % r["saw_change"])
    else:
        print("  укус: правки не было — не заметил никто, как и должно")
else:
    seen = r["saw_change"]
    d = sorted(r["first_see_ms"])
    print("  правку заметили:     %d из %d клиентов" % (seen, clients))
    if d:
        print("  задержка обнаружения: медиана %d мс, максимум %d мс" % (d[len(d)//2], d[-1]))
    if seen < clients * 0.9:
        bad.append("правку заметили лишь %d из %d — доставка рвётся" % (seen, clients))

for b in bad:
    print("  ✗ " + b)
sys.exit(1 if bad else 0)
PY
