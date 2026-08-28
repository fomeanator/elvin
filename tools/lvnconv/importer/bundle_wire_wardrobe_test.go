package importer

// Wardrobe substitution + the howto documentation gates.
//
// replaceWardrobeScenes is the most surgical pass in the bundle import — it
// deletes a span of the author's script and splices ours in. Its two live
// regressions (a kept `goto` that cut the path to the closing marker, and the
// picker's orphaned branch bodies sitting at the end of every chapter) were
// both found in production, and roundtrip_guard_test.go pins the two HELPERS
// that were fixed then (keepInWardrobeBlock, removeUnreachableOps). What was
// missing is a test of the pass ITSELF: which ops come out, in what order, and
// what the author is told when the block is malformed.
//
// The second half pins the documentation: howto/wardrobe/ and
// howto/import-articy/ describe this exact code to authors, and a doc that
// describes last month's importer is worse than no doc. Both examples are
// checked against the real output, so they cannot rot silently.

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/lvns"
)

// repoRoot is the repository root as seen from tools/lvnconv/importer.
func repoRoot() string { return filepath.Join("..", "..", "..") }

// opNames flattens ops to their op names, for readable assertions.
func opNames(ops []map[string]any) []string {
	out := make([]string, 0, len(ops))
	for _, op := range ops {
		n, _ := op["op"].(string)
		out = append(out, n)
	}
	return out
}

func decodeOps(t *testing.T, data []byte) []map[string]any {
	t.Helper()
	ops, _, ok := decodeScriptOps(data)
	if !ok {
		t.Fatalf("script did not decode: %s", data)
	}
	return ops
}

// TestReplaceWardrobeSwapsThePickerForTheSheet is the core contract: between the
// flag markers the hand-built picker disappears and `actor` + `wardrobe_show`
// take its place, while everything that is NOT the picker (audio, bg, other
// sets) survives in order.
func TestReplaceWardrobeSwapsThePickerForTheSheet(t *testing.T) {
	ops := []map[string]any{
		{"op": "set", "key": "Open.Wardrobe", "value": true},
		{"op": "say", "who": "Мира", "text": "Что надеть?"},
		{"op": "audio", "channel": "music", "action": "play", "url": "/content/audio/music/Calm.ogg"},
		{"op": "set", "key": "Wardrobe.mainCh_Clothes", "value": 1}, // picker's write — dropped
		{"op": "set", "key": "Plot.Flag", "value": true},            // unrelated — kept
		{"op": "choice", "options": []any{map[string]any{"text": "Платье", "goto": "n10"}}},
		{"op": "set", "key": "Open.Wardrobe", "value": false},
		{"op": "say", "who": "Мира", "text": "Готова."},
	}
	data, _ := json.Marshal(ops)
	sf := ScriptFile{Rel: "scripts/ch01.lvn", Data: data}

	replaceWardrobeInScript(&sf, "Мира", nil)
	got := decodeOps(t, sf.Data)

	want := []string{"set", "actor", "wardrobe_show", "audio", "set", "set", "say"}
	if strings.Join(opNames(got), ",") != strings.Join(want, ",") {
		t.Fatalf("op sequence = %v, want %v", opNames(got), want)
	}
	if got[1]["id"] != "Мира" || got[1]["show"] != true || got[1]["position"] != "center" {
		t.Errorf("injected actor = %v, want the wardrobe char staged center", got[1])
	}
	if got[2]["char"] != "Мира" {
		t.Errorf("wardrobe_show char = %v, want Мира", got[2]["char"])
	}
	// The kept `set` must be the unrelated plot flag, not the picker's outfit write.
	if got[4]["key"] != "Plot.Flag" {
		t.Errorf("kept set = %v, want the unrelated Plot.Flag (picker writes are dropped)", got[4])
	}
	if got[5]["key"] != "Open.Wardrobe" || got[5]["value"] != false {
		t.Errorf("op[5] = %v, want the closing Open.Wardrobe marker", got[5])
	}
}

