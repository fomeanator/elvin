package main

import (
	"os"
	"path/filepath"
	"testing"
)

func testWallet(t *testing.T) *WalletService {
	t.Helper()
	dir := t.TempDir()
	catalog := filepath.Join(dir, "iap-catalog.json")
	if err := os.WriteFile(catalog, []byte(`{}`), 0o644); err != nil {
		t.Fatal(err)
	}
	db := testStore(t)
	auth, _ := NewAuthService(dir)
	auth.db = db
	wallet, err := NewWalletService(filepath.Join(dir, "wallet"), db, auth, catalog, false, nil)
	if err != nil {
		t.Fatal(err)
	}
	return wallet
}

// ЗА КРУТКУ ПЛАТЯТ ПО-НАСТОЯЩЕМУ: пустой кошелёк получает отказ, а не крутку
// в долг.
//
// Живой случай: списание за прокрут шло через Clawback — административное
// изъятие, которое УПИРАЕТСЯ В НОЛЬ и молча отвечает «готово». Игрок с нулём
// кристаллов крутил барабан бесконечно, и на проде это заметили только по
// журналу кошелька: «spend 50 crystals» при балансе 0.
func TestChargeRefusesWhenShort(t *testing.T) {
	w := testWallet(t)
	const user = "u_test_charge_0001"

	if err := w.Charge(user, "crystals", 50, "gacha spin"); err == nil {
		t.Fatal("пустой кошелёк заплатил за крутку: списание не проверило баланс")
	}
	if err := w.Grant(user, "crystals", 120, "test"); err != nil {
		t.Fatal(err)
	}
	if err := w.Charge(user, "crystals", 50, "gacha spin"); err != nil {
		t.Fatalf("денег хватало, а платёж отказал: %v", err)
	}
	doc := w.AdminLoad(user)
	if doc.Balances["crystals"] != 70 {
		t.Fatalf("после платы осталось %d, ожидалось 70", doc.Balances["crystals"])
	}
	// Второй платёж дороже остатка: в минус кошелёк не уходит.
	if err := w.Charge(user, "crystals", 100, "gacha spin"); err == nil {
		t.Fatal("заплатили больше, чем было")
	}
	if got := w.AdminLoad(user).Balances["crystals"]; got != 70 {
		t.Fatalf("отказ изменил баланс: %d", got)
	}
}

// СУПЕР-СЕКТОР НЕ ПУСТЕЕТ: выбитый приз остаётся в наборе (Илья 15.09:
// «неправильно убирать скин, если выбил — лента лысеет»), повтор — копия.
// Сектор уходит с барабана только если призов в наборе нет вовсе.
func TestSuperSectorStaysWhilePrizesExist(t *testing.T) {
	cfg := gachaConfig{
		Sectors: []gachaSector{
			{ID: "super", Kind: "super", Weight: 10},
			{ID: "cr5", Kind: "currency", Currency: "crystals", Amount: 5, Weight: 90},
		},
		Prizes: []gachaPrize{{SKU: "wardrobe:a", Label: "Наряд"}},
	}
	if len(cfg.wheel(nil)) != 2 {
		t.Fatal("пока приз есть, супер-сектор обязан быть на барабане")
	}
	if wheel := cfg.wheel([]string{"wardrobe:a"}); len(wheel) != 2 {
		t.Fatalf("выбитый приз убрал супер-сектор, а должен остаться копией: %+v", wheel)
	}
	if len(cfg.left([]string{"wardrobe:a"})) != 0 {
		t.Fatal("выбитый приз остался в списке ещё не полученных")
	}
	empty := gachaConfig{Sectors: cfg.Sectors}
	if wheel := empty.wheel(nil); len(wheel) != 1 || wheel[0].ID != "cr5" {
		t.Fatalf("без призов супер-сектор обязан уйти: %+v", wheel)
	}
}

// КОПИИ ПОМНЯТСЯ И ПЕРЕЖИВАЮТ ЗАПИСЬ: строка «sku:n» туда и обратно.
func TestGachaCopiesRoundTrip(t *testing.T) {
	m := map[string]int{"wardrobe:a": 2, "wardrobe:b": 1, "zero": 0}
	text := copiesText(m)
	if text != "wardrobe:a:2,wardrobe:b:1" {
		t.Fatalf("копии в строку: %q", text)
	}
	back := parseCopies(text)
	if back["wardrobe:a"] != 2 || back["wardrobe:b"] != 1 || len(back) != 2 {
		t.Fatalf("копии из строки: %+v", back)
	}
	if len(parseCopies("")) != 0 {
		t.Fatal("пустая строка — пустые копии")
	}
}

// TestGachaPickWeighsRarity — бессмертный приз с весом 1 против обычного с
// весом 40: при броске у нижней границы выпадает обычный, у верхней — бессмертный,
// и клиенту уходит вес каждого приза.
func TestGachaPickWeighsRarity(t *testing.T) {
	cfg := gachaConfig{
		Prizes: []gachaPrize{
			{SKU: "a", Rarity: "common"},
			{SKU: "b", Rarity: "immortal"},
			{SKU: "c"}, // без ступени — вес 1
		},
		RarityWeights: map[string]float64{"common": 40, "immortal": 1},
	}
	left := cfg.left(nil)
	if left[0].Weight != 40 || left[1].Weight != 1 || left[2].Weight != 1 {
		t.Fatalf("веса призов: %+v", left)
	}
	if all := cfg.all(); len(all) != 3 || all[0].Weight != 40 {
		t.Fatalf("весь набор с весами: %+v", all)
	}
	svc := &GachaService{roll: func() float64 { return 0.1 }}
	if got := svc.pick(left); got.SKU != "a" {
		t.Fatalf("бросок 0.1 при весах 40/1/1 должен дать обычный, дал %s", got.SKU)
	}
	svc.roll = func() float64 { return 0.97 }
	if got := svc.pick(left); got.SKU != "b" {
		t.Fatalf("бросок 0.97 при весах 40/1/1 должен дать бессмертный, дал %s", got.SKU)
	}
}
