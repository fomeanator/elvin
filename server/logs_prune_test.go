package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// Дневники клиента — единственное, что растёт на боксе без предела: сутки
// живого тестирования дают десятки мегабайт, а уборки не было никакой (189 МБ
// за неполный месяц, замер на проде 03.09.2026). Диск на маленьком боксе
// кончается тихо и разом, и первым перестаёт писаться не лог, а кошелёк.
func TestClientLogsKeepOnlyRecentDays(t *testing.T) {
	dir := t.TempDir()
	svc, err := NewClientLogService(dir, "t")
	if err != nil {
		t.Fatal(err)
	}
	now := time.Date(2026, 9, 3, 12, 0, 0, 0, time.UTC)
	names := map[string]bool{ // имя → должно ли пережить уборку
		now.Format("2006-01-02") + ".jsonl":                    true,
		now.AddDate(0, 0, -1).Format("2006-01-02") + ".jsonl":  true,
		now.AddDate(0, 0, -13).Format("2006-01-02") + ".jsonl": true,
		now.AddDate(0, 0, -30).Format("2006-01-02") + ".jsonl": false,
		now.AddDate(0, 0, -90).Format("2006-01-02") + ".jsonl": false,
		// Не наш файл — не наше дело: сводки и всё, что положил человек.
		"_rollup.json": true,
		"README.md":    true,
	}
	for name := range names {
		if err := os.WriteFile(filepath.Join(dir, name), []byte("{}"), 0o600); err != nil {
			t.Fatal(err)
		}
	}

	svc.pruneOldDays(now)
	for name, keep := range names {
		_, err := os.Stat(filepath.Join(dir, name))
		if keep && err != nil {
			t.Errorf("%s удалён, а должен остаться", name)
		}
		if !keep && err == nil {
			t.Errorf("%s пережил уборку", name)
		}
	}

	// Второй раз за те же сутки каталог не обходится: цена уборки не должна
	// зависеть от того, сколько устройств пишет.
	old := filepath.Join(dir, now.AddDate(0, 0, -60).Format("2006-01-02")+".jsonl")
	if err := os.WriteFile(old, []byte("{}"), 0o600); err != nil {
		t.Fatal(err)
	}
	svc.pruneOldDays(now)
	if _, err := os.Stat(old); err != nil {
		t.Errorf("уборка повторилась в те же сутки — обход каталога на каждую пачку")
	}
	// Сменились сутки — прибираемся снова.
	svc.pruneOldDays(now.AddDate(0, 0, 1))
	if _, err := os.Stat(old); err == nil {
		t.Errorf("новые сутки наступили, а уборка не прошла")
	}
}

// ПУЛЬТ ПОДРОБНОГО ЛОГА (TR-86): указание для устройства живёт свой срок,
// уезжает в ответе на пачку именно этого устройства, снимается нулём часов
// и переживает новый экземпляр сервиса (файл рядом с дневниками).
func TestLogDirectiveTravelsWithIngestResponse(t *testing.T) {
	dir := t.TempDir()
	svc, err := NewClientLogService(filepath.Join(dir, "client-logs"), "tok")
	if err != nil {
		t.Fatal(err)
	}
	put := func(body string) *httptest.ResponseRecorder {
		req := httptest.NewRequest(http.MethodPut, "/v1/admin/log-level", strings.NewReader(body))
		req.Header.Set("Authorization", "Bearer tok")
		rec := httptest.NewRecorder()
		svc.handleLogLevel(rec, req)
		return rec
	}
	if rec := put(`{"device":"dev-a","hours":24}`); rec.Code != http.StatusOK {
		t.Fatalf("указание не принято: %d %s", rec.Code, rec.Body.String())
	}
	ingest := func(s *ClientLogService, dev string) map[string]any {
		body := `{"device":{"id":"` + dev + `","session":"s"},"lines":[{"level":"info","msg":"[x] hi"}]}`
		req := httptest.NewRequest(http.MethodPost, "/v1/log/client", strings.NewReader(body))
		req.RemoteAddr = "10.0.0.1:1"
		rec := httptest.NewRecorder()
		s.handleIngest(rec, req)
		var out map[string]any
		_ = json.Unmarshal(rec.Body.Bytes(), &out)
		return out
	}
	if out := ingest(svc, "dev-a"); out["log"] == nil {
		t.Fatalf("устройство с указанием не получило log.until: %v", out)
	}
	if out := ingest(svc, "dev-b"); out["log"] != nil {
		t.Fatalf("чужое устройство получило указание: %v", out)
	}
	again, _ := NewClientLogService(filepath.Join(dir, "client-logs"), "tok")
	if out := ingest(again, "dev-a"); out["log"] == nil {
		t.Fatalf("указание не пережило новый экземпляр: %v", out)
	}
	put(`{"device":"dev-a","hours":0}`)
	if out := ingest(svc, "dev-a"); out["log"] != nil {
		t.Fatalf("снятое указание продолжает ехать: %v", out)
	}
}

