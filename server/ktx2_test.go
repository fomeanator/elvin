package main

import (
	"encoding/binary"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
	"time"
)

func newKtx2TestServer(t *testing.T) (http.Handler, string) {
	t.Helper()
	dir := t.TempDir()
	s := &server{content: dir}
	h := s.withKTX2(newDownscaler(), s.contentHandler(dir))
	return h, dir
}

// mkParent ensures the sub-directory a test asset lands in exists (the shared
// writeTestPNG in downscale_test.go writes flat paths only).
func mkParent(t *testing.T, path string) string {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	return path
}

// KTX2 magic: «0xAB 'K' 'T' 'X' ' ' '2' '0' 0xBB 0x0D 0x0A 0x1A 0x0A».
var ktx2Magic = []byte{0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A}

func requireBasisu(t *testing.T) {
	t.Helper()
	if _, err := exec.LookPath("basisu"); err != nil {
		t.Skip("basisu not on PATH — on-demand KTX2 encode not testable here")
	}
}

// awaitFile polls for a background-encoded artifact (the worker runs async).
func awaitFile(t *testing.T, path string) {
	t.Helper()
	deadline := time.Now().Add(60 * time.Second)
	for !fileExists(path) {
		if time.Now().After(deadline) {
			t.Fatalf("background encode never produced %s", path)
		}
		time.Sleep(100 * time.Millisecond)
	}
}

// A cold .ktx2 request 404s immediately (client falls back to PNG), queues a
// background encode, and a LATER request serves the well-formed KTX2 stream.
func TestKtx2EncodesInBackground(t *testing.T) {
	requireBasisu(t)
	h, dir := newKtx2TestServer(t)
	// 37×53: deliberately NOT a multiple of the 4×4 UASTC block — the exact
	// shape that broke the raw-.astc path.
	writeTestPNG(t, mkParent(t, filepath.Join(dir, "bg", "scene.png")), 37, 53)

	if rec := get(t, h, "/content/bg/scene.ktx2"); rec.Code != http.StatusNotFound {
		t.Fatalf("cold miss must 404 instantly, got %d", rec.Code)
	}
	awaitFile(t, filepath.Join(dir, "bg", "scene.ktx2"))

	rec := get(t, h, "/content/bg/scene.ktx2")
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rec.Code)
	}
	body := rec.Body.Bytes()
	if len(body) < 80 {
		t.Fatalf("suspiciously small ktx2: %d bytes", len(body))
	}
	for i, b := range ktx2Magic {
		if body[i] != b {
			t.Fatalf("byte %d = %#x, want %#x (not a KTX2 stream)", i, body[i], b)
		}
	}
	// Header pixelWidth/pixelHeight live at offsets 20/24 (after magic +
	// vkFormat + typeSize) and must be the ORIGINAL dimensions.
	if w := binary.LittleEndian.Uint32(body[20:]); w != 37 {
		t.Fatalf("pixelWidth = %d, want 37", w)
	}
	if hh := binary.LittleEndian.Uint32(body[24:]); hh != 53 {
		t.Fatalf("pixelHeight = %d, want 53", hh)
	}
}

// A @2k.ktx2 request whose @2k PNG doesn't exist yet materializes the
// downscale in the worker; a source already inside the box encodes from the
// original (and no @2k png is minted for it).
func TestKtx2MaterializesDownscaleVariant(t *testing.T) {
	requireBasisu(t)
	h, dir := newKtx2TestServer(t)
	writeTestPNG(t, mkParent(t, filepath.Join(dir, "bg", "small.png")), 64, 48)

	if rec := get(t, h, "/content/bg/small@2k.ktx2"); rec.Code != http.StatusNotFound {
		t.Fatalf("cold miss must 404 instantly, got %d", rec.Code)
	}
	awaitFile(t, filepath.Join(dir, "bg", "small@2k.ktx2"))

	rec := get(t, h, "/content/bg/small@2k.ktx2")
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rec.Code)
	}
	if w := binary.LittleEndian.Uint32(rec.Body.Bytes()[20:]); w != 64 {
		t.Fatalf("pixelWidth = %d, want 64 (encoded from the original)", w)
	}
	// The 64×48 source fits the 2048 box — no @2k png must be minted for it.
	if fileExists(filepath.Join(dir, "bg", "small@2k.png")) {
		t.Fatal("errFitsAlready source must not mint an @2k png")
	}
}

