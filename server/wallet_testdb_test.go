package main

import (
	"database/sql"
	"testing"
)

// testStore — своя база на каждое испытание. Раньше кошельки лежали файлами и
// изоляцию давал t.TempDir(); теперь её даёт отдельный файл базы в том же
// временном каталоге.
func testStore(t *testing.T) *sql.DB {
	t.Helper()
	db, err := openStore(t.TempDir())
	if err != nil {
		t.Fatalf("база не завелась: %v", err)
	}
	t.Cleanup(func() { db.Close() })
	return db
}
