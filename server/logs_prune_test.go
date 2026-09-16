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
	day := func(d int) string { return now.AddDate(0, 0, -d).Format("2006-01-02") }
	line := `{"ts":"2026-09-01T10:00:00Z","level":"error","msg":"[x] beda","dev":"d1","session":"s1"}` + "\n" +
		`{"ts":"2026-09-01T10:00:01Z","level":"info","msg":"[lvn-perf] W n=600 fps=60","dev":"d1","session":"s1"}` + "\n"
	for _, d := range []int{0, 1, 13, 30, 90} {
		if err := os.WriteFile(filepath.Join(dir, day(d)+".jsonl"), []byte(line), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	for _, name := range []string{"_rollup.json", "README.md"} {
		_ = os.WriteFile(filepath.Join(dir, name), []byte("{}"), 0o600)
	}

	svc.pruneOldDays(now)
	exists := func(name string) bool { _, err := os.Stat(filepath.Join(dir, name)); return err == nil }
	// сегодня и вчера — сырьё живо
	for _, d := range []int{0, 1} {
		if !exists(day(d) + ".jsonl") {
			t.Errorf("сырой дневник %s удалён, а должен остаться", day(d))
		}
	}
	// 13 и 30 суток — склеены: сырья нет, есть архив и сводка
	for _, d := range []int{13, 30} {
		if exists(day(d)+".jsonl") || !exists(day(d)+".keep.jsonl.gz") || !exists(day(d)+".summary.json") {
			t.Errorf("день %s не склеен как надо", day(d))
		}
	}
	// 90 суток — удалён целиком
	if exists(day(90)+".jsonl") || exists(day(90)+".keep.jsonl.gz") {
		t.Errorf("день %s пережил уборку", day(90))
	}
	for _, name := range []string{"_rollup.json", "README.md"} {
		if !exists(name) {
			t.Errorf("%s удалён, а должен остаться", name)
		}
	}
	// в архиве — ошибка, серой массы (окно замеров) нет; сводка их сосчитала
	sum := svc.loadSummary(day(13))
	if sum == nil || sum.Lines != 2 || sum.Kept != 1 || sum.Levels["error"] != 1 || sum.Tags["[lvn-perf]"] != 1 || len(sum.Sessions) != 1 {
		t.Fatalf("сводка склеенного дня не та: %+v", sum)
	}
	r := httptest.NewRequest(http.MethodGet, "/v1/admin/client-logs?day="+day(13), nil)
	r.Header.Set("Authorization", "Bearer t")
	w := httptest.NewRecorder()
	svc.handleTail(w, r)
	if !strings.Contains(w.Body.String(), "beda") || strings.Contains(w.Body.String(), "lvn-perf] W") {
		t.Fatalf("хвост склеенного дня читается не из архива: %s", w.Body.String())
	}
	// повторный вызов в тот же день — ничего не делает (сторож дня)
	svc.pruneOldDays(now)
	// следующий день — вчерашнее «сегодня» ещё сырое, позавчера склеится
	svc.pruneOldDays(now.AddDate(0, 0, 2))
	if exists(day(0)+".jsonl") || !exists(day(0)+".keep.jsonl.gz") {
		t.Errorf("через двое суток сырьё %s должно быть склеено", day(0))
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
