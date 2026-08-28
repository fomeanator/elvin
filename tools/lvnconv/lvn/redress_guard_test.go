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

// Класс, который НЕ умеет переодеваться, не вправе ставить словарную подпись
// строкой — переодеть её будет некому.
//
// Первый страж (выше) ловит `new Label(LvnWords…)` — случай, когда источник
// теряется в момент создания. Живой баг 28.08 прошёл мимо него: подписи ставили
// не так. Заголовки разделов уезжали в помощник (`SectionTitle("Профиль")`),
// кнопки — через инициализатор (`new Button { text = LvnWords… }`), вкладки
// магазина — присваиванием. Три разные записи одной ошибки, и ни одна не
// выглядела как та, которую страж знал в лицо.
//
// Различать «застынет» и «переживёт» по одной строке нельзя: та же запись
// законна внутри Rebuild, который экран зовёт на каждое переодевание. Зато
// можно судить по КЛАССУ: если ни один его файл не упоминает Redress, значит
// пересобирать его подписи не будет никто и никогда — тут запись виновна без
// оговорок.
//
// Класс собирается из партиалов по имени файла до первой точки
// (WardrobeSheet.Strip.cs → WardrobeSheet): у листа гардероба Redress лежит в
// одном файле, а подписи — в другом, и пофайловая проверка обвинила бы его зря.
func TestFrozenLabelsOnlyWhereSomethingRedresses(t *testing.T) {
	root := repoRoot(t)

	// text = LvnWords.… — присваивание или инициализатор, всё равно строка.
	frozen := regexp.MustCompile(`text\s*=\s*(Lvn\.Content\.)?LvnWords\.(Of|Pick|Name)\(`)

	byClass := map[string][]string{} // класс → его файлы
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
			cls := strings.SplitN(base, ".", 2)[0]
			byClass[cls] = append(byClass[cls], path)
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", pkg, err)
		}
	}

	var found []string
	for _, files := range byClass {
		bodies := make(map[string]string, len(files))
		dresses := false
		for _, p := range files {
			b, err := os.ReadFile(p)
			if err != nil {
				t.Fatalf("чтение %s: %v", p, err)
			}
			bodies[p] = string(b)
			if strings.Contains(bodies[p], "Redress") {
				dresses = true
			}
		}
		if dresses {
			continue // подписи пересоберёт он сам
		}
		for p, body := range bodies {
			for i, ln := range strings.Split(body, "\n") {
				if frozen.MatchString(ln) {
					found = append(found, fmt.Sprintf("%s:%d", filepath.Base(p), i+1))
				}
			}
		}
	}

	sort.Strings(found)
	if len(found) > 0 {
		t.Fatalf("подпись из словаря застынет — класс не умеет переодеваться: %s\n"+
			"либо привяжите её (LvnRedress.Bind), либо дайте классу Redress(), "+
			"который пересоберёт этот кусок: иначе смена языка пройдёт мимо",
			strings.Join(found, ", "))
	}
}
