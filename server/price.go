package main

// Цена ЧИСЛОМ — то, без чего нельзя сложить выручку.
//
// В каталоге цена живёт витринной строкой ("$4.99"): её показывают на карточке
// пака, а биллинг всё равно ведёт стор. Для отчёта этого мало — сложить строки
// нельзя, а без суммы нет ни ARPU, ни среднего чека.
//
// Правильный ответ — явное числовое поле price_value в каталоге. Но требовать
// его прямо сейчас значит получить пустой отчёт на живом каталоге из
// пятнадцати паков, поэтому строка разбирается как запасной вариант. Разбор
// намеренно узкий: понятные символы валют и обычные разделители. Что не
// разобралось — не ноль, а «цена неизвестна»: ноль тут соврал бы, будто пак
// раздают даром, и занизил бы выручку молча.
//
// Отдельно, чтобы не было иллюзий: это ОЦЕНКА ПО ПРАЙСУ, а не выручка из
// стора. Стор берёт свою комиссию (~30%), пересчитывает цену в местную валюту
// и удерживает налоги. Сходиться с выпиской это не обязано и не будет.

import (
	"strconv"
	"strings"
)

// currencySymbols — символ/код → трёхбуквенный код. Список короткий
// намеренно: лучше честно сказать «не знаю такую валюту», чем угадать.
var currencySymbols = []struct {
	token string
	code  string
}{
	{"$", "USD"}, {"usd", "USD"},
	{"€", "EUR"}, {"eur", "EUR"},
	{"£", "GBP"}, {"gbp", "GBP"},
	{"₽", "RUB"}, {"rub", "RUB"}, {"руб", "RUB"}, {"р.", "RUB"},
	{"¥", "JPY"}, {"jpy", "JPY"},
	{"₸", "KZT"}, {"kzt", "KZT"},
	{"₴", "UAH"}, {"uah", "UAH"},
}

// priceOf возвращает цену пака числом и валюту. ok=false означает «цена
// неизвестна» — вызывающий обязан не считать это нулём.
func priceOf(p iapProduct) (value float64, currency string, ok bool) {
	// Явное поле всегда важнее разбора витрины: витрину пишут для человека.
	if p.PriceValue > 0 {
		cur := strings.ToUpper(strings.TrimSpace(p.PriceCurrency))
		if cur == "" {
			// Валюту не назвали, но цену дали: берём её из витринной строки,
			// иначе складывать будет нечего.
			if _, c, found := parsePriceDisplay(p.Price); found {
				cur = c
			}
		}
		if cur == "" {
			return 0, "", false
		}
		return p.PriceValue, cur, true
	}
	return parsePriceDisplay(p.Price)
}

// parsePriceDisplay разбирает витринную строку: "$4.99", "399 ₽", "4,99 EUR".
func parsePriceDisplay(s string) (value float64, currency string, ok bool) {
	s = strings.TrimSpace(strings.ReplaceAll(s, " ", " ")) // неразрывный пробел
	if s == "" {
		return 0, "", false
	}
	low := strings.ToLower(s)
	for _, c := range currencySymbols {
		if strings.Contains(low, c.token) {
			currency = c.code
			break
		}
	}
	if currency == "" {
		return 0, "", false
	}

	// Оставляем только цифры и разделители дробной части.
	var num strings.Builder
	for _, r := range s {
		switch {
		case r >= '0' && r <= '9':
			num.WriteRune(r)
		case r == '.' || r == ',':
			num.WriteRune('.')
		}
	}
	raw := num.String()
	if raw == "" {
		return 0, "", false
	}
	// "1.299.00" — разделитель тысяч вперемешку с дробным: берём последнюю
	// точку за дробную, остальные выкидываем.
	if n := strings.Count(raw, "."); n > 1 {
		i := strings.LastIndex(raw, ".")
		raw = strings.ReplaceAll(raw[:i], ".", "") + raw[i:]
	}
	v, err := strconv.ParseFloat(raw, 64)
	if err != nil || v <= 0 {
		return 0, "", false
	}
	return v, currency, true
}
