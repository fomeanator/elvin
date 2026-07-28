package importer

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
)

// DefaultTemplate is fully compiled and carries the built-in default conventions.
func TestDefaultTemplateCompiled(t *testing.T) {
	tpl := DefaultTemplate()
	if tpl.sceneMarker == nil {
		t.Fatal("scene marker regex not compiled")
	}
	if !tpl.isNarrator("Автор") || !tpl.isProtagonist("ГГ") || !tpl.isProtagSpeaker("Игрок") {
		t.Fatal("role sets not built from defaults")
	}
	if loc, ok := tpl.sceneMarkerMatch("Сцена 3. Двор."); !ok || loc != "Двор" {
		t.Fatalf("scene marker match = %q,%v", loc, ok)
	}
	// nil receiver resolves to the default.
	if (*Template)(nil).resolve().protagonistStageID() != "Главный_герой" {
		t.Fatal("nil template did not resolve to default")
	}
}

// LoadTemplate is overlay-by-presence: a partial file inherits every field it
// doesn't state from the built-in default.
func TestLoadTemplateOverlay(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "en.json")
	partial := `{
      "name": "en",
      "staging": {
        "narrator_roles": ["Narrator"],
        "protagonist_roles": ["MC"],
        "scene_marker_regex": "^Scene \\d+\\. (.+)$",
        "protagonist_label": "MC"
      }
    }`
	if err := os.WriteFile(path, []byte(partial), 0o644); err != nil {
		t.Fatal(err)
	}
	tpl, err := LoadTemplate(path)
	if err != nil {
		t.Fatal(err)
	}
	// Overridden fields.
	if !tpl.isNarrator("Narrator") || tpl.isNarrator("Автор") {
		t.Fatal("narrator_roles were not replaced")
	}
	if loc, ok := tpl.sceneMarkerMatch("Scene 2. Park"); !ok || loc != "Park" {
		t.Fatalf("scene marker not recompiled: %q,%v", loc, ok)
	}
	// Inherited-from-default fields (not stated in the partial file).
	if tpl.Wardrobe.FlagKey != "Open.Wardrobe" {
		t.Fatalf("wardrobe.flag_key not inherited: %q", tpl.Wardrobe.FlagKey)
	}
	if len(tpl.Audio) != 2 || tpl.Audio[0].VarPrefix != "Music." {
		t.Fatalf("audio cues not inherited: %+v", tpl.Audio)
	}
	if tpl.Staging.PlayerTemplate != "{player}" {
		t.Fatalf("player_template not inherited: %q", tpl.Staging.PlayerTemplate)
	}
}

// ParseTemplateJSON is LoadTemplate's byte-oriented twin — same overlay-onto-
// DefaultTemplate()+compile behaviour, for callers that hold the body in
// memory (the Template CRUD validator, a detect-roles draft preview) instead
// of a file path.
func TestParseTemplateJSONOverlaysAndCompiles(t *testing.T) {
	tpl, err := ParseTemplateJSON([]byte(`{"speaker_aliases":{"Главный герой":"ГГ"}}`))
	if err != nil {
		t.Fatal(err)
	}
	if tpl.SpeakerAliases["Главный герой"] != "ГГ" {
		t.Fatalf("speaker_aliases not parsed: %v", tpl.SpeakerAliases)
	}
	// Inherited-from-default field, same as LoadTemplate's overlay contract.
	if tpl.Wardrobe.FlagKey != "Open.Wardrobe" {
		t.Fatalf("wardrobe.flag_key not inherited: %q", tpl.Wardrobe.FlagKey)
	}
	if tpl.sceneMarker == nil {
		t.Fatal("template not compiled — sceneMarker regex is nil")
	}
}

func TestParseTemplateJSONRejectsBadRegex(t *testing.T) {
	if _, err := ParseTemplateJSON([]byte(`{"staging":{"scene_marker_regex":"(unclosed"}}`)); err == nil {
		t.Fatal("expected an error for an invalid scene_marker_regex")
	}
}

// ResolveTemplate: built-in names, a <name>.json in the dir, an explicit path, and
// the unknown-name error.
func TestResolveTemplate(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "myproj.json"),
		[]byte(`{"name":"myproj","staging":{"npc_side":"center"}}`), 0o644); err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{"", "default"} {
		tpl, err := ResolveTemplate(name, dir)
		if err != nil || tpl.Name != "default" {
			t.Fatalf("ResolveTemplate(%q) = %v, %v", name, tpl, err)
		}
	}
	tpl, err := ResolveTemplate("myproj", dir)
	if err != nil || tpl.Staging.NpcSide != "center" {
		t.Fatalf("named resolve failed: %v, %v", tpl, err)
	}
	if _, err := ResolveTemplate("nope", dir); err == nil {
		t.Fatal("unknown template should error")
	}
}

