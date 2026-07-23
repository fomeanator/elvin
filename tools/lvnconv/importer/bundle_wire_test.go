package importer

import (
	"encoding/json"
	"testing"
)

func TestPostProcessBundle(t *testing.T) {
	// A character entity keyed by tech name, plus the layered heroine.
	res := &Result{
		Sprites: map[string]any{
			"cold_matvey": map[string]any{
				"name": "Matvey",
				"kind": "layered",
				"axes": map[string]any{"emotion": []any{"idle", "happy"}},
			},
			"cold_main": map[string]any{
				"name":   "Главная героиня",
				"kind":   "layered",
				"layers": []any{map[string]any{"id": "hair"}, map[string]any{"id": "clothes"}},
				"axes":   map[string]any{"hair": []any{"1", "2"}},
			},
		},
	}

	// One script as a bare op array: a bg for an articy location and a Music set.
	scriptOps := []map[string]any{
		{"op": "bg", "id": "Двор", "sprite_url": "/content/bg/Двор.jpg"},
		{"op": "set", "key": "Music.Battle", "value": true},
		{"op": "set", "key": "Music.Calm", "value": false}, // falsy → no audio op
		{"op": "say", "who": "Matvey", "text": "Hi"},
	}
	sb, _ := json.Marshal(scriptOps)
	res.Scripts = []ScriptFile{{Rel: "scripts/ch1.lvn", Data: sb}}

	xd := XlsxData{
		Chars: []CharMap{
			{StoryName: "Matvey", TechName: "Cold_Matvey", Role: "ВТОР"},
			{StoryName: "Katya", TechName: "Cold_Main", Role: "ГГ"},
		},
		Locations: map[string]string{"Двор": "Cold_yard"},
		Wardrobe: map[string][]WardrobeItem{
			"Wardrobe.mainCh_Hair": {
				{Variable: "Wardrobe.mainCh_Hair", Value: "11", Name: "Офисная причёска.", TechName: "Cold_Main_Hairs_11"},
				{Variable: "Wardrobe.mainCh_Hair", Value: "12", Name: "", TechName: "Cold_Main_Hairs_12"},   // blank → fallback name
				{Variable: "Wardrobe.mainCh_Hair", Value: "13", Hide: true, TechName: "Cold_Main_Hairs_13"}, // hidden → dropped
			},
			"Wardrobe.mainCh_Clothes": {
				{Variable: "Wardrobe.mainCh_Clothes", Value: "1", Name: "Костюм", TechName: "Cold_Main_clothes_1"},
			},
		},
	}
	xd.Protagonist = &xd.Chars[1]

	PostProcessBundle(res, xd, "", nil) // "" = rewrite bg unconditionally (no on-disk фон gate)

	// 1) Character re-key: cold_matvey → Matvey.
	if _, ok := res.Sprites["cold_matvey"]; ok {
		t.Errorf("expected old key cold_matvey removed")
	}
	if _, ok := res.Sprites["Matvey"]; !ok {
		t.Errorf("expected character re-keyed to actor id Matvey")
	}

	// 2) Protagonist alias: Katya added, cold_main kept.
	if _, ok := res.Sprites["Katya"]; !ok {
		t.Errorf("expected heroine aliased to protagonist actor id Katya")
	}
	if _, ok := res.Sprites["cold_main"]; !ok {
		t.Errorf("expected cold_main alias kept for title.hero")
	}

	// 3) Wardrobe block built on the heroine (shared with the Katya alias).
	ent := res.Sprites["cold_main"].(map[string]any)
	wb, ok := ent["wardrobe"].(map[string]any)
	if !ok {
		t.Fatalf("expected wardrobe block on heroine entity")
	}
	hair, ok := wb["hair"].(map[string]any)
	if !ok {
		t.Fatalf("expected hair axis in wardrobe")
	}
	items := hair["items"].([]any)
	if len(items) != 2 { // 11 + 12, the hidden 13 dropped
		t.Fatalf("expected 2 hair items, got %d", len(items))
	}
	if got := items[0].(map[string]any)["name"]; got != "Офисная причёска." {
		t.Errorf("hair item 0 name = %v, want Офисная причёска.", got)
	}
	if got := items[1].(map[string]any)["name"]; got != "Причёска 12" {
		t.Errorf("hair item 1 fallback name = %v, want Причёска 12", got)
	}
	if _, ok := wb["outfit"].(map[string]any); !ok {
		t.Errorf("expected outfit axis in wardrobe")
	}
	// Layers preserved.
	if _, ok := ent["layers"].([]any); !ok {
		t.Errorf("expected existing layers preserved on heroine entity")
	}

	// 4 + 5) Re-parse the transformed script.
	var outOps []map[string]any
	if err := json.Unmarshal(res.Scripts[0].Data, &outOps); err != nil {
		t.Fatalf("script re-parse: %v", err)
	}

	// A player-name default is seeded at the very top (the display name is player-
	// entered); drop it so the positional assertions below read the original script.
	if len(outOps) > 0 && outOps[0]["op"] == "set" && outOps[0]["key"] == "player" {
		if outOps[0]["value"] != "Katya" || outOps[0]["default"] != true {
			t.Errorf("player seed = %v, want value=Katya default=true", outOps[0])
		}
		outOps = outOps[1:]
	} else {
		t.Errorf("expected a leading player-name seed, got %v", outOps[0])
	}

	// bg url rewritten via Locations (id kept as-is).
	if outOps[0]["op"] != "bg" || outOps[0]["id"] != "Двор" {
		t.Fatalf("first op should stay the bg for Двор, got %v", outOps[0])
	}
	if got := outOps[0]["sprite_url"]; got != "/content/bg/Cold_yard.png" {
		t.Errorf("bg sprite_url = %v, want /content/bg/Cold_yard.png", got)
	}

	// Music set followed by an inserted audio op.
	if outOps[1]["op"] != "set" || outOps[1]["key"] != "Music.Battle" {
		t.Fatalf("op[1] should be the Music.Battle set, got %v", outOps[1])
	}
	audio := outOps[2]
	if audio["op"] != "audio" || audio["channel"] != "music" || audio["action"] != "play" {
		t.Fatalf("expected inserted music audio op, got %v", audio)
	}
	if got := audio["url"]; got != "/content/audio/music/Battle.ogg" {
		t.Errorf("audio url = %v, want /content/audio/music/Battle.ogg", got)
	}

	// The falsy Music.Calm set must NOT get an audio op after it.
	if outOps[3]["key"] != "Music.Calm" {
		t.Fatalf("op[3] should be the Music.Calm set, got %v", outOps[3])
	}
	if outOps[4]["op"] != "say" {
		t.Errorf("no audio op should follow a falsy set; op[4] = %v", outOps[4])
	}
}

