package main

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Отчёт обязан показывать цель И предохранители рядом: вариант, поднявший
// покупки и уронивший дочитывание, — это не победа.
func TestExperimentReportShowsGuardrails(t *testing.T) {
	dir := t.TempDir()
	ev := func(name, user, ab, day string) string {
		return `{"name":"` + name + `","ts":"` + day + `T10:00:00Z","user":"` + user +
			`","props":{"ab_первая_сцена":"` + ab + `"}}` + "\n"
	}
	var b strings.Builder
	// Группа «a»: пятеро, четверо дочитали, никто не платит.
	for i, u := range []string{"a1", "a2", "a3", "a4", "a5"} {
		b.WriteString(ev("chapter_start", u, "a", "2026-08-02"))
		if i < 4 {
			b.WriteString(ev("chapter_finish", u, "a", "2026-08-02"))
		}
	}
	// Группа «b»: пятеро, дочитал один, зато один платит.
	for i, u := range []string{"b1", "b2", "b3", "b4", "b5"} {
		b.WriteString(ev("chapter_start", u, "b", "2026-08-02"))
		if i < 1 {
			b.WriteString(ev("chapter_finish", u, "b", "2026-08-02"))
		}
	}
	if err := os.WriteFile(filepath.Join(dir, "2026-08-02.jsonl"), []byte(b.String()), 0o644); err != nil {
		t.Fatal(err)
	}
	pay := &fakePayments{
		buys: []walletPurchase{{User: "b1", TS: "2026-08-02T11:00:00Z", SKU: "pack"}},
		prices: map[string]struct {
			v   float64
			cur string
		}{"pack": {4.99, "USD"}},
	}
	s := &AnalyticsService{dir: dir, rollups: newRollupStore(dir), adminToken: "t", payments: pay}
	mux := http.NewServeMux()
	s.Routes(mux)
	rec, _ := call(t, mux, "GET",
		"/v1/analytics/experiment?name=первая_сцена&from=2026-08-02&to=2026-08-02", "t", nil)
	if rec.Code != http.StatusOK {
		t.Fatalf("код %d: %s", rec.Code, rec.Body.String())
	}
	var rep experimentReport
	if err := json.Unmarshal(rec.Body.Bytes(), &rep); err != nil {
		t.Fatal(err)
	}
	if len(rep.Variants) != 2 {
		t.Fatalf("ожидались два варианта: %+v", rep.Variants)
	}
	a, bb := rep.Variants[0], rep.Variants[1]
	if a.Players != 5 || bb.Players != 5 {
		t.Errorf("по пять игроков в группе: %d/%d", a.Players, bb.Players)
	}
	if a.Complete != 0.8 || bb.Complete != 0.2 {
		t.Errorf("дочитывание: a=%v b=%v (ожидалось 0.8 и 0.2)", a.Complete, bb.Complete)
	}
	// Деньги выросли, дочитывание рухнуло — обе строки обязаны быть в вердикте.
	if bb.Revenue != 4.99 || bb.Payers != 1 {
		t.Errorf("деньги группы b: %+v", bb)
	}
	metrics := map[string]verdictLine{}
	for _, v := range rep.Verdict {
		metrics[v.Metric] = v
	}
	if _, ok := metrics["дочитали главу"]; !ok {
		t.Error("нет строки про дочитывание — предохранитель потерян")
	}
	if _, ok := metrics["доля платящих"]; !ok {
		t.Error("нет строки про платящих")
	}
	if _, ok := metrics["доход на игрока"]; !ok {
		t.Error("нет строки про доход")
	}
	if strings.Join(rep.Notes, " ") == "" {
		t.Error("отчёт обязан объяснить правило выбора победителя")
	}
}

// «Неразличимо» и «эффекта нет» — разные вещи. Отчёт обязан говорить, сколько
// наблюдений нужно, иначе первое читается как второе и тест закрывают рано.
func TestExperimentSaysHowMuchDataIsNeeded(t *testing.T) {
	v := compareShare("проверка", 0.20, 0.25, 40, 40)
	if v.Significant {
		t.Error("на сорока наблюдениях разница в 5 пунктов не может быть значимой")
	}
	if v.NeedPlayers < 100 {
		t.Errorf("нужный размер выборки посчитан неправдоподобно: %d", v.NeedPlayers)
	}
	if !strings.Contains(v.Text, "НЕ значит") {
		t.Errorf("текст обязан различать «неразличимо» и «эффекта нет»: %q", v.Text)
	}
	// А на большой выборке та же разница обязана стать различимой.
	big := compareShare("проверка", 0.20, 0.25, 5000, 5000)
	if !big.Significant {
		t.Error("на пяти тысячах разница в 5 пунктов обязана быть значимой")
	}
	if !strings.Contains(big.Text, "лучше") {
		t.Errorf("направление не названо: %q", big.Text)
	}
	worse := compareShare("проверка", 0.25, 0.20, 5000, 5000)
	if !strings.Contains(worse.Text, "ХУЖЕ") {
		t.Errorf("ухудшение обязано быть названо громко: %q", worse.Text)
	}
}
