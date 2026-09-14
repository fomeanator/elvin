package main

// Performance reports reuse the existing device log intake and retention.
// Read complete daily files, not the 2,000-line tail: a tail ranks whichever
// clients happened to log last, rather than those with the most jank.

import (
	"bufio"
	"encoding/json"
	"io"
	"math"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"
)

type performanceClient struct {
	Device       string  `json:"device"`
	App          string  `json:"app"`
	Model        string  `json:"model,omitempty"`
	OS           string  `json:"os,omitempty"`
	Sessions     int     `json:"sessions"`
	Windows      int     `json:"windows"`
	Frames       int64   `json:"frames"`
	Seconds      float64 `json:"measured_seconds"`
	FPS          float64 `json:"fps"`
	Slow         int64   `json:"slow_frames"`
	SlowPercent  float64 `json:"slow_percent"`
	Over50       int64   `json:"over50_frames"`
	Over100      int64   `json:"over100_frames"`
	Worst        float64 `json:"worst_ms"`
	P95WindowMax float64 `json:"p95_window_max_ms"`
	P99WindowMax float64 `json:"p99_window_max_ms"`
	WorstTop     string  `json:"worst_window_top"`
	sessions     map[string]bool
}

type performanceReport struct {
	Day      string               `json:"day"`
	Clients  []*performanceClient `json:"clients"`
	Total    int                  `json:"total_clients"`
	Windows  int                  `json:"windows"`
	Contract string               `json:"metric_contract"`
}

func performanceFields(message string) map[string]string {
	fields := make(map[string]string)
	for _, part := range strings.Fields(message) {
		if k, v, ok := strings.Cut(part, "="); ok {
			fields[k] = v
		}
	}
	return fields
}

func performanceNumber(fields map[string]string, key string) (float64, bool) {
	v, err := strconv.ParseFloat(fields[key], 64)
	return v, err == nil && !math.IsNaN(v) && !math.IsInf(v, 0) && v >= 0
}

