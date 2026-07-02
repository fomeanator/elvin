package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestValidID(t *testing.T) {
	good := []string{"soviet", "my-title_2", "ABC123"}
	bad := []string{"", "../etc", "a/b", "a b", "a.b", "a$", "..", "a..b"}
	for _, g := range good {
		if !validID(g) {
			t.Errorf("validID(%q) = false, want true", g)
		}
	}
	for _, b := range bad {
		if validID(b) {
			t.Errorf("validID(%q) = true, want false", b)
		}
	}
}

func TestBearerOK(t *testing.T) {
	newReq := func(auth string) *http.Request {
		r, _ := http.NewRequest("GET", "/", nil)
		if auth != "" {
			r.Header.Set("Authorization", auth)
		}
		return r
	}
	if !bearerOK(newReq("Bearer secret"), "secret") {
		t.Error("exact token must pass")
	}
	if bearerOK(newReq("Bearer wrong"), "secret") {
		t.Error("wrong token must fail")
	}
	if bearerOK(newReq("Bearer secretx"), "secret") {
		t.Error("token with extra bytes must fail")
	}
	if bearerOK(newReq(""), "secret") {
		t.Error("missing header must fail")
	}
}

func TestImportDirAllowed(t *testing.T) {
	open := &server{importRoot: ""}
	if !open.importDirAllowed("/anywhere/at/all") {
		t.Error("unset import-root must allow any dir")
	}
	gated := &server{importRoot: "/srv/imports"}
	if !gated.importDirAllowed("/srv/imports/proj") {
		t.Error("dir under root must be allowed")
	}
	if !gated.importDirAllowed("/srv/imports") {
		t.Error("the root itself must be allowed")
	}
	if gated.importDirAllowed("/srv/imports/../../etc") {
		t.Error("traversal out of root must be rejected")
	}
	if gated.importDirAllowed("/etc/passwd") {
		t.Error("dir outside root must be rejected")
	}
	// A sibling that merely shares a prefix string must not slip through.
	if gated.importDirAllowed("/srv/imports-evil") {
		t.Error("prefix-only sibling must be rejected")
	}
}

func TestWriteCappedRejectsOversize(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "ok.txt")
	if _, err := writeCapped(p, strings.NewReader("hello"), 100); err != nil {
		t.Fatalf("within-limit write failed: %v", err)
	}
	if b, _ := os.ReadFile(p); string(b) != "hello" {
		t.Fatalf("content = %q, want hello", b)
	}
	big := filepath.Join(dir, "big.txt")
	if _, err := writeCapped(big, strings.NewReader("way too many bytes"), 3); err == nil {
		t.Fatal("oversize write must fail")
	}
	if _, err := os.Stat(big); !os.IsNotExist(err) {
		t.Error("failed oversize write must not leave a file behind")
	}
}

func TestStatePutGetRoundtripAndPersist(t *testing.T) {
	dir := t.TempDir()
	s := &server{content: dir, state: map[string]stateEntry{}}

	// PUT a save.
	body := `{"vars":{"gold":5},"updatedAt":123}`
	rec := httptest.NewRecorder()
	req := httptest.NewRequest("PUT", "/v1/state?user=u1__title", strings.NewReader(body))
	s.handleState(rec, req)
	if rec.Code != 200 {
		t.Fatalf("PUT code = %d, want 200", rec.Code)
	}
	// It must be durable on disk (atomic write), not just in memory.
	if _, err := os.Stat(s.stateFile("u1__title")); err != nil {
		t.Fatalf("state not persisted to disk: %v", err)
	}

	// GET returns the doc plus the server-owned _version token.
	checkGet := func(label string) {
		rec = httptest.NewRecorder()
		req = httptest.NewRequest("GET", "/v1/state?user=u1__title", nil)
		s.handleState(rec, req)
		if rec.Code != 200 {
			t.Fatalf("%s: GET code = %d, want 200", label, rec.Code)
		}
		var got map[string]any
		if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
			t.Fatalf("%s: GET body not JSON: %v", label, err)
		}
		vars, _ := got["vars"].(map[string]any)
		if vars == nil || vars["gold"] != float64(5) {
			t.Fatalf("%s: vars lost: %v", label, got)
		}
		if got["_version"] != float64(1) {
			t.Fatalf("%s: _version = %v, want 1", label, got["_version"])
		}
	}
	checkGet("fresh")

	// Eviction from the in-memory cache still serves from disk (with version).
	s.mu.Lock()
	delete(s.state, "u1__title")
	s.mu.Unlock()
	checkGet("after eviction")
}

