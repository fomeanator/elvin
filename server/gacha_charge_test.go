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

// ОПУСТЕВШИЙ СУПЕР-СЕКТОР УХОДИТ С БАРАБАНА. Жеребьёвка берёт приз из списка
// оставшихся по остатку от деления — на пустом списке это паника, то есть
// отказ всей ручки. Значит колесо обязано выкинуть сектор заранее.
func TestSuperSectorLeavesTheWheelWhenPrizesRunOut(t *testing.T) {
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
	wheel := cfg.wheel([]string{"wardrobe:a"})
	if len(wheel) != 1 || wheel[0].ID != "cr5" {
		t.Fatalf("выбитый приз не убрал супер-сектор: %+v", wheel)
	}
	if len(cfg.left([]string{"wardrobe:a"})) != 0 {
		t.Fatal("выбитый приз остался в списке доступных")
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
	svc := &GachaService{roll: func() float64 { return 0.1 }}
	if got := svc.pick(left); got.SKU != "a" {
		t.Fatalf("бросок 0.1 при весах 40/1/1 должен дать обычный, дал %s", got.SKU)
	}
	svc.roll = func() float64 { return 0.97 }
	if got := svc.pick(left); got.SKU != "b" {
		t.Fatalf("бросок 0.97 при весах 40/1/1 должен дать бессмертный, дал %s", got.SKU)
	}
}
