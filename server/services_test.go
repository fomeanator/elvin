package main

// The product services end-to-end over httptest: device registration and its
// idempotency, token rotation, wallet earn/spend and the insufficient-funds
// contract, dev-mode IAP against the catalog, and the analytics batch →
// summary loop.

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func servicesMux(t *testing.T, iapDev bool) (*http.ServeMux, string) {
	mux, _, dir := servicesMuxFull(t, iapDev)
	return mux, dir
}

func servicesMuxFull(t *testing.T, iapDev bool) (*http.ServeMux, *AuthService, string) {
	t.Helper()
	dir := t.TempDir()
	catalog := filepath.Join(dir, "iap-catalog.json")
	_ = os.WriteFile(catalog, []byte(`{"gold_100": {"currency": "gold", "amount": 100}}`), 0o644)

	auth, err := NewAuthService(dir)
	if err != nil {
		t.Fatal(err)
	}
	wallet, err := NewWalletService(filepath.Join(dir, "wallet"), auth, catalog, iapDev)
	if err != nil {
		t.Fatal(err)
	}
	analytics, err := NewAnalyticsService(filepath.Join(dir, "analytics"), auth, "admintok")
	if err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	auth.Routes(mux)
	wallet.Routes(mux)
	analytics.Routes(mux)
	return mux, auth, dir
}

func call(t *testing.T, mux *http.ServeMux, method, path, token string, body any) (*httptest.ResponseRecorder, map[string]any) {
	t.Helper()
	var buf bytes.Buffer
	if body != nil {
		_ = json.NewEncoder(&buf).Encode(body)
	}
	req := httptest.NewRequest(method, path, &buf)
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	var out map[string]any
	_ = json.Unmarshal(rec.Body.Bytes(), &out)
	return rec, out
}

const device = "device-secret-guid-0123456789"

func register(t *testing.T, mux *http.ServeMux) (string, string) {
	t.Helper()
	rec, out := call(t, mux, "POST", "/v1/auth/register", "", map[string]string{"device_id": device})
	if rec.Code != 200 {
		t.Fatalf("register: %d %s", rec.Code, rec.Body)
	}
	return out["user_id"].(string), out["token"].(string)
}

func TestAuth_RegisterIsIdempotentAndRotatesTheToken(t *testing.T) {
	mux, _ := servicesMux(t, false)
	user1, tok1 := register(t, mux)
	user2, tok2 := register(t, mux)
	if user1 != user2 {
		t.Fatalf("same device produced two users: %s vs %s", user1, user2)
	}
	if tok1 == tok2 {
		t.Fatal("token must rotate on re-register")
	}
	if rec, _ := call(t, mux, "GET", "/v1/auth/me", tok1, nil); rec.Code != 401 {
		t.Fatalf("old token must be revoked, got %d", rec.Code)
	}
	if rec, out := call(t, mux, "GET", "/v1/auth/me", tok2, nil); rec.Code != 200 || out["user_id"] != user1 {
		t.Fatalf("fresh token must work: %d %v", rec.Code, out)
	}
}

func TestWallet_EarnSpendAndInsufficientFunds(t *testing.T) {
	mux, _ := servicesMux(t, false)
	_, tok := register(t, mux)

	if rec, _ := call(t, mux, "GET", "/v1/wallet", "", nil); rec.Code != 401 {
		t.Fatalf("anonymous wallet must 401, got %d", rec.Code)
	}
	rec, out := call(t, mux, "POST", "/v1/wallet/earn", tok,
		map[string]any{"currency": "gold", "amount": 50, "reason": "quest:ch1"})
	if rec.Code != 200 {
		t.Fatalf("earn: %d %s", rec.Code, rec.Body)
	}
	rec, out = call(t, mux, "POST", "/v1/wallet/spend", tok,
		map[string]any{"currency": "gold", "amount": 30, "sku": "sword", "reason": "shop"})
	if rec.Code != 200 {
		t.Fatalf("spend: %d %s", rec.Code, rec.Body)
	}
	bal := out["balances"].(map[string]any)
	if bal["gold"].(float64) != 20 {
		t.Fatalf("balance after spend: %v", bal)
	}
	inv := out["inventory"].(map[string]any)
	if inv["sword"].(float64) != 1 {
		t.Fatalf("sku not granted: %v", inv)
	}
	rec, out = call(t, mux, "POST", "/v1/wallet/spend", tok,
		map[string]any{"currency": "gold", "amount": 999, "reason": "greed"})
	if rec.Code != 409 || out["error"] != "insufficient_funds" {
		t.Fatalf("overdraft must 409 insufficient_funds: %d %v", rec.Code, out)
	}
}

