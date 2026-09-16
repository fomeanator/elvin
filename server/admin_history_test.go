package main

import (
	"os"
	"path/filepath"
	"testing"
)

// ИСТОРИЯ ЗНАЕТ, КТО И ЧТО (TR-95): снимок пишет автора и пометку рядом с
// собой, а сводка изменений считает строки и тронутые верхние ключи.
func TestHistorySnapshotKeepsAuthorAndDeltaCounts(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "gacha.json"), []byte("{\n  \"spin_price\": 50,\n  \"prizes\": []\n}\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	snapshotHistory(dir, "gacha.json", "ilya", "gacha.json из панели")
	hist := filepath.Join(dir, ".history", "gacha.json")
	entries, _ := os.ReadDir(hist)
	var ts string
	for _, e := range entries {
		if filepath.Ext(e.Name()) == ".bak" {
			ts = e.Name()[:len(e.Name())-4]
		}
	}
	if ts == "" {
		t.Fatal("снимок не записан")
	}
	meta := readHistoryMeta(hist, ts)
	if meta.Who != "ilya" || meta.Note != "gacha.json из панели" || meta.At == "" {
		t.Fatalf("автор и пометка не сохранились: %+v", meta)
	}
	d := historyDeltaOf([]byte("{\n  \"spin_price\": 50,\n  \"prizes\": []\n}\n"),
		[]byte("{\n  \"spin_price\": 30,\n  \"prizes\": [],\n  \"cases\": []\n}\n"))
	if d.Added != 3 || d.Removed != 2 {
		t.Fatalf("строки посчитаны неверно: %+v", d)
	}
	if len(d.Keys) != 2 || d.Keys[0] != "cases" || d.Keys[1] != "spin_price" {
		t.Fatalf("тронутые ключи: %+v", d.Keys)
	}
}
