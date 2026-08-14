package main

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// fakePayments — ведомость из трёх записей вместо каталога файлов.
type fakePayments struct {
	buys   []walletPurchase
	prices map[string]struct {
		v   float64
		cur string
	}
}

func (f *fakePayments) Purchases() []walletPurchase { return f.buys }
func (f *fakePayments) Price(sku string) (float64, string, bool) {
	p, ok := f.prices[sku]
	if !ok {
		return 0, "", false
	}
	return p.v, p.cur, true
}

func moneyFixture(t *testing.T, events string, pay *fakePayments) moneyReport {
	t.Helper()
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "2026-08-02.jsonl"), []byte(events), 0o644); err != nil {
		t.Fatal(err)
	}
	s := &AnalyticsService{dir: dir, rollups: newRollupStore(dir), adminToken: "t", payments: pay}
	mux := http.NewServeMux()
	s.Routes(mux)
	rec, _ := call(t, mux, "GET", "/v1/analytics/money?from=2026-08-02&to=2026-08-02", "t", nil)
	if rec.Code != http.StatusOK {
		t.Fatalf("код %d: %s", rec.Code, rec.Body.String())
	}
	var rep moneyReport
	if err := json.Unmarshal(rec.Body.Bytes(), &rep); err != nil {
		t.Fatal(err)
	}
	return rep
}

// Четыре игрока, двое платят: конверсия 50%, ARPU считается от ВСЕХ активных,
// ARPPU — только от платящих. Спутать их значит принять решение о рекламе по
// числу, которое к рекламе отношения не имеет.
func TestMoneyBasicArithmetic(t *testing.T) {
	events := ""
	for _, u := range []string{"a", "b", "c", "d"} {
		events += `{"name":"boot","ts":"2026-08-02T10:00:00Z","user":"` + u + `"}` + "\n"
	}
	pay := &fakePayments{
		buys: []walletPurchase{
			{User: "a", TS: "2026-08-02T11:00:00Z", SKU: "gold_100"},
			{User: "a", TS: "2026-08-02T12:00:00Z", SKU: "gold_550"},
			{User: "b", TS: "2026-08-02T13:00:00Z", SKU: "gold_100"},
		},
		prices: map[string]struct {
			v   float64
			cur string
		}{
			"gold_100": {0.99, "USD"},
			"gold_550": {4.99, "USD"},
		},
	}
	rep := moneyFixture(t, events, pay)

	if rep.Active != 4 || rep.Payers != 2 || rep.Purchases != 3 {
		t.Fatalf("активные/платящие/покупки: %d/%d/%d", rep.Active, rep.Payers, rep.Purchases)
	}
	if rep.Revenue != 6.97 { // 0.99 + 4.99 + 0.99
		t.Errorf("выручка %v, ожидалось 6.97", rep.Revenue)
	}
	if rep.Conversion != 0.5 {
		t.Errorf("конверсия %v, ожидалось 0.5", rep.Conversion)
	}
	if rep.ARPU != 1.74 { // 6.97 / 4
		t.Errorf("ARPU %v, ожидалось 1.74", rep.ARPU)
	}
	if rep.ARPPU != 3.49 { // 6.97 / 2
		t.Errorf("ARPPU %v, ожидалось 3.49", rep.ARPPU)
	}
	if rep.AvgCheck != 2.32 { // 6.97 / 3
		t.Errorf("средний чек %v, ожидалось 2.32", rep.AvgCheck)
	}
	if rep.RefCurrency != "USD" {
		t.Errorf("валюта отчёта %q", rep.RefCurrency)
	}
	// Сверху — пак, который принёс больше всего денег, а не который чаще купили.
	if len(rep.BySKU) == 0 || rep.BySKU[0].SKU != "gold_550" {
		t.Errorf("по SKU сортировка по выручке: %+v", rep.BySKU)
	}
	// Двое платящих: «a» купил через час после первого события, «b» через три.
	// При чётном числе берётся верхняя медиана — то же соглашение, что в
	// замере загрузки; для «сколько ждать до первой покупки» это осторожная
	// сторона, и лучше пусть она будет одна на весь отчёт.
	if rep.ToFirstPurchase == nil || rep.ToFirstPurchase.N != 2 || rep.ToFirstPurchase.MedianHours != 3 {
		t.Errorf("время до первой покупки: %+v", rep.ToFirstPurchase)
	}
}

