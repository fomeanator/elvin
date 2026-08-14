package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// segFixture: два дня событий, два игрока с разными каналами и A/B-группами,
// один из них платит. Ровно та смесь, ради которой сегменты и заводились.
func segFixture(t *testing.T) (*AnalyticsService, *http.ServeMux) {
	t.Helper()
	dir := t.TempDir()
	// «рекламный» — канал telegram/aug, группа b, платит.
	// «прямой»    — без канала,       группа a, не платит.
	day1 := ""
	for _, e := range []struct{ name, user, ab string }{
		{"boot", "рекламный", "b"},
		{"chapter_start", "рекламный", "b"},
		{"boot", "прямой", "a"},
	} {
		day1 += `{"name":"` + e.name + `","ts":"2026-08-02T10:00:00Z","user":"` + e.user +
			`","props":{"title":"cold","chapter":"cold-ch1","ab_первая_сцена":"` + e.ab + `"}}` + "\n"
	}
	day2 := `{"name":"boot","ts":"2026-08-03T10:00:00Z","user":"рекламный","props":{"ab_первая_сцена":"b"}}` + "\n" +
		`{"name":"boot","ts":"2026-08-03T10:00:00Z","user":"прямой","props":{"ab_первая_сцена":"a"}}` + "\n"
	for name, body := range map[string]string{"2026-08-02.jsonl": day1, "2026-08-03.jsonl": day2} {
		if err := os.WriteFile(filepath.Join(dir, name), []byte(body), 0o644); err != nil {
			t.Fatal(err)
		}
	}

	auth, err := NewAuthService(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	auth.mu.Lock()
	auth.users["рекламный"] = &authUser{Created: "2026-08-01T00:00:00Z"}
	auth.users["прямой"] = &authUser{Created: "2026-08-01T00:00:00Z"}
	auth.mu.Unlock()
	if _, ok := auth.SetAttributionFirstTouch("рекламный",
		parseAttribution("?utm_source=telegram&utm_campaign=aug")); !ok {
		t.Fatal("атрибуция не записалась")
	}
	pay := &fakePayments{
		buys: []walletPurchase{{User: "рекламный", TS: "2026-08-02T11:00:00Z", SKU: "pack"}},
		prices: map[string]struct {
			v   float64
			cur string
		}{"pack": {4.99, "USD"}},
	}
	s := &AnalyticsService{dir: dir, rollups: newRollupStore(dir), adminToken: "t",
		auth: auth, payments: pay}
	mux := http.NewServeMux()
	s.Routes(mux)
	return s, mux
}

func getReport(t *testing.T, mux *http.ServeMux, url string, out any) {
	t.Helper()
	req := httptest.NewRequest(http.MethodGet, url, nil)
	req.Header.Set("Authorization", "Bearer t")
	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("%s → код %d: %s", url, rec.Code, rec.Body.String())
	}
	if err := json.Unmarshal(rec.Body.Bytes(), out); err != nil {
		t.Fatalf("%s: %v", url, err)
	}
}

// Вопрос из постановки задачи целиком: «сколько у когорты этой недели,
// пришедшей с кампании X, в группе B». Ответ должен быть числом, а не
// выгрузкой.
func TestSegmentNarrowsEveryReport(t *testing.T) {
	_, mux := segFixture(t)
	const win = "from=2026-08-02&to=2026-08-03"

	var all, seg moneyReport
	getReport(t, mux, "/v1/analytics/money?"+win, &all)
	getReport(t, mux, "/v1/analytics/money?"+win+"&segment=channel:telegram/aug", &seg)
	if all.Active != 2 {
		t.Fatalf("без сегмента активных должно быть двое: %d", all.Active)
	}
	if seg.Active != 1 || seg.Payers != 1 || seg.Revenue != 4.99 {
		t.Errorf("канал telegram/aug: активных %d, платящих %d, выручка %v",
			seg.Active, seg.Payers, seg.Revenue)
	}
	// Отчёт обязан сказать, на кого он смотрит: два разных числа без подписи
	// читаются как противоречие.
	if !strings.Contains(seg.Segment, "telegram/aug") {
		t.Errorf("сегмент не назван в ответе: %q", seg.Segment)
	}

	// A/B-группа приезжает из props каждого события.
	var groupB, groupA retentionReport
	getReport(t, mux, "/v1/analytics/retention?"+win+"&segment=ab:первая_сцена=b", &groupB)
	getReport(t, mux, "/v1/analytics/retention?"+win+"&segment=ab:первая_сцена=a", &groupA)
	if groupB.Players != 1 || groupA.Players != 1 {
		t.Errorf("группы должны делить аудиторию пополам: b=%d a=%d",
			groupB.Players, groupA.Players)
	}

	// Пересечение сужает, а не складывает.
	var both moneyReport
	getReport(t, mux, "/v1/analytics/money?"+win+"&segment=channel:telegram/aug,ab:первая_сцена=a", &both)
	if both.Active != 0 {
		t.Errorf("рекламный сидит в группе b — пересечение должно быть пустым: %d", both.Active)
	}
}

// Платящий и неплатящий — тот самый разрез, из-за которого средние врут.
func TestSegmentPayerSplit(t *testing.T) {
	_, mux := segFixture(t)
	const win = "from=2026-08-02&to=2026-08-03"
	var payers, nonPayers firstSessionReport
	getReport(t, mux, "/v1/analytics/first-session?"+win+"&segment=payer:yes", &payers)
	getReport(t, mux, "/v1/analytics/first-session?"+win+"&segment=payer:no", &nonPayers)
	if payers.Newcomers != 1 || nonPayers.Newcomers != 1 {
		t.Errorf("один платит, один нет: %d/%d", payers.Newcomers, nonPayers.Newcomers)
	}
}

// Сегментированный запрос складывает СЫРЬЁ заново, минуя чекпоинты. Если бы он
// читал общую свёртку, счётчики событий остались бы по всем игрокам — цифры
// выглядели бы правдоподобно и были бы чужими.
func TestSegmentRefoldsRawEvents(t *testing.T) {
	_, mux := segFixture(t)
	const win = "from=2026-08-02&to=2026-08-03"
	var all, seg struct {
		Total int `json:"total"`
		Users int `json:"unique_users"`
	}
	getReport(t, mux, "/v1/analytics/summary?"+win, &all)
	getReport(t, mux, "/v1/analytics/summary?"+win+"&segment=channel:telegram/aug", &seg)
	if all.Total != 5 {
		t.Fatalf("всего событий должно быть пять: %d", all.Total)
	}
	if seg.Total != 3 || seg.Users != 1 {
		t.Errorf("у рекламного три события и он один: %d событий, %d игроков", seg.Total, seg.Users)
	}
}

// Опечатку в сегменте надо назвать, а не молча показать всех: «отчёт по
// сегменту, который на самом деле по всем» — худший из возможных ответов.
func TestSegmentRejectsGarbage(t *testing.T) {
	_, mux := segFixture(t)
	for _, bad := range []string{"segment=нечто:x", "segment=ab:безгруппы", "segment=payer:иногда", "segment=простотекст"} {
		req := httptest.NewRequest(http.MethodGet, "/v1/analytics/money?days=7&"+bad, nil)
		req.Header.Set("Authorization", "Bearer t")
		rec := httptest.NewRecorder()
		mux.ServeHTTP(rec, req)
		if rec.Code != http.StatusBadRequest {
			t.Errorf("%s: код %d, ожидался 400", bad, rec.Code)
		}
	}
}
