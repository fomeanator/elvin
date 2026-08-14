package main

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// slidesFixture: один день событий + скомпилированная глава на диске, чтобы
// отчёт мог достать имена меток и тексты вариантов — как на живом сервере.
func slidesFixture(t *testing.T, events string) slidesReport {
	t.Helper()
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "2026-08-02.jsonl"), []byte(events), 0o644); err != nil {
		t.Fatal(err)
	}
	content := t.TempDir()
	script := `{"scene":"ch1","script":[
		{"op":"label","id":"начало"},
		{"op":"bg","sprite_url":"/content/bg/двор.png"},
		{"op":"say","who":"Аня","text":"Привет."},
		{"op":"label","id":"развилка"},
		{"op":"choice","options":[
			{"text":"Пойти домой"},
			{"text":"Остаться до утра"}
		]},
		{"op":"label","id":"финал"},
		{"op":"say","who":"Аня","text":"Конец."}
	]}`
	if err := os.MkdirAll(filepath.Join(content, "cold"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(content, "cold", "ch1.lvn"), []byte(script), 0o644); err != nil {
		t.Fatal(err)
	}
	// Манифест настоящий: имя главы и путь к скрипту отчёт берёт оттуда же,
	// откуда их берёт игрок, — угадывать имя файла нельзя.
	manifest := `{"titles":[{"id":"cold","name":"Cold","seasons":[{"chapters":[
		{"id":"cold-ch1","name":"Эпизод 1","script_url":"/content/cold/ch1.lvn"}]}]}]}`
	if err := os.WriteFile(filepath.Join(content, "manifest.json"), []byte(manifest), 0o644); err != nil {
		t.Fatal(err)
	}
	s := &AnalyticsService{dir: dir, rollups: newRollupStore(dir), adminToken: "t",
		chapters: newChapterIndex(filepath.Join(content, "manifest.json"))}
	mux := http.NewServeMux()
	s.Routes(mux)
	rec, _ := call(t, mux, "GET",
		"/v1/analytics/slides?title=cold&chapter=cold-ch1&from=2026-08-02&to=2026-08-02", "t", nil)
	if rec.Code != http.StatusOK {
		t.Fatalf("код %d: %s", rec.Code, rec.Body.String())
	}
	var rep slidesReport
	if err := json.Unmarshal(rec.Body.Bytes(), &rep); err != nil {
		t.Fatal(err)
	}
	return rep
}

func ev(name, user string, props string) string {
	p := ""
	if props != "" {
		p = `,"props":{"title":"cold","chapter":"cold-ch1",` + props + `}`
	} else {
		p = `,"props":{"title":"cold","chapter":"cold-ch1"}`
	}
	return `{"name":"` + name + `","ts":"2026-08-02T10:00:00Z","user":"` + user + `"` + p + "}\n"
}

// Воронка по слайдам читается сверху вниз и показывает, между какими метками
// перестали читать. Порядок — по ходу главы, а не по частоте.
func TestSlidesFunnelInScriptOrder(t *testing.T) {
	e := ""
	for _, u := range []string{"a", "b", "c", "d"} {
		e += ev("chapter_start", u, "")
		e += ev("label_reach", u, `"label":"начало","at":0`)
	}
	// До развилки дошли трое, до финала — один.
	for _, u := range []string{"a", "b", "c"} {
		e += ev("label_reach", u, `"label":"развилка","at":3`)
	}
	e += ev("label_reach", "a", `"label":"финал","at":6`)
	rep := slidesFixture(t, e)

	if len(rep.Slides) != 3 {
		t.Fatalf("ожидалось три метки, получено %d: %+v", len(rep.Slides), rep.Slides)
	}
	if rep.Slides[0].At != 0 || rep.Slides[1].At != 3 || rep.Slides[2].At != 6 {
		t.Errorf("порядок должен быть по ходу главы: %+v", rep.Slides)
	}
	if rep.Slides[0].Label != "начало" || rep.Slides[2].Label != "финал" {
		t.Errorf("имена меток берутся из скрипта: %+v", rep.Slides)
	}
	// Самая дорогая потеря — между развилкой и финалом: минус двое.
	if rep.Slides[2].Lost != 2 || rep.Worst != "финал" || rep.WorstLost != 2 {
		t.Errorf("худшее падение не найдено: worst=%q lost=%d %+v", rep.Worst, rep.WorstLost, rep.Slides)
	}
	if rep.Slides[1].OfStart != 0.75 {
		t.Errorf("доля от вошедших: %v", rep.Slides[1].OfStart)
	}
	// Кадр восстанавливается тем же способом, что и в отчёте о выходах.
	if rep.Slides[0].BG == "" || rep.Slides[0].Line == "" {
		t.Errorf("у метки должен быть кадр: %+v", rep.Slides[0])
	}
}

