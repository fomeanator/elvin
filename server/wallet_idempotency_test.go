package main

import "testing"

// The money invariant: a mutation whose RESPONSE was lost gets replayed by the
// client's offline queue with the SAME op_id — it must apply exactly once.
// A double-applied spend silently eats a purchase; a double earn mints money.
func TestWallet_OpIdMakesMutationsIdempotent(t *testing.T) {
	mux, _ := servicesMux(t, false)
	_, tok := register(t, mux)

	earn := map[string]any{"currency": "gold", "amount": 100, "reason": "test", "op_id": "op-earn-1"}
	rec, _ := call(t, mux, "POST", "/v1/wallet/earn", tok, earn)
	if rec.Code != 200 {
		t.Fatalf("earn: %d %s", rec.Code, rec.Body)
	}
	// the replay (lost response → client retried the exact same op)
	rec, out := call(t, mux, "POST", "/v1/wallet/earn", tok, earn)
	if rec.Code != 200 {
		t.Fatalf("earn replay: %d %s", rec.Code, rec.Body)
	}
	bal := out["balances"].(map[string]any)["gold"].(float64)
	if bal != 100 {
		t.Fatalf("earn applied twice: balance %v, want 100", bal)
	}

	spend := map[string]any{"currency": "gold", "amount": 60, "reason": "wardrobe",
		"sku": "wardrobe:hero:armor:chain", "op_id": "op-spend-1"}
	rec, _ = call(t, mux, "POST", "/v1/wallet/spend", tok, spend)
	if rec.Code != 200 {
		t.Fatalf("spend: %d %s", rec.Code, rec.Body)
	}
	rec, out = call(t, mux, "POST", "/v1/wallet/spend", tok, spend)
	if rec.Code != 200 {
		t.Fatalf("spend replay must be OK (not a 409): %d %s", rec.Code, rec.Body)
	}
	bal = out["balances"].(map[string]any)["gold"].(float64)
	if bal != 40 {
		t.Fatalf("spend applied twice: balance %v, want 40", bal)
	}
	inv := out["inventory"].(map[string]any)["wardrobe:hero:armor:chain"].(float64)
	if inv != 1 {
		t.Fatalf("sku granted %v times, want exactly 1", inv)
	}

	// a DIFFERENT op with the same shape is a new purchase — it applies
	spend2 := map[string]any{"currency": "gold", "amount": 40, "reason": "wardrobe", "op_id": "op-spend-2"}
	rec, out = call(t, mux, "POST", "/v1/wallet/spend", tok, spend2)
	if rec.Code != 200 {
		t.Fatalf("second spend: %d %s", rec.Code, rec.Body)
	}
	if bal := out["balances"].(map[string]any)["gold"].(float64); bal != 0 {
		t.Fatalf("fresh op must apply: balance %v, want 0", bal)
	}

	// the idempotency memory survives a service restart (it rides the doc)
	rec, out = call(t, mux, "POST", "/v1/wallet/earn", tok, earn)
	if rec.Code != 200 || out["balances"].(map[string]any)["gold"].(float64) != 0 {
		t.Fatalf("earn replay after everything must still be a no-op: %d %v", rec.Code, out["balances"])
	}
}
