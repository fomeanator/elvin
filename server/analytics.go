package main

// Analytics service — an honest append-only event log. Clients batch events
// to /v1/analytics/events (anonymous or authenticated); each day lands in
// its own JSONL file, one event per line, ready for jq / DuckDB / anything.
// /v1/analytics/summary gives the admin per-name counts without any storage
// engine — the files ARE the database at this scale.

import (
	"bufio"
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"sync"
	"time"
)

type AnalyticsService struct {
	mu         sync.Mutex
	dir        string
	auth       *AuthService
	adminToken string
}

func NewAnalyticsService(dir string, auth *AuthService, adminToken string) (*AnalyticsService, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	return &AnalyticsService{dir: dir, auth: auth, adminToken: adminToken}, nil
}

func (s *AnalyticsService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/analytics/events", s.handleEvents)
	mux.HandleFunc("/v1/analytics/summary", s.handleSummary)
}

type analyticsEvent struct {
	Name  string         `json:"name"`
	TS    string         `json:"ts,omitempty"`
	Props map[string]any `json:"props,omitempty"`
	User  string         `json:"user,omitempty"` // filled server-side, never trusted from the client
}

var reDay = regexp.MustCompile(`^\d{4}-\d{2}-\d{2}$`)

func (s *AnalyticsService) handleEvents(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}
	var events []analyticsEvent
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 256<<10)).Decode(&events); err != nil {
		http.Error(w, "a JSON array of {name, ts?, props?} required", http.StatusBadRequest)
		return
	}
	if len(events) == 0 || len(events) > 100 {
		http.Error(w, "1..100 events per batch", http.StatusBadRequest)
		return
	}
	user := s.auth.UserFromRequest(r) // "" for anonymous — allowed by design
	now := time.Now().UTC()

	s.mu.Lock()
	defer s.mu.Unlock()
	path := filepath.Join(s.dir, now.Format("2006-01-02")+".jsonl")
	f, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
	if err != nil {
		http.Error(w, "storage error", http.StatusInternalServerError)
		return
	}
	defer f.Close()
	accepted := 0
	for _, ev := range events {
		if ev.Name == "" || len(ev.Name) > 64 {
			continue
		}
		if ev.TS == "" {
			ev.TS = now.Format(time.RFC3339)
		}
		ev.User = user
		line, _ := json.Marshal(ev)
		if _, err := f.Write(append(line, '\n')); err == nil {
			accepted++
		}
	}
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]int{"accepted": accepted})
}

func (s *AnalyticsService) handleSummary(w http.ResponseWriter, r *http.Request) {
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

	s.mu.Lock()
	defer s.mu.Unlock()
	counts := map[string]int{}
	users := map[string]bool{}
	total := 0
	if f, err := os.Open(filepath.Join(s.dir, day+".jsonl")); err == nil {
		defer f.Close()
		sc := bufio.NewScanner(f)
		sc.Buffer(make([]byte, 64<<10), 1<<20)
		for sc.Scan() {
			var ev analyticsEvent
			if json.Unmarshal(sc.Bytes(), &ev) != nil {
				continue
			}
			counts[ev.Name]++
			total++
			if ev.User != "" {
				users[ev.User] = true
			}
		}
	}
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]any{
		"day": day, "total": total, "unique_users": len(users), "by_name": counts,
	})
}