// TestReplaceWardrobeLeavesUnterminatedBlockIntact: a missing `= false` marker is
// a content bug the importer must not paper over — the whole block is left as
// the author wrote it. But leaving it SILENTLY is how one scene in a novel ships
// the source's own text picker while every other dressing scene opens the real
// sheet, with nothing in the import report to explain it. Both halves are pinned:
// the script is untouched, and the import is told.
func TestReplaceWardrobeLeavesUnterminatedBlockIntact(t *testing.T) {
	ops := []map[string]any{
		{"op": "set", "key": "Open.Wardrobe", "value": true},
		{"op": "say", "who": "Мира", "text": "Что надеть?"},
		{"op": "choice", "options": []any{map[string]any{"text": "Платье", "goto": "n10"}}},
		{"op": "label", "id": "n10"},
		{"op": "say", "who": "Мира", "text": "Платье."},
	}
	data, _ := json.Marshal(ops)
	sf := ScriptFile{Rel: "scripts/ch01.lvn", Data: data}
	before := string(sf.Data)

	warnings := replaceWardrobeInScript(&sf, "Мира", nil)

	if string(sf.Data) != before {
		t.Errorf("an unterminated wardrobe block must be left untouched, got:\n%s", sf.Data)
	}
	if len(warnings) != 1 {
		t.Fatalf("warnings = %v, want exactly one about the unclosed block", warnings)
	}
	for _, want := range []string{"scripts/ch01.lvn", "Open.Wardrobe"} {
		if !strings.Contains(warnings[0], want) {
			t.Errorf("warning %q should name %q so the author can find the scene", warnings[0], want)
		}
	}
}

// TestPostProcessBundleReportsUnclosedWardrobeBlock: the warning has to reach
// the import report (CLI stderr / the API response), not just the helper's
// return value.
func TestPostProcessBundleReportsUnclosedWardrobeBlock(t *testing.T) {
	ops := []map[string]any{
		{"op": "set", "key": "Open.Wardrobe", "value": true},
		{"op": "say", "who": "Мира", "text": "Что надеть?"},
	}
	data, _ := json.Marshal(ops)
	res := &Result{
		Sprites: map[string]any{"demo_main": map[string]any{"name": "Мира"}},
		Scripts: []ScriptFile{{Rel: "scripts/ch01.lvn", Data: data}},
	}
	xd := XlsxData{Chars: []CharMap{{StoryName: "Мира", TechName: "Demo_Main", Role: "ГГ"}}}
	xd.Protagonist = &xd.Chars[0]

	PostProcessBundle(res, xd, "", nil)

	found := false
	for _, w := range res.Warnings {
		if strings.Contains(w, "Open.Wardrobe") {
			found = true
		}
	}
	if !found {
		t.Errorf("res.Warnings = %v, want the unclosed-wardrobe-block warning", res.Warnings)
	}
}

// TestReplaceWardrobeDropsThePickersDeadTail: the linearizer appends every
// choice branch's BODY at the script tail, so after the swap those bodies are
// unreachable — and they used to sit at the visible end of every chapter,
// reading to the author as "broken wardrobe choices". They must be gone, and
// the story after the block must stay reachable (the regression that made a
// third of a live chapter unplayable).
func TestReplaceWardrobeDropsThePickersDeadTail(t *testing.T) {
	ops := []map[string]any{
		{"op": "set", "key": "Open.Wardrobe", "value": true},
		{"op": "choice", "options": []any{
			map[string]any{"text": "Платье", "goto": "n10"},
			map[string]any{"text": "Костюм", "goto": "n11"},
		}},
		{"op": "goto", "label": "n10"}, // the picker's own control flow — must not survive
		{"op": "set", "key": "Open.Wardrobe", "value": false},
		{"op": "say", "who": "Мира", "text": "Готова."},
		{"op": "goto", "label": "__end"},
		// The branch bodies live OUTSIDE the block, at the script tail.
		{"op": "label", "id": "n10"},
		{"op": "set", "key": "Wardrobe.mainCh_Clothes", "value": 1},
		{"op": "goto", "label": "__end"},
		{"op": "label", "id": "n11"},
		{"op": "set", "key": "Wardrobe.mainCh_Clothes", "value": 2},
		{"op": "goto", "label": "__end"},
	}
	data, _ := json.Marshal(ops)
	sf := ScriptFile{Rel: "scripts/ch01.lvn", Data: data}

	replaceWardrobeInScript(&sf, "Мира", nil)
	got := decodeOps(t, sf.Data)

	for _, op := range got {
		switch n, _ := op["op"].(string); n {
		case "label":
			if id, _ := op["id"].(string); id == "n10" || id == "n11" {
				t.Errorf("dead picker branch %q survived the swap: %v", id, opNames(got))
			}
		case "set":
			if k, _ := op["key"].(string); k == "Wardrobe.mainCh_Clothes" {
				t.Errorf("the picker's outfit write survived: %v", op)
			}
		}
	}
	want := []string{"set", "actor", "wardrobe_show", "set", "say", "goto"}
	if strings.Join(opNames(got), ",") != strings.Join(want, ",") {
		t.Errorf("op sequence = %v, want %v (block swapped, tail pruned, story still reachable)",
			opNames(got), want)
	}
}

