package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// НАКЛАДНОЙ ЭКРАН ЗАКРЫВАЕТСЯ ВСЕГДА.
//
// Четыре экрана оболочки живут одним скелетом: «уже открыт — уйти», поднять
// флаг, дождаться затвора, и в `finally` — прибраться и флаг снять. Скелет
// один, а написан четырьмя копиями (`LvnOverlayScreen`, `CgGalleryScreen`,
// `PopupScreen`, `WardrobeSheet`), потому что общего предка у них нет: каждый
// сам себе `VisualElement`.
//
// Опасна в этом скелете ровно одна строка. Снять флаг НАДО в `finally`, а не
// после ожидания: отмена главы, закрытие приложения, исключение внутри — и
// экран остаётся «открытым» навсегда. Следующий вызов упрётся в собственную
// защиту от повторного входа и молча не откроется. Ни ошибки, ни лога:
// гардероб просто перестаёт открываться до перезахода (живой случай был).
//
// Сводить копии в один дом дороже, чем держать: тела у них разные по существу
// (проявление, кошелёк, просмотрщик), а совпадает именно скелет. Поэтому
// сторожим ИНВАРИАНТ, а не форму.
func TestSheetsAlwaysDropTheirOpenFlag(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	// Тело метода от `_open = true` до конца — грубо, но достаточно: нас
	// интересует, встречается ли снятие внутри finally ниже по тексту.
	raise := regexp.MustCompile(`(?m)^\s*_open\s*=\s*true\s*;`)
	drop := regexp.MustCompile(`(?s)finally\s*\{[^}]*_open\s*=\s*false`)

	seen := 0
	var loose []string
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
			continue
		}
		body := stripComments(string(mustRead(t, filepath.Join(dir, e.Name()))))
		locs := raise.FindAllStringIndex(body, -1)
		if len(locs) == 0 {
			continue
		}
		seen++
		for _, loc := range locs {
			tail := body[loc[1]:]
			if next := raise.FindStringIndex(tail); next != nil {
				tail = tail[:next[0]]
			}
			if !drop.MatchString(tail) {
				loose = append(loose, e.Name())
			}
		}
	}
	sawSources(t, seen, 3, "экранов с флагом открытия")

	sort.Strings(loose)
	if len(loose) > 0 {
		t.Errorf("флаг открытия снимается НЕ в finally: %s\n\n"+
			"Отмена, исключение или закрытие приложения посреди ожидания — и экран "+
			"остаётся «открытым» навсегда: следующий вызов упрётся в защиту от "+
			"повторного входа и молча не откроется.",
			strings.Join(loose, ", "))
	}
}

// «СКРЫТ ЛИ» — ВОПРОС К ДОМУ, А НЕ ПРИВЕДЕНИЕ ТИПА.
//
// Компилятор булевых значений НЕ приводит: `show=no` доезжает до рантайма
// СТРОКОЙ. Разбирает это дом `Lvn.LvnBool`; всякий, кто вместо него пишет
// `(bool?)c["show"]`, видит только настоящий `bool` и молча считает скрытую
// героиню видимой.
//
// Правило было написано СЕМЬ раз: движок (верно), повтор кадра на Go, три
// копии в веб-плеере — и ДВЕ в тестах. Последние опаснее всех: заглушка сцены
// и модель корпуса СЕРТИФИЦИРУЮТ поведение, и приведение заставляло их
// сертифицировать не то, что делает движок. Зелёный отчёт при разъехавшихся
// рантаймах — худшее, что может случиться со стражем.
func TestNobodyCastsShowToBool(t *testing.T) {
	root := repoRoot(t)
	bad := regexp.MustCompile(`\(bool\??\)\s*\w*\[?"?(show|hide|off)"?\]?`)
	seen := 0
	var strays []string
	err := filepath.Walk(filepath.Join(root, "unity", "Packages"),
		func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			seen++
			if strings.HasSuffix(path, "LvnBool.cs") {
				return nil // дом вправе приводить: он и есть разбор
			}
			for _, m := range bad.FindAllString(stripComments(string(mustRead(t, path))), -1) {
				strays = append(strays, filepath.Base(path)+": "+m)
			}
			return nil
		})
	if err != nil {
		t.Fatal(err)
	}
	sawSources(t, seen, 300, "файлов .cs")
	sort.Strings(strays)
	if len(strays) > 0 {
		t.Errorf("поле «да-нет» читают приведением, мимо словаря:\n  %s\n\n"+
			"Берите Lvn.LvnBool.Of(поле, умолчание): `show=no` приходит СТРОКОЙ, "+
			"и приведение молча оставляет скрытого на сцене.",
			strings.Join(strays, "\n  "))
	}
}

// КАРТИНКУ ИЗ БАЙТОВ ДЕЛАЕТ ДОМ.
//
// Обряд короткий: завести пустую текстуру и попросить её разобрать байты. Шага
// два, а мест было четыре — и одно из них на неудаче текстуру не уничтожало.
// Битый или неподдерживаемый файл оставлял пустую текстуру в памяти при каждой
// попытке: ошибки нет, лог молчит, память растёт ровно у тех, у кого контент
// побился, — то есть у самых невезучих игроков.
//
// Сторожим не «есть ли Destroy рядом», а сам вызов: `LoadImage` вне дома
// означает, что обряд снова расписали руками, а забыть уборку в нём проще, чем
// вспомнить.
func TestOnlyOneHomeDecodesImages(t *testing.T) {
	root := repoRoot(t)
	home := filepath.Join("Runtime", "Content", "AssetMemory.cs")
	seen := 0
	var strays []string
	err := filepath.Walk(filepath.Join(root, "unity", "Packages"),
		func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			seen++
			if strings.HasSuffix(path, home) || strings.Contains(path, "/Tests/") {
				return nil
			}
			body := stripComments(string(mustRead(t, path)))
			if strings.Contains(body, ".LoadImage(") {
				strays = append(strays, filepath.Base(path))
			}
			return nil
		})
	if err != nil {
		t.Fatal(err)
	}
	sawSources(t, seen, 300, "файлов .cs")
	sort.Strings(strays)
	if len(strays) > 0 {
		t.Errorf("картинку из байтов делают мимо дома: %s\n\n"+
			"Берите AssetMemory.Decode(bytes): он возвращает null и убирает за "+
			"собой, а расписанный руками обряд забывает уборку — битый файл "+
			"течёт пустой текстурой при каждой попытке.",
			strings.Join(strays, ", "))
	}
}
