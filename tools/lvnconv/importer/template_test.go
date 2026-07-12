package importer

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
)

// DefaultTemplate is fully compiled and carries the original "cold" conventions.
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

// ResolveTemplate: built-in names, a <name>.json in the dir, an explicit path, and
// the unknown-name error.
func TestResolveTemplate(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "myproj.json"),
		[]byte(`{"name":"myproj","staging":{"npc_side":"center"}}`), 0o644); err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{"", "default", "cold"} {
		tpl, err := ResolveTemplate(name, dir)
		if err != nil || tpl.Name != "cold" {
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

// cold.json in server content mirrors the built-in DefaultTemplate exactly, so the
// shipped reference file and the code can't drift apart silently.
func TestColdJSONMatchesDefault(t *testing.T) {
	path := filepath.FromSlash("../../../server/content/import-templates/cold.json")
	if _, err := os.Stat(path); err != nil {
		t.Skipf("cold.json not present: %v", err)
	}
	loaded, err := LoadTemplate(path)
	if err != nil {
		t.Fatal(err)
	}
	def := DefaultTemplate()
	// Compare the serializable projection (derived fields are unexported).
	a, _ := json.Marshal(loaded)
	b, _ := json.Marshal(def)
	if string(a) != string(b) {
		t.Fatalf("cold.json drifted from DefaultTemplate:\n cold.json: %s\n default:   %s", a, b)
	}
}