// TestReplaceWardrobeRetargetsReferencesToDroppedLabels: a label that lived
// INSIDE the block goes away with the picker, so anything still pointing at it
// would be a dangling jump — the validator's hardest error to explain. Those
// references are retargeted to __end, which is minted if the script lacks it.
func TestReplaceWardrobeRetargetsReferencesToDroppedLabels(t *testing.T) {
	ops := []map[string]any{
		{"op": "set", "key": "Open.Wardrobe", "value": true},
		{"op": "label", "id": "wpick"}, // the picker's re-open hub — dropped with the block
		{"op": "choice", "options": []any{map[string]any{"text": "Платье", "goto": "n10"}}},
		{"op": "set", "key": "Open.Wardrobe", "value": false},
		{"op": "say", "who": "Мира", "text": "Готова."},
		{"op": "goto", "label": "wpick"}, // reachable, and its target just vanished
	}
	data, _ := json.Marshal(ops)
	sf := ScriptFile{Rel: "scripts/ch01.lvn", Data: data}

	replaceWardrobeInScript(&sf, "Мира", nil)
	got := decodeOps(t, sf.Data)

	var jump map[string]any
	hasEnd := false
	for _, op := range got {
		switch n, _ := op["op"].(string); n {
		case "goto":
			jump = op
		case "label":
			if id, _ := op["id"].(string); id == "wpick" {
				t.Errorf("the in-block label survived the swap: %v", opNames(got))
			} else if id == "__end" {
				hasEnd = true
			}
		}
	}
	if jump == nil || jump["label"] != "__end" {
		t.Errorf("goto = %v, want it retargeted to __end", jump)
	}
	if !hasEnd {
		t.Errorf("__end was retargeted to but never defined: %v", opNames(got))
	}
}

// ─────────────────────── documentation gates ───────────────────────

// howtoWardrobeCatalog is the wardrobe half of the doc example, parsed.
func howtoWardrobeCatalog(t *testing.T) map[string]any {
	t.Helper()
	path := filepath.Join(repoRoot(), "howto", "wardrobe", "manifest.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read %s: %v", path, err)
	}
	var mf map[string]any
	if err := json.Unmarshal(data, &mf); err != nil {
		t.Fatalf("parse %s: %v", path, err)
	}
	sprites, _ := mf["sprites"].(map[string]any)
	if len(sprites) == 0 {
		t.Fatalf("%s has no sprites — the gate cannot check anything", path)
	}
	return sprites
}

