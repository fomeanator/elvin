package main

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func perfWindow(frames, first, slow int, seconds, worst float64) string {
	return fmt.Sprintf("[lvn-perf] window frames=%d first_frame=%d last_frame=%d seconds=%.2f p95_ms=16.00 p99_ms=20.00 max_ms=%.2f over_budget=%d over50=0 over100=0 top_self_ms=[ActorUpdate:12.00/total=12.00/max=2.00/calls=6/alloc_B=96]", frames, first, first+frames-1, seconds, worst, slow)
}

func perfLine(dev, app, session, msg string) string {
	line, _ := json.Marshal(clientLogLine{Dev: dev, App: app, Session: session, Msg: msg})
	return string(line) + "\n"
}

func TestPerformanceRanksFrameShareAndWeightsFPSByObservedTime(t *testing.T) {
	a := perfLine("a", "1", "s1", perfWindow(600, 1, 60, 10, 80))
	text := a + a + // retry of the same batch must be counted once
		perfLine("a", "1", "s1", perfWindow(30, 601, 30, 10, 120)) +
		perfLine("b", "1", "s2", perfWindow(60, 1, 30, 2, 40)) +
		perfLine("a", "2", "s3", perfWindow(600, 1, 0, 10, 20)) +
		`{"level":"device","dev":"a","app":"1","session":"s1","model":"Phone A","os":"Android"}` + "\n"
	clients, windows, err := readPerformance(strings.NewReader(text), "", "")
	if err != nil || windows != 4 || len(clients) != 3 {
		t.Fatalf("clients=%+v windows=%d err=%v", clients, windows, err)
	}
	if clients[0].Device != "b" || clients[0].SlowPercent != 50 {
		t.Fatalf("rank by share, not absolute slow count: %+v", clients[0])
	}
	c := clients[1]
	if c.Device != "a" || c.App != "1" || c.Frames != 630 || c.FPS != 31.5 || c.Worst != 120 || c.Sessions != 1 || c.Model != "Phone A" {
		t.Fatalf("weighted totals, hardware and build isolation: %+v", c)
	}
	if !strings.Contains(c.WorstTop, "ActorUpdate") {
		t.Fatal("no clue about the expensive work")
	}
	filtered, _, _ := readPerformance(strings.NewReader(text), "a", "2")
	if len(filtered) != 1 || filtered[0].App != "2" {
		t.Fatalf("build/device filter: %+v", filtered)
	}
}

func TestPerformanceIgnoresUnusableOrLegacyMetrics(t *testing.T) {
	valid := perfWindow(60, 1, 1, 1, 30)
	var text strings.Builder
	for i, message := range []string{
		"[lvn-perf] FRAME HITCH 900ms", "unrelated",
		strings.Replace(valid, "seconds=1.00", "seconds=0", 1),
		strings.Replace(valid, "seconds=1.00", "seconds=NaN", 1),
		strings.Replace(valid, "over_budget=1", "over_budget=99", 1),
		strings.Replace(valid, "max_ms=30.00", "max_ms=+Inf", 1),
		strings.Replace(valid, "p99_ms=20.00", "p99_ms=900.00", 1),
	} {
		text.WriteString(perfLine("device", "1", fmt.Sprint(i), message))
	}
	text.WriteString(perfLine("", "1", "x", valid))
	text.WriteString(perfLine("device", "1", "", valid))
	clients, windows, err := readPerformance(strings.NewReader(text.String()), "", "")
	if err != nil || windows != 0 || len(clients) != 0 {
		t.Fatalf("unusable input was ranked: %+v %d %v", clients, windows, err)
	}
}

