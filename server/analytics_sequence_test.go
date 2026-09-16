package main

import (
	"encoding/json"
	"testing"
)

func seqLine(name, ts, user, props string) seqEvent {
	var p map[string]json.RawMessage
	_ = json.Unmarshal([]byte(props), &p)
	return seqEvent{Name: name, TS: ts, Chapter: rollupProp(p, "chapter"), Props: p}
}

// ВОРОНКА ПО ШАГАМ (Илья 16.09): «начал главу 0 → прошёл 10 строк → был в
// гардеробе → купил» считается по игроку в порядке времени; у обрыва видно,
// куда ушли вместо следующего шага.
func TestSequenceFunnelCountsPlayersInOrder(t *testing.T) {
	byUser := map[string][]seqEvent{
		"a": {
			seqLine("chapter_start", "T1", "a", `{"chapter":"ch0"}`),
			seqLine("chapter_abandon", "T2", "a", `{"chapter":"ch0","at":57}`),
			seqLine("ui_use", "T3", "a", `{"time":{"WardrobeTabScreen":30},"taps":{"WardrobeTabScreen/buy":1}}`),
			seqLine("wardrobe_buy", "T4", "a", `{"sku":"wardrobe:x"}`),
		},
		"b": {
			seqLine("chapter_start", "T1", "b", `{"chapter":"ch0"}`),
			seqLine("chapter_abandon", "T2", "b", `{"chapter":"ch0","at":3}`),
			seqLine("ui_use", "T3", "b", `{"time":{"GachaScreen":40}}`),
		},
		"c": {
			seqLine("chapter_start", "T1", "c", `{"chapter":"ch0"}`),
			seqLine("chapter_finish", "T2", "c", `{"chapter":"ch0"}`),
			seqLine("ui_use", "T3", "c", `{"time":{"WardrobeTabScreen":12}}`),
			seqLine("ui_use", "T4", "c", `{"time":{"GachaScreen":50}}`),
		},
	}
	steps := parseSteps("chapter_start:chapter=ch0; progress:chapter=ch0,at>=10; screen=WardrobeTabScreen; wardrobe_buy")
	if len(steps) != 4 || steps[2].Name != "screen" {
		t.Fatalf("шаги разобраны не так: %+v", steps)
	}
	rep := sequenceFunnel(byUser, steps)
	want := []int{3, 2, 2, 1}
	for i, w := range want {
		if rep.Steps[i].Users != w {
			t.Fatalf("шаг %d: игроков %d, ожидалось %d (%+v)", i, rep.Steps[i].Users, w, rep.Steps)
		}
	}
	// b ушёл из главы на строке 3 — это и есть «куда ушёл вместо» после старта
	if rep.Steps[1].Lost != 1 || len(rep.Steps[0].Instead) == 0 || rep.Steps[0].Instead[0].Name != "chapter_abandon ch0 #3" {
		t.Fatalf("«куда ушли вместо» после первого шага: %+v / %+v", rep.Steps[0], rep.Steps[1])
	}
	// c был в гардеробе, но не купил — ушёл в крутки
	if rep.Steps[3].Lost != 1 || len(rep.Steps[2].Instead) == 0 || rep.Steps[2].Instead[0].Name != "screen:GachaScreen" {
		t.Fatalf("«куда ушли вместо» после гардероба: %+v / %+v", rep.Steps[2], rep.Steps[3])
	}
}

// ПУТИ С ЭКРАНА (Илья 16.09: «какой процент на магазин кликает в меню»):
// доля игроков экрана по элементам, тапы одного игрока не раздувают долю.
func TestPathsCountPlayersNotTaps(t *testing.T) {
	byUser := map[string][]seqEvent{
		"a": {seqLine("ui_use", "T1", "a", `{"time":{"home":30},"taps":{"home/nav-store":5,"home/nav-wardrobe":1},"chains":{"home/nav-store>screen:PackShopScreen":5}}`)},
		"b": {seqLine("ui_use", "T1", "b", `{"time":{"home":10},"taps":{"home/nav-wardrobe":2}}`)},
		"c": {seqLine("ui_use", "T1", "c", `{"time":{"home":10}}`)},
		"d": {seqLine("ui_use", "T1", "d", `{"time":{"GachaScreen":10}}`)},
	}
	rep := pathsFor(byUser, "home")
	if rep.Players != 3 {
		t.Fatalf("на главной было 3 игрока, насчитано %d", rep.Players)
	}
	if len(rep.Elements) != 2 || rep.Elements[0].Element != "nav-wardrobe" || rep.Elements[0].Players != 2 {
		t.Fatalf("элементы по игрокам: %+v", rep.Elements)
	}
	store := rep.Elements[1]
	if store.Players != 1 || store.Taps != 5 || store.Next[0].Name != "screen:PackShopScreen" {
		t.Fatalf("магазин: %+v", store)
	}
	if len(rep.Screens) != 2 || rep.Screens[0].Name != "home" || rep.Screens[0].Count != 3 {
		t.Fatalf("экраны окна: %+v", rep.Screens)
	}
}
