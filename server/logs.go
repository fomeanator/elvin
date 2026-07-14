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
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"
)

type ClientLogService struct {
	mu         sync.Mutex
	dir        string
	adminToken string
}

func NewClientLogService(dir, adminToken string) (*ClientLogService, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	return &ClientLogService{dir: dir, adminToken: adminToken}, nil
}

func (s *ClientLogService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/log/client", s.handleIngest)
	mux.HandleFunc("/v1/admin/client-logs", s.handleTail)
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
}

const clientLogDayMaxSize = 256 << 20

func (s *ClientLogService) handleIngest(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}
	if !analyticsAllow("log:"+clientIP(r), time.Now()) {
		http.Error(w, "rate limited", http.StatusTooManyRequests)
		return
	}
	var batch clientLogBatch
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 512<<10)).Decode(&batch); err != nil {
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
		line, _ := json.Marshal(ln)
		if _, err := f.Write(append(line, '\n')); err == nil {
			accepted++
		}
	}
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]int{"accepted": accepted})
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
func (s *ClientLogService) handleTail(w http.ResponseWriter, r *http.Request) {
	if s.adminToken == "" || !bearerOK(r, s.adminToken) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
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
	n, _ := strconv.Atoi(r.URL.Query().Get("n"))
	if n <= 0 || n > 2000 {
		n = 200
	}

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
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{"day": day, "lines": tail})
}