// TestPostProcessBundleRefreshesCoverURL is a regression test for a live bug:
// collectArt captures Title.CoverURL / Chapter.BgURL from the FIRST bg op's
// sprite_url before indexBackgrounds/rewriteScriptOps swap placeholder-era
// urls for the real HD фон files. Left unrefreshed, the title card and
// chapter-1 thumbnail point at a stale (often missing/corrupt) file even
// though the script itself plays the correct background — exactly the "no
// first background" symptom reported against a live Cold import.
func TestPostProcessBundleRefreshesCoverURL(t *testing.T) {
	scriptOps := []map[string]any{
		{"op": "bg", "id": "Двор", "sprite_url": "/content/bg/STALE_PLACEHOLDER.png"},
		{"op": "say", "who": "Matvey", "text": "Hi"},
	}
	sb, _ := json.Marshal(scriptOps)
	res := &Result{
		Sprites: map[string]any{},
		Scripts: []ScriptFile{{Rel: "scripts/ch1.lvn", Data: sb}},
		Title: Title{
			Seasons: []Season{{Chapters: []Chapter{{
				ID: "ch1", BgURL: "/content/bg/STALE_PLACEHOLDER.png",
			}}}},
		},
	}
	xd := XlsxData{Locations: map[string]string{"Двор": "Cold_yard"}}

	PostProcessBundle(res, xd, "", nil) // "" = rewrite bg unconditionally (no on-disk фон gate)

	const want = "/content/bg/Cold_yard.png"
	if got := res.Title.CoverURL; got != want {
		t.Errorf("Title.CoverURL = %q, want %q (stale pre-rewrite url leaked through)", got, want)
	}
	if got := res.Title.Seasons[0].Chapters[0].BgURL; got != want {
		t.Errorf("Chapter.BgURL = %q, want %q (stale pre-rewrite url leaked through)", got, want)
	}
}

func TestPostProcessBundleDocShapeAndSound(t *testing.T) {
	// The .lvn document object shape ({scene, script:[...]}) with a Sound cue.
	doc := map[string]any{
		"scene": "s1",
		"script": []any{
			map[string]any{"op": "set", "key": "Sound.Door", "value": "1"},
		},
	}
	db, _ := json.Marshal(doc)
	res := &Result{Scripts: []ScriptFile{{Rel: "scripts/ch1.lvn", Data: db}}}

	PostProcessBundle(res, XlsxData{}, "", nil)

	var out map[string]any
	if err := json.Unmarshal(res.Scripts[0].Data, &out); err != nil {
		t.Fatalf("re-parse doc: %v", err)
	}
	if out["scene"] != "s1" {
		t.Errorf("scene should be preserved, got %v", out["scene"])
	}
	script := out["script"].([]any)
	if len(script) != 2 {
		t.Fatalf("expected set + inserted audio op, got %d", len(script))
	}
	audio := script[1].(map[string]any)
	if audio["channel"] != "sfx" || audio["url"] != "/content/audio/sfx/Door.ogg" {
		t.Errorf("expected sfx Door audio op, got %v", audio)
	}
}
