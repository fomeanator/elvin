package importer

import (
	"testing"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/articy"
)

// A say's marker colour resolves to an emotion (case/alpha tolerant), the probe
// tallies distinct colours with a sample, and unmapped colours are reported with
// no emotion. AutoStage then copies the emotion onto the actor, and BuildCatalog
// records it as the character's emotion axis.
func TestEmotionThreading(t *testing.T) {
	doc := &articy.Doc{Script: []articy.Cmd{
		{"op": "say", "who": "Тимур", "text": "Дом, милый дом.", "color": "#00b050"},
		{"op": "say", "who": "Тимур", "text": "Но почему…", "color": "#7030A0"},      // upper-case
		{"op": "say", "who": "Тимур", "text": "Страшно.", "color": "#ff0c0c0c"},      // leading ff alpha
		{"op": "say", "who": "Люба", "text": "Что это за цвет?", "color": "#123456"}, // unmapped
	}}

	probe := newColorProbe(nil)
	probe.scan(doc)

	// emotions stamped on the says (transient color removed)
	wantEmo := []string{"happy", "sad", "fear", ""}
	for i, c := range doc.Script {
		if _, ok := c["color"]; ok {
			t.Errorf("say %d still carries transient color", i)
		}
		if got, _ := c["emotion"].(string); got != wantEmo[i] {
			t.Errorf("say %d emotion = %q, want %q", i, got, wantEmo[i])
		}
	}

	// probe: 4 distinct colours, the unmapped one carries no emotion but a sample.
	stats := probe.stats()
	if len(stats) != 4 {
		t.Fatalf("want 4 distinct colours, got %d: %+v", len(stats), stats)
	}
	byHex := map[string]ColorStat{}
	for _, s := range stats {
		byHex[s.Hex] = s
	}
	if s := byHex["#00b050"]; s.Emotion != "happy" || s.Count != 1 || s.Sample == "" {
		t.Errorf("#00b050 stat = %+v", s)
	}
	if s := byHex["#123456"]; s.Emotion != "" || s.Count != 1 {
		t.Errorf("unmapped #123456 stat = %+v (want empty emotion)", s)
	}

	// AutoStage copies each emotion onto the actor show cmd (re-showing Тимур as his
	// emotion changes), so BuildCatalog records the full emotion axis.
	cast := map[string]string{"Тимур": "t.png", "Люба": "l.png"}
	AutoStage(doc, cast, nil)
	sprites, _ := BuildCatalog(doc)
	ent, _ := sprites["Тимур"].(map[string]any)
	axes, _ := ent["axes"].(map[string]any)
	em, _ := axes["emotion"].([]any)
	if len(em) != 3 { // joy, sadness, fear
		t.Fatalf("Тимур emotion axis = %v, want 3 values (joy/sadness/fear)", em)
	}
}

// Options.EmotionColors extends and overrides the default legend.
func TestEmotionColorsOverride(t *testing.T) {
	table := emotionTable(map[string]string{
		"#FF0000": "anger",   // extend: a colour not in the legend
		"#00b050": "delight", // override: remap the legend's joy
	})
	if table["ff0000"] != "anger" {
		t.Errorf("extension not applied: %q", table["ff0000"])
	}
	if table["00b050"] != "delight" {
		t.Errorf("override not applied: %q", table["00b050"])
	}
	if table["7030a0"] != "sad" {
		t.Errorf("default legend lost: %q", table["7030a0"])
	}
}
