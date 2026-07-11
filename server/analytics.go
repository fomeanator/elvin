package main

// Analytics service — an honest append-only event log. Clients batch events
// to /v1/analytics/events (anonymous or authenticated); each day lands in
// its own JSONL file, one event per line, ready for jq / DuckDB / anything.
// /v1/analytics/summary gives the admin per-name counts without any storage
// engine — the files ARE the database at this scale.

import (
	"bufio"
	"encoding/json"
	"net"
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

// Analytics ingest is anonymous by design, which also makes it the cheapest
// disk-filling target on the server. Two honest caps instead of trust:
// a per-source token bucket (IP or user id) and a hard per-day file size.
const (
	analyticsBurst      = 30        // instant burst per source
	analyticsPerMinute  = 60        // sustained batches/min per source
	analyticsDayMaxSize = 256 << 20 // one day's JSONL hard cap (bytes)
)

// clientIP: the peer address without the port. Deliberately IGNORES
// X-Forwarded-For — a spoofable header would let one host mint unlimited
// rate-limit identities; behind a reverse proxy all traffic shares one
// bucket, which for an analytics firehose is an acceptable floor.
func clientIP(r *http.Request) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}

type anaBucket struct {
	tokens float64
	last   time.Time
}

var (
	anaMu      sync.Mutex
	anaBuckets = map[string]*anaBucket{}
)

func analyticsAllow(source string, now time.Time) bool {
	anaMu.Lock()
	defer anaMu.Unlock()
	if len(anaBuckets) > 10000 { // transient counters; shed wholesale
		anaBuckets = map[string]*anaBucket{}
	}
	b, ok := anaBuckets[source]
	if !ok {
		b = &anaBucket{tokens: analyticsBurst, last: now}
		anaBuckets[source] = b
	}
	b.tokens += now.Sub(b.last).Minutes() * analyticsPerMinute
	if b.tokens > analyticsBurst {
		b.tokens = analyticsBurst
	}
	b.last = now
	if b.tokens < 1 {
		return false
	}
	b.tokens--
	return true
}

func (s *AnalyticsService) handleEvents(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}
	source := s.auth.UserFromRequest(r)
	if source == "" {
		source = clientIP(r)
	}
	if !analyticsAllow(source, time.Now()) {
		http.Error(w, "rate limited", http.StatusTooManyRequests)
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
	if st, err := os.Stat(path); err == nil && st.Size() > analyticsDayMaxSize {
		http.Error(w, "daily volume cap reached", http.StatusTooManyRequests)
		return
	}
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
