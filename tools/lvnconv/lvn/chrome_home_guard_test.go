package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Скругление всех четырёх углов — работа Рамочника (LvnChrome.Round), а не
// четыре строки на месте.
//
// Признак, которым это нашлось, стоит отдельного слова: не дубль кода, а ВЫЗОВ,
// КОТОРОГО НЕ СТАЛО там, где он ожидался. Дом выделен, задокументирован, у него
// сто с лишним вызовов — и рядом три десятка мест, делающих ту же работу
// руками. Ни одно из них не выглядит нарушением: четыре присваивания подряд
// читаются как «просто стиль».
//
// Цена не в строках. Скругление у панели, кнопки и карточки обязано меняться
// ОДНИМ движением — темой; место, вписавшее 12 руками, из темы выпадает молча,
// и заметит это не тест, а глаз на чужой палитре.
//
// Проверка окном в четыре строки: именно так эти места и пишутся. Стиль,
// добытый как IStyle (авторский слой строит узлы по описанию из .lvn), под
// правило не попадает — Round принимает элемент, а не стиль.
func TestFullRoundingGoesThroughChrome(t *testing.T) {
	root := repoRoot(t)

	corners := []string{
		"borderTopLeftRadius", "borderTopRightRadius",
		"borderBottomLeftRadius", "borderBottomRightRadius",
	}

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
			if base == "LvnChrome.cs" {
				return nil // сам дом
			}
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			lines := strings.Split(string(b), "\n")
			for i := range lines {
				end := i + 4
				if end > len(lines) {
					end = len(lines)
				}
				win := strings.Join(lines[i:end], "\n")
				all := true
				for _, c := range corners {
					if !strings.Contains(win, c) {
						all = false
						break
					}
				}
				if !all {
					continue
				}
				// Стиль без элемента: у автора узел строится по описанию, и
				// элемента под рукой нет — Round тут не применим.
				if strings.Contains(win, "s.borderTopLeftRadius") {
					continue
				}
				found = append(found, fmt.Sprintf("%s:%d", base, i+1))
				break
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}

	if len(found) > 0 {
		t.Fatalf("углы скругляются вручную: %s\n"+
			"зовите LvnChrome.Round(el, r) — четыре присваивания подряд выпадают "+
			"из темы молча, и заметит это глаз на чужой палитре, а не тест",
			strings.Join(found, ", "))
	}
}