func TestIAP_DevModeGrantsFromCatalog_ProdRefusesHonestly(t *testing.T) {
	devMux, _ := servicesMux(t, true)
	_, tok := register(t, devMux)
	rec, out := call(t, devMux, "POST", "/v1/iap/verify", tok,
		map[string]any{"platform": "dev", "sku": "gold_100", "receipt": "test-receipt"})
	if rec.Code != 200 {
		t.Fatalf("dev IAP: %d %s", rec.Code, rec.Body)
	}
	if out["balances"].(map[string]any)["gold"].(float64) != 100 {
		t.Fatalf("catalog grant missing: %v", out)
	}
	if rec, _ := call(t, devMux, "POST", "/v1/iap/verify", tok,
		map[string]any{"platform": "dev", "sku": "nope", "receipt": "r"}); rec.Code != 404 {
		t.Fatalf("unknown sku must 404, got %d", rec.Code)
	}

	prodMux, _ := servicesMux(t, false)
	_, tok2 := register(t, prodMux)
	if rec, _ := call(t, prodMux, "POST", "/v1/iap/verify", tok2,
		map[string]any{"platform": "gplay", "sku": "gold_100", "receipt": "r"}); rec.Code != 501 {
		t.Fatalf("unverifiable store receipt must 501, got %d", rec.Code)
	}
}

func TestAnalytics_BatchThenSummary(t *testing.T) {
	mux, _ := servicesMux(t, false)
	_, tok := register(t, mux)

	rec, out := call(t, mux, "POST", "/v1/analytics/events", tok, []map[string]any{
		{"name": "chapter_start", "props": map[string]any{"ch": "ch1"}},
		{"name": "chapter_start"},
		{"name": "choice_pick"},
		{"name": ""}, // dropped
	})
	if rec.Code != 200 || out["accepted"].(float64) != 3 {
		t.Fatalf("batch: %d %v", rec.Code, out)
	}
	// anonymous events are allowed
	if rec, _ := call(t, mux, "POST", "/v1/analytics/events", "", []map[string]any{{"name": "boot"}}); rec.Code != 200 {
		t.Fatalf("anonymous batch: %d", rec.Code)
	}

	if rec, _ := call(t, mux, "GET", "/v1/analytics/summary", "", nil); rec.Code != 401 {
		t.Fatalf("summary without admin must 401, got %d", rec.Code)
	}
	rec, out = call(t, mux, "GET", "/v1/analytics/summary", "admintok", nil)
	if rec.Code != 200 {
		t.Fatalf("summary: %d %s", rec.Code, rec.Body)
	}
	if out["total"].(float64) != 4 || out["unique_users"].(float64) != 1 {
		t.Fatalf("summary numbers off: %v", out)
	}
	if out["by_name"].(map[string]any)["chapter_start"].(float64) != 2 {
		t.Fatalf("by_name off: %v", out)
	}
}

