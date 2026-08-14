package main

import (
	"os"
	"path/filepath"
	"testing"
)

// База должна открываться на пустом месте и переживать повторное открытие:
// миграции идут только вперёд и ровно один раз.
func TestStoreMigratesOnceAndReopens(t *testing.T) {
	dir := t.TempDir()
	db, err := openStore(dir)
	if err != nil {
		t.Fatal(err)
	}
	var v int
	if err := db.QueryRow("PRAGMA user_version").Scan(&v); err != nil {
		t.Fatal(err)
	}
	if v != len(migrations) {
		t.Errorf("версия схемы %d, ожидалось %d", v, len(migrations))
	}
	if _, err := db.Exec(`INSERT INTO users(id, created) VALUES('u1','2026-08-14T00:00:00Z')`); err != nil {
		t.Fatal(err)
	}
	db.Close()

	again, err := openStore(dir)
	if err != nil {
		t.Fatal(err)
	}
	defer again.Close()
	var n int
	if err := again.QueryRow(`SELECT count(*) FROM users`).Scan(&n); err != nil {
		t.Fatal(err)
	}
	if n != 1 {
		t.Errorf("данные не пережили переоткрытие: %d", n)
	}
	if _, err := os.Stat(filepath.Join(dir, "lvn.db")); err != nil {
		t.Errorf("файл базы не создан: %v", err)
	}
}

// Внешние ключи в SQLite выключены по умолчанию, И МОЛЧА. Если прагма не
// доехала, мусорные связи копятся годами и всплывают на выгрузке.
func TestStoreEnforcesForeignKeys(t *testing.T) {
	db, err := openStore(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	_, err = db.Exec(`INSERT INTO user_providers(user_id, provider, subject)
	                  VALUES('несуществующий','google','x')`)
	if err == nil {
		t.Error("связь на несуществующего игрока принята — внешние ключи выключены")
	}
}
