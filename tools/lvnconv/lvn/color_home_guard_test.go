package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
)

// Цвет из строки разбирается ровно в одном месте — UiColor.
//
// Дом уже был, но половина жильцов ходила мимо: в ОДНОМ файле `rim_color` шёл
// через UiColor, а соседний `glow_color` — напрямую через Unity. Разница
// невидима в коде и видна автору: hex без решётки один эффект красил, другой
// молча пропускал. Страж не даёт разбору расселиться снова.
func TestColorParsingLivesInOneHome(t *testing.T) {
	scanned := 0
	root := repoRoot(t)
	home := filepath.Join("Runtime", "UI", "UiColor.cs")
	var strays []string
	err := filepath.Walk(filepath.Join(root, "unity", "Packages"),
		func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			scanned++
			if strings.HasSuffix(path, home) {
				return nil
			}
			b, err := os.ReadFile(path)
			if err != nil {
				return err
			}
			for i, line := range strings.Split(string(b), "\n") {
				if strings.Contains(line, "TryParseHtmlString") {
					rel, _ := filepath.Rel(root, path)
					strays = append(strays, fmt.Sprintf("%s:%d", rel, i+1))
				}
			}
			return nil
		})
	if err != nil {
		t.Fatalf("обход пакетов: %v", err)
	}
	atLeast(t, scanned, 60, "просмотренных файлов")

	if len(strays) > 0 {
		t.Fatalf("цвет разбирают мимо UiColor:\n  %s\n\nЗовите UiColor.Parse/TryParse/FromCmd:"+
			" иначе соседние поля одной команды начнут понимать разное написание.",
			strings.Join(strays, "\n  "))
	}
}

// ОБОЛОЧКА БЕРЁТ ЦВЕТ У ТЕМЫ, А НЕ ИЗ ЧИСЕЛ.
//
// Правило простое и записано даже в памятке новому человеку: цвета и отступы
// приходят из токенов. Ломается оно тихо — литерал не ошибка, он просто
// перестаёт слушаться темы. В кибер-теме белая грань чужая, в светлой
// невидима, и увидит это игрок, а не сборка.
//
// Так и вышло: вопрос «эта штука выбрана?» задавали из пяти мест и отвечали
// пятью парами чисел — три альфы белого и три пары толщин. Серебро с бронзой
// на подиуме стояли числами в тернарнике, хотя золото рядом уже приходило
// токеном. Красный «удалить аккаунт» экран заводил свой, потому что у темы
// смыслового цвета не было вовсе — его знала только предтемовая палитра.
//
// Храповик, а не запрет: часть литералов законна — затемняющая вуаль поверх
// арта (она обязана быть почти чёрной в любой теме), палитра опознавательных
// цветов аватара и запасные значения для полей, которые автор вправе
// переназначить. Число обязано только УМЕНЬШАТЬСЯ.
func TestShellTakesColorFromTheme(t *testing.T) {
	const budget = 20
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	total, seen := 0, 0
	var where []string
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
			continue
		}
		seen++
		body := stripComments(string(mustRead(t, filepath.Join(dir, e.Name()))))
		n := strings.Count(body, "new Color(")
		if n > 0 {
			total += n
			where = append(where, fmt.Sprintf("%s ×%d", e.Name(), n))
		}
	}
	sawSources(t, seen, 40, "файлов оболочки")
	if total > budget {
		sort.Strings(where)
		t.Errorf("цветов числом в оболочке %d при пороге %d:\n  %s\n\n"+
			"Литерал не слушается темы: в кибер-теме он чужой, в светлой невидим. "+
			"Берите LvnTokens (Ok/Bad/Medal/Border/Faint/Track) или LvnStyler.Chosen.",
			total, budget, strings.Join(where, "\n  "))
	}
	if total < budget {
		t.Errorf("цветов числом стало %d — храповик опустите до этого числа "+
			"(в TestShellTakesColorFromTheme), иначе он снова пустит их обратно", total)
	}
}