func readPerformance(r io.Reader, device, app string) ([]*performanceClient, int, error) {
	clients := make(map[string]*performanceClient)
	seen := make(map[string]bool)
	type hardware struct{ model, os string }
	hw := make(map[string]hardware)
	windows := 0
	sc := bufio.NewScanner(r)
	sc.Buffer(make([]byte, 64<<10), 1<<20)
	for sc.Scan() {
		var line struct {
			clientLogLine
			Model string `json:"model"`
			OS    string `json:"os"`
		}
		if json.Unmarshal(sc.Bytes(), &line) != nil || line.Dev == "" || line.Session == "" {
			continue
		}
		if device != "" && !strings.HasPrefix(line.Dev, device) {
			continue
		}
		key := line.Dev + "\x00" + line.App
		if line.Level == "device" {
			hw[key] = hardware{line.Model, line.OS}
			continue
		}
		if !strings.HasPrefix(line.Msg, "[lvn-perf] window ") {
			continue
		}
		f := performanceFields(line.Msg)
		// The outer batch describes the SENDING launch. Persisted diagnostics
		// can belong to an earlier run/build; use their producing identity.
		if run, build := f["run"], f["build"]; run != "" && build != "" {
			decoded, err := url.PathUnescape(build)
			if err != nil || len(run) > 64 || len(decoded) > 64 {
				continue
			}
			line.Session, line.App = run, decoded
			key = line.Dev + "\x00" + line.App
		}
		if app != "" && line.App != app {
			continue
		}
		frames, ok := performanceNumber(f, "frames")
		seconds, okTime := performanceNumber(f, "seconds")
		slow, okSlow := performanceNumber(f, "over_budget")
		first, okFirst := performanceNumber(f, "first_frame")
		last, okLast := performanceNumber(f, "last_frame")
		worst, okWorst := performanceNumber(f, "max_ms")
		p95, ok95 := performanceNumber(f, "p95_ms")
		p99, ok99 := performanceNumber(f, "p99_ms")
		over50, ok50 := performanceNumber(f, "over50")
		over100, ok100 := performanceNumber(f, "over100")
		if !ok || !okTime || !okSlow || !okFirst || !okLast || !okWorst || !ok95 || !ok99 || !ok50 || !ok100 ||
			frames < 1 || frames > 1e6 || frames != math.Trunc(frames) || seconds <= 0 || seconds > 3600 ||
			last < first || slow > frames || over50 > frames || over100 > over50 || p95 > p99 || p99 > worst {
			continue
		}
		// A lost HTTP response can replay a batch. The original frame interval
		// is its identity, not the server arrival time or collapse counter N.
		id := key + "\x00" + line.Session + "\x00" + f["first_frame"] + ":" + f["last_frame"]
		if seen[id] {
			continue
		}
		seen[id] = true
		c := clients[key]
		if c == nil {
			c = &performanceClient{Device: line.Dev, App: line.App, sessions: make(map[string]bool)}
			clients[key] = c
		}
		c.sessions[line.Session] = true
		c.Windows++
		windows++
		c.Frames += int64(frames)
		c.Seconds += seconds
		c.Slow += int64(slow)
		c.Over50 += int64(over50)
		c.Over100 += int64(over100)
		c.P95WindowMax = math.Max(c.P95WindowMax, p95)
		c.P99WindowMax = math.Max(c.P99WindowMax, p99)
		if worst >= c.Worst {
			c.Worst = worst
			_, c.WorstTop, _ = strings.Cut(line.Msg, " top_self_ms=")
		}
	}
	if err := sc.Err(); err != nil {
		return nil, 0, err
	}
	result := make([]*performanceClient, 0, len(clients))
	for key, c := range clients {
		c.Model, c.OS = hw[key].model, hw[key].os
		c.Sessions = len(c.sessions)
		c.FPS = float64(c.Frames) / c.Seconds
		c.SlowPercent = float64(c.Slow) * 100 / float64(c.Frames)
		result = append(result, c)
	}
	sort.Slice(result, func(i, j int) bool {
		a, b := result[i], result[j]
		if a.SlowPercent != b.SlowPercent {
			return a.SlowPercent > b.SlowPercent
		}
		if a.Worst != b.Worst {
			return a.Worst > b.Worst
		}
		return a.Device+"/"+a.App < b.Device+"/"+b.App
	})
	return result, windows, nil
}

// GET /v1/admin/performance?day=YYYY-MM-DD&app=<version>&device=<prefix>&n=50
// Per device AND build. Slow means >1.25x the frame budget on that device;
// p95/p99 are maxima of window percentiles, not invented pooled percentiles.
func (s *ClientLogService) handlePerformance(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.adminToken) || !onlyMethod(w, r, http.MethodGet) {
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
	clients := []*performanceClient{}
	windows := 0
	f, err := os.Open(filepath.Join(s.dir, day+".jsonl"))
	if err == nil {
		defer f.Close()
		stat, statErr := f.Stat()
		if statErr != nil {
			http.Error(w, "log stat failed", http.StatusInternalServerError)
			return
		}
		// A fixed byte snapshot avoids chasing active appends. No ingestion
		// mutex is held while scanning a whole day's diagnostics.
		clients, windows, err = readPerformance(io.LimitReader(f, stat.Size()), r.URL.Query().Get("device"), r.URL.Query().Get("app"))
	}
	if err != nil && !os.IsNotExist(err) {
		http.Error(w, "log read failed", http.StatusInternalServerError)
		return
	}
	total := len(clients)
	if n := qtyParam(r, "n", 50, 200); len(clients) > n {
		clients = clients[:n]
	}
	writeJSON(w, http.StatusOK, performanceReport{Day: day, Clients: clients, Total: total, Windows: windows,
		Contract: "received foreground windows only; ranked by frames >1.25x device budget; fps=frames/seconds; p95/p99=max window percentile; device+build grouping; short samples are not conclusive"})
}
