package lvn

import (
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// ЭКРАНЫ РАССВЕТА КРАСЯТСЯ ИЗ ОДНОГО МЕСТА.
//
// Вуаль загрузки и выбор сервера показываются РАНЬШЕ темы: тема приезжает с
// манифестом на второй секунде, вуаль встаёт на семидесятой миллисекунде, а
// выбор сервера по определению идёт до манифеста — он и решает, откуда его
// брать. Цвета им нужны свои, и это не небрежность, а задача.
//
// Решали её раздельно, и вышло то, что выходит всегда: два экрана, идущие
// ПОДРЯД, красились по разным палитрам — вуаль холодной сталью на #101015,
// выбор сервера тёплым золотом на #1c1c21. Двадцать литералов на два файла, и
// при передаче эстафеты весь тон сдвигался.
//
// Теперь роли названы один раз в `LvnDawn`. Страж держит это: литеральный цвет
// в этих двух файлах снова разведёт их по разным палитрам, и заметит это
// человек, а не сборка.
func TestDawnScreensTakeColorFromOneHome(t *testing.T) {
	root := repoRoot(t)
	shell := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime")

	// Полупрозрачная вуаль-затемнение — не цвет палитры, а приём: белое или
	// чёрное с альфой. Такие оставляем.
	scrim := regexp.MustCompile(`new Color\(\s*(?:0f|1f|0\.0f|1\.0f)\s*,\s*(?:0f|1f|0\.0f|1\.0f)\s*,\s*(?:0f|1f|0\.0f|1\.0f)\s*,`)
	lit := regexp.MustCompile(`new Color\([^)]*\)`)

	var loud []string
	seen := 0
	for _, name := range []string{"BootVeil.cs", "ServerSelectScreen.cs"} {
		body := stripComments(string(mustRead(t, filepath.Join(shell, name))))
		seen++
		for _, m := range lit.FindAllString(body, -1) {
			if scrim.MatchString(m) {
				continue
			}
			loud = append(loud, name+": "+m)
		}
	}
	sawSources(t, seen, 2, "экранов рассвета")

	if len(loud) > 0 {
		t.Errorf("цвет числом на экране, который показывается ДО темы (%d):\n  %s\n\n"+
			"Эти экраны идут подряд: свой литерал здесь — это сдвиг тона при "+
			"передаче эстафеты. Роль есть в LvnDawn; нет нужной — заведите её там.",
			len(loud), strings.Join(loud, "\n  "))
	}
}