// Пак без цены — не бесплатный пак. Ноль здесь занизил бы выручку молча, а
// молча заниженная выручка ведёт к выводу «магазин не работает».
func TestMoneyUnpricedIsNotZero(t *testing.T) {
	events := `{"name":"boot","ts":"2026-08-02T10:00:00Z","user":"a"}` + "\n"
	pay := &fakePayments{
		buys: []walletPurchase{
			{User: "a", TS: "2026-08-02T11:00:00Z", SKU: "gold_100"},
			{User: "a", TS: "2026-08-02T11:30:00Z", SKU: "секретный_пак"},
		},
		prices: map[string]struct {
			v   float64
			cur string
		}{"gold_100": {0.99, "USD"}},
	}
	rep := moneyFixture(t, events, pay)
	if rep.Revenue != 0.99 {
		t.Errorf("в выручку попал пак без цены: %v", rep.Revenue)
	}
	if rep.UnpricedBuys != 1 || len(rep.Unpriced) != 1 || rep.Unpriced[0] != "секретный_пак" {
		t.Errorf("пак без цены обязан быть назван: %+v", rep.Unpriced)
	}
	if !strings.Contains(strings.Join(rep.Notes, " "), "price_value") {
		t.Errorf("отчёт должен подсказать, как это чинить: %v", rep.Notes)
	}
}

// Доллары с рублями не складывают. Сумма в смешанной валюте выглядит
// правдоподобно и потому опаснее пустой.
func TestMoneyRefusesToMixCurrencies(t *testing.T) {
	events := `{"name":"boot","ts":"2026-08-02T10:00:00Z","user":"a"}` + "\n"
	pay := &fakePayments{
		buys: []walletPurchase{
			{User: "a", TS: "2026-08-02T11:00:00Z", SKU: "usd_a"},
			{User: "a", TS: "2026-08-02T11:10:00Z", SKU: "usd_b"},
			{User: "a", TS: "2026-08-02T11:20:00Z", SKU: "rub"},
		},
		prices: map[string]struct {
			v   float64
			cur string
		}{
			"usd_a": {1, "USD"}, "usd_b": {2, "USD"}, "rub": {399, "RUB"},
		},
	}
	rep := moneyFixture(t, events, pay)
	if rep.RefCurrency != "USD" || rep.Revenue != 3 {
		t.Errorf("рубли попали в долларовую сумму: %v %v", rep.RefCurrency, rep.Revenue)
	}
	if len(rep.OtherCurrencies) != 1 || rep.OtherCurrencies[0] != "rub" {
		t.Errorf("пак в другой валюте обязан быть назван: %+v", rep.OtherCurrencies)
	}
}

// Тестовая выдача с устройства, которого аналитика не видела, не должна давать
// конверсию выше ста процентов.
func TestMoneyConversionCountsOnlyActivePayers(t *testing.T) {
	events := `{"name":"boot","ts":"2026-08-02T10:00:00Z","user":"a"}` + "\n"
	pay := &fakePayments{
		buys: []walletPurchase{
			{User: "a", TS: "2026-08-02T11:00:00Z", SKU: "p"},
			{User: "призрак", TS: "2026-08-02T11:00:00Z", SKU: "p"},
		},
		prices: map[string]struct {
			v   float64
			cur string
		}{"p": {1, "USD"}},
	}
	rep := moneyFixture(t, events, pay)
	if rep.Conversion > 1 {
		t.Errorf("конверсия %v больше единицы", rep.Conversion)
	}
	if rep.Payers != 2 {
		t.Errorf("платящих всё равно двое: %d", rep.Payers)
	}
}

// Витринную строку разбираем только когда числа не дали — и не выдумываем
// цену там, где валюта незнакома.
func TestPriceParsing(t *testing.T) {
	cases := []struct {
		in   iapProduct
		v    float64
		cur  string
		ok   bool
		name string
	}{
		{iapProduct{Price: "$4.99"}, 4.99, "USD", true, "доллар"},
		{iapProduct{Price: "399 ₽"}, 399, "RUB", true, "рубль"},
		{iapProduct{Price: "4,99 EUR"}, 4.99, "EUR", true, "запятая"},
		{iapProduct{Price: "1.299.00 ₽"}, 1299, "RUB", true, "разделитель тысяч"},
		{iapProduct{Price: "бесплатно"}, 0, "", false, "без цены"},
		{iapProduct{Price: "4.99"}, 0, "", false, "без валюты"},
		{iapProduct{Price: "$4.99", PriceValue: 5.49, PriceCurrency: "USD"}, 5.49, "USD", true, "число важнее витрины"},
		{iapProduct{Price: "399 ₽", PriceValue: 399}, 399, "RUB", true, "валюта из витрины"},
	}
	for _, c := range cases {
		v, cur, ok := priceOf(c.in)
		if v != c.v || cur != c.cur || ok != c.ok {
			t.Errorf("%s: получено %v/%q/%v, ожидалось %v/%q/%v", c.name, v, cur, ok, c.v, c.cur, c.ok)
		}
	}
}
