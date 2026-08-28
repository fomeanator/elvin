package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// Секунды превращает в подпись один дом.
//
// Одно и то же ожидание игрок видел двумя видами: плашка кошелька писала
// «1:12:30», окно кассира про ту же энергию — «+1 через 1 ч 12 мин». Виды оба
// законны, но правило перевода стояло дважды. С сохранениями было хуже: список
// слотов писал «27.08 14:32», экран новеллы про ТОТ ЖЕ слот — «2 h ago».
//
// Признак работы «перевести секунды в подпись» — деление на 3600 или остаток по
// 60 рядом с форматированием. Внутри дома это работа, снаружи — вторая копия
// правила, которая разойдётся с первой молча.
func TestDurationFormattingHasOneHome(t *testing.T) {
	root := repoRoot(t)

	// Своё: арифметика времени, не подпись. Каждая строка — с причиной.
	allowed := map[string]string{
		"LvnTimeWords.cs":  "сам дом",
		"LvnNetRoom.cs":    "срок ожидания комнаты — величина, а не подпись",
		"LvnStageClock.cs": "барьеры сцены считаются в секундах и наружу не показываются",
	}

	divRe := regexp.MustCompile(`/ 3600|% 3600|% 60\b`)
	formatRe := regexp.MustCompile(`\{[a-z]\}|\{[a-z]:00\}|ToString\("`)
	var found []string

	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell", "com.lvn.engine.services"} {
		dir := filepath.Join(root, "unity", "Packages", pkg, "Runtime")
		if _, err := os.Stat(dir); err != nil {
			continue
		}
		err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			base := filepath.Base(path)
			if _, ok := allowed[base]; ok {
				return nil
			}
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			lines := strings.Split(string(b), "\n")
			for i, ln := range lines {
				if !divRe.MatchString(ln) {
					continue
				}
				// Подпись собирается тут же или соседней строкой.
				window := ln
				if i+1 < len(lines) {
					window += "\n" + lines[i+1]
				}
				if formatRe.MatchString(window) {
					found = append(found, base)
					break
				}
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}

	sort.Strings(found)
	if len(found) > 0 {
		t.Fatalf("секунды переводят в подпись мимо дома: %s\n"+
			"возьмите LvnTimeWords.Clock/Coarse/Ago/Stamp — вид выбирает экран, "+
			"а правило перевода одно на всех",
			strings.Join(found, ", "))
	}
}