// Missing source and traversal attempts 404 cleanly.
func TestKtx2MissesAre404(t *testing.T) {
	h, _ := newKtx2TestServer(t)
	if rec := get(t, h, "/content/bg/nothing.ktx2"); rec.Code != http.StatusNotFound {
		t.Fatalf("missing source: status = %d, want 404", rec.Code)
	}
	if rec := get(t, h, "/content/../secrets.ktx2"); rec.Code != http.StatusNotFound {
		t.Fatalf("traversal: status = %d, want 404", rec.Code)
	}
}

// writeKtx2Header writes a minimal KTX2 file whose header claims w×h. Enough
// for the geometry guard, which only ever reads the first 28 bytes.
func writeKtx2Header(t *testing.T, path string, w, h int) {
	t.Helper()
	buf := make([]byte, 28)
	copy(buf, ktx2Magic)
	binary.LittleEndian.PutUint32(buf[20:24], uint32(w))
	binary.LittleEndian.PutUint32(buf[24:28], uint32(h))
	if err := os.WriteFile(path, buf, 0o644); err != nil {
		t.Fatal(err)
	}
}

// An encode whose picture is a different SIZE than the art beside it was made
// from art that has since been replaced under the same name — mtime can't see
// it (the bad encode is NEWER), so geometry has to. Live case 26.08: 37 of the
// heroine's layers served 1210×2048 encodes of long-gone art next to 1600×2048
// PNGs, squeezing every KTX2-served layer and coarsening its detail.
func TestKtx2MisshapenEncodeIsStale(t *testing.T) {
	dir := t.TempDir()
	art := filepath.Join(dir, "hero.png")
	writeTestPNG(t, art, 1600, 800)

	matching := filepath.Join(dir, "hero.ktx2")
	writeKtx2Header(t, matching, 1600, 800)
	if ktx2Stale(matching) {
		t.Error("encode matching its source must stay fresh")
	}

	wrong := filepath.Join(dir, "hero2.ktx2")
	writeTestPNG(t, filepath.Join(dir, "hero2.png"), 1600, 800)
	writeKtx2Header(t, wrong, 1210, 800)
	if !ktx2Stale(wrong) {
		t.Error("encode of differently-shaped art must be treated as stale")
	}
}

// A variant encode ("X@2k.ktx2") with no @2k.png on disk is measured against
// the ORIGINAL through the downscale box: art inside the box passes through at
// its own size, art above it is compared to the resized dimensions.
func TestKtx2MisshapenVariantUsesDownscaleBox(t *testing.T) {
	dir := t.TempDir()
	writeTestPNG(t, filepath.Join(dir, "big.png"), 4096, 2048)
	ok := filepath.Join(dir, "big@2k.ktx2")
	writeKtx2Header(t, ok, 2048, 1024) // what the downscaler would produce
	if ktx2Stale(ok) {
		t.Error("variant encode at the downscaled size must stay fresh")
	}

	writeTestPNG(t, filepath.Join(dir, "small.png"), 1600, 2048)
	fits := filepath.Join(dir, "small@2k.ktx2")
	writeKtx2Header(t, fits, 1600, 2048) // inside the box — encoded from the original
	if ktx2Stale(fits) {
		t.Error("variant encode of art already inside the box must stay fresh")
	}
}

// A truncated or non-KTX2 payload is never trusted: the client must fall back
// to the PNG path rather than hand torn bytes to the transcoder.
func TestKtx2UnreadableHeaderIsStale(t *testing.T) {
	dir := t.TempDir()
	writeTestPNG(t, filepath.Join(dir, "x.png"), 64, 64)
	torn := filepath.Join(dir, "x.ktx2")
	if err := os.WriteFile(torn, ktx2Magic[:8], 0o644); err != nil {
		t.Fatal(err)
	}
	if !ktx2Stale(torn) {
		t.Error("truncated encode must be treated as stale")
	}
}