// TestHowtoWardrobeExampleDocumentsEveryEmittedField: every field the importer
// actually writes into a wardrobe slot/item must appear in the documented
// example. Adding a field to buildWardrobes without showing it to authors is
// how a doc goes stale; this makes it a test failure instead.
func TestHowtoWardrobeExampleDocumentsEveryEmittedField(t *testing.T) {
	// An import that exercises every branch of buildWardrobes: the protagonist's
	// hair + outfit slots, an NPC outfit slot, and a [premium] priced item.
	res := &Result{Sprites: map[string]any{
		"Мира":  map[string]any{"name": "Мира"},
		"Felix": map[string]any{"name": "Felix"},
	}}
	xd := XlsxData{
		Chars: []CharMap{
			{StoryName: "Мира", TechName: "Demo_Main", Role: "ГГ"},
			{StoryName: "Felix", TechName: "Demo_Felix", Role: "ВТОР"},
		},
		Wardrobe: map[string][]WardrobeItem{
			"Wardrobe.mainCh_Hair": {
				{Variable: "Wardrobe.mainCh_Hair", Value: "1", Name: "Хвост", TechName: "Demo_Main_Hairs_1"},
			},
			"Wardrobe.mainCh_Clothes": {
				{Variable: "Wardrobe.mainCh_Clothes", Value: "0", Name: "Без одежды", TechName: "Demo_Main_clothes_0"},
				{Variable: "Wardrobe.mainCh_Clothes", Value: "1", Name: "Платье", TechName: "Demo_Main_clothes_1", Premium: true},
			},
			"Wardrobe.Felix": {
				{Variable: "Wardrobe.Felix", Value: "1", Name: "Пальто", TechName: "Demo_Felix_clothes_1"},
			},
		},
	}
	xd.Protagonist = &xd.Chars[0]
	buildWardrobes(res, xd, nil)

	emittedSlot, emittedItem := map[string]bool{}, map[string]bool{}
	for _, ent := range res.Sprites {
		wb, _ := ent.(map[string]any)["wardrobe"].(map[string]any)
		for _, slot := range wb {
			sm, _ := slot.(map[string]any)
			for k := range sm {
				if k != "items" {
					emittedSlot[k] = true
				}
			}
			items, _ := sm["items"].([]any)
			for _, it := range items {
				for k := range it.(map[string]any) {
					emittedItem[k] = true
				}
			}
		}
	}
	if len(emittedSlot) == 0 || len(emittedItem) == 0 {
		t.Fatalf("buildWardrobes emitted nothing — the gate would pass vacuously")
	}

	docSlot, docItem := map[string]bool{}, map[string]bool{}
	for _, ent := range howtoWardrobeCatalog(t) {
		wb, _ := ent.(map[string]any)["wardrobe"].(map[string]any)
		for _, slot := range wb {
			sm, _ := slot.(map[string]any)
			for k := range sm {
				if k != "items" {
					docSlot[k] = true
				}
			}
			items, _ := sm["items"].([]any)
			for _, it := range items {
				for k := range it.(map[string]any) {
					docItem[k] = true
				}
			}
		}
	}

	const fix = "show it in howto/wardrobe/manifest.json (and describe it in that folder's README) " +
		"— an author cannot use a field nobody documented"
	for k := range emittedSlot {
		if !docSlot[k] {
			t.Errorf("the importer writes wardrobe slot field %q, the howto example never shows it: %s", k, fix)
		}
	}
	for k := range emittedItem {
		if !docItem[k] {
			t.Errorf("the importer writes wardrobe item field %q, the howto example never shows it: %s", k, fix)
		}
	}
}

// TestHowtoWardrobeExampleIsSelfConsistent checks the doc example the way the
// runtime will: the script only dresses entities that HAVE a wardrobe, every
// slot dresses an axis the layers actually use, and every offered item value is
// a declared axis value (an undeclared one is never prefetched and pops in).
func TestHowtoWardrobeExampleIsSelfConsistent(t *testing.T) {
	sprites := howtoWardrobeCatalog(t)

	for id, raw := range sprites {
		ent, _ := raw.(map[string]any)
		wb, _ := ent["wardrobe"].(map[string]any)
		if len(wb) == 0 {
			continue
		}
		axes, _ := ent["axes"].(map[string]any)
		layers, _ := ent["layers"].([]any)
		var layerText string
		for _, l := range layers {
			if lm, ok := l.(map[string]any); ok {
				u, _ := lm["url"].(string)
				layerText += u + "\n"
			}
		}
		for axis, raw := range wb {
			if !strings.Contains(layerText, "{"+axis+"}") {
				t.Errorf("%s: wardrobe slot %q dresses an axis no layer template uses", id, axis)
			}
			slot, _ := raw.(map[string]any)
			declared := map[string]bool{}
			if vals, ok := axes[axis].([]any); ok {
				for _, v := range vals {
					declared[v.(string)] = true
				}
			}
			items, _ := slot["items"].([]any)
			if len(items) == 0 {
				t.Errorf("%s: wardrobe slot %q has no items", id, axis)
			}
			for _, it := range items {
				v, _ := it.(map[string]any)["value"].(string)
				if v == "" {
					t.Errorf("%s.%s: an item without a value draws nothing", id, axis)
				} else if !declared[v] {
					t.Errorf("%s.%s: item value %q is not in the entity's axes — it is never prefetched", id, axis, v)
				}
			}
		}
	}

	// The script half must only open the sheet for entities that have one.
	src, err := os.ReadFile(filepath.Join(repoRoot(), "howto", "wardrobe", "wardrobe.lvns"))
	if err != nil {
		t.Fatalf("read the example script: %v", err)
	}
	doc, err := lvns.Convert(string(src))
	if err != nil {
		t.Fatalf("howto/wardrobe/wardrobe.lvns no longer compiles: %v", err)
	}
	opened := 0
	for _, cmd := range doc.Script {
		if op, _ := cmd["op"].(string); op != "wardrobe_show" {
			continue
		}
		opened++
		char, _ := cmd["char"].(string)
		ent, ok := sprites[char].(map[string]any)
		if !ok {
			t.Errorf("wardrobe_show char=%q — no such entity in the example manifest", char)
			continue
		}
		if wb, _ := ent["wardrobe"].(map[string]any); len(wb) == 0 {
			t.Errorf("wardrobe_show char=%q — that entity has no wardrobe block, the sheet opens empty", char)
		}
	}
	if opened == 0 {
		t.Errorf("howto/wardrobe/wardrobe.lvns no longer opens a wardrobe — the example stopped being one")
	}
}

