package importer

import (
	"os"
	"path/filepath"
	"testing"
)

// writePNG drops a tiny non-empty file standing in for a PNG.
func writePNG(t *testing.T, path string, size int) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, make([]byte, size), 0o644); err != nil {
		t.Fatal(err)
	}
}

func TestMapBackgroundsSynthetic(t *testing.T) {
	base := t.TempDir()
	bg := filepath.Join(base, "bg")
	writePNG(t, filepath.Join(bg, "NEW", "Cold_camp.png"), 10)
	writePNG(t, filepath.Join(bg, "NEW", "Road_Corpse.png"), 11) // no Cold_ prefix, still a bg
	content := t.TempDir()
	m, err := MapBackgrounds(bg, content)
	if err != nil {
		t.Fatal(err)
	}
	if m["Cold_camp"] != "/content/bg/Cold_camp.png" {
		t.Errorf("Cold_camp url = %q", m["Cold_camp"])
	}
	if _, ok := m["Road_Corpse"]; !ok {
		t.Errorf("Road_Corpse background dropped")
	}
	if _, err := os.Stat(filepath.Join(content, "bg", "Cold_camp.png")); err != nil {
		t.Errorf("Cold_camp not copied: %v", err)
	}
}

// fakeChars builds a characters/NEW/ tree + the matching sheet roster (XlsxData).
// The sheet names the EXACT emotion art stems (with case drift) and the wardrobe
// tech names — MapCharacters resolves them to the on-disk files and copies to
// canonical destinations.
func fakeChars(t *testing.T) (charsDir string, xd XlsxData) {
	t.Helper()
	charsDir = filepath.Join(t.TempDir(), "chars")
	// Adele: a base body + two emotions the sheet names with case drift.
	writePNG(t, filepath.Join(charsDir, "NEW", "Cold_Adele", "Cold_Adele_idle.png"), 20)
	writePNG(t, filepath.Join(charsDir, "NEW", "Cold_Adele", "Cold_Adele_Happy.png"), 21)
	writePNG(t, filepath.Join(charsDir, "NEW", "Cold_Adele", "Cold_Adele_Body.png"), 22)
	// Heroine Cold_Main: body + emotion + hair + clothes wardrobe art.
	writePNG(t, filepath.Join(charsDir, "NEW", "Cold_Main", "Cold_Main_Idle.png"), 30)
	writePNG(t, filepath.Join(charsDir, "NEW", "Cold_Main", "Cold_main_body.png"), 31)
	writePNG(t, filepath.Join(charsDir, "NEW", "Cold_Main", "Cold_Main_Hairs_11.png"), 32)
	writePNG(t, filepath.Join(charsDir, "NEW", "Cold_Main", "Cold_main_clothes_11.png"), 33)

	xd = XlsxData{
		Chars: []CharMap{
			{StoryName: "Adele", TechName: "Cold_Adele", Role: "ЛИ",
				Emotions: map[string]string{"idle": "Cold_Adele_idle", "happy": "Cold_Adele_Happy"}},
			{StoryName: "Katya", TechName: "Cold_Main", Role: "ГГ",
				Emotions: map[string]string{"idle": "Cold_Main_Idle"}},
		},
		Wardrobe: map[string][]WardrobeItem{
			"Wardrobe.mainCh_Hair":    {{Variable: "Wardrobe.mainCh_Hair", Value: "11", Name: "Офисная причёска", TechName: "Cold_Main_Hairs_11"}},
			"Wardrobe.mainCh_Clothes": {{Variable: "Wardrobe.mainCh_Clothes", Value: "11", Name: "Офисная одежда", TechName: "Cold_main_clothes_11"}},
		},
	}
	xd.Protagonist = &xd.Chars[1]
	return
}

