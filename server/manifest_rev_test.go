package main

import (
	"encoding/json"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// putManifest PUTs a manifest body against a throwaway server rooted in dir.
func putManifest(t *testing.T, srv *server, body string) *httptest.ResponseRecorder {
	t.Helper()
	req := httptest.NewRequest("PUT", "/v1/admin/assets/manifest.json", strings.NewReader(body))
	req.Header.Set("Authorization", "Bearer t")
	rec := httptest.NewRecorder()
	srv.handleAdminAsset(rec, req)
	return rec
}

// Манифест правят несколько агентов: PUT устаревшей копии обязан получать
// 409 с инструкцией, а не молча стирать чужие тайтлы. rev двигается только
// вперёд и инкрементируется сервером.
func TestManifestRevGate(t *testing.T) {
	dir := t.TempDir()
	srv := &server{content: dir, adminToken: "t"}

	// Миграция: ни на диске, ни в теле rev нет — принимается, rev станет 1.
	if rec := putManifest(t, srv, `{"titles":[]}`); rec.Code != 200 {
		t.Fatalf("миграционный PUT: код %d (%s)", rec.Code, rec.Body.String())
	}
	raw, _ := os.ReadFile(filepath.Join(dir, "manifest.json"))
	var m map[string]any
	_ = json.Unmarshal(raw, &m)
	if m["rev"].(float64) != 1 {
		t.Fatalf("после миграции rev = %v, ждали 1", m["rev"])
	}

	// Старая копия (без rev или с прошлым rev) — 409 с внятным текстом.
	if rec := putManifest(t, srv, `{"titles":[]}`); rec.Code != 409 {
		t.Fatalf("PUT без rev по живому манифесту: код %d, ждали 409", rec.Code)
	} else if !strings.Contains(rec.Body.String(), "Обновите манифест") {
		t.Fatalf("409 без инструкции: %s", rec.Body.String())
	}
	if rec := putManifest(t, srv, `{"titles":[],"rev":0}`); rec.Code != 409 {
		t.Fatalf("PUT с устаревшим rev: код %d, ждали 409", rec.Code)
	}

	// Свежая копия (rev совпал) — принимается, rev двигается вперёд.
	if rec := putManifest(t, srv, `{"titles":[],"rev":1}`); rec.Code != 200 {
		t.Fatalf("PUT со свежим rev: код %d (%s)", rec.Code, rec.Body.String())
	}
	raw, _ = os.ReadFile(filepath.Join(dir, "manifest.json"))
	_ = json.Unmarshal(raw, &m)
	if m["rev"].(float64) != 2 {
		t.Fatalf("после записи rev = %v, ждали 2", m["rev"])
	}
}