// importedBeatFixture is the articy-side chapter the howto/import-articy example
// is generated from: a hand-built dress picker between the Open.Wardrobe
// markers, an audio cue set, a location bg, the protagonist and one NPC.
func importedBeatFixture() (*Result, XlsxData) {
	ops := []map[string]any{
		{"op": "bg", "id": "Двор"},
		{"op": "set", "key": "Music.Calm", "value": true},
		{"op": "actor", "id": "Главный_герой", "show": true, "position": "left"},
		{"op": "say", "who": "Главный герой", "text": "Пора одеваться."},
		{"op": "actor", "id": "Felix", "show": true, "position": "right", "emotion": "idle"},
		{"op": "say", "who": "Felix", "text": "Ты опоздаешь."},
		{"op": "set", "key": "Open.Wardrobe", "value": true},
		{"op": "say", "who": "Главный герой", "text": "Что надеть?"},
		{"op": "choice", "options": []any{
			map[string]any{"text": "Платье", "goto": "n10"},
			map[string]any{"text": "Костюм", "goto": "n11"},
		}},
		{"op": "label", "id": "n10"},
		{"op": "set", "key": "Wardrobe.mainCh_Clothes", "value": 1},
		{"op": "say", "who": "Главный герой", "text": "Платье."},
		{"op": "goto", "label": "wend"},
		{"op": "label", "id": "n11"},
		{"op": "set", "key": "Wardrobe.mainCh_Clothes", "value": 2},
		{"op": "say", "who": "Главный герой", "text": "Костюм."},
		{"op": "goto", "label": "wend"},
		{"op": "label", "id": "wend"},
		{"op": "set", "key": "Open.Wardrobe", "value": false},
		{"op": "set", "key": "Sound.Door", "value": true},
		{"op": "say", "who": "Главный герой", "text": "Готова."},
	}
	data, _ := json.Marshal(map[string]any{"scene": "ch01", "script": ops})
	res := &Result{
		Sprites: map[string]any{
			"demo_main":  map[string]any{"name": "Главная героиня", "kind": "layered"},
			"demo_felix": map[string]any{"name": "Felix", "kind": "layered"},
		},
		Scripts: []ScriptFile{{Rel: "scripts/ch01.lvn", Data: data}},
	}
	xd := XlsxData{
		Chars: []CharMap{
			{StoryName: "Мира", TechName: "Demo_Main", Role: "ГГ"},
			{StoryName: "Felix", TechName: "Demo_Felix", Role: "ВТОР"},
		},
		Locations: map[string]string{"Двор": "Demo_yard"},
		Wardrobe: map[string][]WardrobeItem{
			"Wardrobe.mainCh_Clothes": {
				{Variable: "Wardrobe.mainCh_Clothes", Value: "1", Name: "Платье", TechName: "Demo_Main_clothes_1"},
			},
			"Wardrobe.Felix": {
				{Variable: "Wardrobe.Felix", Value: "1", Name: "Пальто", TechName: "Demo_Felix_clothes_1"},
			},
		},
	}
	xd.Protagonist = &xd.Chars[0]
	return res, xd
}

