package importer

import (
	"os"
	"strings"
	"testing"
)

// realVarsXlsx is the actual game spreadsheet. Tests that need it skip when it
// is absent so the suite stays green in checkouts without the content tree.
const realVarsXlsx = "/Users/fomean/ominis/elvin/content/cold/Переменные ХВП.xlsx"

func TestParseVarsXlsx_Real(t *testing.T) {
	if _, err := os.Stat(realVarsXlsx); err != nil {
		t.Skipf("real spreadsheet not present: %v", err)
	}
	data, err := ParseVarsXlsx(realVarsXlsx)
	if err != nil {
		t.Fatalf("ParseVarsXlsx: %v", err)
	}
	if len(data.Vars) == 0 {
		t.Fatalf("expected non-empty Vars")
	}
	if len(data.EmotionColors) == 0 {
		t.Fatalf("expected non-empty EmotionColors")
	}

	// Every declaration must have a non-empty key.
	for _, v := range data.Vars {
		if strings.TrimSpace(v.Key) == "" {
			t.Errorf("var with empty key: %+v", v)
		}
	}

	// Keys are unique (declarations, not per-option rows).
	seen := map[string]bool{}
	for _, v := range data.Vars {
		if seen[v.Key] {
			t.Errorf("duplicate key emitted: %s", v.Key)
		}
		seen[v.Key] = true
	}

	// Spot-check known wardrobe variables from the real sheet.
	for _, want := range []string{"Wardrobe.mainCh_Hair", "Wardrobe.mainCh_Clothes", "Wardrobe.Matvey"} {
		if !seen[want] {
			t.Errorf("expected variable %q in parsed decls", want)
		}
	}

	// Spot-check the emotion legend (color → label) from the real palette.
	wantColors := map[string]string{
		"#00B050": "Happy",
		"#7030A0": "Sad",
		"#FF0000": "Anger",
		"#FFFF00": "Surprised",
		"#0070C0": "Thoughtfulness",
		"#D6006E": "Flirt",
		"#0C0C0C": "Fear",
	}
	for hex, label := range wantColors {
		got, ok := data.EmotionColors[hex]
		if !ok {
			t.Errorf("emotion color %s (%s) missing from legend", hex, label)
			continue
		}
		if got != label {
			t.Errorf("color %s = %q, want %q", hex, got, label)
		}
	}

	// SetDefaultLines must produce one well-formed line per variable.
	lines := data.SetDefaultLines()
	if len(lines) != len(data.Vars) {
		t.Fatalf("SetDefaultLines count = %d, want %d", len(lines), len(data.Vars))
	}
	for _, l := range lines {
		if !strings.HasPrefix(l, "set default=true key=\"") || !strings.Contains(l, " value=") {
			t.Errorf("malformed decl line: %q", l)
		}
	}

	// ---- rich three-sheet mappings ------------------------------------

	if len(data.Chars) == 0 {
		t.Fatalf("expected non-empty Chars")
	}
	if data.Protagonist == nil {
		t.Fatalf("expected a non-nil Protagonist (Role==ГГ row)")
	}
	if data.Protagonist.TechName != "Cold_Main" {
		t.Errorf("Protagonist.TechName = %q, want %q", data.Protagonist.TechName, "Cold_Main")
	}
	if data.Protagonist.Role != "ГГ" {
		t.Errorf("Protagonist.Role = %q, want %q", data.Protagonist.Role, "ГГ")
	}
	if len(data.Protagonist.Emotions) == 0 {
		t.Errorf("Protagonist has no emotions parsed")
	}
	// Emotion labels are lowercased and map to art stems.
	if got := data.Protagonist.Emotions["idle"]; got != "Cold_Main_Idle" {
		t.Errorf("Protagonist idle stem = %q, want %q", got, "Cold_Main_Idle")
	}

	// Spot-check a known secondary character.
	var matvey *CharMap
	for i := range data.Chars {
		if data.Chars[i].TechName == "Cold_Matvey" {
			matvey = &data.Chars[i]
		}
	}
	if matvey == nil {
		t.Errorf("expected a character with TechName Cold_Matvey")
	} else if matvey.Emotions["happy"] == "" {
		t.Errorf("Cold_Matvey has no happy emotion stem")
	}

	if len(data.Locations) == 0 {
		t.Fatalf("expected non-empty Locations")
	}
	// A known articy→tech background mapping (trimmed).
	if got := data.Locations["2nd_floor"]; got != "Cold_2nd_floor" {
		t.Errorf("Locations[2nd_floor] = %q, want %q", got, "Cold_2nd_floor")
	}

	if len(data.Wardrobe) == 0 {
		t.Fatalf("expected non-empty Wardrobe")
	}
	if items := data.Wardrobe["Wardrobe.mainCh_Hair"]; len(items) == 0 {
		t.Errorf("expected Wardrobe.mainCh_Hair items")
	} else {
		// Values must be clean ("11", not "11.0").
		for _, it := range items {
			if strings.Contains(it.Value, ".") {
				t.Errorf("wardrobe value not normalized: %q", it.Value)
			}
		}
		if items[0].Value != "11" || items[0].TechName != "Cold_Main_Hairs_11" || !items[0].Hide {
			t.Errorf("first hair item = %+v, want Value=11 TechName=Cold_Main_Hairs_11 Hide=true", items[0])
		}
	}

	t.Logf("parsed %d vars, %d emotion colors, %d chars, %d locations, %d wardrobe groups",
		len(data.Vars), len(data.EmotionColors), len(data.Chars), len(data.Locations), len(data.Wardrobe))
}