func TestDaily_StreakGrowsResetsAndRefusesSecondClaim(t *testing.T) {
	mux, dir := servicesMux(t, false)
	_, tok := register(t, mux)

	// A time machine for the daily service: rebuild it on the same stores
	// with a controllable clock.
	auth, _ := NewAuthService(dir)
	wallet, _ := NewWalletService(filepath.Join(dir, "wallet"), auth, "", false)
	day := time.Date(2026, 7, 4, 10, 0, 0, 0, time.UTC)
	daily, _ := NewDailyService(filepath.Join(dir, "daily"), auth, wallet,
		filepath.Join(dir, "no-rewards.json")) // default: 25 gold
	daily.now = func() time.Time { return day }
	dm := http.NewServeMux()
	daily.Routes(dm)

	claim := func() (*httptest.ResponseRecorder, map[string]any) {
		return call(t, dm, "POST", "/v1/daily/claim", tok, nil)
	}

	rec, out := claim()
	if rec.Code != 200 || out["streak"].(float64) != 1 {
		t.Fatalf("day1: %d %v", rec.Code, out)
	}
	if rec, out = claim(); rec.Code != 409 || out["error"] != "already_claimed" {
		t.Fatalf("second claim same day must 409: %d %v", rec.Code, out)
	}

	day = day.AddDate(0, 0, 1) // next day → streak 2
	if rec, out = claim(); rec.Code != 200 || out["streak"].(float64) != 2 {
		t.Fatalf("day2 streak: %d %v", rec.Code, out)
	}

	day = day.AddDate(0, 0, 3) // a gap → streak resets to 1
	if rec, out = claim(); rec.Code != 200 || out["streak"].(float64) != 1 {
		t.Fatalf("gap must reset the streak: %d %v", rec.Code, out)
	}

	// The wallet actually received the three grants (3 × 25 default).
	wm := http.NewServeMux()
	wallet.Routes(wm)
	rec, out = call(t, wm, "GET", "/v1/wallet", tok, nil)
	if rec.Code != 200 || out["balances"].(map[string]any)["gold"].(float64) != 75 {
		t.Fatalf("wallet after 3 dailies: %d %v", rec.Code, out)
	}
}

func TestLeaderboard_BestScoreWinsAndRanks(t *testing.T) {
	mux, auth, dir := servicesMuxFull(t, false)
	lb, _ := NewLeaderboardService(filepath.Join(dir, "lb"), auth)
	lb.Routes(mux)

	_, tok1 := register(t, mux)
	// a second player from another device
	rec, out := call(t, mux, "POST", "/v1/auth/register", "",
		map[string]string{"device_id": "another-device-9876543210fedcba"})
	if rec.Code != 200 {
		t.Fatal("second register failed")
	}
	tok2 := out["token"].(string)

	rec, out = call(t, mux, "POST", "/v1/leaderboard/quiz_score", tok1,
		map[string]any{"score": 100, "name": "Фомин"})
	if rec.Code != 200 || out["rank"].(float64) != 1 {
		t.Fatalf("first submit: %d %v", rec.Code, out)
	}
	rec, out = call(t, mux, "POST", "/v1/leaderboard/quiz_score", tok2,
		map[string]any{"score": 150, "name": "Арам"})
	if out["rank"].(float64) != 1 {
		t.Fatalf("higher score must lead: %v", out)
	}
	// a worse re-submit never downgrades
	rec, out = call(t, mux, "POST", "/v1/leaderboard/quiz_score", tok2,
		map[string]any{"score": 50})
	if out["improved"].(bool) != false || out["rank"].(float64) != 1 {
		t.Fatalf("worse score must keep the best: %v", out)
	}

	rec, out = call(t, mux, "GET", "/v1/leaderboard/quiz_score?n=10", tok1, nil)
	if rec.Code != 200 || out["total"].(float64) != 2 {
		t.Fatalf("top: %d %v", rec.Code, out)
	}
	top := out["top"].([]any)
	if top[0].(map[string]any)["name"] != "Арам" {
		t.Fatalf("order off: %v", top)
	}
	if out["me"].(map[string]any)["rank"].(float64) != 2 {
		t.Fatalf("caller's rank off: %v", out["me"])
	}

	// ServeMux normalizes ".." with a redirect before our handler; the slug
	// regexp still guards encoded variants. Either way: no 200, no file.
	if rec, _ := call(t, mux, "POST", "/v1/leaderboard/../etc", tok1, map[string]any{"score": 1}); rec.Code == 200 {
		t.Fatalf("path traversal must not succeed, got %d", rec.Code)
	}
	if rec, _ := call(t, mux, "POST", "/v1/leaderboard/Bad%2FName", tok1, map[string]any{"score": 1}); rec.Code == 200 {
		t.Fatalf("encoded slash in board name must not succeed, got %d", rec.Code)
	}
}