// A custom template redirects AutoStage: its role names and staging sides drive
// the stage instead of the built-in ones.
func TestAutoStageWithCustomTemplate(t *testing.T) {
	tpl := DefaultTemplate()
	tpl.Staging.NarratorRoles = []string{"Narrator"}
	tpl.Staging.ProtagonistRoles = []string{"MC"}
	tpl.Staging.ProtagonistSide = "center"
	tpl.Staging.SceneMarkerRegex = `^Scene \d+\. (.+)$`
	if err := tpl.compile(); err != nil {
		t.Fatal(err)
	}
	doc := &articy.Doc{Script: []articy.Cmd{
		{"op": "say", "text": "Scene 1. Room"},
		{"op": "say", "who": "MC", "text": "hi"},
		{"op": "say", "who": "Narrator", "text": "..."}, // clears the stage
	}}
	cast := map[string]string{"MC": "mc.png", "Narrator": "n.png"}
	AutoStage(doc, cast, tpl)

	var bg, mcActor articy.Cmd
	for _, c := range doc.Script {
		switch c["op"] {
		case "bg":
			bg = c
		case "actor":
			if c["id"] == "MC" && c["show"] == true {
				mcActor = c
			}
		}
	}
	if bg == nil || bg["id"] != "Room" {
		t.Fatalf("custom scene marker not honoured: %v", bg)
	}
	if mcActor == nil || mcActor["position"] != "center" {
		t.Fatalf("protagonist not staged center: %v", mcActor)
	}
}

// The custom template drives the bundle audio wiring: a different cue prefix maps
// to a different channel/url layout.
func TestAudioCueFromTemplate(t *testing.T) {
	tpl := DefaultTemplate()
	tpl.Audio = []AudioCueTemplate{
		{VarPrefix: "Bgm.", Channel: "music", PathPrefix: "/x/", Ext: ".mp3"},
	}
	op := map[string]any{"op": "set", "key": "Bgm.theme", "value": true}
	got := audioOpForSet(op, tpl)
	if got == nil || got["url"] != "/x/theme.mp3" || got["channel"] != "music" {
		t.Fatalf("audio op = %v", got)
	}
	// The old built-in prefix no longer matches this template.
	if audioOpForSet(map[string]any{"op": "set", "key": "Music.x", "value": true}, tpl) != nil {
		t.Fatal("stale prefix should not match custom template")
	}
}

// default.json in server content mirrors the built-in DefaultTemplate exactly, so the
// shipped reference file and the code can't drift apart silently.
// The contract INVERTED (ResolveTemplate: the author's file always wins, the
// code built-in is only the no-file fallback): default.json is now authoritative
// and legitimately diverges from DefaultTemplate (emotion legend, speaker
// names). Assert the new contract instead of the old byte-for-byte sync.
func TestDefaultJSONFileWins(t *testing.T) {
	dir := filepath.FromSlash("../../../server/content/import-templates")
	if _, err := os.Stat(filepath.Join(dir, "default.json")); err != nil {
		t.Skipf("default.json not present: %v", err)
	}
	tpl, err := ResolveTemplate("default", dir)
	if err != nil {
		t.Fatal(err)
	}
	r := tpl.resolve()
	// A field only the FILE carries proves the file was actually loaded.
	if len(r.SpeakerNames) == 0 {
		t.Fatal("ResolveTemplate(\"default\") returned the built-in — the file must win when present")
	}
	// Core conventions must still resolve (overlay-by-presence keeps defaults).
	if r.Wardrobe.FlagKey == "" || r.Staging.ProtagonistLabel == "" {
		t.Fatalf("file template lost built-in defaults: %+v", r)
	}
	// And with NO directory the built-in fallback still works.
	fb, err := ResolveTemplate("default", t.TempDir())
	if err != nil || fb == nil {
		t.Fatalf("built-in fallback broken: %v", err)
	}
}

// An illustration trigger becomes real art on screen: the truthy write shows the
// CG (which is also what unlocks it in the gallery), the falsy one hands the
// scene back its own background — not a blank screen, and never another CG.
func TestCutsceneTriggersBecomeBackgrounds(t *testing.T) {
	tpl := &Template{Cutscenes: []CutsceneTemplate{
		{VarPrefix: "Cutscenes.show", PathPrefix: "/cg/", Ext: ".jpg"},
	}}
	ops := []map[string]any{
		{"op": "bg", "id": "apartment", "sprite_url": "/bg/apartment.jpg"},
		{"op": "set", "key": "Cutscenes.showMap", "value": true},
		{"op": "say", "text": "Вот карта."},
		{"op": "set", "key": "Cutscenes.showMap", "value": false},
	}
	out := transformOps(ops, nil, tpl)

	var shown, restored map[string]any
	for i, o := range out {
		if o["op"] != "bg" {
			continue
		}
		if o["sprite_url"] == "/cg/Map.jpg" {
			shown = out[i]
		} else if shown != nil && restored == nil && i > 1 {
			restored = out[i]
		}
	}
	if shown == nil {
		t.Fatalf("cutscene did not become a background:\n%v", out)
	}
	if restored == nil || restored["sprite_url"] != "/bg/apartment.jpg" {
		t.Fatalf("scene background not restored after the cutscene:\n%v", out)
	}
}

