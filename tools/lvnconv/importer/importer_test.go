package importer

import (
	"bytes"
	"encoding/json"
	"image"
	"image/color"
	"image/png"
	"os"
	"path/filepath"
	"testing"
)

// makePNG builds a w×h PNG: a solid near-white field with a colored block in the
// middle, so Matte should clear the border-connected white and keep the block.
func makePNG(t *testing.T, w, h int) []byte {
	t.Helper()
	img := image.NewNRGBA(image.Rect(0, 0, w, h))
	for y := 0; y < h; y++ {
		for x := 0; x < w; x++ {
			img.SetNRGBA(x, y, color.NRGBA{250, 250, 250, 255}) // near-white background
		}
	}
	for y := h / 3; y < 2*h/3; y++ {
		for x := w / 3; x < 2*w/3; x++ {
			img.SetNRGBA(x, y, color.NRGBA{180, 40, 30, 255}) // opaque character block
		}
	}
	var buf bytes.Buffer
	if err := png.Encode(&buf, img); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

func TestMatteCutsBorderWhiteKeepsInterior(t *testing.T) {
	out, err := Matte(makePNG(t, 60, 60))
	if err != nil {
		t.Fatal(err)
	}
	img, err := png.Decode(bytes.NewReader(out))
	if err != nil {
		t.Fatal(err)
	}
	at := func(x, y int) color.NRGBA { return color.NRGBAModel.Convert(img.At(x, y)).(color.NRGBA) }

	if a := at(0, 0).A; a != 0 {
		t.Errorf("corner should be transparent, got alpha %d", a)
	}
	if a := at(30, 30).A; a != 255 {
		t.Errorf("character centre should stay opaque, got alpha %d", a)
	}
	if c := at(30, 30); c.R != 180 || c.G != 40 || c.B != 30 {
		t.Errorf("character colour altered: %+v", c)
	}
}

func TestMergeTitleReplaceAndAppendPreservesOtherKeys(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "manifest.json")
	// Seed a manifest with an unrelated top-level field and an existing title.
	seed := map[string]any{
		"ui":     map[string]any{"theme": "dark"},
		"titles": []any{map[string]any{"id": "soviet", "name": "old"}, map[string]any{"id": "other", "name": "keep"}},
	}
	b, _ := json.Marshal(seed)
	os.WriteFile(path, b, 0o644)

	if err := MergeTitleIntoManifest(path, Title{ID: "soviet", Name: "new"}); err != nil {
		t.Fatal(err)
	}
	if err := MergeTitleIntoManifest(path, Title{ID: "fresh", Name: "added"}); err != nil {
		t.Fatal(err)
	}

	var got map[string]any
	raw, _ := os.ReadFile(path)
	json.Unmarshal(raw, &got)

	if got["ui"] == nil {
		t.Error("unrelated 'ui' field was dropped")
	}
	titles, _ := got["titles"].([]any)
	if len(titles) != 3 {
		t.Fatalf("want 3 titles (replace soviet, keep other, append fresh), got %d", len(titles))
	}
	byID := map[string]string{}
	for _, raw := range titles {
		m := raw.(map[string]any)
		byID[m["id"].(string)], _ = m["name"].(string)
	}
	if byID["soviet"] != "new" {
		t.Errorf("soviet should be replaced with new, got %q", byID["soviet"])
	}
	if byID["other"] != "keep" {
		t.Errorf("unrelated title dropped: %q", byID["other"])
	}
	if byID["fresh"] != "added" {
		t.Errorf("new title not appended: %q", byID["fresh"])
	}
}

func TestNormKeyFoldsUnicodeFormAndCase(t *testing.T) {
	// Build both forms from explicit runes so the file's own normalisation can't
	// collapse the decomposed one back to NFC. ̆ is the combining breve that
	// turns и (U+0438) into a decomposed й — the form macOS stores on disk.
	base := []rune("Тимур_Обычны")
	nfc := string(append(append([]rune{}, base...), 'й'))      // precomposed й
	nfd := string(append(append([]rune{}, base...), 'и', '̆')) // и + combining breve
	if normKey(nfc) != normKey(nfd) {
		t.Errorf("NFC/NFD keys differ: %q vs %q", normKey(nfc), normKey(nfd))
	}
	if normKey("ДВОР") != normKey("двор") {
		t.Error("normKey should be case-insensitive")
	}
}

func TestStripHashRemovesArticyTag(t *testing.T) {
	if got := stripHash("Тимур_Обычный(00A9)"); got != "Тимур_Обычный" {
		t.Errorf("stripHash kept the tag: %q", got)
	}
	if got := stripHash("plain_name"); got != "plain_name" {
		t.Errorf("stripHash mangled a tag-less name: %q", got)
	}
}
