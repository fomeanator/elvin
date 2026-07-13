package main

import (
	"encoding/binary"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
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

// A .ktx2 request for an image that exists encodes it on demand, caches the
// artifact, and serves a well-formed KTX2 stream.
func TestKtx2EncodesOnDemand(t *testing.T) {
	requireBasisu(t)
	h, dir := newKtx2TestServer(t)
	// 37×53: deliberately NOT a multiple of the 4×4 UASTC block — the exact
	// shape that broke the raw-.astc path.
	writeTestPNG(t, mkParent(t, filepath.Join(dir, "bg", "scene.png")), 37, 53)

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
	if !fileExists(filepath.Join(dir, "bg", "scene.ktx2")) {
		t.Fatal("encoded artifact was not cached to disk")
	}
}

// A @2k.ktx2 request whose @2k PNG doesn't exist yet materializes the
// downscale first; a source already inside the box encodes from the original.
func TestKtx2MaterializesDownscaleVariant(t *testing.T) {
	requireBasisu(t)
	h, dir := newKtx2TestServer(t)
	writeTestPNG(t, mkParent(t, filepath.Join(dir, "bg", "small.png")), 64, 48)

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
