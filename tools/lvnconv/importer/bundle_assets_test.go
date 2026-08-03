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
	writePNG(t, filepath.Join(bg, "NEW", "Demo_camp.png"), 10)
	writePNG(t, filepath.Join(bg, "NEW", "Road_Corpse.png"), 11) // no Demo_ prefix, still a bg
	content := t.TempDir()
	m, err := MapBackgrounds(bg, content)
	if err != nil {
		t.Fatal(err)
	}
	if m["Demo_camp"] != "/content/bg/Demo_camp.png" {
		t.Errorf("Demo_camp url = %q", m["Demo_camp"])
	}
	if _, ok := m["Road_Corpse"]; !ok {
		t.Errorf("Road_Corpse background dropped")
	}
	if _, err := os.Stat(filepath.Join(content, "bg", "Demo_camp.png")); err != nil {
		t.Errorf("Demo_camp not copied: %v", err)
	}
}

// fakeChars builds a characters/NEW/ tree + the matching sheet roster (XlsxData).
// The sheet names the EXACT emotion art stems (with case drift) and the wardrobe
// tech names — MapCharacters resolves them to the on-disk files and copies to
// canonical destinations.
func fakeChars(t *testing.T) (charsDir string, xd XlsxData) {
	t.Helper()
	charsDir = filepath.Join(t.TempDir(), "chars")
	// Lina: a base body + two emotions the sheet names with case drift.
	writePNG(t, filepath.Join(charsDir, "NEW", "Demo_Lina", "Demo_Lina_idle.png"), 20)
	writePNG(t, filepath.Join(charsDir, "NEW", "Demo_Lina", "Demo_Lina_Happy.png"), 21)
	writePNG(t, filepath.Join(charsDir, "NEW", "Demo_Lina", "Demo_Lina_Body.png"), 22)
	// Heroine Demo_Main: body + emotion + hair + clothes wardrobe art.
	writePNG(t, filepath.Join(charsDir, "NEW", "Demo_Main", "Demo_Main_Idle.png"), 30)
	writePNG(t, filepath.Join(charsDir, "NEW", "Demo_Main", "Demo_main_body.png"), 31)
	writePNG(t, filepath.Join(charsDir, "NEW", "Demo_Main", "Demo_Main_Hairs_11.png"), 32)
	writePNG(t, filepath.Join(charsDir, "NEW", "Demo_Main", "Demo_main_clothes_11.png"), 33)

	xd = XlsxData{
		Chars: []CharMap{
			{StoryName: "Lina", TechName: "Demo_Lina", Role: "ЛИ",
				Emotions: map[string]string{"idle": "Demo_Lina_idle", "happy": "Demo_Lina_Happy"}},
			{StoryName: "Mira", TechName: "Demo_Main", Role: "ГГ",
				Emotions: map[string]string{"idle": "Demo_Main_Idle"}},
		},
		Wardrobe: map[string][]WardrobeItem{
			"Wardrobe.mainCh_Hair":    {{Variable: "Wardrobe.mainCh_Hair", Value: "11", Name: "Офисная причёска", TechName: "Demo_Main_Hairs_11"}},
			"Wardrobe.mainCh_Clothes": {{Variable: "Wardrobe.mainCh_Clothes", Value: "11", Name: "Офисная одежда", TechName: "Demo_main_clothes_11"}},
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

	// Lina: body + face layers; emotion axis = the sheet's resolved values, no body.
	lina, ok := m["demo_lina"]
	if !ok {
		t.Fatalf("demo_lina missing; got %v", keysOf(m))
	}
	if lina["kind"] != "layered" {
		t.Errorf("lina kind = %v", lina["kind"])
	}
	byID := layersByID(lina)
	if byID["body"] != "/content/art/Demo_Lina_body.png" {
		t.Errorf("lina body layer = %q", byID["body"])
	}
	if byID["face"] != "/content/art/Demo_Lina_{emotion}.png" {
		t.Errorf("lina face layer = %q", byID["face"])
	}
	em := lina["axes"].(map[string]any)["emotion"].([]any)
	if !containsAny(em, "idle") || !containsAny(em, "happy") || containsAny(em, "body") {
		t.Errorf("lina emotions = %v, want idle+happy (no body)", em)
	}
	// Art copied to canonical dest (case drift absorbed: Demo_Lina_Happy → _happy).
	if _, err := os.Stat(filepath.Join(content, "art", "Demo_Lina_happy.png")); err != nil {
		t.Errorf("canonical Demo_Lina_happy.png not copied: %v", err)
	}

	// Heroine (keyed by tech "demo_main"): body + clothes + face + hair, hair/outfit axes.
	kat, ok := m["demo_main"]
	if !ok {
		t.Fatalf("demo_main (heroine) missing; got %v", keysOf(m))
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
	if _, err := os.Stat(filepath.Join(content, "art", "Demo_Main_hair_11.png")); err != nil {
		t.Errorf("canonical heroine hair art not copied: %v", err)
	}
}

// TestMappersRealData opportunistically runs the sheet-driven mappers against the
// extracted source archives + real spreadsheet; skips when the data is absent.
func TestMappersRealData(t *testing.T) {
	root := os.Getenv("LVN_COLDWORK")
	if root == "" {
		root = os.Getenv("LVN_IMPORT_FIXTURES")
	}
	xlsx := os.Getenv("LVN_COLDXLSX")
	if xlsx == "" {
		xlsx = os.Getenv("LVN_VARS_XLSX")
	}
	if _, err := os.Stat(filepath.Join(root, "x-bg.done")); err != nil {
		t.Skip("import fixtures absent; skipping real-data mapper test")
	}
	if _, err := os.Stat(xlsx); err != nil {
		t.Skip("таблица переменных не задана; пропускаем")
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
	if _, ok := chars["demo_main"]; !ok {
		t.Errorf("heroine demo_main missing from character catalog")
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
