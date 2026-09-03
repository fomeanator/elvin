package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// ПЕРЕЕЗД ДЕНЕГ ИЗ ФАЙЛОВ В БАЗУ — то, что на проде произойдёт ровно один раз
// и с настоящими кошельками. Проверяется именно это: ничего не потеряно,
// ничего не удвоено, прежние файлы остались лежать.
func TestWalletFilesMoveIntoTheStore(t *testing.T) {
	dir := t.TempDir()
	walletDir := filepath.Join(dir, "wallet")
	if err := os.MkdirAll(walletDir, 0o755); err != nil {
		t.Fatal(err)
	}
	write := func(user string, doc walletDoc) {
		raw, _ := json.MarshalIndent(doc, "", "  ")
		if err := os.WriteFile(filepath.Join(walletDir, user+".json"), raw, 0o644); err != nil {
			t.Fatal(err)
		}
	}
	write("u1", walletDoc{
		Version:   7,
		Balances:  map[string]int64{"gold": 120, "energy": 2},
		Inventory: map[string]int64{"wardrobe:hero:armor": 1},
		History: []walletEntry{
			{TS: "2026-08-01T10:00:00Z", Type: "iap", SKU: "gold_100", Reason: "store", Title: "cold", Author: "elvin"},
			{TS: "2026-08-02T10:00:00Z", Type: "spend", Currency: "gold", Amount: 30, Reason: "wardrobe"},
		},
		Transactions: []string{"txn-1"},
		Regen:        map[string]int64{"energy": 1788000000},
		AppliedOps:   []string{"op-a", "op-b"},
	})
	write("u2", walletDoc{Version: 1, Balances: map[string]int64{"gold": 5}})

	db := testStore(t)
	moved, err := importWalletFiles(db, walletDir)
	if err != nil {
		t.Fatalf("переезд сорвался: %v", err)
	}
	if moved != 2 {
		t.Fatalf("перенесено %d кошельков, ждали 2", moved)
	}

	got, err := walletLoad(db, "u1", nil)
	if err != nil {
		t.Fatal(err)
	}
	if got.Balances["gold"] != 120 || got.Balances["energy"] != 2 {
		t.Errorf("баланс переехал не тот: %v", got.Balances)
	}
	if got.Inventory["wardrobe:hero:armor"] != 1 {
		t.Errorf("инвентарь переехал не тот: %v", got.Inventory)
	}
	if got.Version != 7 {
		t.Errorf("номер записи стал %d, был 7", got.Version)
	}
	if len(got.History) != 2 || got.History[0].Type != "iap" || got.History[1].Type != "spend" {
		t.Errorf("журнал переехал не тот (и не в том порядке): %v", got.History)
	}
	if got.History[0].Title != "cold" || got.History[0].Author != "elvin" {
		t.Errorf("привязка покупки к новелле потеряна: %+v", got.History[0])
	}
	if len(got.Transactions) != 1 || got.Transactions[0] != "txn-1" {
		t.Errorf("чеки переехали не те: %v", got.Transactions)
	}
	if got.Regen["energy"] != 1788000000 {
		t.Errorf("отметка восполнения потеряна: %v", got.Regen)
	}
	if len(got.AppliedOps) != 2 {
		t.Errorf("метки идемпотентности переехали не все: %v", got.AppliedOps)
	}

	// Прежние файлы НЕ удалены: на момент переезда это единственная копия.
	if _, err := os.Stat(walletDir); err == nil {
		t.Error("каталог с файлами остался под прежним именем — следующий старт перенёс бы их снова")
	}
	if _, err := os.Stat(walletDir + ".migrated"); err != nil {
		t.Errorf("прежние файлы должны остаться рядом с пометкой: %v", err)
	}

	// Повторный переезд ничего не удваивает: каталога уже нет.
	again, err := importWalletFiles(db, walletDir)
	if err != nil || again != 0 {
		t.Errorf("повторный переезд: перенесено %d, ошибка %v", again, err)
	}
	after, _ := walletLoad(db, "u1", nil)
	if after.Balances["gold"] != 120 || len(after.History) != 2 {
		t.Errorf("повторный переезд тронул деньги: %v / %d записей", after.Balances, len(after.History))
	}
}

