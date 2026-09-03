package main

// Client log intake — field diagnostics without adb. Devices batch their
// warnings, errors, exceptions (with stack traces) and the engine's
// "[lvn-boot]"/"[lvn-perf]" timing marks to /v1/log/client; each day lands in
// its own JSONL file next to analytics, one line per entry, enriched with the
// device header the client sends once per batch. The admin reads any device's
// tail with a curl — the answer to "it crashes on the partner's phone".
//
// Same trust model as analytics: anonymous by design, so the same token
// bucket rate limit and a hard per-day size cap bound a hostile writer.

import (
	"bufio"
	"encoding/json"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

type ClientLogService struct {
	mu         sync.Mutex
	dir        string
	adminToken string
	// pruned — за какой день уборка уже прошла. Пустая строка значит «в этой
	// жизни процесса ещё не прибирались».
	pruned string
}

// clientLogKeepDays — сколько суток диагностики держим.
//
// Это ДНЕВНИК ОТЛАДКИ, а не история продукта: по нему отвечают на «почему у
// партнёра падает на этой сборке», и вопрос этот всегда про недавнее. Файлы
// при этом крупные — сутки живого тестирования дают 37 и 49 МБ (замер на
// проде 03.09.2026), а уборки не было НИКАКОЙ: 189 МБ накопилось за неполный
// месяц и продолжало расти, пока не кончился бы диск. Диск на маленьком
// боксе кончается тихо и разом: первым перестаёт писаться не лог, а кошелёк.
//
// Две недели — с запасом на «вернусь к этому после выходных».
const clientLogKeepDays = 14

func NewClientLogService(dir, adminToken string) (*ClientLogService, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	return &ClientLogService{dir: dir, adminToken: adminToken}, nil
}

func (s *ClientLogService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/log/client", s.handleIngest)
	mux.HandleFunc("/v1/admin/client-logs", s.handleTail)
	// Падения, сгруппированные по сути, а не по строкам (crashes.go): то же,
	// зачем ставят Sentry, на данных, которые уже собираются.
	mux.HandleFunc("/v1/admin/crashes", s.handleCrashes)
}

type clientLogBatch struct {
	Device map[string]string `json:"device"` // id, model, os, app, session — informational
	Lines  []clientLogLine   `json:"lines"`
}

type clientLogLine struct {
	TS    string `json:"ts,omitempty"`
	Level string `json:"level,omitempty"` // exception | error | warning | info
	Msg   string `json:"msg"`
	Stack string `json:"stack,omitempty"`
	N     int    `json:"n,omitempty"` // collapse count for repeated lines
	// Server-stamped:
	Dev     string `json:"dev,omitempty"`
	Session string `json:"session,omitempty"`
	// App — версия сборки. Клиент присылает её один раз на пачку, а нужна она
	// НА СТРОКЕ: без этого нельзя ответить на «в какой сборке это появилось»,
	// а это первый вопрос к любому падению.
	App string `json:"app,omitempty"`
}

const clientLogDayMaxSize = 256 << 20

func (s *ClientLogService) handleIngest(w http.ResponseWriter, r *http.Request) {
	if !onlyMethod(w, r, http.MethodPost) {
		return
	}
	if !analyticsAllow("log:"+clientIP(r), time.Now()) {
		http.Error(w, "rate limited", http.StatusTooManyRequests)
		return
	}
	var batch clientLogBatch
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, bodyDoc)).Decode(&batch); err != nil {
		http.Error(w, "JSON {device, lines:[{ts,level,msg,stack?}]} required", http.StatusBadRequest)
		return
	}
	if len(batch.Lines) == 0 || len(batch.Lines) > 200 {
		http.Error(w, "1..200 lines per batch", http.StatusBadRequest)
		return
	}
	dev := clip(batch.Device["id"], 64)
	session := clip(batch.Device["session"], 64)
	now := time.Now().UTC()

	s.mu.Lock()
	defer s.mu.Unlock()
	s.pruneOldDays(now)
	path := filepath.Join(s.dir, now.Format("2006-01-02")+".jsonl")
	if st, err := os.Stat(path); err == nil && st.Size() > clientLogDayMaxSize {
		http.Error(w, "daily volume cap reached", http.StatusTooManyRequests)
		return
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
	if err != nil {
		http.Error(w, "storage error", http.StatusInternalServerError)
		return
	}
	defer f.Close()

	// The device header rides once per batch as its own line — a session's
	// first batch documents the hardware the rest of its lines ran on.
	if len(batch.Device) > 0 {
		hdr := map[string]any{"ts": now.Format(time.RFC3339), "level": "device", "dev": dev, "session": session}
		for k, v := range batch.Device {
			if k != "id" && k != "session" {
				hdr[k] = clip(v, 128)
			}
		}
		line, _ := json.Marshal(hdr)
		_, _ = f.Write(append(line, '\n'))
	}
	accepted := 0
	for _, ln := range batch.Lines {
		if ln.Msg == "" {
			continue
		}
		ln.Msg = clip(ln.Msg, 4096)
		ln.Stack = clip(ln.Stack, 8192)
		ln.Level = clip(ln.Level, 16)
		if ln.TS == "" {
			ln.TS = now.Format(time.RFC3339)
		}
		ln.Dev = dev
		ln.Session = session
		ln.App = clip(batch.Device["app"], 64)
		line, _ := json.Marshal(ln)
		if _, err := f.Write(append(line, '\n')); err == nil {
			accepted++
		}
	}
	writeJSON(w, http.StatusOK, map[string]int{"accepted": accepted})
}

