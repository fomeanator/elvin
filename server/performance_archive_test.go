package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

func archivedPerformance(t *testing.T, svc *ClientLogService, query string) (*httptest.ResponseRecorder, performanceReport) {
	t.Helper()
	req := httptest.NewRequest(http.MethodGet, "/v1/admin/performance?day=2026-09-17"+query, nil)
	req.Header.Set("Authorization", "Bearer test")
	w := httptest.NewRecorder()
	svc.handlePerformance(w, req)
	var report performanceReport
	if err := json.Unmarshal(w.Body.Bytes(), &report); err != nil {
		t.Fatal(err)
	}
	return w, report
}

func TestPerformanceSurvivesCompactionAndRestart(t *testing.T) {
	dir := t.TempDir()
	svc, _ := NewClientLogService(dir, "test")
	raw := filepath.Join(dir, "2026-09-17.jsonl")
	a := perfLine("device-a", "build1", "s1", perfWindow(600, 1, 60, 10, 80))
	data := a + a + perfLine("device-a", "build1", "s1", perfWindow(30, 601, 30, 10, 120)) +
		perfLine("device-b", "build1", "s2", perfWindow(60, 1, 30, 2, 40)) +
		perfLine("device-a", "build2", "s3", perfWindow(600, 1, 0, 10, 20))
	if err := os.WriteFile(raw, []byte(data), 0600); err != nil {
		t.Fatal(err)
	}
	queries := []string{"", "&device=device-a", "&app=build2", "&device=device-b&app=build2", "&n=1"}
	before := make([]performanceReport, len(queries))
	for i, query := range queries {
		_, before[i] = archivedPerformance(t, svc, query)
	}
	if err := svc.compactDay("2026-09-17"); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(raw); !os.IsNotExist(err) {
		t.Fatal("test must read the archive, not surviving raw input")
	}
	svc, _ = NewClientLogService(dir, "test")
	for i, query := range queries {
		w, after := archivedPerformance(t, svc, query)
		if w.Code != http.StatusOK || !after.Compacted {
			t.Fatalf("archive not used: %d %s", w.Code, w.Body.String())
		}
		after.Compacted = false
		if !reflect.DeepEqual(before[i], after) {
			t.Fatalf("changed metrics for %s: before=%+v after=%+v", query, before[i], after)
		}
	}
}

func TestOldArchiveDoesNotPretendThereWereNoClients(t *testing.T) {
	svc, _ := NewClientLogService(t.TempDir(), "test")
	if err := os.WriteFile(svc.summaryPath("2026-09-17"), []byte(`{"day":"2026-09-17","sessions":[{"windows":42}]}`), 0600); err != nil {
		t.Fatal(err)
	}
	w, _ := archivedPerformance(t, svc, "")
	if w.Code != http.StatusGone || !strings.Contains(w.Body.String(), "detailed_performance_expired") || !strings.Contains(w.Body.String(), "sessions_url") {
		t.Fatalf("lost raw metrics were presented as zero clients: %d %s", w.Code, w.Body.String())
	}
}

func TestCompactionKeepsRawInputWhenScannerFails(t *testing.T) {
	svc, _ := NewClientLogService(t.TempDir(), "test")
	raw := filepath.Join(svc.dir, "2026-09-17.jsonl")
	data := perfLine("d", "1", "s", perfWindow(60, 1, 1, 1, 30)) + strings.Repeat("x", 2<<20)
	if err := os.WriteFile(raw, []byte(data), 0600); err != nil {
		t.Fatal(err)
	}
	if err := svc.compactDay("2026-09-17"); err == nil {
		t.Fatal("oversized line should fail the scan")
	}
	kept, err := os.ReadFile(raw)
	if err != nil || string(kept) != data {
		t.Fatal("incomplete scan destroyed original diagnostic data")
	}
	if svc.loadSummary("2026-09-17") != nil {
		t.Fatal("incomplete summary must not be published")
	}
}
