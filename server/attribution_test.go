package main

import (
	"database/sql"
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// attributionMux is servicesMuxFull with a real owner index over a manifest
// that declares who owns which title.
func attributionMux(t *testing.T) (*http.ServeMux, string, *sql.DB) {
	t.Helper()
	dir := t.TempDir()
	db := testStore(t)
	manifest := filepath.Join(dir, "manifest.json")
	if err := os.WriteFile(manifest, []byte(`{"titles":[
		{"id":"tour","author":"elvin"},
		{"id":"guest-novel","author":"masha"},
		{"id":"orphan"}
	]}`), 0o644); err != nil {
		t.Fatal(err)
	}
	owners := newOwnerIndex(manifest)

	auth, err := NewAuthService(dir)
	if err != nil {
		t.Fatal(err)
	}
	wallet, err := NewWalletService(filepath.Join(dir, "wallet"), db, auth, "", false, owners)
	if err != nil {
		t.Fatal(err)
	}
	analytics, err := NewAnalyticsService(filepath.Join(dir, "analytics"), auth, "admintok", owners)
	if err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	auth.Routes(mux)
	wallet.Routes(mux)
	analytics.Routes(mux)
	return mux, dir, db
}

func analyticsLines(t *testing.T, dir string) []map[string]any {
	t.Helper()
	var out []map[string]any
	entries, _ := os.ReadDir(filepath.Join(dir, "analytics"))
	for _, e := range entries {
		body, err := os.ReadFile(filepath.Join(dir, "analytics", e.Name()))
		if err != nil {
			continue
		}
		for _, line := range strings.Split(strings.TrimSpace(string(body)), "\n") {
			if line == "" {
				continue
			}
			var m map[string]any
			if json.Unmarshal([]byte(line), &m) == nil {
				out = append(out, m)
			}
		}
	}
	return out
}

// The author of a title is resolved by the SERVER. A client that names its own
// payee must not be believed — with a creator revenue share that is the whole
// attack, and it costs one line to get wrong.
func TestAttributionIsServerResolvedNotClientSupplied(t *testing.T) {
	mux, dir, _ := attributionMux(t)
	_, tok := register(t, mux)

	rec, _ := call(t, mux, "POST", "/v1/analytics/events", tok, []any{
		map[string]any{"name": "chapter_start", "title": "guest-novel", "author": "attacker"},
	})
	if rec.Code != http.StatusOK && rec.Code != http.StatusAccepted && rec.Code != http.StatusNoContent {
		t.Fatalf("ingest: %d %s", rec.Code, rec.Body.String())
	}
	lines := analyticsLines(t, dir)
	if len(lines) != 1 {
		t.Fatalf("want 1 event, got %d", len(lines))
	}
	if got := lines[0]["author"]; got != "masha" {
		t.Errorf("author = %v, want \"masha\" (resolved from the manifest, NOT the client's \"attacker\")", got)
	}
	if got := lines[0]["title"]; got != "guest-novel" {
		t.Errorf("title = %v, want the client-reported \"guest-novel\"", got)
	}
}

// The title may also arrive inside props — that is where the Unity helper puts
// context today, and an event that carries it must not attribute to nobody.
func TestAttributionAcceptsTitleFromProps(t *testing.T) {
	mux, dir, _ := attributionMux(t)
	_, tok := register(t, mux)
	call(t, mux, "POST", "/v1/analytics/events", tok, []any{
		map[string]any{"name": "choice_pick", "props": map[string]any{"title": "tour"}},
	})
	lines := analyticsLines(t, dir)
	if len(lines) != 1 || lines[0]["author"] != "elvin" {
		t.Fatalf("author not resolved from props: %+v", lines)
	}
}

// A title nobody declared attributes to NOTHING rather than to a guess.
func TestAttributionOfAnUndeclaredTitleIsEmpty(t *testing.T) {
	mux, dir, _ := attributionMux(t)
	_, tok := register(t, mux)
	call(t, mux, "POST", "/v1/analytics/events", tok, []any{
		map[string]any{"name": "x", "title": "orphan"},
		map[string]any{"name": "y", "title": "no-such-title"},
	})
	for _, l := range analyticsLines(t, dir) {
		if a, ok := l["author"]; ok && a != "" {
			t.Errorf("event %v attributed to %q — an undeclared title must attribute to nobody", l["name"], a)
		}
	}
}

// The money path: a spend has to carry which title it happened in, or a payout
// can never be computed from history. This is the half that cannot be backfilled.
func TestWalletHistoryCarriesAttribution(t *testing.T) {
	mux, _, db := attributionMux(t)
	_, tok := register(t, mux)

	call(t, mux, "POST", "/v1/wallet/earn", tok, map[string]any{
		"currency": "gold", "amount": 100, "reason": "test", "op_id": "e1", "title": "tour",
	})
	rec, _ := call(t, mux, "POST", "/v1/wallet/spend", tok, map[string]any{
		"currency": "gold", "amount": 40, "reason": "choice", "op_id": "s1", "title": "guest-novel",
	})
	if rec.Code != http.StatusOK {
		t.Fatalf("spend: %d %s", rec.Code, rec.Body.String())
	}

	var found bool
	// Журнал лежит в базе строками — спрашиваем его запросом. Раньше здесь
	// шёл обход файлов игроков; с переездом денег в базу файлов нет, а
	// вопрос остался прежним: у траты записаны новелла и её владелец.
	rows, qerr := db.Query(`SELECT title, author FROM wallet_ledger WHERE type = 'spend'`)
	if qerr != nil {
		t.Fatalf("журнал не прочитался: %v", qerr)
	}
	defer rows.Close()
	for rows.Next() {
		var title, author string
		if err := rows.Scan(&title, &author); err != nil {
			t.Fatal(err)
		}
		found = true
		if title != "guest-novel" || author != "masha" {
			t.Errorf("трата отнесена к title=%q author=%q, ждали guest-novel/masha", title, author)
		}
	}
	if !found {
		t.Fatal("no spend entry in wallet history — attribution cannot be checked")
	}
}

// The index follows the manifest: an author added after boot must be picked up,
// or transferring a title would silently keep paying the previous owner.
func TestOwnerIndexFollowsTheManifest(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "manifest.json")
	_ = os.WriteFile(path, []byte(`{"titles":[{"id":"a","author":"first"}]}`), 0o644)
	idx := newOwnerIndex(path)
	if got := idx.authorOf("a"); got != "first" {
		t.Fatalf("initial read = %q", got)
	}
	// Rewrite with a different owner; bypass the 2s stat floor the hot path uses.
	_ = os.WriteFile(path, []byte(`{"titles":[{"id":"a","author":"second"}]}`), 0o644)
	idx.mu.Lock()
	idx.checked = idx.checked.Add(-time.Hour)
	idx.mu.Unlock()
	if got := idx.authorOf("a"); got != "second" {
		t.Errorf("after the manifest changed, author = %q, want \"second\"", got)
	}
}
