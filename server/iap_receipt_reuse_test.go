package main

// ЧЕК — ПЛАТЁЖ, А НЕ КУПОН.
//
// Условие с натуры: игрок купил набор монет. Дальше три развилки, и сервер
// обязан различать их, имея на руках один и тот же чек:
//
//	он же, снова        переустановил игру или нажал «восстановить покупки» —
//	                    начислить ОДИН раз, повтор не удваивает;
//	другой аккаунт      чек ушёл приятелю текстом (или это переустановка без
//	                    привязки) — начислять нельзя, но сказать надо, ЧТО
//	                    делать: войти в тот аккаунт;
//	аккаунт удалён      игрок стёр учётку и начинает заново — его чек снова
//	                    его, иначе покупка пропала бы вместе с записью о ней.
//
// Настоящий чек проверяет Apple, поэтому проверяющий здесь подменён: цена
// вопроса не в разборе чека, а в том, что сервер делает с его номером.

import (
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func iapMux(t *testing.T) (*http.ServeMux, *AuthService, *accountEraser) {
	t.Helper()
	dir := t.TempDir()
	catalog := filepath.Join(dir, "iap-catalog.json")
	if err := os.WriteFile(catalog, []byte(`{"gold_100": {"currency": "gold", "amount": 100}}`), 0o644); err != nil {
		t.Fatal(err)
	}
	db := testStore(t)
	auth, _ := NewAuthService(dir)
	auth.db = db
	wallet, err := NewWalletService(filepath.Join(dir, "wallet"), db, auth, catalog, false, nil)
	if err != nil {
		t.Fatal(err)
	}
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
	eraser := &accountEraser{auth: auth, db: db}
	eraser.Routes(mux)
	return mux, auth, eraser
}

func TestReceiptBelongsToOneAccount(t *testing.T) {
	mux, _, _ := iapMux(t)
	tokenOf := func(device string) string {
		rec, out := call(t, mux, "POST", "/v1/auth/register", "", map[string]string{"device_id": device})
		if rec.Code != 200 {
			t.Fatalf("register %s: %d", device, rec.Code)
		}
		return out["token"].(string)
	}
	buy := func(tok string) (int, map[string]any) {
		return func() (int, map[string]any) {
			rec, out := call(t, mux, "POST", "/v1/iap/verify", tok,
				map[string]string{"platform": "appstore", "sku": "gold_100", "receipt": "valid-receipt"})
			return rec.Code, out
		}()
	}
	gold := func(out map[string]any) float64 {
		b, _ := out["balances"].(map[string]any)
		if b == nil {
			return -1
		}
		v, _ := b["gold"].(float64)
		return v
	}

	buyer := tokenOf("устройство-покупателя-0001")
	if code, out := buy(buyer); code != 200 || gold(out) != 100 {
		t.Fatalf("покупка не прошла: %d %v", code, out)
	}

	// Он же снова: восстановление после переустановки на том же аккаунте.
	if code, out := buy(buyer); code != 200 || gold(out) != 100 {
		t.Errorf("повтор своего чека: %d, золота %v (ждали 200 и ровно 100)", code, gold(out))
	}

	// Чужой аккаунт: раньше получал ещё 100 монет, и так сколько угодно раз.
	friend := tokenOf("устройство-приятеля-0002")
	rec, _ := call(t, mux, "POST", "/v1/iap/verify", friend,
		map[string]string{"platform": "appstore", "sku": "gold_100", "receipt": "valid-receipt"})
	if rec.Code != http.StatusConflict {
		t.Errorf("чужой аккаунт с тем же чеком: код %d, ждали 409 — иначе покупку раздают текстом", rec.Code)
	}
	if body := rec.Body.String(); !strings.Contains(body, "another account") {
		t.Errorf("отказ не говорит, что делать: %q", body)
	}
	if _, out := call(t, mux, "GET", "/v1/wallet", friend, nil); gold(out) > 0 {
		t.Errorf("приятелю всё-таки начислили: %v", out["balances"])
	}
}

// Удалил аккаунт — забрал свои чеки с собой.
func TestDeletedAccountReleasesItsReceipts(t *testing.T) {
	mux, _, _ := iapMux(t)
	tokenOf := func(device string) string {
		rec, out := call(t, mux, "POST", "/v1/auth/register", "", map[string]string{"device_id": device})
		if rec.Code != 200 {
			t.Fatalf("register: %d", rec.Code)
		}
		return out["token"].(string)
	}
	first := tokenOf("устройство-первое-0001")
	if rec, _ := call(t, mux, "POST", "/v1/iap/verify", first,
		map[string]string{"platform": "appstore", "sku": "gold_100", "receipt": "valid-receipt"}); rec.Code != 200 {
		t.Fatalf("покупка не прошла: %d", rec.Code)
	}
	if rec, _ := call(t, mux, "POST", "/v1/account/delete", first,
		map[string]string{"confirm": "DELETE"}); rec.Code != 200 {
		t.Fatalf("удаление аккаунта: %d", rec.Code)
	}

	again := tokenOf("устройство-новое-0002")
	rec, out := call(t, mux, "POST", "/v1/iap/verify", again,
		map[string]string{"platform": "appstore", "sku": "gold_100", "receipt": "valid-receipt"})
	if rec.Code != 200 {
		t.Fatalf("свой же чек после удаления аккаунта отклонён (%d) — покупка пропала вместе с записью", rec.Code)
	}
	if b, _ := out["balances"].(map[string]any); b == nil || b["gold"].(float64) != 100 {
		t.Errorf("после восстановления на счету %v, ждали 100", out["balances"])
	}
}