// КУСОК КОЛЬЦА ЗАДНИМ ЧИСЛОМ (TR-86, этап 2): запрос периода едет устройству
// в ответе на пачку, снимается подтверждением «fetched» вместе с самим куском,
// строки куска ложатся с исходным временем уровнем ring.
func TestLogFetchRangeTravelsAndIsAcknowledged(t *testing.T) {
	dir := t.TempDir()
	svc, err := NewClientLogService(filepath.Join(dir, "client-logs"), "tok")
	if err != nil {
		t.Fatal(err)
	}
	req := httptest.NewRequest(http.MethodPut, "/v1/admin/log-fetch",
		strings.NewReader(`{"device":"dev-a","from":"2026-09-15T10:00:00Z","to":"2026-09-15T11:00:00Z"}`))
	req.Header.Set("Authorization", "Bearer tok")
	rec := httptest.NewRecorder()
	svc.handleLogFetch(rec, req)
	if rec.Code != http.StatusOK || !strings.Contains(rec.Body.String(), `"fetch"`) {
		t.Fatalf("запрос периода не принят: %d %s", rec.Code, rec.Body.String())
	}
	ingest := func(body string) map[string]any {
		r := httptest.NewRequest(http.MethodPost, "/v1/log/client", strings.NewReader(body))
		r.RemoteAddr = "10.0.0.2:1"
		w := httptest.NewRecorder()
		svc.handleIngest(w, r)
		var out map[string]any
		_ = json.Unmarshal(w.Body.Bytes(), &out)
		return out
	}
	out := ingest(`{"device":{"id":"dev-a","session":"s"},"lines":[{"level":"info","msg":"[x] hi"}]}`)
	logd, _ := out["log"].(map[string]any)
	if logd == nil || logd["fetch"] == nil {
		t.Fatalf("устройство не получило запрос периода: %v", out)
	}
	out = ingest(`{"device":{"id":"dev-a","session":"s"},"fetched":{"from":"2026-09-15T10:00:00Z","to":"2026-09-15T11:00:00Z"},"lines":[{"level":"ring","ts":"2026-09-15T10:30:00Z","msg":"[lvn-stage] шаг"}]}`)
	if out["log"] != nil {
		t.Fatalf("после подтверждения запрос должен быть снят: %v", out)
	}
	// строка куска лежит с исходным временем
	r := httptest.NewRequest(http.MethodGet, "/v1/admin/client-logs?level=ring", nil)
	r.Header.Set("Authorization", "Bearer tok")
	w := httptest.NewRecorder()
	svc.handleTail(w, r)
	if !strings.Contains(w.Body.String(), `"ts":"2026-09-15T10:30:00Z"`) {
		t.Fatalf("кусок кольца не лёг с исходным временем: %s", w.Body.String())
	}
}
