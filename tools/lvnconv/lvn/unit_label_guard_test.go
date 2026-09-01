package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// ЕДИНИЦА ДОВОДА НАЗВАНА ТАМ, ГДЕ ИМЯ ОБЕЩАЕТ ДРУГУЮ.
//
// `VnTheme.MotionMs` берёт СЕКУНДЫ и отдаёт миллисекунды: имя говорит про
// выход, и это ловушка. `MotionMs(220)` читается как «двести двадцать
// миллисекунд», а даёт двести двадцать СЕКУНД движения — экран застывает,
// ошибки нет нигде, и виноватым выглядит загрузчик.
//
// Замер 01.09 по всему слою вида нашёл ровно один такой перевёртыш и ни одной
// настоящей путаницы единиц: `NotFoundTtlSeconds = 120` (две минуты) и
// `SpinDegreesPerSecond = 260` честны. То есть беда не в величинах, а в ОДНОМ
// имени, и лечится она подписью на месте вызова.
//
// Страж держит подпись: именованный довод стоит дешевле, чем разбор, почему
// сцена замерла на четыре минуты.
func TestSecondsAreLabelledWhereNameSaysMs(t *testing.T) {
	root := repoRoot(t)
	call := regexp.MustCompile(`VnTheme\.MotionMs\(`)
	labelled := regexp.MustCompile(`VnTheme\.MotionMs\(\s*seconds:`)

	var loud []string
	seen := 0
	for _, dir := range []string{
		"unity/Packages/com.lvn.engine/Runtime",
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine/Tests",
	} {
		_ = filepath.Walk(filepath.Join(root, dir), func(p string, i os.FileInfo, err error) error {
			if err != nil || i.IsDir() || !strings.HasSuffix(p, ".cs") {
				return err
			}
			if filepath.Base(p) == "VnTheme.cs" {
				return nil // сам дом
			}
			for n, line := range strings.Split(stripComments(string(mustRead(t, p))), "\n") {
				if call.MatchString(line) {
					seen++
					if !labelled.MatchString(line) {
						loud = append(loud, filepath.Base(p)+":"+itoa(n+1))
					}
				}
			}
			return nil
		})
	}
	sawSources(t, seen, 4, "вызовов MotionMs")

	sort.Strings(loud)
	if len(loud) > 0 {
		t.Errorf("вызов MotionMs без подписи единицы (%d):\n  %s\n\n"+
			"Имя обещает миллисекунды, довод берётся в СЕКУНДАХ. Напишите "+
			"`seconds:` — иначе следующий читатель передаст 220 и получит "+
			"четыре минуты неподвижной сцены без единой жалобы в логе.",
			len(loud), strings.Join(loud, "\n  "))
	}
}