// A client echoing a stale _version gets a 409 with the current doc, so it can
// merge instead of clobbering another device's progress. A matching version
// (or none at all — legacy last-write-wins) is accepted.
func TestStateVersionConflict(t *testing.T) {
	s := &server{content: t.TempDir(), state: map[string]stateEntry{}}
	put := func(body string) *httptest.ResponseRecorder {
		rec := httptest.NewRecorder()
		req := httptest.NewRequest("PUT", "/v1/state?user=u1", strings.NewReader(body))
		s.handleState(rec, req)
		return rec
	}

	// Device A writes twice — versions 1 then 2 (echoing 1).
	if rec := put(`{"vars":{"g":1},"updatedAt":1}`); rec.Code != 200 {
		t.Fatalf("first PUT = %d", rec.Code)
	}
	if rec := put(`{"vars":{"g":2},"updatedAt":2,"_version":1}`); rec.Code != 200 {
		t.Fatalf("versioned PUT = %d: %s", rec.Code, rec.Body.String())
	}

	// Device B, still holding version 1, writes → conflict with the current doc.
	rec := put(`{"vars":{"g":99},"updatedAt":3,"_version":1}`)
	if rec.Code != http.StatusConflict {
		t.Fatalf("stale PUT code = %d, want 409", rec.Code)
	}
	var conflict map[string]any
	_ = json.Unmarshal(rec.Body.Bytes(), &conflict)
	if conflict["version"] != float64(2) {
		t.Fatalf("conflict version = %v, want 2", conflict["version"])
	}
	doc, _ := conflict["doc"].(map[string]any)
	if doc == nil || doc["vars"].(map[string]any)["g"] != float64(2) {
		t.Fatalf("conflict must carry the winning doc: %v", conflict)
	}

	// Device B merges and retries with the fresh version → accepted, version 3.
	rec = put(`{"vars":{"g":100},"updatedAt":4,"_version":2}`)
	if rec.Code != 200 {
		t.Fatalf("merged retry = %d", rec.Code)
	}
	var ok map[string]any
	_ = json.Unmarshal(rec.Body.Bytes(), &ok)
	if ok["version"] != float64(3) {
		t.Fatalf("retry version = %v, want 3", ok["version"])
	}

	// A legacy client (no _version) is still accepted — LWW fallback.
	if rec := put(`{"vars":{"g":7},"updatedAt":5}`); rec.Code != 200 {
		t.Fatalf("legacy PUT = %d, want 200 (LWW fallback)", rec.Code)
	}
}

// Legacy on-disk saves (raw client JSON, pre-versioning) read as version 0 and
// upgrade transparently on the next write.
func TestStateLegacyDiskFileMigrates(t *testing.T) {
	dir := t.TempDir()
	s := &server{content: dir, state: map[string]stateEntry{}}
	// Simulate an old install: raw doc on disk, no wrapper.
	p := s.stateFile("old")
	_ = os.MkdirAll(filepath.Dir(p), 0o755)
	_ = os.WriteFile(p, []byte(`{"vars":{"g":1},"updatedAt":1}`), 0o644)

	rec := httptest.NewRecorder()
	req := httptest.NewRequest("GET", "/v1/state?user=old", nil)
	s.handleState(rec, req)
	var got map[string]any
	_ = json.Unmarshal(rec.Body.Bytes(), &got)
	if got["_version"] != float64(0) {
		t.Fatalf("legacy file version = %v, want 0", got["_version"])
	}
	vars, _ := got["vars"].(map[string]any)
	if vars == nil || vars["g"] != float64(1) {
		t.Fatalf("legacy doc lost: %v", got)
	}
}

func TestStateRejectsNonJSON(t *testing.T) {
	s := &server{content: t.TempDir(), state: map[string]stateEntry{}}
	rec := httptest.NewRecorder()
	req := httptest.NewRequest("PUT", "/v1/state?user=u1", strings.NewReader("not json"))
	s.handleState(rec, req)
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("non-JSON PUT code = %d, want 400", rec.Code)
	}
}

func TestStateTokenGate(t *testing.T) {
	s := &server{content: t.TempDir(), state: map[string]stateEntry{}, stateToken: "sekret"}
	// No token → 401.
	rec := httptest.NewRecorder()
	req := httptest.NewRequest("GET", "/v1/state?user=u1", nil)
	s.handleState(rec, req)
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("unauthenticated state code = %d, want 401", rec.Code)
	}
	// Correct token → proceeds (404 = no save, but past the gate).
	rec = httptest.NewRecorder()
	req = httptest.NewRequest("GET", "/v1/state?user=u1", nil)
	req.Header.Set("Authorization", "Bearer sekret")
	s.handleState(rec, req)
	if rec.Code != http.StatusNotFound {
		t.Fatalf("authenticated state code = %d, want 404 (past the gate)", rec.Code)
	}
}

func TestStateMemBounded(t *testing.T) {
	s := &server{content: t.TempDir(), state: map[string]stateEntry{}}
	for i := 0; i < stateMemMax+50; i++ {
		s.putState("user"+strconvItoa(i), stateEntry{body: []byte("{}")})
	}
	s.mu.RLock()
	n := len(s.state)
	s.mu.RUnlock()
	if n > stateMemMax {
		t.Fatalf("in-memory state grew to %d, want <= %d", n, stateMemMax)
	}
}

func TestBootSourceEscapesServerUrl(t *testing.T) {
	evil := `http://x"; System.IO.File.Delete("/"); var y = "`
	src := bootSource(exportConfig{ServerURL: evil})
	// The URL must land as a single JSON/C# string literal — its inner quotes
	// escaped — so it can't break out and inject a statement.
	lit, _ := json.Marshal(evil)
	if !strings.Contains(src, "public const string ServerUrl = "+string(lit)+";") {
		t.Fatalf("serverUrl not safely escaped into Boot.cs:\n%s", src)
	}
	// The raw unescaped breakout must not appear verbatim.
	if strings.Contains(src, `= "http://x"; System.IO.File.Delete`) {
		t.Fatal("Boot.cs literal broke out — injection possible")
	}
}

// strconvItoa avoids importing strconv just for one use in a table test.
func strconvItoa(i int) string {
	if i == 0 {
		return "0"
	}
	var b [20]byte
	pos := len(b)
	for i > 0 {
		pos--
		b[pos] = byte('0' + i%10)
		i /= 10
	}
	return string(b[pos:])
}