// ЖУРНАЛ БОЛЬШЕ НЕ ОБРЕЗАЕТСЯ. Файл держал последние сто записей, и по этой
// же истории считаются выплаты авторам: у активного игрока покупки полугодовой
// давности просто исчезали. Игроку по-прежнему едет хвост, но в базе лежит всё.
func TestWalletLedgerKeepsEverythingWhileTheAnswerStaysShort(t *testing.T) {
	db := testStore(t)
	const user = "u1"
	doc, err := walletLoad(db, user, nil)
	if err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 250; i++ {
		doc.Balances["gold"] += 1
		doc.History = append(doc.History, walletEntry{
			TS: "2026-09-03T00:00:00Z", Type: "earn", Currency: "gold", Amount: 1, Reason: "quest",
		})
		if err := walletSave(db, user, doc); err != nil {
			t.Fatalf("запись %d: %v", i, err)
		}
	}
	var stored int
	if err := db.QueryRow(`SELECT COUNT(*) FROM wallet_ledger WHERE user_id = ?`, user).Scan(&stored); err != nil {
		t.Fatal(err)
	}
	if stored != 250 {
		t.Errorf("в журнале %d записей, ждали 250 — история снова обрезается", stored)
	}
	fresh, err := walletLoad(db, user, nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(fresh.History) != walletHistoryView {
		t.Errorf("игроку едет %d записей, ждали %d", len(fresh.History), walletHistoryView)
	}
	if fresh.Balances["gold"] != 250 {
		t.Errorf("баланс %d, ждали 250", fresh.Balances["gold"])
	}
}

// Ведомость покупок — один запрос вместо обхода всех файлов, и она видит
// покупки ЛЮБОЙ давности, а не только те, что уцелели в хвосте.
func TestWalletPurchasesSeeEveryIapEvenOldOnes(t *testing.T) {
	db := testStore(t)
	doc, _ := walletLoad(db, "u1", nil)
	doc.History = append(doc.History, walletEntry{
		TS: "2026-01-01T00:00:00Z", Type: "iap", SKU: "gold_100", Title: "cold"})
	for i := 0; i < 150; i++ { // топим старую покупку под хвостом
		doc.History = append(doc.History, walletEntry{
			TS: "2026-09-03T00:00:00Z", Type: "earn", Currency: "gold", Amount: 1, Reason: "quest"})
	}
	if err := walletSave(db, "u1", doc); err != nil {
		t.Fatal(err)
	}
	other, _ := walletLoad(db, "u2", nil)
	other.History = append(other.History, walletEntry{
		TS: "2026-02-02T00:00:00Z", Type: "iap", SKU: "gold_500", Title: "agency"})
	if err := walletSave(db, "u2", other); err != nil {
		t.Fatal(err)
	}

	got := walletPurchases(db)
	if len(got) != 2 {
		t.Fatalf("покупок %d, ждали 2: %+v", len(got), got)
	}
	var users []string
	for _, p := range got {
		users = append(users, p.User+":"+p.SKU+":"+p.Title)
	}
	joined := strings.Join(users, " ")
	if !strings.Contains(joined, "u1:gold_100:cold") || !strings.Contains(joined, "u2:gold_500:agency") {
		t.Errorf("ведомость покупок собралась не та: %s", joined)
	}
}

// Сделка — целиком или никак: деньги, метка идемпотентности и запись журнала
// ложатся вместе. Проверяется тем, что сорванная запись не оставляет следов.
func TestWalletSaveIsAllOrNothing(t *testing.T) {
	db := testStore(t)
	doc, _ := walletLoad(db, "u1", nil)
	doc.Balances["gold"] = 100
	doc.History = append(doc.History, walletEntry{TS: "t", Type: "earn", Currency: "gold", Amount: 100, Reason: "seed"})
	doc.AppliedOps = append(doc.AppliedOps, "op-1")
	if err := walletSave(db, "u1", doc); err != nil {
		t.Fatal(err)
	}

	// Ломаем одну таблицу и пробуем записать ещё раз.
	if _, err := db.Exec("DROP TABLE wallet_ledger"); err != nil {
		t.Fatal(err)
	}
	doc.Balances["gold"] = 999
	doc.History = append(doc.History, walletEntry{TS: "t2", Type: "earn", Currency: "gold", Amount: 899, Reason: "bad"})
	doc.AppliedOps = append(doc.AppliedOps, "op-2")
	if err := walletSave(db, "u1", doc); err == nil {
		t.Fatal("запись в сломанное хранилище обязана дать ошибку")
	}

	var gold int64
	if err := db.QueryRow(`SELECT amount FROM wallet_balances WHERE user_id = 'u1' AND currency = 'gold'`).Scan(&gold); err != nil {
		t.Fatal(err)
	}
	if gold != 100 {
		t.Errorf("сорванная сделка сдвинула баланс: %d, ждали прежние 100", gold)
	}
	var ops int
	if err := db.QueryRow(`SELECT COUNT(*) FROM wallet_ops WHERE user_id = 'u1'`).Scan(&ops); err != nil {
		t.Fatal(err)
	}
	if ops != 1 {
		t.Errorf("метка от сорванной сделки осталась: меток %d, ждали 1", ops)
	}
}

// Сорвавшийся на середине переезд: часть кошельков уже в базе, каталог не
// переименован, сервер стартует снова. Журнал ДОПИСЫВАЕТСЯ, поэтому второй
// заход по тем же файлам удвоил бы записи — а по журналу считают выплаты
// авторам, то есть удвоение это неверные деньги, а не лишние строки.
func TestWalletImportDoesNotDoubleOnRetry(t *testing.T) {
	db := testStore(t)
	dir := t.TempDir()
	raw := []byte(`{"version":3,"balances":{"gold":250},"history":[
		{"ts":"2026-09-01T10:00:00Z","type":"earn","currency":"gold","amount":300,"reason":"grant"},
		{"ts":"2026-09-01T11:00:00Z","type":"spend","currency":"gold","amount":50,"reason":"choice","title":"tour","author":"elvin"}
	]}`)
	if err := os.WriteFile(filepath.Join(dir, "u1.json"), raw, 0o600); err != nil {
		t.Fatal(err)
	}

	if n, err := importWalletFiles(db, dir); err != nil || n != 1 {
		t.Fatalf("первый переезд: перенесено %d, ошибка %v", n, err)
	}

	// Второй заход по ТЕМ ЖЕ файлам (каталог кладём обратно под прежним именем,
	// как было бы, если бы переименование не случилось).
	if err := os.Rename(dir+".migrated", dir); err != nil {
		t.Fatal(err)
	}
	if n, err := importWalletFiles(db, dir); err != nil || n != 0 {
		t.Fatalf("повторный переезд: перенесено %d (ждали 0), ошибка %v", n, err)
	}

	var entries, balance int64
	if err := db.QueryRow(`SELECT COUNT(*) FROM wallet_ledger WHERE user_id = 'u1'`).Scan(&entries); err != nil {
		t.Fatal(err)
	}
	if entries != 2 {
		t.Errorf("в журнале %d записей, а в файле было 2 — переезд их удвоил", entries)
	}
	if err := db.QueryRow(`SELECT amount FROM wallet_balances WHERE user_id = 'u1' AND currency = 'gold'`).Scan(&balance); err != nil {
		t.Fatal(err)
	}
	if balance != 250 {
		t.Errorf("баланс %d, а в файле было 250", balance)
	}
}
