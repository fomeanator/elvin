package main

// staleness_test.go — ЗАМЕНА ИСХОДНИКА ОБЯЗАНА ДОЕЗЖАТЬ ДО КЛИЕНТА.
//
// «Кэшируем на диск навсегда» у производных файлов (@2k-даунскейлы,
// ktx2-перекодировки) сломалось в тот момент, когда арт стал заменяемым:
// автор кладёт качественную картинку поверх старой, индекс версий двигает
// клиентский ключ кэша, клиент честно перекачивает вариант — а сервер отдаёт
// ему перекодировку файла, которого больше нет. Живой случай: героиня
// оставалась мыльной миниатюрой сквозь три замены арта, потому что её
// @2k.ktx2 был закодирован из превьюшки 25 КБ и продолжал раздаваться.

import (
	"image"
	"image/color"
	"image/png"
	"net/http"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func writeSolidPNG(t *testing.T, path string, w, h int, c color.NRGBA) {
	t.Helper()
	img := image.NewNRGBA(image.Rect(0, 0, w, h))
	for y := 0; y < h; y++ {
		for x := 0; x < w; x++ {
			img.Set(x, y, c)
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

// makeOlder pushes a file's mtime an hour into the past, so anything written
// after it counts as newer without the test sleeping.
func makeOlder(t *testing.T, path string) {
	t.Helper()
	old := time.Now().Add(-time.Hour)
	if err := os.Chtimes(path, old, old); err != nil {
		t.Fatal(err)
	}
}

func TestWithDownscale_RegeneratesWhenTheSourceIsReplaced(t *testing.T) {
	_, h, dir := newDownscaleTestServer(t)
	src := filepath.Join(dir, "big.png")
	writeSolidPNG(t, src, 3000, 2400, color.NRGBA{R: 255, A: 255})

	first := get(t, h, "/content/big@2k.png")
	if first.Code != http.StatusOK {
		t.Fatalf("first variant request: %d", first.Code)
	}
	makeOlder(t, filepath.Join(dir, "big@2k.png"))

	// The author drops a different image over the same name.
	writeSolidPNG(t, src, 3000, 2400, color.NRGBA{G: 255, A: 255})

	second := get(t, h, "/content/big@2k.png")
	if second.Code != http.StatusOK {
		t.Fatalf("variant request after replace: %d", second.Code)
	}
	if string(first.Body.Bytes()) == string(second.Body.Bytes()) {
		t.Fatal("replaced source still serves the OLD downscale — the stale-variant hole")
	}
}

func TestWithDownscale_StaleVariantOfNowSmallSourceIsDropped(t *testing.T) {
	_, h, dir := newDownscaleTestServer(t)
	src := filepath.Join(dir, "art.png")
	writeSolidPNG(t, src, 3000, 2400, color.NRGBA{R: 255, A: 255})
	if rec := get(t, h, "/content/art@2k.png"); rec.Code != http.StatusOK {
		t.Fatalf("prime the variant: %d", rec.Code)
	}
	variant := filepath.Join(dir, "art@2k.png")
	makeOlder(t, variant)

	// The replacement already fits the 2K box — the source IS the variant now.
	writeSolidPNG(t, src, 800, 600, color.NRGBA{B: 255, A: 255})

	rec := get(t, h, "/content/art@2k.png")
	if rec.Code != http.StatusOK {
		t.Fatalf("after replace: %d", rec.Code)
	}
	if w, hh := decodePNGSize(t, rec.Body.Bytes()); w != 800 || hh != 600 {
		t.Fatalf("served %dx%d, want the fresh 800x600 source", w, hh)
	}
	if fileExists(variant) {
		t.Fatal("the stale @2k file must be removed, or it shadows the small source forever")
	}
}

func TestWithKTX2_AStaleEncodeIs404edAndRemoved(t *testing.T) {
	// No basisu for this test: the 404 path must not depend on a re-encode.
	t.Setenv("PATH", "")
	dir := t.TempDir()
	s := &server{content: dir}
	h := s.withKTX2(newDownscaler(), s.contentHandler(dir))

	ktx2 := filepath.Join(dir, "hero@2k.ktx2")
	if err := os.WriteFile(ktx2, []byte("encode of the OLD art"), 0o644); err != nil {
		t.Fatal(err)
	}
	makeOlder(t, ktx2)
	// The original behind the variant chain is replaced AFTER the encode.
	writeSolidPNG(t, filepath.Join(dir, "hero.png"), 100, 100, color.NRGBA{R: 255, A: 255})

	rec := get(t, h, "/content/hero@2k.ktx2")
	if rec.Code != http.StatusNotFound {
		t.Fatalf("stale ktx2 answered %d — it must 404 so the client falls back to the fresh PNG", rec.Code)
	}
	if fileExists(ktx2) {
		t.Fatal("stale ktx2 left on disk — the next request would serve yesterday's art again")
	}

	// And a FRESH encode keeps serving statically.
	fresh := filepath.Join(dir, "fresh@2k.ktx2")
	writeSolidPNG(t, filepath.Join(dir, "fresh.png"), 100, 100, color.NRGBA{G: 255, A: 255})
	if err := os.WriteFile(fresh, []byte("encode of the CURRENT art"), 0o644); err != nil {
		t.Fatal(err)
	}
	if rec := get(t, h, "/content/fresh@2k.ktx2"); rec.Code != http.StatusOK {
		t.Fatalf("fresh ktx2 must serve statically, got %d", rec.Code)
	}
}

func TestKtx2Stale_TheOriginalBehindTheVariantChainCounts(t *testing.T) {
	dir := t.TempDir()
	ktx2 := filepath.Join(dir, "hero@2k.ktx2")
	intermediate := filepath.Join(dir, "hero@2k.png")
	original := filepath.Join(dir, "hero.png")

	if err := os.WriteFile(ktx2, []byte("k"), 0o644); err != nil {
		t.Fatal(err)
	}
	writeSolidPNG(t, intermediate, 50, 50, color.NRGBA{R: 255, A: 255})
	// Explicit, well-separated mtimes — the strict After() comparison must not
	// hinge on how many microseconds apart two test writes landed.
	at := func(path string, age time.Duration) {
		when := time.Now().Add(-age)
		if err := os.Chtimes(path, when, when); err != nil {
			t.Fatal(err)
		}
	}
	at(intermediate, 2*time.Hour)
	at(ktx2, 1*time.Hour)
	// Only the ORIGINAL moves — the intermediate @2k.png sits untouched, which
	// is exactly how a replaced photo looks before anything regenerates.
	writeSolidPNG(t, original, 100, 100, color.NRGBA{B: 255, A: 255})

	if !ktx2Stale(ktx2) {
		t.Fatal("replacing the original must mark the whole variant chain stale")
	}
	at(original, 3*time.Hour)
	if ktx2Stale(ktx2) {
		t.Fatal("nothing is newer than the encode — it must count as fresh")
	}
}
