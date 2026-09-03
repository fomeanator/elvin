package main

import (
	"os"
	"path/filepath"
	"testing"
	"time"
)

// Дневники клиента — единственное, что растёт на боксе без предела: сутки
// живого тестирования дают десятки мегабайт, а уборки не было никакой (189 МБ
// за неполный месяц, замер на проде 03.09.2026). Диск на маленьком боксе
// кончается тихо и разом, и первым перестаёт писаться не лог, а кошелёк.
func TestClientLogsKeepOnlyRecentDays(t *testing.T) {
	dir := t.TempDir()
	svc, err := NewClientLogService(dir, "t")
	if err != nil {
		t.Fatal(err)
	}
	now := time.Date(2026, 9, 3, 12, 0, 0, 0, time.UTC)
	names := map[string]bool{ // имя → должно ли пережить уборку
		now.Format("2006-01-02") + ".jsonl":                    true,
		now.AddDate(0, 0, -1).Format("2006-01-02") + ".jsonl":  true,
		now.AddDate(0, 0, -13).Format("2006-01-02") + ".jsonl": true,
		now.AddDate(0, 0, -30).Format("2006-01-02") + ".jsonl": false,
		now.AddDate(0, 0, -90).Format("2006-01-02") + ".jsonl": false,
		// Не наш файл — не наше дело: сводки и всё, что положил человек.
		"_rollup.json": true,
		"README.md":    true,
	}
	for name := range names {
		if err := os.WriteFile(filepath.Join(dir, name), []byte("{}"), 0o600); err != nil {
			t.Fatal(err)
		}
	}

	svc.pruneOldDays(now)
	for name, keep := range names {
		_, err := os.Stat(filepath.Join(dir, name))
		if keep && err != nil {
			t.Errorf("%s удалён, а должен остаться", name)
		}
		if !keep && err == nil {
			t.Errorf("%s пережил уборку", name)
		}
	}

	// Второй раз за те же сутки каталог не обходится: цена уборки не должна
	// зависеть от того, сколько устройств пишет.
	old := filepath.Join(dir, now.AddDate(0, 0, -60).Format("2006-01-02")+".jsonl")
	if err := os.WriteFile(old, []byte("{}"), 0o600); err != nil {
		t.Fatal(err)
	}
	svc.pruneOldDays(now)
	if _, err := os.Stat(old); err != nil {
		t.Errorf("уборка повторилась в те же сутки — обход каталога на каждую пачку")
	}
	// Сменились сутки — прибираемся снова.
	svc.pruneOldDays(now.AddDate(0, 0, 1))
	if _, err := os.Stat(old); err == nil {
		t.Errorf("новые сутки наступили, а уборка не прошла")
	}
}
