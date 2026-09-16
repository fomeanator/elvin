package main

import (
	"encoding/json"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/lvn"
)

// МИНУТА ИСПОЛЬЗОВАНИЯ СКЛАДЫВАЕТСЯ ПО ЭКРАНАМ (TR-126): секунды суммируются,
// тапы ложатся под свой экран по ключу «экран/элемент», слияние дней не
// теряет ни того, ни другого, отчёт сортирует экраны по времени.
func TestUsageFoldsMinutesIntoScreens(t *testing.T) {
	r := newDayRollup("2026-09-15")
	line := func(props string) []byte {
		return []byte(`{"name":"ui_use","ts":"2026-09-15T10:00:00Z","props":` + props + `}`)
	}
	r.foldLine(line(`{"sid":"s1","time":{"home":40,"GachaScreen":20},"taps":{"home/nav-gacha":2,"GachaScreen/gacha-spin":5}}`))
	r.foldLine(line(`{"sid":"s1","time":{"GachaScreen":60},"taps":{"GachaScreen/gacha-spin":7,"GachaScreen/tap":1}}`))
	if r.Usage["home"].Seconds != 40 || r.Usage["GachaScreen"].Seconds != 80 {
		t.Fatalf("секунды не сложились: %+v", r.Usage)
	}
	if r.Usage["GachaScreen"].Taps["gacha-spin"] != 12 || r.Usage["home"].Taps["nav-gacha"] != 2 {
		t.Fatalf("тапы не легли под экран: %+v", r.Usage["GachaScreen"].Taps)
	}
	other := newDayRollup("2026-09-14")
	other.foldLine(line(`{"sid":"s2","time":{"home":10},"taps":{"home/nav-gacha":1}}`))
	r.mergeFrom(other)
	if r.Usage["home"].Seconds != 50 || r.Usage["home"].Taps["nav-gacha"] != 3 {
		t.Fatalf("слияние дней потеряло использование: %+v", r.Usage["home"])
	}
	// цепочка «нажал → сделал»: конверсия считается от тапов по элементу
	r.foldLine(line(`{"sid":"s1","chains":{"GachaScreen/gacha-spin>wardrobe_buy":3,"GachaScreen/gacha-spin>screen:home":6}}`))
	rep := usageReportOf(r, 2)
	if len(rep.Screens) != 2 || rep.Screens[0].Screen != "GachaScreen" || rep.Screens[0].Taps != 13 {
		t.Fatalf("отчёт не по времени или тапы не те: %+v", rep.Screens)
	}
	if rep.TotalSeconds != 130 || rep.Screens[0].Elements[0].Name != "gacha-spin" {
		t.Fatalf("итоги отчёта разошлись: %+v", rep)
	}
	ch := rep.Screens[0].Chains
	if len(ch) != 2 || ch[0].Outcome != "screen:home" || ch[0].N != 6 || ch[1].Outcome != "wardrobe_buy" || ch[1].Share != 0.25 {
		t.Fatalf("цепочки не сложились: %+v", ch)
	}
	// свёртка переживает запись и чтение
	raw, _ := json.Marshal(r)
	var back dayRollup
	if err := json.Unmarshal(raw, &back); err != nil || back.Usage["home"].Seconds != 50 {
		t.Fatalf("использование не переживает сериализацию: %v %+v", err, back.Usage)
	}
}

// ВОРОНКА ПО СТРОКАМ (TR-126, Илья: «0-ю строку увидели 100, дальше кликнули
// 97…»): дошедших до строки — дочитавшие плюс ушедшие на индексе не раньше
// неё; строки — только реплики, развилки и метки автора.
func TestLinesReachCountsFromExits(t *testing.T) {
	doc, err := lvn.Parse([]byte(`{"script":[
	  {"op":"say","who":"Она","text":"Привет"},
	  {"op":"bg","sprite_url":"/bg.jpg"},
	  {"op":"say","who":"Он","text":"Привет-привет"},
	  {"op":"choice","options":[{"text":"а"},{"text":"б"}]},
	  {"op":"say","text":"Конец"}
	]}`))
	if err != nil {
		t.Fatal(err)
	}
	ch := &chapRoll{Starts: 100, Finishes: 80, Abandons: 20,
		Exits: map[string]int{"0": 3, "2": 10, "3": 5, "4": 2}}
	rows := linesReach(ch, doc)
	if len(rows) != 4 {
		t.Fatalf("строк должно быть 4 (bg не строка): %d", len(rows))
	}
	want := []int{100, 97, 87, 82}
	for i, w := range want {
		if rows[i].Reached != w {
			t.Fatalf("строка %d (#%d): дошло %d, ожидалось %d", i, rows[i].At, rows[i].Reached, w)
		}
	}
	if rows[1].Lost != 3 || rows[2].Lost != 10 || rows[1].Who != "Он" || rows[1].Line != "Привет-привет" || rows[2].Op != "choice" {
		t.Fatalf("потери или текст строки не те: %+v / %+v", rows[1], rows[2])
	}
}

// ВРЕМЯ НА СТРОКЕ (TR-126, Илья 16.09): список секунд из конца главы
// складывается по строкам, слияние дней суммирует, воронка по строкам
// показывает среднее по сессиям, где строку видели.
func TestDwellFoldsIntoLineAverages(t *testing.T) {
	r := newDayRollup("2026-09-16")
	fin := `{"name":"chapter_finish","ts":"2026-09-16T10:00:00Z","props":{"title":"t","chapter":"c","dwell":[3,10,0,7]}}`
	ab := `{"name":"chapter_abandon","ts":"2026-09-16T10:05:00Z","props":{"title":"t","chapter":"c","at":1,"dwell":[5,20]}}`
	r.foldLine([]byte(fin))
	r.foldLine([]byte(ab))
	ch := r.Titles["t"].Chapters["c"]
	if ch.Dwell["1"] != 30 || ch.DwellN["1"] != 2 || ch.DwellN["2"] != 0 || ch.Dwell["3"] != 7 {
		t.Fatalf("секунды по строкам не сложились: %+v / %+v", ch.Dwell, ch.DwellN)
	}
	other := newDayRollup("2026-09-15")
	other.foldLine([]byte(fin))
	r.mergeFrom(other)
	ch = r.Titles["t"].Chapters["c"]
	if ch.Dwell["1"] != 40 || ch.DwellN["1"] != 3 {
		t.Fatalf("слияние дней потеряло время на строке: %+v / %+v", ch.Dwell, ch.DwellN)
	}
	doc, _ := lvn.Parse([]byte(`{"script":[{"op":"say","text":"а"},{"op":"say","text":"б"},{"op":"bg"},{"op":"say","text":"в"}]}`))
	rows := linesReach(ch, doc)
	if len(rows) != 3 || rows[1].Seconds != 13.3333 || rows[1].Measured != 3 || rows[2].Measured != 2 || rows[2].Seconds != 7 {
		t.Fatalf("среднее по строке не то: %+v", rows)
	}
}
