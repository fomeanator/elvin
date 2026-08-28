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

// Подпись, собранная из словаря, обязана помнить свой источник.
//
// В UI Toolkit текст задаётся строкой: `label.text = "Профиль"`. После этого
// узнать, из какого ключа он собран, невозможно — и при смене языка подпись
// остаётся прежней. Так и выходило: чинили главную, оставался гардероб; чинили
// гардероб, оставался профиль. Список мест всегда отставал от их числа.
//
// Правило: подпись, собранная из LvnWords, создаётся через LvnRedress.Bind —
// тогда глобальная перерисовка перечитает её сама, и экрану не нужно ни хранить
// ссылку, ни объявлять интерфейс.
//
// Страж смотрит на конструкцию `new Label(LvnWords…)`: это ровно тот случай,
// когда источник известен в момент создания и теряется сразу после.
func TestDictionaryLabelsRememberTheirSource(t *testing.T) {
	root := repoRoot(t)

	// Подпись, которая не переживает смены языка по своей природе.
	allowed := map[string]string{
		"LvnRedress.cs": "сам дом",
		"BootScreen.cs": "бут идёт до загрузки словаря: там ещё нечего перечитывать",
	}

	// new Label(LvnWords.…) — источник есть и тут же теряется.
	bare := regexp.MustCompile(`new Label\(\s*LvnWords\.(Of|Pick|Name)\(`)
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
			for i, ln := range strings.Split(string(b), "\n") {
				if bare.MatchString(ln) {
					found = append(found, fmt.Sprintf("%s:%d", base, i+1))
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
		t.Fatalf("подпись из словаря не помнит источник: %s\n"+
			"оберните её: LvnRedress.Bind(new Label(), () => LvnWords.Of(…)) — "+
			"иначе при смене языка она останется прежней, а найдёт это игрок, а не тест",
			strings.Join(found, ", "))
	}
}
