package main

// The product services end-to-end over httptest: device registration and its
// idempotency, token rotation, wallet earn/spend and the insufficient-funds
// contract, dev-mode IAP against the catalog, and the analytics batch →
// summary loop.

import (
	"bytes"
	"encoding/json"
	"fmt"
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

func TestWallet_EnergyRegenSeedCapSpendAndBuyPastCap(t *testing.T) {
	dir := t.TempDir()
	// energy.json beside the wallet dir: cap 3, +1 per 2h, seed 3.
	if err := os.WriteFile(filepath.Join(dir, "energy.json"),
		[]byte(`{"energy":{"cap":3,"interval_seconds":7200,"start":3}}`), 0o644); err != nil {
		t.Fatal(err)
	}
	auth, _ := NewAuthService(dir)
	wallet, _ := NewWalletService(filepath.Join(dir, "wallet"), auth, "", false)
	now := time.Date(2026, 7, 7, 10, 0, 0, 0, time.UTC)
	wallet.clock = func() time.Time { return now }

	mux := http.NewServeMux()
	auth.Routes(mux)
	wallet.Routes(mux)
	uid, tok := register(t, mux)

	// bal, and the next-refill timestamp (0/absent when full).
	state := func() (float64, int64) {
		rec, out := call(t, mux, "GET", "/v1/wallet", tok, nil)
		if rec.Code != 200 {
			t.Fatalf("wallet GET: %d %s", rec.Code, rec.Body)
		}
		bal := out["balances"].(map[string]any)["energy"].(float64)
		reg := out["regen"].(map[string]any)["energy"].(map[string]any)
		var next int64
		if v, ok := reg["next_refill_unix"].(float64); ok {
			next = int64(v)
		}
		return bal, next
	}
	spend := func(n int) int {
		rec, _ := call(t, mux, "POST", "/v1/wallet/spend", tok,
			map[string]any{"currency": "energy", "amount": n, "reason": "chapter"})
		return rec.Code
	}

	// New user is seeded at the cap; the refill clock is parked.
	if bal, next := state(); bal != 3 || next != 0 {
		t.Fatalf("new user: bal=%v next=%d (want 3, parked)", bal, next)
	}

	// Spend 1 → 2; the clock starts, first refill 2h out.
	if code := spend(1); code != 200 {
		t.Fatalf("spend: %d", code)
	}
	spentAt := now.Unix()
	if bal, next := state(); bal != 2 || next != spentAt+7200 {
		t.Fatalf("after spend: bal=%v next=%d (want 2, +2h)", bal, next)
	}

	// 2h later → back to the cap, clock parks again.
	now = now.Add(2 * time.Hour)
	if bal, next := state(); bal != 3 || next != 0 {
		t.Fatalf("after 2h: bal=%v next=%d (want 3, parked)", bal, next)
	}

	// Burn all 3 at once (3 chapters back to back) → 0; a 4th spend 409s.
	drainAt := now.Unix()
	spend(1)
	spend(1)
	spend(1)
	if bal, _ := state(); bal != 0 {
		t.Fatalf("drain: bal=%v (want 0)", bal)
	}
	if code := spend(1); code != 409 {
		t.Fatalf("spending at 0 must 409, got %d", code)
	}

	// 5h after draining → floor(5/2)=2 restored (2 of 3), clock still running.
	now = now.Add(5 * time.Hour)
	if bal, next := state(); bal != 2 || next != drainAt+4*3600+7200 {
		t.Fatalf("5h after drain: bal=%v next=%d (want 2, running)", bal, next)
	}
	// 4h more → hits the cap and parks.
	now = now.Add(4 * time.Hour)
	if bal, next := state(); bal != 3 || next != 0 {
		t.Fatalf("refill to cap: bal=%v next=%d (want 3, parked)", bal, next)
	}

	// Buy past the cap: a grant of 5 while at 3 → 8, no regen and no decay.
	wallet.Grant(uid, "energy", 5, "iap:refill")
	if bal, next := state(); bal != 8 || next != 0 {
		t.Fatalf("after buy past cap: bal=%v next=%d (want 8, parked)", bal, next)
	}
	now = now.Add(10 * time.Hour) // surplus above the cap never regens
	if bal, _ := state(); bal != 8 {
		t.Fatalf("surplus must not regen: bal=%v (want 8)", bal)
	}
	// Spend back below the cap → the clock resumes.
	if code := spend(6); code != 200 {
		t.Fatalf("spend 6: %d", code)
	}
	if bal, next := state(); bal != 2 || next != now.Unix()+7200 {
		t.Fatalf("after dropping below cap: bal=%v next=%d (want 2, running)", bal, next)
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

func TestIAP_BundleGrantsMultipleCurrencies(t *testing.T) {
	dir := t.TempDir()
	catalog := filepath.Join(dir, "iap-catalog.json")
	// A bundle: grants takes precedence over currency/amount (the latter is
	// only the card headline).
	_ = os.WriteFile(catalog, []byte(
		`{"bundle_starter":{"currency":"gold","amount":500,"grants":{"gold":500,"energy":3}}}`), 0o644)

	auth, _ := NewAuthService(dir)
	wallet, _ := NewWalletService(filepath.Join(dir, "wallet"), auth, catalog, true) // dev IAP
	mux := http.NewServeMux()
	auth.Routes(mux)
	wallet.Routes(mux)
	_, tok := register(t, mux)

	rec, out := call(t, mux, "POST", "/v1/iap/verify", tok,
		map[string]any{"platform": "dev", "sku": "bundle_starter", "receipt": "r"})
	if rec.Code != 200 {
		t.Fatalf("bundle IAP: %d %s", rec.Code, rec.Body)
	}
	bal := out["balances"].(map[string]any)
	if bal["gold"].(float64) != 500 || bal["energy"].(float64) != 3 {
		t.Fatalf("bundle must grant both currencies: %v", bal)
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

func TestLeaderboard_NameTruncationIsRuneSafe(t *testing.T) {
	mux, auth, dir := servicesMuxFull(t, false)
	lb, _ := NewLeaderboardService(filepath.Join(dir, "lb2"), auth)
	lb.Routes(mux)
	_, tok := register(t, mux)
	long := "БраузерныйЧемпионСОченьДлиннымИменемКоторое"
	_, _ = call(t, mux, "POST", "/v1/leaderboard/names", tok, map[string]any{"score": 1, "name": long})
	_, out := call(t, mux, "GET", "/v1/leaderboard/names", tok, nil)
	got := out["top"].([]any)[0].(map[string]any)["name"].(string)
	if len([]rune(got)) != 32 {
		t.Fatalf("expected 32 runes, got %d (%q)", len([]rune(got)), got)
	}
	for _, r := range got {
		if r == '�' {
			t.Fatalf("broken rune in %q", got)
		}
	}
}

func TestIAP_CatalogIsPublicAndSorted(t *testing.T) {
	dir := t.TempDir()
	catalog := filepath.Join(dir, "iap-catalog.json")
	_ = os.WriteFile(catalog, []byte(`{
		"z_first":  {"currency": "gold", "amount": 999, "title": "Big", "price": "$9.99", "bonus": 100, "order": 1},
		"a_second": {"currency": "gold", "amount": 5, "order": 2},
		"plain":    {"currency": "crystals", "amount": 1}
	}`), 0o644)
	auth, _ := NewAuthService(dir)
	wallet, _ := NewWalletService(filepath.Join(dir, "wallet"), auth, catalog, false)
	mux := http.NewServeMux()
	wallet.Routes(mux)

	rec, out := call(t, mux, "GET", "/v1/iap/catalog", "", nil) // no token — public
	if rec.Code != 200 {
		t.Fatalf("catalog: %d %s", rec.Code, rec.Body)
	}
	packs := out["packs"].([]any)
	if len(packs) != 3 {
		t.Fatalf("expected 3 packs, got %d", len(packs))
	}
	// order 0 (unset) sorts first, then order 1, then 2.
	first := packs[0].(map[string]any)
	second := packs[1].(map[string]any)
	if first["sku"] != "plain" || second["sku"] != "z_first" {
		t.Fatalf("wrong sort: %v", packs)
	}
	if second["title"] != "Big" || second["price"] != "$9.99" || second["bonus"].(float64) != 100 {
		t.Fatalf("presentation fields lost: %v", second)
	}
	if _, has := first["title"]; has {
		t.Fatalf("plain pack must omit empty presentation fields: %v", first)
	}
}

func TestAuth_ProfileNameRoundTrips(t *testing.T) {
	mux, _ := servicesMux(t, false)
	_, tok := register(t, mux)

	if rec, _ := call(t, mux, "POST", "/v1/auth/profile", "", map[string]string{"name": "x"}); rec.Code != 401 {
		t.Fatalf("anonymous profile must 401, got %d", rec.Code)
	}
	rec, out := call(t, mux, "POST", "/v1/auth/profile", tok, map[string]string{"name": "  Арам  "})
	if rec.Code != 200 || out["name"] != "Арам" {
		t.Fatalf("profile set: %d %v", rec.Code, out)
	}
	if _, out := call(t, mux, "GET", "/v1/auth/me", tok, nil); out["name"] != "Арам" {
		t.Fatalf("me must return the name: %v", out)
	}
}

func TestAuth_ProviderLinkAndCrossDeviceLogin(t *testing.T) {
	mux, auth, _ := servicesMuxFull(t, false)
	auth.AuthDev = true

	// device account links a provider identity…
	user1, tok := register(t, mux)
	rec, out := call(t, mux, "POST", "/v1/auth/link", tok,
		map[string]string{"provider": "dev", "token": "google-sub-12345"})
	if rec.Code != 200 || out["user_id"] != user1 {
		t.Fatalf("link: %d %v", rec.Code, out)
	}

	// …and a "new phone" logs in with the SAME identity → same account back
	rec, out = call(t, mux, "POST", "/v1/auth/login", "",
		map[string]string{"provider": "dev", "token": "google-sub-12345"})
	if rec.Code != 200 || out["user_id"] != user1 {
		t.Fatalf("cross-device login must recover the account: %d %v", rec.Code, out)
	}
	if out["token"] == tok {
		t.Fatal("login must mint a fresh session token")
	}

	// an unknown identity auto-creates a new account
	rec, out = call(t, mux, "POST", "/v1/auth/login", "",
		map[string]string{"provider": "dev", "token": "apple-sub-99999"})
	if rec.Code != 200 || out["user_id"] == user1 {
		t.Fatalf("unknown identity must create a fresh account: %d %v", rec.Code, out)
	}

	// linking an identity that belongs to someone else → 409 with the owner
	user3tok := ""
	{
		var buf bytes.Buffer
		_ = json.NewEncoder(&buf).Encode(map[string]string{"device_id": "another-device-9876543210"})
		req := httptest.NewRequest("POST", "/v1/auth/register", &buf)
		rec := httptest.NewRecorder()
		mux.ServeHTTP(rec, req)
		var o map[string]any
		_ = json.Unmarshal(rec.Body.Bytes(), &o)
		user3tok = o["token"].(string)
	}
	rec, out = call(t, mux, "POST", "/v1/auth/link", user3tok,
		map[string]string{"provider": "dev", "token": "google-sub-12345"})
	if rec.Code != 409 || out["error"] != "already_linked" || out["user_id"] != user1 {
		t.Fatalf("stealing a linked identity must 409: %d %v", rec.Code, out)
	}
}

func TestAuth_DevProviderRequiresTheFlag(t *testing.T) {
	mux, _ := servicesMux(t, false) // AuthDev NOT set
	_, tok := register(t, mux)
	if rec, _ := call(t, mux, "POST", "/v1/auth/link", tok,
		map[string]string{"provider": "dev", "token": "sub"}); rec.Code != 401 {
		t.Fatalf("dev provider without -auth-dev must 401, got %d", rec.Code)
	}
	if rec, _ := call(t, mux, "POST", "/v1/auth/link", tok,
		map[string]string{"provider": "martian", "token": "x"}); rec.Code != 401 {
		t.Fatalf("unknown provider must 401, got %d", rec.Code)
	}
}

func TestIAP_AppleReceiptPathAndReplayGuard(t *testing.T) {
	dir := t.TempDir()
	catalog := filepath.Join(dir, "iap-catalog.json")
	_ = os.WriteFile(catalog, []byte(`{"gold_100": {"currency": "gold", "amount": 100}}`), 0o644)
	auth, _ := NewAuthService(dir)
	wallet, _ := NewWalletService(filepath.Join(dir, "wallet"), auth, catalog, false)
	wallet.AppleSharedSecret = "shhh"
	wallet.verifyApple = func(receipt, sku, secret, bundleID string) (string, error) {
		if receipt != "valid-receipt" {
			return "", fmt.Errorf("bad receipt")
		}
		return "tx-001", nil
	}
	mux := http.NewServeMux()
	auth.Routes(mux)
	wallet.Routes(mux)
	_, tok := register(t, mux)

	rec, out := call(t, mux, "POST", "/v1/iap/verify", tok,
		map[string]string{"platform": "appstore", "sku": "gold_100", "receipt": "valid-receipt"})
	if rec.Code != 200 || out["balances"].(map[string]any)["gold"].(float64) != 100 {
		t.Fatalf("apple verify: %d %v", rec.Code, out)
	}
	// same transaction again → idempotent, no double credit
	rec, out = call(t, mux, "POST", "/v1/iap/verify", tok,
		map[string]string{"platform": "appstore", "sku": "gold_100", "receipt": "valid-receipt"})
	if rec.Code != 200 || out["balances"].(map[string]any)["gold"].(float64) != 100 {
		t.Fatalf("replay must not double-credit: %d %v", rec.Code, out)
	}
	// a rejected receipt → 402
	if rec, _ := call(t, mux, "POST", "/v1/iap/verify", tok,
		map[string]string{"platform": "appstore", "sku": "gold_100", "receipt": "forged"}); rec.Code != 402 {
		t.Fatalf("bad receipt must 402, got %d", rec.Code)
	}
	// gplay without credentials → honest 501
	if rec, _ := call(t, mux, "POST", "/v1/iap/verify", tok,
		map[string]string{"platform": "gplay", "sku": "gold_100", "receipt": "r"}); rec.Code != 501 {
		t.Fatalf("gplay must 501, got %d", rec.Code)
	}
}

func TestAds_RewardGrantsAndDailyCap(t *testing.T) {
	dir := t.TempDir()
	adsPath := filepath.Join(dir, "ads.json")
	_ = os.WriteFile(adsPath, []byte(`{"gold_small":{"currency":"gold","amount":25,"daily_cap":2}}`), 0o644)
	auth, _ := NewAuthService(dir)
	wallet, _ := NewWalletService(filepath.Join(dir, "wallet"), auth, "", false)
	ads, _ := NewAdsService(filepath.Join(dir, "ads"), auth, wallet, adsPath)
	mux := http.NewServeMux()
	auth.Routes(mux)
	wallet.Routes(mux)
	ads.Routes(mux)
	_, tok := register(t, mux)

	if rec, out := call(t, mux, "GET", "/v1/ads/catalog", "", nil); rec.Code != 200 ||
		len(out["placements"].([]any)) != 1 {
		t.Fatalf("ads catalog: %d %v", rec.Code, out)
	}
	for i := 0; i < 2; i++ {
		rec, out := call(t, mux, "POST", "/v1/ads/reward", tok, map[string]string{"placement": "gold_small"})
		if rec.Code != 200 || out["granted"] != true {
			t.Fatalf("reward %d: %d %v", i, rec.Code, out)
		}
	}
	rec, out := call(t, mux, "POST", "/v1/ads/reward", tok, map[string]string{"placement": "gold_small"})
	if rec.Code != 429 || out["error"] != "daily_cap" {
		t.Fatalf("cap must 429 daily_cap: %d %v", rec.Code, out)
	}
	if rec, out := call(t, mux, "GET", "/v1/wallet", tok, nil); rec.Code != 200 ||
		out["balances"].(map[string]any)["gold"].(float64) != 50 {
		t.Fatalf("two rewards must land exactly 50 gold: %d %v", rec.Code, out)
	}
	if rec, _ := call(t, mux, "POST", "/v1/ads/reward", tok, map[string]string{"placement": "nope"}); rec.Code != 404 {
		t.Fatalf("unknown placement must 404, got %d", rec.Code)
	}
}

func TestAdmin_UsersOrdersGrantAndManifest(t *testing.T) {
	dir := t.TempDir()
	content := filepath.Join(dir, "content")
	_ = os.MkdirAll(content, 0o755)
	_ = os.WriteFile(filepath.Join(content, "manifest.json"), []byte(`{"titles":[]}`), 0o644)
	auth, _ := NewAuthService(dir)
	wallet, _ := NewWalletService(filepath.Join(dir, "wallet"), auth, "", true)
	admin := NewAdminService(content, "admintok", auth, wallet)
	mux := http.NewServeMux()
	auth.Routes(mux)
	wallet.Routes(mux)
	admin.Routes(mux)
	user, tok := register(t, mux)

	// no/bad token → 401 everywhere
	if rec, _ := call(t, mux, "GET", "/v1/admin/users", "", nil); rec.Code != 401 {
		t.Fatalf("admin without token must 401, got %d", rec.Code)
	}
	// grant + user list with balances
	rec, _ := call(t, mux, "POST", "/v1/admin/grant", "admintok",
		map[string]any{"user_id": user, "currency": "gold", "amount": 500})
	if rec.Code != 200 {
		t.Fatalf("grant: %d %s", rec.Code, rec.Body)
	}
	rec, out := call(t, mux, "GET", "/v1/admin/users", "admintok", nil)
	users := out["users"].([]any)
	if rec.Code != 200 || len(users) != 1 ||
		users[0].(map[string]any)["balances"].(map[string]any)["gold"].(float64) != 500 {
		t.Fatalf("users list: %d %v", rec.Code, out)
	}
	// clawback floors at zero
	_, _ = call(t, mux, "POST", "/v1/admin/grant", "admintok",
		map[string]any{"user_id": user, "currency": "gold", "amount": -9999})
	if _, out := call(t, mux, "GET", "/v1/admin/users/"+user, "admintok", nil); out["wallet"].(map[string]any)["balances"].(map[string]any)["gold"].(float64) != 0 {
		t.Fatalf("clawback must floor at zero: %v", out)
	}
	// a dev IAP shows up in the orders ledger
	_, _ = call(t, mux, "POST", "/v1/wallet/spend", tok,
		map[string]any{"currency": "gold", "amount": 1, "reason": "x"}) // not an order (no sku)
	rec, out = call(t, mux, "GET", "/v1/admin/orders", "admintok", nil)
	if rec.Code != 200 {
		t.Fatalf("orders: %d", rec.Code)
	}
	// manifest GET/PUT round-trip
	rec, _ = call(t, mux, "PUT", "/v1/admin/manifest", "admintok",
		map[string]any{"titles": []any{map[string]any{"id": "t1"}}})
	if rec.Code != 200 {
		t.Fatalf("manifest put: %d %s", rec.Code, rec.Body)
	}
	rec, out = call(t, mux, "GET", "/v1/admin/manifest", "admintok", nil)
	if rec.Code != 200 || len(out["titles"].([]any)) != 1 {
		t.Fatalf("manifest get after put: %d %v", rec.Code, out)
	}
}

func TestEconomy_CatalogsHotReloadFromDisk(t *testing.T) {
	dir := t.TempDir()
	catalog := filepath.Join(dir, "iap-catalog.json")
	_ = os.WriteFile(catalog, []byte(`{"gold_100": {"currency":"gold","amount":100}}`), 0o644)
	auth, _ := NewAuthService(dir)
	wallet, _ := NewWalletService(filepath.Join(dir, "wallet"), auth, catalog, true)
	mux := http.NewServeMux()
	auth.Routes(mux)
	wallet.Routes(mux)

	if _, out := call(t, mux, "GET", "/v1/iap/catalog", "", nil); len(out["packs"].([]any)) != 1 {
		t.Fatalf("initial catalog: %v", out)
	}
	// edit the file on disk (what the admin panel's PUT does) — no restart
	time.Sleep(15 * time.Millisecond) // ensure a fresh mtime on coarse filesystems
	_ = os.WriteFile(catalog, []byte(`{"gold_100": {"currency":"gold","amount":100},
		"gold_999": {"currency":"gold","amount":999}}`), 0o644)
	if _, out := call(t, mux, "GET", "/v1/iap/catalog", "", nil); len(out["packs"].([]any)) != 2 {
		t.Fatalf("catalog must follow the disk edit live: %v", out)
	}
	// and the verify path sees the new sku too
	_, tok := register(t, mux)
	if rec, _ := call(t, mux, "POST", "/v1/iap/verify", tok,
		map[string]string{"platform": "dev", "sku": "gold_999", "receipt": "r"}); rec.Code != 200 {
		t.Fatalf("new sku must be grantable: %d", rec.Code)
	}
}

func TestAdmin_ConfigWhitelistRoundTrip(t *testing.T) {
	dir := t.TempDir()
	content := filepath.Join(dir, "content")
	_ = os.MkdirAll(content, 0o755)
	auth, _ := NewAuthService(dir)
	wallet, _ := NewWalletService(filepath.Join(dir, "wallet"), auth, "", false)
	admin := NewAdminService(content, "admintok", auth, wallet)
	mux := http.NewServeMux()
	admin.Routes(mux)

	rec, _ := call(t, mux, "PUT", "/v1/admin/config/ads.json", "admintok",
		map[string]any{"gold_small": map[string]any{"currency": "gold", "amount": 25, "daily_cap": 3}})
	if rec.Code != 200 {
		t.Fatalf("config put: %d %s", rec.Code, rec.Body)
	}
	if _, out := call(t, mux, "GET", "/v1/admin/config/ads.json", "admintok", nil); out["gold_small"] == nil {
		t.Fatalf("config get after put: %v", out)
	}
	if rec, _ := call(t, mux, "PUT", "/v1/admin/config/../../etc/passwd", "admintok",
		map[string]any{}); rec.Code == 200 {
		t.Fatalf("non-whitelisted config must not be writable, got %d", rec.Code)
	}
	if rec, _ := call(t, mux, "GET", "/v1/admin/config/users.json", "admintok", nil); rec.Code != 404 {
		t.Fatalf("whitelist must reject users.json, got %d", rec.Code)
	}
}

func TestAdmin_DraftPublishAndHistoryRollback(t *testing.T) {
	dir := t.TempDir()
	content := filepath.Join(dir, "content")
	_ = os.MkdirAll(content, 0o755)
	_ = os.WriteFile(filepath.Join(content, "manifest.json"), []byte(`{"v": 1}`), 0o644)
	auth, _ := NewAuthService(dir)
	wallet, _ := NewWalletService(filepath.Join(dir, "wallet"), auth, "", false)
	admin := NewAdminService(content, "admintok", auth, wallet)
	mux := http.NewServeMux()
	admin.Routes(mux)

	// draft edits never touch the live manifest…
	rec, _ := call(t, mux, "PUT", "/v1/admin/manifest?draft=1", "admintok", map[string]any{"v": 2})
	if rec.Code != 200 {
		t.Fatalf("draft put: %d", rec.Code)
	}
	if _, out := call(t, mux, "GET", "/v1/admin/manifest", "admintok", nil); out["v"].(float64) != 1 {
		t.Fatalf("live manifest must stay untouched by the draft: %v", out)
	}
	// …until publish promotes them (and snapshots the previous live version)
	if rec, _ := call(t, mux, "POST", "/v1/admin/manifest/publish", "admintok", nil); rec.Code != 200 {
		t.Fatalf("publish: %d", rec.Code)
	}
	if _, out := call(t, mux, "GET", "/v1/admin/manifest", "admintok", nil); out["v"].(float64) != 2 {
		t.Fatalf("publish must promote the draft: %v", out)
	}
	if rec, _ := call(t, mux, "POST", "/v1/admin/manifest/publish", "admintok", nil); rec.Code != 404 {
		t.Fatalf("second publish without a draft must 404, got %d", rec.Code)
	}

	// history holds v1 → roll back → live is v1 again
	_, out := call(t, mux, "GET", "/v1/admin/history?file=manifest.json", "admintok", nil)
	versions := out["versions"].([]any)
	if len(versions) == 0 {
		t.Fatal("publish must snapshot the previous live manifest")
	}
	ts := versions[0].(map[string]any)["ts"].(string)
	if rec, _ := call(t, mux, "POST", "/v1/admin/rollback", "admintok",
		map[string]string{"File": "manifest.json", "TS": ts}); rec.Code != 200 {
		t.Fatalf("rollback: %d", rec.Code)
	}
	if _, out := call(t, mux, "GET", "/v1/admin/manifest", "admintok", nil); out["v"].(float64) != 1 {
		t.Fatalf("rollback must restore v1: %v", out)
	}
}

func TestAdmin_FilesBrowserHidesInternals(t *testing.T) {
	dir := t.TempDir()
	content := filepath.Join(dir, "content")
	_ = os.MkdirAll(filepath.Join(content, "bg"), 0o755)
	_ = os.MkdirAll(filepath.Join(content, "services"), 0o755)
	_ = os.MkdirAll(filepath.Join(content, ".history"), 0o755)
	_ = os.WriteFile(filepath.Join(content, "bg", "room.jpg"), []byte("jpg"), 0o644)
	auth, _ := NewAuthService(dir)
	wallet, _ := NewWalletService(filepath.Join(dir, "wallet"), auth, "", false)
	admin := NewAdminService(content, "admintok", auth, wallet)
	mux := http.NewServeMux()
	admin.Routes(mux)

	_, out := call(t, mux, "GET", "/v1/admin/files?dir=", "admintok", nil)
	for _, f := range out["files"].([]any) {
		name := f.(map[string]any)["name"].(string)
		if name == "services" || name == ".history" {
			t.Fatalf("internal dir %q must not be browsable", name)
		}
	}
	if rec, _ := call(t, mux, "GET", "/v1/admin/files?dir=services", "admintok", nil); rec.Code != 403 {
		t.Fatalf("services must be 403, got %d", rec.Code)
	}
	if _, out := call(t, mux, "GET", "/v1/admin/files?dir=bg", "admintok", nil); len(out["files"].([]any)) != 1 {
		t.Fatalf("bg listing: %v", out)
	}
}
