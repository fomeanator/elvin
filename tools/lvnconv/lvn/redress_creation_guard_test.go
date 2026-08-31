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

// ИСТОЧНИК ОТДАЁТСЯ В МОМЕНТ СОЗДАНИЯ — даже там, где переодевание есть.
//
// Два стража рядом (см. redress_guard_test.go) держат случаи, где подпись
// застывает НАВЕРНЯКА: `new Label(LvnWords…)` и словарная строка в классе, у
// которого переодевания нет вовсе. Между ними осталась щель: класс, у которого
// Redress ЕСТЬ, вправе поставить подпись строкой при создании — и почти всегда
// это ошибка, просто не видная стражам.
//
// Она стоила: тринадцать кнопок оболочки («Играть», «Загрузить», «Закрыть»,
// «Восстановить», «Забрать») создавались готовой строкой и оставались на
// прежнем языке навсегда — переодевание до них не доходило, потому что ссылок
// на них никто не держал: это местные переменные. А те трое, до кого доходило,
// платили за это отдельной строкой в Redress, которую полагалось не забыть.
//
// Правило простое: источник отдаётся ОДИН РАЗ, при создании. Тогда переодевание
// не нужно ни писать, ни помнить. Подпись, зависящая от состояния, делает
// источником само состояние (`() => armed ? «Точно?» : «Удалить»`) и зовёт
// LvnRedress.Refresh — это ещё и чинит рассинхрон, при котором смена языка
// возвращала взведённой кнопке невзведённый вид.
func TestИсточникОтдаётсяПриСоздании(t *testing.T) {
	root := repoRoot(t)

	// Бут идёт до загрузки словаря — там ещё нечего перечитывать.
	allowed := map[string]bool{"LvnRedress.cs": true, "BootScreen.cs": true}

	bad := []*regexp.Regexp{
		regexp.MustCompile(`new Button\([^)]*\)\s*\{\s*text\s*=\s*(Lvn\.Content\.)?LvnWords\.`),
		regexp.MustCompile(`new Button\s*\{\s*text\s*=\s*(Lvn\.Content\.)?LvnWords\.`),
		regexp.MustCompile(`new Label\(\s*(Lvn\.Content\.)?LvnWords\.`),
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
			if allowed[base] {
				return nil
			}
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			for i, line := range strings.Split(string(b), "\n") {
				for _, re := range bad {
					if re.MatchString(line) {
						found = append(found, fmt.Sprintf("%s:%d: %s", base, i+1, strings.TrimSpace(line)))
						break
					}
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
		t.Errorf("подпись создана готовой строкой из словаря — при смене языка она останется прежней.\n"+
			"  Отдай источник при создании: LvnRedress.Bind(new Button(…), () => LvnWords.Of(…)).\n"+
			"  Зависит от состояния — источником сделай состояние\n"+
			"  (() => armed ? «Точно?» : «Удалить») и зови LvnRedress.Refresh при его смене.\n  %s",
			strings.Join(found, "\n  "))
	}
}
