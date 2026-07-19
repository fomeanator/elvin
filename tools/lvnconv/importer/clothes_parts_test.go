package importer

import (
	"image"
	"image/color"
	"image/png"
	"os"
	"path/filepath"
	"testing"
)

func writePng(t *testing.T, path string, w, h int, c color.NRGBA) {
	t.Helper()
	img := image.NewNRGBA(image.Rect(0, 0, w, h))
	for y := 0; y < h; y++ {
		for x := 0; x < w; x++ {
			img.SetNRGBA(x, y, c)
		}
	}
	f, err := os.Create(path)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	if err := png.Encode(f, img); err != nil {
		t.Fatal(err)
	}
}

// The demo bug this guards: outfit 13 shipped as 13_1 (base with the HANDS)
// + 13_2 (jacket); losing a part loses body parts painted in it.
func TestCompositeParts_StacksInOrder(t *testing.T) {
	dir := t.TempDir()
	base := filepath.Join(dir, "p1.png")
	top := filepath.Join(dir, "p2.png")
	writePng(t, base, 4, 4, color.NRGBA{R: 255, A: 255}) // opaque red base
	writePng(t, top, 4, 4, color.NRGBA{G: 255, A: 0})    // fully transparent top
	dst := filepath.Join(dir, "out", "combined.png")
	if err := compositeParts([]string{base, top}, dst); err != nil {
		t.Fatal(err)
	}
	f, _ := os.Open(dst)
	defer f.Close()
	img, err := png.Decode(f)
	if err != nil {
		t.Fatal(err)
	}
	r, _, _, a := img.At(2, 2).RGBA()
	if a == 0 || r == 0 {
		t.Fatalf("base part lost under a transparent top: r=%d a=%d", r, a)
	}

	// an opaque top must WIN over the base — order matters
	writePng(t, top, 4, 4, color.NRGBA{G: 255, A: 255})
	if err := compositeParts([]string{base, top}, dst); err != nil {
		t.Fatal(err)
	}
	f2, _ := os.Open(dst)
	defer f2.Close()
	img2, _ := png.Decode(f2)
	_, g, _, _ := img2.At(2, 2).RGBA()
	if g == 0 {
		t.Fatal("top part did not stack over the base")
	}
}

func TestCompositeParts_RejectsMismatchedCanvases(t *testing.T) {
	dir := t.TempDir()
	a := filepath.Join(dir, "a.png")
	b := filepath.Join(dir, "b.png")
	writePng(t, a, 4, 4, color.NRGBA{A: 255})
	writePng(t, b, 8, 4, color.NRGBA{A: 255})
	if err := compositeParts([]string{a, b}, filepath.Join(dir, "out.png")); err == nil {
		t.Fatal("mismatched canvases must error, not silently misdress the character")
	}
}

func TestSplitPartSuffix_OnlyNumericPairs(t *testing.T) {
	if base, idx, ok := splitPartSuffix("13_2"); !ok || base != "13" || idx != 2 {
		t.Fatalf("13_2 → %q %d %v", base, idx, ok)
	}
	for _, n := range []string{"13", "red_1", "_1", "13_x"} {
		if _, _, ok := splitPartSuffix(n); ok {
			t.Fatalf("%q must not split", n)
		}
	}
}

func TestResolveParts_FindsOrderedParts(t *testing.T) {
	dir := t.TempDir()
	writePng(t, filepath.Join(dir, "Cold_main_clothes_13_1.png"), 2, 2, color.NRGBA{A: 255})
	writePng(t, filepath.Join(dir, "Cold_main_clothes_13_2.png"), 2, 2, color.NRGBA{A: 255})
	fi := indexFolder(dir)
	parts := fi.resolveParts("cold_main_clothes_13")
	if len(parts) != 2 {
		t.Fatalf("want 2 parts, got %v", parts)
	}
	if filepath.Base(parts[0]) != "Cold_main_clothes_13_1.png" {
		t.Fatalf("part order wrong: %v", parts)
	}
	if got := fi.resolveParts("cold_main_clothes_11"); got != nil {
		t.Fatalf("phantom parts for a single-file outfit: %v", got)
	}
}
