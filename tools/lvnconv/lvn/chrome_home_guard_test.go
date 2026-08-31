package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
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
	scanned := 0
	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell", "com.lvn.engine.services"} {
		dir := filepath.Join(root, "unity", "Packages", pkg, "Runtime")
		if _, err := os.Stat(dir); err != nil {
			continue
		}
		err := filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			scanned++
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

	// «Ничего не нашли» — законный результат этого стража, и потому он обязан
	// доказать, что вообще смотрел: иначе сбитый обход выглядит как чистота.
	atLeast(t, scanned, 200, "просмотренных файлов")

	if len(found) > 0 {
		t.Fatalf("углы скругляются вручную: %s\n"+
			"зовите LvnChrome.Round(el, r) — четыре присваивания подряд выпадают "+
			"из темы молча, и заметит это глаз на чужой палитре, а не тест",
			strings.Join(found, ", "))
	}
}

// РАСТЯЖКА ВО ВЕСЬ РОДИТЕЛЬ ИДЁТ ЧЕРЕЗ ДОМ.
//
// «Absolute плюс четыре нуля» — не пять строк стиля, а одно понятие: элемент
// занимает родителя целиком. Дом ему уже есть (LvnChrome.Stretch), и звали его
// из тридцати девяти мест — а ещё девятнадцать писали пятёрку руками. Такая
// копия не падает: она расходится. Ровно так однажды у одного слоя оказалось
// три нуля из четырёх, и полоса на дне экрана не доставала до края — заметить
// это можно было только глазами и только на нужном разрешении.
func TestРастяжкаИдётЧерезДом(t *testing.T) {
	root := repoRoot(t)

	side := regexp.MustCompile(`(?:([A-Za-z_]\w*)\.)?style\.(left|right|top|bottom)\s*=\s*0\s*;`)
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
				// Окно в четыре строки: четыре стороны одного хозяина обнулены
				// подряд — это и есть растяжка, записанная руками.
				owner, sides := "", map[string]bool{}
				same := true
				for j := i; j < len(lines) && j < i+4 && len(sides) < 4; j++ {
					for _, m := range side.FindAllStringSubmatch(lines[j], -1) {
						if owner == "" {
							owner = m[1]
						} else if owner != m[1] {
							same = false
						}
						sides[m[2]] = true
					}
				}
				if same && len(sides) == 4 {
					rel, _ := filepath.Rel(root, path)
					found = append(found, fmt.Sprintf("%s:%d", rel, i+1))
					break // одного упоминания на файл довольно
				}
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}

	if len(found) > 0 {
		sort.Strings(found)
		t.Errorf("растяжка во весь родитель записана руками — зовите LvnChrome.Stretch(el):\n  %s\n"+
			"  Пять строк вместо одной не падают, они РАСХОДЯТСЯ: три нуля из четырёх\n"+
			"  видно только глазами и только на нужном разрешении.",
			strings.Join(found, "\n  "))
	}
}