func TestPerformanceIngestToAdminReport(t *testing.T) {
	svc, err := NewClientLogService(t.TempDir(), "test-perf-token")
	if err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	svc.Routes(mux)
	body, _ := json.Marshal(clientLogBatch{
		Device: map[string]string{"id": "device", "session": "session", "app": "20260912", "model": "Test phone"},
		Lines:  []clientLogLine{{Msg: perfWindow(600, 1, 12, 10, 80), Level: "info"}},
	})
	post := httptest.NewRecorder()
	mux.ServeHTTP(post, httptest.NewRequest(http.MethodPost, "/v1/log/client", strings.NewReader(string(body))))
	if post.Code != http.StatusOK {
		t.Fatalf("intake: %d %s", post.Code, post.Body.String())
	}
	denied := httptest.NewRecorder()
	mux.ServeHTTP(denied, httptest.NewRequest(http.MethodGet, "/v1/admin/performance", nil))
	if denied.Code == http.StatusOK {
		t.Fatal("client rankings must require admin credentials")
	}
	req := httptest.NewRequest(http.MethodGet, "/v1/admin/performance?app=20260912", nil)
	req.Header.Set("Authorization", "Bearer test-perf-token")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, req)
	var report performanceReport
	if err := json.Unmarshal(response.Body.Bytes(), &report); err != nil {
		t.Fatalf("report %d: %s: %v", response.Code, response.Body.String(), err)
	}
	if response.Code != 200 || len(report.Clients) != 1 || report.Clients[0].SlowPercent != 2 || report.Clients[0].Model != "Test phone" {
		t.Fatalf("round trip: %+v", report)
	}
	req = httptest.NewRequest(http.MethodGet, "/v1/admin/performance?day=../../secret", nil)
	req.Header.Set("Authorization", "Bearer test-perf-token")
	response = httptest.NewRecorder()
	mux.ServeHTTP(response, req)
	if response.Code != http.StatusBadRequest {
		t.Fatalf("invalid day: %d", response.Code)
	}
}

func TestPerformanceOfflineQueueKeepsProducingRunAndBuild(t *testing.T) {
	window := perfWindow(600, 1, 12, 10, 80)
	old := strings.Replace(window, "window ", "window run=old-run build=old%2Bbuild ", 1)
	other := strings.Replace(window, "window ", "window run=another-old-run build=old%2Bbuild ", 1)
	current := strings.Replace(window, "window ", "window run=new-run build=new-build ", 1)
	// Identical frame ranges from different launches are separate windows;
	// a retry of the same old message remains a duplicate after app restart.
	data := perfLine("phone", "new-build", "new-run", old) +
		perfLine("phone", "new-build", "new-run", other) +
		perfLine("phone", "new-build", "new-run", current) +
		perfLine("phone", "newer-build", "next-run", old)
	clients, windows, err := readPerformance(strings.NewReader(data), "phone", "old+build")
	if err != nil || windows != 2 || len(clients) != 1 || clients[0].Sessions != 2 || clients[0].Frames != 1200 {
		t.Fatalf("offline origin lost or duplicate counted: %+v windows=%d err=%v", clients, windows, err)
	}
	clients, windows, err = readPerformance(strings.NewReader(data), "", "new-build")
	if err != nil || windows != 1 || len(clients) != 1 || clients[0].Sessions != 1 {
		t.Fatalf("old observations attributed to new build: %+v windows=%d err=%v", clients, windows, err)
	}
}

// ФОРМАТ V2 — КОРОТКИЕ КЛЮЧИ (TR-86, «вместо текста коды»): окно «W» с
// ключами n/f0/f1/s/p95/p99/max/ob/o50/o100/top читается той же сводкой,
// что и старое «window …», и даёт те же числа; run/build в строке нет —
// устройство и сборка берутся из заголовка пачки.
func TestPerformanceReadsCompactWindow(t *testing.T) {
	compact := "[lvn-perf] W n=600 f0=100 f1=699 s=10.0 fps=60.0 p50=16.7 p95=16.7 p99=20.0 max=133.3 ps=600 ob=6 o50=2 o100=1 gc=3 inv=0 m=16.9/133.6 top=FontGlyphs:10.9/10.9/2;Diagnostics:0.3/0.5/3"
	text := perfLine("dev-a", "20260915.2000", "s1", compact)
	clients, windows, err := readPerformance(strings.NewReader(text), "", "")
	if err != nil {
		t.Fatal(err)
	}
	if windows != 1 || len(clients) != 1 {
		t.Fatalf("компактное окно не прочитано: окон %d, клиентов %d", windows, len(clients))
	}
	c := clients[0]
	if c.Frames != 600 || c.Slow != 6 || c.Over50 != 2 || c.Over100 != 1 || c.Worst != 133.3 || c.P95WindowMax != 16.7 {
		t.Fatalf("числа окна разошлись: %+v", c)
	}
	if !strings.HasPrefix(c.WorstTop, "FontGlyphs:10.9") {
		t.Fatalf("части худшего кадра не взяты из top=: %q", c.WorstTop)
	}
}
