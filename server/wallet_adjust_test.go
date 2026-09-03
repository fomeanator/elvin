package main

import "testing"

// newWalletServiceForTest — кошелёк на временном каталоге. Каталог свой у
// каждого испытания: записи игроков лежат файлами, и общий каталог связал бы
// испытания через диск.
func newWalletServiceForTest(t *testing.T) *WalletService {
	t.Helper()
	s, err := NewWalletService(t.TempDir(), testStore(t), nil, "", false, nil)
	if err != nil {
		t.Fatalf("кошелёк не завёлся: %v", err)
	}
	return s
}

// ЗНАК ПРАВКИ И СЛОВО В ИСТОРИИ — ИЗ ОДНОГО ДОВОДА.
//
// Выдача и изъятие писали их порознь: «минус к балансу» здесь, «earn» там.
// Разойдись они — баланс уменьшился бы, а история сказала «начислено»; отчёт по
// деньгам перестал бы сходиться, и найти причину можно только сверкой всех
// записей игрока.
func TestИсторияСовпадаетСоЗнакомПравки(t *testing.T) {
	s := newWalletServiceForTest(t)
	const u, cur = "u1", "soft"

	if err := s.Grant(u, cur, 100, "подарок"); err != nil {
		t.Fatalf("выдача не прошла: %v", err)
	}
	doc, _ := s.load(u)
	if doc.Balances[cur] != 100 {
		t.Fatalf("после выдачи баланс %d, ждали 100", doc.Balances[cur])
	}
	if last := doc.History[len(doc.History)-1]; last.Type != "earn" {
		t.Fatalf("выдача записана как %q — история разошлась со знаком", last.Type)
	}

	if err := s.Clawback(u, cur, 30, "ошибка начисления"); err != nil {
		t.Fatalf("изъятие не прошло: %v", err)
	}
	doc, _ = s.load(u)
	if doc.Balances[cur] != 70 {
		t.Fatalf("после изъятия баланс %d, ждали 70", doc.Balances[cur])
	}
	if last := doc.History[len(doc.History)-1]; last.Type != "spend" {
		t.Fatalf("изъятие записано как %q — история разошлась со знаком", last.Type)
	}
}

// В минус не уводим: отрицательный баланс не значит ничего, что игра умела бы
// показать, и следующая покупка спорила бы с числом, которого игрок не видел.
func TestИзъятиеНеУводитВМинус(t *testing.T) {
	s := newWalletServiceForTest(t)
	const u, cur = "u2", "soft"
	_ = s.Grant(u, cur, 10, "мало")
	if err := s.Clawback(u, cur, 1000, "перебор"); err != nil {
		t.Fatalf("изъятие не прошло: %v", err)
	}
	doc, _ := s.load(u)
	if doc.Balances[cur] != 0 {
		t.Fatalf("баланс %d — изъятие увело в минус", doc.Balances[cur])
	}
}

func TestНеположительнаяСуммаОтвергается(t *testing.T) {
	s := newWalletServiceForTest(t)
	for _, amount := range []int64{0, -5} {
		if err := s.Grant("u3", "soft", amount, "нуль"); err == nil {
			t.Fatalf("сумма %d прошла — запись в историю без движения денег", amount)
		}
	}
}
