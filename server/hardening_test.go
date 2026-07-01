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
	s := &server{content: dir, state: map[string][]byte{}}

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

	// GET returns it back.
	rec = httptest.NewRecorder()
	req = httptest.NewRequest("GET", "/v1/state?user=u1__title", nil)
	s.handleState(rec, req)
	if rec.Code != 200 || rec.Body.String() != body {
		t.Fatalf("GET = (%d) %q, want (200) %q", rec.Code, rec.Body.String(), body)
	}

	// Eviction from the in-memory cache still serves from disk.
	s.mu.Lock()
	delete(s.state, "u1__title")
	s.mu.Unlock()
	rec = httptest.NewRecorder()
	req = httptest.NewRequest("GET", "/v1/state?user=u1__title", nil)
	s.handleState(rec, req)
	if rec.Code != 200 || rec.Body.String() != body {
		t.Fatalf("GET after eviction = (%d) %q, want it reloaded from disk", rec.Code, rec.Body.String())
	}
}

func TestStateRejectsNonJSON(t *testing.T) {
	s := &server{content: t.TempDir(), state: map[string][]byte{}}
	rec := httptest.NewRecorder()
	req := httptest.NewRequest("PUT", "/v1/state?user=u1", strings.NewReader("not json"))
	s.handleState(rec, req)
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("non-JSON PUT code = %d, want 400", rec.Code)
	}
}

func TestStateTokenGate(t *testing.T) {
	s := &server{content: t.TempDir(), state: map[string][]byte{}, stateToken: "sekret"}
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
	s := &server{content: t.TempDir(), state: map[string][]byte{}}
	for i := 0; i < stateMemMax+50; i++ {
		s.putState("user"+strconvItoa(i), []byte("{}"))
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
