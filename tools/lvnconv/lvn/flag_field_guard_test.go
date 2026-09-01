package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// ПОЛЕ-ПРИЗНАК ЧИТАЕТСЯ ОДНИМ ПРАВИЛОМ.
//
// Часть полей команды — не значения, а флаги: `fx off`, `sfx off`, `fx reset`.
// Компилятор кладёт в них `true`, и рантайм читал их ПЯТЬЮ местами как «поле
// вообще есть» (`cmd["off"] != null`), а одним — как «значение истинно».
//
// Расхождение спит, пока значение приходит от компилятора. Но сырой `.lvn`
// пишут и руками, и там `"off": false` или `"off": 0` означает ровно обратное —
// а четыре из пяти мест всё равно выключат. Автор написал «не выключать» и
// получил выключено, без единой жалобы в логе. Это тот самый тихий отказ, ради
// которого заведён чтец «да-нет».
//
// Правило одно и живёт в `LvnBool.Flag`: нет поля — признака нет; есть поле —
// признак есть, если слово в нём не отменяет его явно.
func TestFlagFieldsReadOneWay(t *testing.T) {
	root := repoRoot(t)
	// Проверка на присутствие поля-признака вместо вопроса к чтецу.
	lit := regexp.MustCompile(`\w+\[\s*"(off|reset|hide|clear|force|instant|silent|mute)"\s*\]\s*(?:!=|==)\s*null`)

	var loud []string
	scanned := 0
	for _, dir := range []string{
		"unity/Packages/com.lvn.engine/Runtime",
		"unity/Packages/com.lvn.engine.shell/Runtime",
	} {
		_ = filepath.Walk(filepath.Join(root, dir), func(p string, i os.FileInfo, err error) error {
			if err != nil || i.IsDir() || !strings.HasSuffix(p, ".cs") {
				return err
			}
			if filepath.Base(p) == "LvnBool.cs" {
				return nil
			}
			scanned++
			for _, line := range strings.Split(stripComments(string(mustRead(t, p))), "\n") {
				// ПРИВЯЗКА — не чтение признака. Там спрашивают «автор вообще
				// написал это поле?», чтобы связать его с живым значением;
				// `hide: false` обязано привязаться и стать true позже.
				if strings.Contains(line, "Bind(") {
					continue
				}
				for _, m := range lit.FindAllString(line, -1) {
					loud = append(loud, filepath.Base(p)+": "+m)
				}
			}
			return nil
		})
	}
	sawSources(t, scanned, 100, "файлов движка и оболочки")

	sort.Strings(loud)
	if len(loud) > 0 {
		t.Errorf("поле-признак читается по присутствию, а не через чтеца (%d):\n  %s\n\n"+
			"Спросите Lvn.LvnBool.Flag: «off: false» в сыром .lvn означает «не "+
			"выключать», и проверка на присутствие поля выключит вопреки автору.",
			len(loud), strings.Join(loud, "\n  "))
	}
}
