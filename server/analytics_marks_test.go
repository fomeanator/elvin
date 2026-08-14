package main

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Воронка по авторским меткам: считаем ЛЮДЕЙ, а не события — перепрохождение
// одним игроком не делает дошедших больше. И рядом деньги, потому что вопрос
// звучит «где отваливаются ИЛИ платят».
func TestMarksCountPeopleAndMoney(t *testing.T) {
	dir := t.TempDir()
	ev := func(user, mark string) string {
		return `{"name":"track","ts":"2026-08-02T10:00:00Z","user":"` + user +
			`","props":{"title":"cold","chapter":"ch1","mark":"` + mark + `","at":10}}` + "\n"
	}
	var b strings.Builder
	// До «начала» дошли четверо, до «поцелуя» двое, причём один прошёл дважды.
	for _, u := range []string{"a", "b", "c", "d"} {
		b.WriteString(ev(u, "начало главы"))
	}
	b.WriteString(ev("a", "первый поцелуй"))
	b.WriteString(ev("a", "первый поцелуй")) // перепрохождение
	b.WriteString(ev("b", "первый поцелуй"))
	if err := os.WriteFile(filepath.Join(dir, "2026-08-02.jsonl"), []byte(b.String()), 0o644); err != nil {
		t.Fatal(err)
	}
	pay := &fakePayments{
		buys: []walletPurchase{{User: "a", TS: "2026-08-02T11:00:00Z", SKU: "pack"}},
		prices: map[string]struct {
			v   float64
			cur string
		}{"pack": {4.99, "USD"}},
	}
	s := &AnalyticsService{dir: dir, rollups: newRollupStore(dir), adminToken: "t", payments: pay}
	mux := http.NewServeMux()
	s.Routes(mux)
	rec, _ := call(t, mux, "GET", "/v1/analytics/marks?from=2026-08-02&to=2026-08-02", "t", nil)
	if rec.Code != http.StatusOK {
		t.Fatalf("код %d: %s", rec.Code, rec.Body.String())
	}
	var rep marksReport
	if err := json.Unmarshal(rec.Body.Bytes(), &rep); err != nil {
		t.Fatal(err)
	}
	if len(rep.Marks) != 2 {
		t.Fatalf("ожидались две метки: %+v", rep.Marks)
	}
	first, second := rep.Marks[0], rep.Marks[1]
	if first.Mark != "начало главы" || first.Players != 4 {
		t.Errorf("первая метка: %+v", first)
	}
	// Люди и события расходятся — по этому видно перепрохождение.
	if second.Players != 2 || second.Events != 3 {
		t.Errorf("людей 2, событий 3: %+v", second)
	}
	if second.Lost != 2 || rep.Worst != "первый поцелуй" {
		t.Errorf("потеря между метками: lost=%d worst=%q", second.Lost, rep.Worst)
	}
	// Деньги среди дошедших.
	if second.Payers != 1 || second.Revenue != 4.99 {
		t.Errorf("платящие среди дошедших: %+v", second)
	}
	if second.Conversion != 0.5 {
		t.Errorf("конверсия среди дошедших: %v", second.Conversion)
	}
	// Оговорка обязана быть: мы знаем совпадение, а не «заплатил после».
	if !strings.Contains(strings.Join(rep.Notes, " "), "СРЕДИ ДОШЕДШИХ") {
		t.Errorf("нет оговорки про причинность: %v", rep.Notes)
	}
}

// Меток нет — надо сказать, как их поставить, а не показывать пустоту.
func TestMarksExplainsHowToAdd(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "2026-08-02.jsonl"),
		[]byte(`{"name":"boot","ts":"2026-08-02T10:00:00Z","user":"a"}`+"\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	s := &AnalyticsService{dir: dir, rollups: newRollupStore(dir), adminToken: "t"}
	mux := http.NewServeMux()
	s.Routes(mux)
	rec, _ := call(t, mux, "GET", "/v1/analytics/marks?from=2026-08-02&to=2026-08-02", "t", nil)
	var rep marksReport
	if err := json.Unmarshal(rec.Body.Bytes(), &rep); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(rep.Note, "track") {
		t.Errorf("подсказка должна называть команду: %q", rep.Note)
	}
}