// A cutscene must never become the background a later cutscene restores —
// otherwise the second one hands the scene back a picture instead of the room.
func TestCutsceneDoesNotBecomeTheSceneBackground(t *testing.T) {
	tpl := &Template{Cutscenes: []CutsceneTemplate{
		{VarPrefix: "Cutscenes.show", PathPrefix: "/cg/", Ext: ".jpg"},
	}}
	ops := []map[string]any{
		{"op": "bg", "id": "street", "sprite_url": "/bg/street.jpg"},
		{"op": "set", "key": "Cutscenes.showFirst", "value": true},
		{"op": "set", "key": "Cutscenes.showFirst", "value": false},
		{"op": "set", "key": "Cutscenes.showSecond", "value": true},
		{"op": "set", "key": "Cutscenes.showSecond", "value": false},
	}
	out := transformOps(ops, nil, tpl)
	last := out[len(out)-1]
	if last["op"] != "bg" || last["sprite_url"] != "/bg/street.jpg" {
		t.Fatalf("second cutscene restored the wrong background: %v", last)
	}
}

// Without a background to return to, ending a cutscene leaves the picture up
// rather than blanking the screen.
func TestCutsceneWithoutSceneBackgroundKeepsThePicture(t *testing.T) {
	tpl := &Template{Cutscenes: []CutsceneTemplate{
		{VarPrefix: "Cutscenes.show", PathPrefix: "/cg/", Ext: ".jpg"},
	}}
	out := transformOps([]map[string]any{
		{"op": "set", "key": "Cutscenes.showLonely", "value": true},
		{"op": "set", "key": "Cutscenes.showLonely", "value": false},
	}, nil, tpl)
	for _, o := range out {
		if o["op"] == "bg" && o["sprite_url"] != "/cg/Lonely.jpg" {
			t.Fatalf("unexpected background restore: %v", o)
		}
	}
}

// The "[timer]" direction arms the countdown on the choice it introduces — and
// never reaches the player as text.
func TestTimerTagArmsTheChoiceAndLeavesTheLineClean(t *testing.T) {
	tpl := &Template{Timer: TimerTemplate{Seconds: 8, Branch: "first"}}
	ops := transformOps([]map[string]any{
		{"op": "say", "text": "[timer] Что делать?!"},
		{"op": "choice", "options": []any{
			map[string]any{"text": "Бежать", "goto": "run"},
			map[string]any{"text": "Молчать", "goto": "quiet"},
		}},
	}, nil, tpl)

	if got, _ := ops[0]["text"].(string); got != "Что делать?!" {
		t.Fatalf("tag left in the line: %q", got)
	}
	if ops[1]["timeout"] != 8.0 || ops[1]["timeout_goto"] != "run" {
		t.Fatalf("choice not armed: %v", ops[1])
	}
}

// branch=last sends an expired countdown to the bottom option (the passive one).
func TestTimerBranchLast(t *testing.T) {
	tpl := &Template{Timer: TimerTemplate{Seconds: 5, Branch: "last"}}
	ops := transformOps([]map[string]any{
		{"op": "say", "text": "[timer]Я приземлилась…"},
		{"op": "choice", "options": []any{
			map[string]any{"text": "Встать", "goto": "up"},
			map[string]any{"text": "Лежать", "goto": "down"},
		}},
	}, nil, tpl)
	if ops[1]["timeout_goto"] != "down" {
		t.Fatalf("expected the last branch, got %v", ops[1]["timeout_goto"])
	}
}

// A camera direction becomes a camera op: a focus phrase frames the shot, a
// shake code shakes it. An empty direction (the author clearing the field) asks
// for nothing and must not emit anything.
func TestCameraDirections(t *testing.T) {
	tpl := &Template{Camera: CameraTemplate{
		FocusVar: "Temp.focus", ShakeVar: "Effect.shake", ZoomVar: "Effect.zoom", Duration: 0.5,
	}}
	out := transformOps([]map[string]any{
		{"op": "set", "key": "Temp.focus", "value": "зум, слева, 70%"},
		{"op": "set", "key": "Effect.shake", "value": "23"},
		{"op": "set", "key": "Temp.focus", "value": ""},
	}, nil, tpl)

	var zoom, shake map[string]any
	for _, o := range out {
		if o["op"] != "camera" {
			continue
		}
		if o["action"] == "zoom" {
			zoom = o
		} else if o["action"] == "shake" {
			shake = o
		}
	}
	if zoom == nil {
		t.Fatal("focus phrase did not become a camera move")
	}
	if f, _ := zoom["factor"].(float64); f < 1.4 || f > 1.5 {
		t.Fatalf("70%% of the frame should zoom ~1.43x, got %v", zoom["factor"])
	}
	if x, _ := zoom["x"].(float64); x > 0.4 {
		t.Fatalf("«слева» should look left of centre, got x=%v", x)
	}
	if shake == nil {
		t.Fatal("shake code did not become a shake")
	}
	// Exactly two camera ops: the empty direction emitted nothing.
	n := 0
	for _, o := range out {
		if o["op"] == "camera" {
			n++
		}
	}
	if n != 2 {
		t.Fatalf("empty direction should emit nothing; camera ops = %d", n)
	}
}