func TestMapCharactersSheetDriven(t *testing.T) {
	charsDir, xd := fakeChars(t)
	content := t.TempDir()
	m, _, err := MapCharacters(charsDir, content, xd, nil)
	if err != nil {
		t.Fatal(err)
	}

	// Adele: body + face layers; emotion axis = the sheet's resolved values, no body.
	adele, ok := m["cold_adele"]
	if !ok {
		t.Fatalf("cold_adele missing; got %v", keysOf(m))
	}
	if adele["kind"] != "layered" {
		t.Errorf("adele kind = %v", adele["kind"])
	}
	byID := layersByID(adele)
	if byID["body"] != "/content/art/Cold_Adele_body.png" {
		t.Errorf("adele body layer = %q", byID["body"])
	}
	if byID["face"] != "/content/art/Cold_Adele_{emotion}.png" {
		t.Errorf("adele face layer = %q", byID["face"])
	}
	em := adele["axes"].(map[string]any)["emotion"].([]any)
	if !containsAny(em, "idle") || !containsAny(em, "happy") || containsAny(em, "body") {
		t.Errorf("adele emotions = %v, want idle+happy (no body)", em)
	}
	// Art copied to canonical dest (case drift absorbed: Cold_Adele_Happy → _happy).
	if _, err := os.Stat(filepath.Join(content, "art", "Cold_Adele_happy.png")); err != nil {
		t.Errorf("canonical Cold_Adele_happy.png not copied: %v", err)
	}

	// Heroine (keyed by tech "cold_main"): body + clothes + face + hair, hair/outfit axes.
	kat, ok := m["cold_main"]
	if !ok {
		t.Fatalf("cold_main (heroine) missing; got %v", keysOf(m))
	}
	kb := layersByID(kat)
	for _, id := range []string{"body", "clothes", "face", "hair"} {
		if kb[id] == "" {
			t.Errorf("heroine missing %s layer (layers=%v)", id, kb)
		}
	}
	kaxes := kat["axes"].(map[string]any)
	if !containsAny(kaxes["hair"].([]any), "11") {
		t.Errorf("heroine hair axis = %v, want 11", kaxes["hair"])
	}
	if !containsAny(kaxes["outfit"].([]any), "11") {
		t.Errorf("heroine outfit axis = %v, want 11", kaxes["outfit"])
	}
	if _, err := os.Stat(filepath.Join(content, "art", "Cold_Main_hair_11.png")); err != nil {
		t.Errorf("canonical heroine hair art not copied: %v", err)
	}
}

// TestMappersRealData opportunistically runs the sheet-driven mappers against the
// extracted cold-work archives + real spreadsheet; skips when the data is absent.
func TestMappersRealData(t *testing.T) {
	root := os.Getenv("LVN_COLDWORK")
	if root == "" {
		root = "/private/tmp/claude-501/-Users-fomean-ominis-unity-lvn-vn-engine/737487fc-56e4-4536-9525-4d9764956166/scratchpad/cold-work"
	}
	xlsx := os.Getenv("LVN_COLDXLSX")
	if xlsx == "" {
		xlsx = "/Users/fomean/ominis/unity-lvn-vn-engine/content/cold/Переменные ХВП.xlsx"
	}
	if _, err := os.Stat(filepath.Join(root, "x-bg.done")); err != nil {
		t.Skip("cold-work data absent; skipping real-data mapper test")
	}
	if _, err := os.Stat(xlsx); err != nil {
		t.Skip("cold xlsx absent; skipping")
	}
	xd, err := ParseVarsXlsx(xlsx)
	if err != nil {
		t.Fatalf("ParseVarsXlsx: %v", err)
	}
	content := t.TempDir()

	bg, err := MapBackgrounds(filepath.Join(root, "фоны"), content)
	if err != nil || len(bg) == 0 {
		t.Fatalf("MapBackgrounds: %d entries, err=%v", len(bg), err)
	}
	chars, warns, err := MapCharacters(filepath.Join(root, "персонажи"), content, xd, nil)
	if err != nil || len(chars) == 0 {
		t.Fatalf("MapCharacters: %d entities, err=%v", len(chars), err)
	}
	// The heroine is built from her character folder (keyed by tech name).
	if _, ok := chars["cold_main"]; !ok {
		t.Errorf("heroine cold_main missing from character catalog")
	}
	for _, sub := range []string{"bg", "art"} {
		des, err := os.ReadDir(filepath.Join(content, sub))
		if err != nil || len(des) == 0 {
			t.Errorf("no files copied into %s (err=%v)", sub, err)
		}
	}
	t.Logf("real data: %d backgrounds, %d characters, %d warnings", len(bg), len(chars), len(warns))
}

func layersByID(ent map[string]any) map[string]string {
	out := map[string]string{}
	for _, l := range ent["layers"].([]any) {
		if lm, ok := l.(map[string]any); ok {
			if id, _ := lm["id"].(string); id != "" {
				out[id], _ = lm["url"].(string)
			}
		}
	}
	return out
}

func keysOf(m map[string]map[string]any) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	return out
}

func containsAny(a []any, s string) bool {
	for _, v := range a {
		if v == s {
			return true
		}
	}
	return false
}
