package main

import (
	"image"
	"image/color"
	"image/png"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func writeTestPNG(t *testing.T, path string, w, h int) {
	t.Helper()
	img := image.NewNRGBA(image.Rect(0, 0, w, h))
	for y := 0; y < h; y++ {
		for x := 0; x < w; x++ {
			img.Set(x, y, color.NRGBA{R: uint8(x), G: uint8(y), B: 128, A: 255})
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

func decodePNGSize(t *testing.T, data []byte) (int, int) {
	t.Helper()
	cfg, err := png.DecodeConfig(strings.NewReader(string(data)))
	if err != nil {
		t.Fatalf("decode served png: %v", err)
	}
	return cfg.Width, cfg.Height
}

func TestVariantSource(t *testing.T) {
	cases := []struct{ in, want string }{
		{"/c/Hero@2k.png", "/c/Hero.png"},
		{"/c/back@2k.jpg", "/c/back.jpg"},
		{"/c/Hero.png", ""},                 // not a variant
		{"/c/Hero@2k.txt", ""},              // not an image
		{"/c/Hero@2k", ""},                  // no extension
		{"/c/2k@2k@2k.png", "/c/2k@2k.png"}, // only the last suffix strips
	}
	for _, c := range cases {
		if got := variantSource(c.in); got != c.want {
			t.Errorf("variantSource(%q) = %q, want %q", c.in, got, c.want)
		}
	}
}

func newDownscaleTestServer(t *testing.T) (*server, http.Handler, string) {
	t.Helper()
	dir := t.TempDir()
	s := &server{content: dir}
	h := s.withDownscale(s.contentHandler(dir))
	return s, h, dir
}

func get(t *testing.T, h http.Handler, path string) *httptest.ResponseRecorder {
	t.Helper()
	req := httptest.NewRequest(http.MethodGet, path, nil)
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, req)
	return rec
}

func TestWithDownscale_GeneratesAndCaches(t *testing.T) {
	_, h, dir := newDownscaleTestServer(t)
	// 3000×2400 → longest side capped to 2048 → 2048×1638 (aspect preserved).
	writeTestPNG(t, filepath.Join(dir, "big.png"), 3000, 2400)

	rec := get(t, h, "/content/big@2k.png")
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rec.Code)
	}
	w, hh := decodePNGSize(t, rec.Body.Bytes())
	if w != 2048 || hh != 1638 {
		t.Fatalf("served %dx%d, want 2048x1638", w, hh)
	}
	// The variant must now exist on disk (cache-to-disk-forever).
	if !fileExists(filepath.Join(dir, "big@2k.png")) {
		t.Fatal("variant file was not cached to disk")
	}
	// Second request serves the cached file.
	rec2 := get(t, h, "/content/big@2k.png")
	if rec2.Code != http.StatusOK {
		t.Fatalf("cached status = %d, want 200", rec2.Code)
	}
}

func TestWithDownscale_SmallSourceServedAsIs(t *testing.T) {
	_, h, dir := newDownscaleTestServer(t)
	writeTestPNG(t, filepath.Join(dir, "small.png"), 640, 480)

	rec := get(t, h, "/content/small@2k.png")
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rec.Code)
	}
	w, hh := decodePNGSize(t, rec.Body.Bytes())
	if w != 640 || hh != 480 {
		t.Fatalf("served %dx%d, want the untouched 640x480 source", w, hh)
	}
	// No variant file must be written for a source that already fits.
	if fileExists(filepath.Join(dir, "small@2k.png")) {
		t.Fatal("a variant file was written for an already-small source")
	}
}

// A transparent-background source with a hard-edged opaque shape must not
// grow white specks: fully transparent output pixels keep zero RGB (straight-
// alpha white bleed), and the resample runs in premultiplied space with a
// ringing-free kernel (Catmull-Rom overshoot at alpha edges un-premultiplies
// into white dots — the live-observed "tiny white holes" regression).
func TestWithDownscale_AlphaEdgesStayClean(t *testing.T) {
	_, h, dir := newDownscaleTestServer(t)
	img := image.NewNRGBA(image.Rect(0, 0, 3000, 3000))
	// transparent-WHITE background (the worst case: white RGB under alpha 0)
	for y := 0; y < 3000; y++ {
		for x := 0; x < 3000; x++ {
			img.Set(x, y, color.NRGBA{R: 255, G: 255, B: 255, A: 0})
		}
	}
	// hard-edged opaque black square in the middle
	for y := 1000; y < 2000; y++ {
		for x := 1000; x < 2000; x++ {
			img.Set(x, y, color.NRGBA{A: 255})
		}
	}
	f, err := os.Create(filepath.Join(dir, "alpha.png"))
	if err != nil {
		t.Fatal(err)
	}
	if err := png.Encode(f, img); err != nil {
		t.Fatal(err)
	}
	f.Close()

	rec := get(t, h, "/content/alpha@2k.png")
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rec.Code)
	}
	decoded, err := png.Decode(strings.NewReader(rec.Body.String()))
	if err != nil {
		t.Fatal(err)
	}
	out, ok := decoded.(*image.NRGBA)
	if !ok {
		t.Fatalf("decoded as %T, want *image.NRGBA (alpha must survive)", decoded)
	}
	// Raw straight-alpha bytes: fully transparent pixels must carry zero RGB —
	// the premultiplied pipeline guarantees it structurally; the source's
	// white-under-transparent must never leak through. (Ringing specks can't
	// be asserted mechanically — that part is verified by eye on real pages.)
	for i := 0; i < len(out.Pix); i += 4 {
		if out.Pix[i+3] == 0 && (out.Pix[i]|out.Pix[i+1]|out.Pix[i+2]) != 0 {
			t.Fatalf("transparent pixel %d carries RGB %d,%d,%d — white-bleed regression",
				i/4, out.Pix[i], out.Pix[i+1], out.Pix[i+2])
		}
	}
}

func TestWithDownscale_NoSourceIs404(t *testing.T) {
	_, h, _ := newDownscaleTestServer(t)
	if rec := get(t, h, "/content/ghost@2k.png"); rec.Code != http.StatusNotFound {
		t.Fatalf("status = %d, want 404", rec.Code)
	}
}

func TestWithDownscale_NonVariantPassesThrough(t *testing.T) {
	_, h, dir := newDownscaleTestServer(t)
	writeTestPNG(t, filepath.Join(dir, "plain.png"), 8, 8)
	if rec := get(t, h, "/content/plain.png"); rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200 for the plain static file", rec.Code)
	}
}

func TestWithDownscale_TraversalIs404(t *testing.T) {
	_, h, _ := newDownscaleTestServer(t)
	req := httptest.NewRequest(http.MethodGet, "/content/x/", nil)
	req.URL.Path = "/content/../secret@2k.png" // bypass NewRequest's own cleaning
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, req)
	if rec.Code != http.StatusNotFound {
		t.Fatalf("status = %d, want 404 for traversal", rec.Code)
	}
}