func clip(s string, max int) string {
	if len(s) > max {
		return s[:max]
	}
	return s
}

// GET /v1/admin/client-logs?day=YYYY-MM-DD&device=<prefix>&level=error&n=200 —
// the last n matching lines of a day, newest last. The files are also plain
// JSONL on disk for jq when the query outgrows this.
// pruneOldDays убирает дневники старше clientLogKeepDays. Зовётся с приёма
// пачки под тем же замком и работает ОДИН РАЗ ЗА СУТКИ: пока день не
// сменился, обход каталога не повторяется, поэтому цена уборки не зависит от
// того, сколько устройств пишет.
//
// Имя файла и есть его дата — разбираем её, а не время правки: правку меняет
// любой rsync или бэкап, а дата в имени не меняется никогда.
func (s *ClientLogService) pruneOldDays(now time.Time) {
	day := now.Format("2006-01-02")
	if s.pruned == day {
		return
	}
	s.pruned = day
	cutoff := now.AddDate(0, 0, -clientLogKeepDays)
	entries, err := os.ReadDir(s.dir)
	if err != nil {
		return
	}
	for _, e := range entries {
		name := e.Name()
		if e.IsDir() || !strings.HasSuffix(name, ".jsonl") {
			continue
		}
		when, perr := time.Parse("2006-01-02", strings.TrimSuffix(name, ".jsonl"))
		if perr != nil || !when.Before(cutoff) {
			continue
		}
		if os.Remove(filepath.Join(s.dir, name)) == nil {
			log.Printf("[client-logs] дневник %s старше %d суток — удалён", name, clientLogKeepDays)
		}
	}
}

func (s *ClientLogService) handleTail(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.adminToken) {
		return
	}
	day := r.URL.Query().Get("day")
	if day == "" {
		day = time.Now().UTC().Format("2006-01-02")
	}
	if !reDay.MatchString(day) {
		http.Error(w, "day=YYYY-MM-DD", http.StatusBadRequest)
		return
	}
	device := r.URL.Query().Get("device")
	level := r.URL.Query().Get("level")
	n := qtyParam(r, "n", 200, 2000)

	s.mu.Lock()
	defer s.mu.Unlock()
	var tail []json.RawMessage
	if f, err := os.Open(filepath.Join(s.dir, day+".jsonl")); err == nil {
		defer f.Close()
		sc := bufio.NewScanner(f)
		sc.Buffer(make([]byte, 64<<10), 1<<20)
		for sc.Scan() {
			var ln clientLogLine
			if json.Unmarshal(sc.Bytes(), &ln) != nil {
				continue
			}
			if device != "" && !strings.HasPrefix(ln.Dev, device) {
				continue
			}
			if level != "" && ln.Level != level {
				continue
			}
			tail = append(tail, json.RawMessage(append([]byte(nil), sc.Bytes()...)))
			if len(tail) > n {
				tail = tail[1:]
			}
		}
	}
	writeJSON(w, http.StatusOK, map[string]any{"day": day, "lines": tail})
}
