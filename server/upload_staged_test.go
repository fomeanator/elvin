package main

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func newStagedUploadTestServer(t *testing.T) *server {
	t.Helper()
	dir := t.TempDir()
	content := filepath.Join(dir, "content")
	if err := os.MkdirAll(content, 0o755); err != nil {
		t.Fatal(err)
	}
	return &server{content: content, adminToken: "sekret"}
}

func stagedPut(s *server, id string, body []byte, contentRange string) *httptest.ResponseRecorder {
	req := httptest.NewRequest(http.MethodPut, "/v1/admin/staged-upload/"+id, bytes.NewReader(body))
	req.Header.Set("Authorization", "Bearer sekret")
	if contentRange != "" {
		req.Header.Set("Content-Range", contentRange)
	}
	w := httptest.NewRecorder()
	s.handleStagedUpload(w, req)
	return w
}

func stagedGet(s *server, id string) *httptest.ResponseRecorder {
	req := httptest.NewRequest(http.MethodGet, "/v1/admin/staged-upload/"+id, nil)
	req.Header.Set("Authorization", "Bearer sekret")
	w := httptest.NewRecorder()
	s.handleStagedUpload(w, req)
	return w
}

func decodeOffset(t *testing.T, w *httptest.ResponseRecorder) int64 {
	t.Helper()
	var body struct {
		Offset int64 `json:"offset"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &body); err != nil {
		t.Fatalf("decode response %q: %v", w.Body.String(), err)
	}
	return body.Offset
}

func TestStagedUploadRejectsWithoutToken(t *testing.T) {
	s := newStagedUploadTestServer(t)
	req := httptest.NewRequest(http.MethodPut, "/v1/admin/staged-upload/x", strings.NewReader("hi"))
	w := httptest.NewRecorder()
	s.handleStagedUpload(w, req)
	if w.Code != http.StatusUnauthorized {
		t.Fatalf("status = %d, want 401", w.Code)
	}
}

func TestStagedUploadRejectsBadID(t *testing.T) {
	s := newStagedUploadTestServer(t)
	w := stagedPut(s, "..%2f..%2fetc%2fpasswd", []byte("x"), "")
	if w.Code != http.StatusBadRequest {
		t.Fatalf("status = %d, want 400 for a path-traversal id", w.Code)
	}
}

func TestStagedUploadFullPutThenGet(t *testing.T) {
	s := newStagedUploadTestServer(t)
	data := []byte("hello staged upload")
	w := stagedPut(s, "file-1", data, "")
	if w.Code != http.StatusOK {
		t.Fatalf("PUT status = %d, body=%s", w.Code, w.Body.String())
	}
	if got := decodeOffset(t, w); got != int64(len(data)) {
		t.Errorf("offset after full PUT = %d, want %d", got, len(data))
	}

	g := stagedGet(s, "file-1")
	if got := decodeOffset(t, g); got != int64(len(data)) {
		t.Errorf("GET offset = %d, want %d", got, len(data))
	}

	on, err := os.ReadFile(filepath.Join(s.stagingDir(), "file-1"))
	if err != nil {
		t.Fatalf("read staged file: %v", err)
	}
	if string(on) != string(data) {
		t.Errorf("staged file content = %q, want %q", on, data)
	}
}

func TestStagedUploadResumesInChunks(t *testing.T) {
	s := newStagedUploadTestServer(t)
	full := []byte("0123456789ABCDEFGHIJ") // 20 bytes
	first, second := full[:8], full[8:]

	w1 := stagedPut(s, "resumed", first, "bytes 0-7/20")
	if w1.Code != http.StatusOK {
		t.Fatalf("chunk1 status = %d, body=%s", w1.Code, w1.Body.String())
	}
	if got := decodeOffset(t, w1); got != 8 {
		t.Fatalf("offset after chunk1 = %d, want 8", got)
	}

	// GET confirms the client can discover where to resume after a drop.
	g := stagedGet(s, "resumed")
	if got := decodeOffset(t, g); got != 8 {
		t.Fatalf("GET offset before resume = %d, want 8", got)
	}

	w2 := stagedPut(s, "resumed", second, "bytes 8-19/20")
	if w2.Code != http.StatusOK {
		t.Fatalf("chunk2 status = %d, body=%s", w2.Code, w2.Body.String())
	}
	if got := decodeOffset(t, w2); got != 20 {
		t.Fatalf("offset after chunk2 = %d, want 20", got)
	}

	on, err := os.ReadFile(filepath.Join(s.stagingDir(), "resumed"))
	if err != nil {
		t.Fatalf("read staged file: %v", err)
	}
	if string(on) != string(full) {
		t.Errorf("staged file content = %q, want %q", on, full)
	}
}

func TestStagedUploadOutOfSyncOffsetReports409(t *testing.T) {
	s := newStagedUploadTestServer(t)
	stagedPut(s, "sync-test", []byte("12345678"), "bytes 0-7/20")

	// Client thinks it's resuming from 3, but the server already has 8 —
	// it must report the REAL offset rather than silently corrupting the file.
	w := stagedPut(s, "sync-test", []byte("xyz"), "bytes 3-5/20")
	if w.Code != http.StatusConflict {
		t.Fatalf("status = %d, want 409", w.Code)
	}
	if got := decodeOffset(t, w); got != 8 {
		t.Errorf("conflict-reported offset = %d, want 8", got)
	}
}
