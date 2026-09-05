package main

// «УДАЛИТЬ АККАУНТ» ДОХОДИТ ДО ДЕНЕГ.
//
// Требование магазинов простое: игрок стирает свои данные сам, из приложения.
// Кошельки, журнал операций, дневная награда, рекорды и отзывы переехали в
// базу, а удаление по-прежнему сносило учётку и ФАЙЛЫ сервисов — те самые,
// которых после переезда уже нет. Замер 04.09 по проводу: после «удалить
// аккаунт» в базе оставались кошелёк, баланс, две записи журнала, дневная
// награда и рекорд в таблице лидеров — с именем игрока, попросившего себя
// забыть.

import (
	"database/sql"
	"net/http"
	"os"
	"path/filepath"
	"testing"
)

func countRows(t *testing.T, db *sql.DB, table, userID string) int {
	t.Helper()
	var n int
	if err := db.QueryRow(`SELECT count(*) FROM `+table+` WHERE user_id = ?`, userID).Scan(&n); err != nil {
		t.Fatalf("%s: %v", table, err)
	}
	return n
}

func TestAccountDeleteClearsTheDatabase(t *testing.T) {
	dir := t.TempDir()
	catalog := filepath.Join(dir, "iap-catalog.json")
	_ = os.WriteFile(catalog, []byte(`{"gold_100": {"currency": "gold", "amount": 100}}`), 0o644)
	db := testStore(t)
	auth, _ := NewAuthService(dir)
	auth.db = db
	wallet, err := NewWalletService(filepath.Join(dir, "wallet"), db, auth, catalog, true, nil)
	if err != nil {
		t.Fatal(err)
	}
	boards, err := NewLeaderboardService(filepath.Join(dir, "leaderboards"), db, auth)
	if err != nil {
		t.Fatal(err)
	}
	daily, err := NewDailyService(filepath.Join(dir, "daily"), db, auth, wallet, "")
	if err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	auth.Routes(mux)
	wallet.Routes(mux)
	boards.Routes(mux)
	daily.Routes(mux)
	(&accountEraser{auth: auth, db: db}).Routes(mux)

	rec, out := call(t, mux, "POST", "/v1/auth/register", "", map[string]string{"device_id": "уходящий-игрок-0001"})
	if rec.Code != 200 {
		t.Fatalf("register: %d", rec.Code)
	}
	uid, tok := out["user_id"].(string), out["token"].(string)

	// Играем: покупка, ежедневная награда, рекорд.
	if rec, _ := call(t, mux, "POST", "/v1/iap/verify", tok,
		map[string]string{"platform": "gplay", "sku": "gold_100", "receipt": "любой"}); rec.Code != 200 {
		t.Fatalf("покупка: %d", rec.Code)
	}
	call(t, mux, "POST", "/v1/daily/claim", tok, nil)
	call(t, mux, "POST", "/v1/leaderboard/submit", tok,
		map[string]any{"board": "проба", "score": 42, "name": "Игрок"})

	lived := map[string]int{}
	for _, table := range []string{"wallets", "wallet_balances", "wallet_ledger", "daily_claims", "leaderboard"} {
		lived[table] = countRows(t, db, table, uid)
	}
	if lived["wallets"] == 0 || lived["wallet_ledger"] == 0 || lived["leaderboard"] == 0 {
		t.Fatalf("игрок не наследил, стирать нечего: %v", lived)
	}

	if rec, _ := call(t, mux, "POST", "/v1/account/delete", tok, map[string]string{"confirm": "DELETE"}); rec.Code != 200 {
		t.Fatalf("удаление: %d", rec.Code)
	}

	for _, table := range []string{
		"wallets", "wallet_balances", "wallet_inventory", "wallet_ledger",
		"wallet_ops", "wallet_receipts", "iap_receipt_owner",
		"leaderboard", "daily_claims", "ad_users", "ad_placements", "feedback",
	} {
		if n := countRows(t, db, table, uid); n != 0 {
			t.Errorf("после «удалить аккаунт» в %s осталось %d строк(и) игрока", table, n)
		}
	}
	var users int
	if err := db.QueryRow(`SELECT count(*) FROM users WHERE id = ?`, uid).Scan(&users); err != nil {
		t.Fatal(err)
	}
	if users != 0 {
		t.Errorf("учётка осталась в базе (%d)", users)
	}
}
