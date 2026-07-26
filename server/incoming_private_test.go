package main

// A re-import that ends in a conflict parks the incoming version BESIDE the
// live file as <name>.incoming (importer/baseline.go). Unlike every other
// private thing under the content root, it has no directory to hide behind —
// it sits in scripts/ next to the chapter players fetch. That makes two rules
// load-bearing, and both were missing:
//
//   - the static handler must refuse it (it is unreviewed content the author
//     has NOT accepted — serving it publishes every rejected version);
//   - computeVersions must ignore it (otherwise a routine re-import bumps the
//     content version and every client reloads mid-chapter over a file nobody
//     has accepted).
//
// Both rules are one `strings.HasSuffix` away from silently disappearing
// again, hence these tests.

import (
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

func TestParkedIncomingIsNotServedButTheLiveFileIs(t *testing.T) {
	dir := t.TempDir()
	if err := os.MkdirAll(filepath.Join(dir, "scripts"), 0o755); err != nil {
		t.Fatal(err)
	}
	live := filepath.Join(dir, "scripts", "ch1.lvn")
	if err := os.WriteFile(live, []byte(`{"script":[]}`), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(live+".incoming", []byte(`{"script":["unpublished"]}`), 0o644); err != nil {
		t.Fatal(err)
	}

	srv := &server{content: dir}
	h := srv.contentHandler(dir)

	get := func(path string) int {
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, path, nil))
		return rec.Code
	}

	if code := get("/content/scripts/ch1.lvn.incoming"); code != http.StatusNotFound {
		t.Errorf("parked incoming version served with %d — it is unpublished content", code)
	}
	// The rule must stay narrow: the live chapter is exactly what this handler
	// exists to serve.
	if code := get("/content/scripts/ch1.lvn"); code != http.StatusOK {
		t.Errorf("the live chapter must still be served, got %d", code)
	}
}

func TestReimportBookkeepingDoesNotBumpTheContentVersion(t *testing.T) {
	dir := t.TempDir()
	for _, d := range []string{"scripts", ".lvn-import"} {
		if err := os.MkdirAll(filepath.Join(dir, d), 0o755); err != nil {
			t.Fatal(err)
		}
	}
	live := filepath.Join(dir, "scripts", "ch1.lvn")
	if err := os.WriteFile(live, []byte(`{"script":[]}`), 0o644); err != nil {
		t.Fatal(err)
	}

	srv := &server{content: dir}
	before := srv.computeVersions(false)
	if _, ok := before["scripts/ch1.lvn"]; !ok {
		t.Fatalf("the live chapter must be versioned: %v", before)
	}

	// Exactly what a conflicting re-import leaves behind.
	if err := os.WriteFile(live+".incoming", []byte(`{"script":["unpublished"]}`), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, ".lvn-import", "novel.json"), []byte(`{}`), 0o644); err != nil {
		t.Fatal(err)
	}

	after := srv.computeVersions(false)
	if len(after) != len(before) {
		t.Errorf("re-import bookkeeping entered the version index: before=%v after=%v", before, after)
	}
	for rel := range after {
		if rel == "scripts/ch1.lvn.incoming" || rel == ".lvn-import/novel.json" {
			t.Errorf("%s must never bump the content version — it reloads every player mid-chapter", rel)
		}
	}
}