// Показали минус выбрали — это люди, которые ушли ровно в момент, когда игра
// попросила о решении. Хранить только распределение вариантов значит потерять
// самый дорогой сигнал главы.
func TestSlidesChoiceCountsWhoLeftWithoutChoosing(t *testing.T) {
	e := ""
	for _, u := range []string{"a", "b", "c", "d", "e"} {
		e += ev("chapter_start", u, "")
		e += ev("choice_shown", u, `"written":2,"shown":2,"at":4`)
	}
	e += ev("choice_pick", "a", `"option":0,"seconds":3,"at":4`)
	e += ev("choice_pick", "b", `"option":1,"seconds":9,"at":4`)
	e += ev("choice_pick", "c", `"option":1,"seconds":30,"at":4`)
	rep := slidesFixture(t, e)

	if len(rep.Choices) != 1 {
		t.Fatalf("ожидалась одна развилка: %+v", rep.Choices)
	}
	c := rep.Choices[0]
	if c.Shown != 5 || c.Picked != 3 || c.LeftHere != 2 {
		t.Errorf("показали/выбрали/ушли: %d/%d/%d", c.Shown, c.Picked, c.LeftHere)
	}
	if c.LeaveShare != 0.4 {
		t.Errorf("доля ушедших на выборе: %v", c.LeaveShare)
	}
	if c.MedianSecs != 9 {
		t.Errorf("медиана раздумья %d, ожидалось 9", c.MedianSecs)
	}
	if len(c.Options) != 2 || c.Options[0].Option != 1 || c.Options[0].Picks != 2 {
		t.Errorf("сверху самый популярный вариант: %+v", c.Options)
	}
	// Текст варианта — авторский, из скрипта, а не присланный клиентом.
	if c.Options[0].Text != "Остаться до утра" {
		t.Errorf("текст варианта из скрипта: %q", c.Options[0].Text)
	}
	if c.Label != "развилка" {
		t.Errorf("развилка привязана к ближайшей метке: %q", c.Label)
	}
}

// «Написано три, видно один» законно (гейты), но заметить это надо: иначе
// развилка мертва, а в отчёте выглядит как обычная.
func TestSlidesNotesLockedChoice(t *testing.T) {
	e := ev("chapter_start", "a", "") + ev("choice_shown", "a", `"written":3,"shown":1,"at":4`)
	rep := slidesFixture(t, e)
	if len(rep.Choices) != 1 || !strings.Contains(rep.Choices[0].LockedNote, "написано 3") {
		t.Errorf("запертая развилка должна быть названа: %+v", rep.Choices)
	}
}

// Условие приёмки задачи: вошли = дочитали + ушли. Отчёт обязан либо
// подтвердить, либо назвать расхождение числом — молчание тут хуже ошибки.
func TestSlidesBalanceStatement(t *testing.T) {
	e := ""
	for _, u := range []string{"a", "b", "c"} {
		e += ev("chapter_start", u, "")
	}
	e += ev("chapter_finish", "a", "")
	e += ev("chapter_abandon", "b", `"at":5`)
	rep := slidesFixture(t, e)
	if !strings.Contains(rep.Balance, "не сходится на 1") {
		t.Errorf("расхождение обязано быть названо: %q", rep.Balance)
	}
	e += ev("chapter_abandon", "c", `"at":2`)
	rep = slidesFixture(t, e)
	if !strings.HasPrefix(rep.Balance, "сходится") {
		t.Errorf("сошедшийся баланс: %q", rep.Balance)
	}
}

// Свёртка чекпоинтится по дням и потом складывается. Ровно на этом уже один
// раз погорели точки выхода: на диске всё верно, а отчёт за окно пустой.
func TestSlidesSurviveRollupMerge(t *testing.T) {
	dir := t.TempDir()
	day1 := ev("chapter_start", "a", "") + ev("label_reach", "a", `"label":"начало","at":0`) +
		ev("choice_shown", "a", `"written":2,"shown":2,"at":4`) +
		ev("choice_pick", "a", `"option":0,"seconds":2,"at":4`)
	day2 := strings.ReplaceAll(day1, "2026-08-02", "2026-08-03")
	for name, body := range map[string]string{"2026-08-02.jsonl": day1, "2026-08-03.jsonl": day2} {
		if err := os.WriteFile(filepath.Join(dir, name), []byte(body), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	s := &AnalyticsService{dir: dir, rollups: newRollupStore(dir), adminToken: "t"}
	mux := http.NewServeMux()
	s.Routes(mux)
	rec, _ := call(t, mux, "GET",
		"/v1/analytics/slides?title=cold&chapter=cold-ch1&from=2026-08-02&to=2026-08-03", "t", nil)
	var rep slidesReport
	if err := json.Unmarshal(rec.Body.Bytes(), &rep); err != nil {
		t.Fatal(err)
	}
	if len(rep.Slides) != 1 || rep.Slides[0].Reached != 2 {
		t.Errorf("метки за два дня должны сложиться: %+v", rep.Slides)
	}
	if len(rep.Choices) != 1 || rep.Choices[0].Shown != 2 || rep.Choices[0].Picked != 2 {
		t.Errorf("развилки за два дня должны сложиться: %+v", rep.Choices)
	}
	if len(rep.Choices) == 1 && rep.Choices[0].Options[0].Picks != 2 {
		t.Errorf("варианты за два дня должны сложиться: %+v", rep.Choices[0].Options)
	}
}
