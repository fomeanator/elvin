package main

import (
	"os"
	"path/filepath"
	"testing"
	"time"
)

// Индекс версий обходится на каждый опрос клиента. Читать при этом надо
// только изменившиеся файлы — иначе один игрок держит ядро прод-бокса
// занятым хэшированием всего контента (замер 03.09.2026: 1,8 с из каждых
// двух).
func TestVersionIndexRereadsOnlyChangedFiles(t *testing.T) {
	dir := t.TempDir()
	plantTree(t, dir, "scripts/a.lvn", "bg/x.png", "manifest.json")
	s := &server{content: dir}

	first := s.computeVersions(true)
	if s.hashReads != 3 {
		t.Fatalf("первый обход прочитал %d файлов, ждали 3", s.hashReads)
	}
	again := s.computeVersions(true)
	if s.hashReads != 3 {
		t.Errorf("повторный обход без правок перечитал файлы: %d чтений", s.hashReads)
	}
	for k, v := range first {
		if again[k] != v {
			t.Errorf("хэш %s поплыл без правок", k)
		}
	}

	// Правка с другой длиной: перечитывается ровно она.
	a := filepath.Join(dir, "scripts", "a.lvn")
	future := time.Now().Add(5 * time.Second)
	if err := os.WriteFile(a, []byte("data:scripts/a.lvn but longer"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.Chtimes(a, future, future); err != nil {
		t.Fatal(err)
	}
	third := s.computeVersions(true)
	if s.hashReads != 4 {
		t.Errorf("после правки одного файла чтений %d, ждали 4", s.hashReads)
	}
	if third["scripts/a.lvn"] == first["scripts/a.lvn"] {
		t.Errorf("правка не отразилась в индексе")
	}
	if third["bg/x.png"] != first["bg/x.png"] {
		t.Errorf("нетронутый файл сменил хэш")
	}

	// Правка на месте той же длины: mtime решает.
	x := filepath.Join(dir, "bg", "x.png")
	if err := os.WriteFile(x, []byte("DATA:bg/x.png"), 0o644); err != nil {
		t.Fatal(err)
	}
	later := future.Add(5 * time.Second)
	if err := os.Chtimes(x, later, later); err != nil {
		t.Fatal(err)
	}
	fourth := s.computeVersions(true)
	if s.hashReads != 5 {
		t.Errorf("правка той же длины не перечитана: чтений %d", s.hashReads)
	}
	if fourth["bg/x.png"] == first["bg/x.png"] {
		t.Errorf("правка той же длины не отразилась в индексе")
	}

	// Удалённый файл уходит из индекса, а не живёт в кэше вечно.
	if err := os.Remove(a); err != nil {
		t.Fatal(err)
	}
	if _, ok := s.computeVersions(true)["scripts/a.lvn"]; ok {
		t.Errorf("удалённый файл остался в индексе")
	}
}