func TestSplitAssignment(t *testing.T) {
	cases := []struct {
		in, key, def string
	}{
		{"Wardrobe.mainCh_Hair = 11;", "Wardrobe.mainCh_Hair", "11"},
		{"Wardrobe.Matvey = 0", "Wardrobe.Matvey", "0"},
		{"Wardrobe.Edward = 1 ", "Wardrobe.Edward", "1"},
		{"foo=bar", "foo", "bar"},
		{"not an assignment", "", ""},
		{"", "", ""},
		{"= 5", "", ""},
	}
	for _, c := range cases {
		k, d := splitAssignment(c.in)
		if k != c.key || d != c.def {
			t.Errorf("splitAssignment(%q) = (%q,%q), want (%q,%q)", c.in, k, d, c.key, c.def)
		}
	}
}

func TestNormalizeColor(t *testing.T) {
	cases := []struct{ in, out string }{
		{"FF00B050", "#00B050"},
		{"FFFFFF00", "#FFFF00"},
		{"ff0c0c0c", "#0C0C0C"},
		{"00B050", "#00B050"},
		{"#7030A0", "#7030A0"},
	}
	for _, c := range cases {
		if got := normalizeColor(c.in); got != c.out {
			t.Errorf("normalizeColor(%q) = %q, want %q", c.in, got, c.out)
		}
	}
}

func TestFormatValue(t *testing.T) {
	cases := []struct{ in, out string }{
		{"11", "11"},
		{"11.0", "11"},
		{"0", "0"},
		{"1.5", "1.5"},
		{"Office", `"Office"`},
		{"true", "true"},
		{"", `""`},
	}
	for _, c := range cases {
		if got := formatValue(c.in); got != c.out {
			t.Errorf("formatValue(%q) = %q, want %q", c.in, got, c.out)
		}
	}
}

func TestSetDefaultLines(t *testing.T) {
	x := XlsxData{Vars: []VarDecl{
		{Key: "Wardrobe.mainCh_Hair", Default: "11"},
		{Key: "Wardrobe.Matvey", Default: "0"},
		{Key: "Story.name", Default: "Katya"},
	}}
	lines := x.SetDefaultLines()
	want := []string{
		`set default=true key="Wardrobe.mainCh_Hair" value=11`,
		`set default=true key="Wardrobe.Matvey" value=0`,
		`set default=true key="Story.name" value="Katya"`,
	}
	if len(lines) != len(want) {
		t.Fatalf("got %d lines, want %d", len(lines), len(want))
	}
	for i := range want {
		if lines[i] != want[i] {
			t.Errorf("line %d = %q, want %q", i, lines[i], want[i])
		}
	}
}
