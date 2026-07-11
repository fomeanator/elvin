package main

import (
	"bytes"
	"image"
	"image/color"
	"image/png"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
)

func TestContentPathRejectsTraversal(t *testing.T) {
	srv := &server{content: t.TempDir()}
	if _, ok := srv.contentPath("../../etc/passwd"); ok {
		t.Error("contentPath must reject a path that escapes the content root")
	}
	if _, ok := srv.contentPath("spine/hero/../../../etc/passwd"); ok {
		t.Error("contentPath must reject a path that escapes via ..  segments")
	}
	if p, ok := srv.contentPath("spine/hero/Hero.astc"); !ok || filepath.Dir(p) != filepath.Join(srv.content, "spine", "hero") {
		t.Errorf("contentPath got (%q, %v) for a legitimate path", p, ok)
	}
}

func TestFindSource(t *testing.T) {
	dir := t.TempDir()
	os.WriteFile(filepath.Join(dir, "Hero.png"), []byte("fake"), 0o644)
	if got := findSource(filepath.Join(dir, "Hero.astc")); got != filepath.Join(dir, "Hero.png") {
		t.Errorf("findSource found png = %q", got)
	}
	if got := findSource(filepath.Join(dir, "Missing.astc")); got != "" {
		t.Errorf("findSource on a file with no source should return \"\", got %q", got)
	}
}

func TestWithASTC_NoSourceIs404(t *testing.T) {
	dir := t.TempDir()
	srv := &server{content: dir}
	h := srv.withASTC(srv.contentHandler(dir))

	req := httptest.NewRequest(http.MethodGet, "/content/nowhere/Ghost.astc", nil)
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, req)
	if rec.Code != http.StatusNotFound {
		t.Errorf("expected 404 for an .astc with no source image, got %d", rec.Code)
	}
}

func TestWithASTC_TraversalIs404(t *testing.T) {
	dir := t.TempDir()
	srv := &server{content: dir}
	h := srv.withASTC(srv.contentHandler(dir))

	req := httptest.NewRequest(http.MethodGet, "/content/../../../etc/passwd.astc", nil)
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, req)
	if rec.Code == http.StatusOK {
		t.Error("a path-traversal .astc request must never succeed")
	}
}

func TestWithASTC_NonAstcPassesThrough(t *testing.T) {
	dir := t.TempDir()
	os.WriteFile(filepath.Join(dir, "plain.txt"), []byte("hello"), 0o644)
	srv := &server{content: dir}
	h := srv.withASTC(srv.contentHandler(dir))

	req := httptest.NewRequest(http.MethodGet, "/content/plain.txt", nil)
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK || rec.Body.String() != "hello" {
		t.Errorf("a non-.astc request must pass straight through, got %d %q", rec.Code, rec.Body.String())
	}
}

// TestWithASTC_EndToEndTranscode exercises the real astcenc binary — skipped
// where it isn't installed (exactly the condition that makes withASTC 404
// gracefully in production instead of erroring).
func TestWithASTC_EndToEndTranscode(t *testing.T) {
	if _, err := exec.LookPath("astcenc"); err != nil {
		t.Skip("astcenc not on PATH — the graceful-degradation path is covered by TestWithASTC_NoSourceIs404 instead")
	}

	dir := t.TempDir()
	sub := filepath.Join(dir, "spine", "hero")
	os.MkdirAll(sub, 0o755)

	img := image.NewNRGBA(image.Rect(0, 0, 12, 12))
	for y := 0; y < 12; y++ {
		for x := 0; x < 12; x++ {
			img.SetNRGBA(x, y, color.NRGBA{R: uint8(x * 20), G: uint8(y * 20), B: 128, A: 255})
		}
	}
	var buf bytes.Buffer
	if err := png.Encode(&buf, img); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(sub, "Hero.png"), buf.Bytes(), 0o644); err != nil {
		t.Fatal(err)
	}

	srv := &server{content: dir}
	h := srv.withASTC(srv.contentHandler(dir))

	req := httptest.NewRequest(http.MethodGet, "/content/spine/hero/Hero.astc", nil)
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200 after on-demand transcode, got %d: %s", rec.Code, rec.Body.String())
	}

	body := rec.Body.Bytes()
	if len(body) < 16 {
		t.Fatalf("astc output too short: %d bytes", len(body))
	}
	magic := uint32(body[0]) | uint32(body[1])<<8 | uint32(body[2])<<16 | uint32(body[3])<<24
	if magic != 0x5CA1AB13 {
		t.Errorf("bad ASTC magic: got 0x%08X", magic)
	}
	if body[4] != 6 || body[5] != 6 {
		t.Errorf("expected 6x6 block dims, got %d x %d", body[4], body[5])
	}
	w := int(body[7]) | int(body[8])<<8 | int(body[9])<<16
	h2 := int(body[10]) | int(body[11])<<8 | int(body[12])<<16
	if w != 12 || h2 != 12 {
		t.Errorf("expected 12x12 in header, got %dx%d", w, h2)
	}

	// Second request must hit the now-cached file, not re-invoke astcenc —
	// verify the cached artifact was actually written to disk.
	if !fileExists(filepath.Join(sub, "Hero.astc")) {
		t.Error("transcoded .astc was not persisted to disk for reuse")
	}
	rec2 := httptest.NewRecorder()
	h.ServeHTTP(rec2, httptest.NewRequest(http.MethodGet, "/content/spine/hero/Hero.astc", nil))
	if rec2.Code != http.StatusOK || !bytes.Equal(rec2.Body.Bytes(), body) {
		t.Error("cached .astc request should return the identical bytes")
	}
}
