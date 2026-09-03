package main

import (
	"testing"
)

// КРУГ РЕКЛАМНОГО СЧЁТА: записали — прочитали — то же самое; и пустое читается
// ПРИГОДНЫМ, а не nil.
//
// Половины писались порознь и не проверялись ни одна. Обе стороны важны
// по-разному: запись обязана отказаться при сбое (иначе пустая запись затрёт
// счёт), а чтение — вернуть карты, в которые можно писать. Запись в nil-карту
// в Go — это паника, то есть отказ всей ручки, а не одного игрока.
func TestAdsUserRoundTrip(t *testing.T) {
	s := &AdsService{db: testStore(t), dir: t.TempDir()}

	// Пустое место: файла ещё нет.
	doc, err := s.loadUser("u1")
	if err != nil {
		t.Fatalf("нового игрока не прочитать: %v", err)
	}
	if doc.Counts == nil || doc.Spent == nil || doc.Since == nil {
		t.Fatal("карты пришли nil — первая же запись уронит ручку паникой")
	}
	doc.Counts["daily"] = 2
	doc.Spent["daily"] = 30
	doc.Since["daily"] = 1700000000

	if err := s.saveUser("u1", doc); err != nil {
		t.Fatalf("запись не удалась: %v", err)
	}
	back, err := s.loadUser("u1")
	if err != nil {
		t.Fatalf("чтение после записи не удалось: %v", err)
	}
	if back.Counts["daily"] != 2 || back.Spent["daily"] != 30 || back.Since["daily"] != 1700000000 {
		t.Errorf("круг не сошёлся: %+v", back)
	}
}