// TestHowtoImportArticyExampleMatchesImporterOutput pins the "this is what an
// import emits" example to what an import ACTUALLY emits. The file is a
// teaching artifact whose only value is being true; regenerate it with
//
//	LVN_UPDATE_HOWTO=1 go test ./importer/ -run TestHowtoImportArticyExample
//
// (the comment header above the generated body is preserved).
func TestHowtoImportArticyExampleMatchesImporterOutput(t *testing.T) {
	res, xd := importedBeatFixture()
	PostProcessBundle(res, xd, "", nil) // "" = no on-disk фон gate

	var doc articy.Doc
	if err := json.Unmarshal(res.Scripts[0].Data, &doc); err != nil {
		t.Fatalf("re-parse the imported chapter: %v", err)
	}
	want := string(ToLvns(&doc))

	path := filepath.Join(repoRoot(), "howto", "import-articy", "after-import.lvns")
	got, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read %s: %v", path, err)
	}
	if strings.Contains(string(got), want) {
		return
	}
	if os.Getenv("LVN_UPDATE_HOWTO") == "1" {
		header := string(got)
		if i := strings.Index(header, "\nscene "); i >= 0 {
			header = header[:i+1]
		}
		if err := os.WriteFile(path, []byte(header+want), 0o644); err != nil {
			t.Fatalf("rewrite %s: %v", path, err)
		}
		t.Logf("regenerated %s", path)
		return
	}
	t.Errorf("howto/import-articy/after-import.lvns no longer matches what the importer "+
		"emits. The example teaches authors to recognise these lines, so it must be true.\n"+
		"Regenerate with: LVN_UPDATE_HOWTO=1 go test ./importer/ -run TestHowtoImportArticyExample\n"+
		"--- importer output ---\n%s", want)
}

// ВЫБОР БЕЗ ПЕРЕХОДА ПРОВАЛИВАЕТСЯ ДАЛЬШЕ — так делает рантайм, так обязан
// считать и чистильщик недостижимого.
//
// LvnPlayer: `if (target != null) Jump(target); else _ip++;`. Здесь же стоял
// безусловный `return` — провал считался невозможным. Для articy это сходилось
// (тамошний импорт всегда ставит goto: в живой базе 6015 вариантов, без
// перехода НОЛЬ), но цена расхождения высока: этот обход не отчёт составляет,
// а УДАЛЯЕТ команды.
func TestUnreachableSweepFollowsTheRuntimeChoiceRule(t *testing.T) {
	ops := []map[string]any{
		{"op": "say", "text": "вопрос"},
		{"op": "choice", "options": []any{
			map[string]any{"text": "уйти", "goto": "ушли"},
			map[string]any{"text": "остаться"},
		}},
		{"op": "say", "text": "после выбора"},
		{"op": "goto", "label": "конец"},
		{"op": "label", "id": "ушли"},
		{"op": "say", "text": "ушли"},
		{"op": "label", "id": "конец"},
	}
	kept := false
	for _, o := range removeUnreachableOps(ops) {
		if o["op"] == "say" && o["text"] == "после выбора" {
			kept = true
		}
	}
	if !kept {
		t.Fatal("команда после выбора достижима провалом — вырезать её нельзя")
	}
}

// А когда КАЖДЫЙ вариант уходит переходом, провала нет и хвост вырезается.
func TestUnreachableSweepStillDropsTheTailWhenEveryOptionJumps(t *testing.T) {
	ops := []map[string]any{
		{"op": "choice", "options": []any{
			map[string]any{"text": "туда", "goto": "a"},
			map[string]any{"text": "сюда", "goto": "b"},
		}},
		{"op": "say", "text": "недостижимо"},
		{"op": "label", "id": "a"},
		{"op": "say", "text": "a"},
		{"op": "goto", "label": "конец"},
		{"op": "label", "id": "b"},
		{"op": "say", "text": "b"},
		{"op": "label", "id": "конец"},
	}
	for _, o := range removeUnreachableOps(ops) {
		if o["op"] == "say" && o["text"] == "недостижимо" {
			t.Fatal("хвост за выбором, где все варианты уходят, должен быть вырезан")
		}
	}
}

// ТОЧКА ВХОДА ПО КЛИКУ — ТОЖЕ ПУТЬ.
//
// Обход валидатора знал, что `obj`/`actor`/`ui` уводят кликом, перетаскиванием
// и кнопкой в дереве `ui`; чистильщик импортёра не знал про это вовсе — и
// вырезал бы ветку, в которую игрок попадает нажатием. Теперь оба спрашивают у
// одного дома (`lvn.HotspotTargets`).
func TestUnreachableSweepKeepsClickBranches(t *testing.T) {
	ops := []map[string]any{
		{"op": "obj", "id": "дверь", "on_click": "открыли"},
		{"op": "goto", "label": "конец"},
		{"op": "label", "id": "открыли"},
		{"op": "say", "text": "дверь открылась"},
		{"op": "label", "id": "конец"},
	}
	kept := false
	for _, o := range removeUnreachableOps(ops) {
		if o["op"] == "say" && o["text"] == "дверь открылась" {
			kept = true
		}
	}
	if !kept {
		t.Fatal("ветка, в которую ведёт клик по объекту, не должна вырезаться")
	}
}
