package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// ПЕРЕЕЗД ОСТАЛЬНОГО СОСТОЯНИЯ — таблицы лидеров, ежедневки, счёт рекламы.
// На проде он тоже случится один раз и с настоящими данными игроков.
func TestServiceFilesMoveIntoTheStore(t *testing.T) {
	root := t.TempDir()
	db := testStore(t)
	put := func(dir, name string, v any) {
		if err := os.MkdirAll(filepath.Join(root, dir), 0o755); err != nil {
			t.Fatal(err)
		}
		raw, _ := json.Marshal(v)
		if err := os.WriteFile(filepath.Join(root, dir, name+".json"), raw, 0o644); err != nil {
			t.Fatal(err)
		}
	}

	put("leaderboards", "quiz", []lbEntry{
		{User: "u1", Name: "Аня", Score: 90, Updated: "2026-08-01T00:00:00Z"},
		{User: "u2", Name: "Боря", Score: 50, Updated: "2026-08-02T00:00:00Z"},
	})
	put("daily", "u1", dailyDoc{LastClaim: "2026-09-02", Streak: 4})
	put("ads", "u1", adsUserDoc{
		Day:    "2026-09-02",
		Counts: map[string]int{"crystals_ad": 2},
		Spent:  map[string]int{"crystals_ad": 1},
		Since:  map[string]int64{"crystals_ad": 1788000000},
	})

	if n, err := importLeaderboardFiles(db, filepath.Join(root, "leaderboards")); err != nil || n != 1 {
		t.Fatalf("таблицы: перенесено %d, ошибка %v", n, err)
	}
	if n, err := importDailyFiles(db, filepath.Join(root, "daily")); err != nil || n != 1 {
		t.Fatalf("ежедневки: перенесено %d, ошибка %v", n, err)
	}
	if n, err := importAdsFiles(db, filepath.Join(root, "ads")); err != nil || n != 1 {
		t.Fatalf("реклама: перенесено %d, ошибка %v", n, err)
	}

	board, err := lbLoad(db, "quiz")
	if err != nil || len(board) != 2 {
		t.Fatalf("таблица переехала не та: %d записей, ошибка %v", len(board), err)
	}
	if rankOf(board, "u1") != 1 || rankOf(board, "u2") != 2 {
		t.Errorf("порядок в таблице сбился: %+v", board)
	}

	day, err := dailyLoad(db, "u1")
	if err != nil || day.LastClaim != "2026-09-02" || day.Streak != 4 {
		t.Errorf("ежедневка переехала не та: %+v, ошибка %v", day, err)
	}

	ad, err := adsLoad(db, "u1")
	if err != nil {
		t.Fatal(err)
	}
	if ad.Day != "2026-09-02" || ad.Counts["crystals_ad"] != 2 ||
		ad.Spent["crystals_ad"] != 1 || ad.Since["crystals_ad"] != 1788000000 {
		t.Errorf("счёт рекламы переехал не тот: %+v", ad)
	}

	// Каталоги отставлены в сторону, а не удалены: это единственная прежняя копия.
	for _, dir := range []string{"leaderboards", "daily", "ads"} {
		if _, err := os.Stat(filepath.Join(root, dir)); err == nil {
			t.Errorf("%s остался под прежним именем — следующий старт перенёс бы его снова", dir)
		}
		if _, err := os.Stat(filepath.Join(root, dir+".migrated")); err != nil {
			t.Errorf("%s: прежние файлы должны остаться рядом с пометкой: %v", dir, err)
		}
	}
}

// Пустое состояние читается ПРИГОДНЫМ, а не nil: запись в nil-карту в Go —
// паника, то есть отказ всей ручки, а не одного игрока.
func TestEmptyServiceStateIsUsable(t *testing.T) {
	db := testStore(t)
	ad, err := adsLoad(db, "нет-такого")
	if err != nil {
		t.Fatal(err)
	}
	if ad.Counts == nil || ad.Spent == nil || ad.Since == nil {
		t.Fatal("карты пришли nil — первая же запись уронит ручку")
	}
	if day, err := dailyLoad(db, "нет-такого"); err != nil || day == nil || day.Streak != 0 {
		t.Fatalf("пустая ежедневка: %+v, ошибка %v", day, err)
	}
	if board, err := lbLoad(db, "пустая"); err != nil || len(board) != 0 {
		t.Fatalf("пустая таблица: %d записей, ошибка %v", len(board), err)
	}
}
