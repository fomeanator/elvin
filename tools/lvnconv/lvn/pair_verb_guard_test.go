package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// ПАРА, КОТОРУЮ ВЫЗЫВАЮЩИЙ ПОМНИТ НАИЗУСТЬ, — ЭТО ПРОПУЩЕННЫЙ ГЛАГОЛ.
//
// Замер 01.09 по всей оболочке: самой частой последовательностью двух вызовов
// оказалась `LvnAir.PadX` + `LvnAir.PadY` — 39 раз в 29 файлах. Второй была
// «скруглить + обвести» (`LvnChrome.Round` + `.Border`/`.ClearBorder`) — 22
// раза в двадцати. Оба случая описывают ОДНО решение: отступ прямоугольника и
// край прямоугольника. Порознь их держали не по смыслу, а потому что дом умел
// каждое по отдельности и не умел вместе.
//
// Цена такой пары не в лишней строке. Пара, которую держит вызывающий, — это
// место, где можно написать вторую строку с ДРУГИМ элементом, с другой
// величиной или не написать вовсе; и никто не заметит, потому что каждая
// строка по отдельности верна.
//
// Теперь есть `LvnAir.Pad(el, x, y)` и `LvnChrome.Frame(el, r, …)`. Страж
// держит, чтобы пары не вернулись.
func TestNoHandHeldPairs(t *testing.T) {
	root := repoRoot(t)
	pairs := []struct{ name, first, second string }{
		{"LvnAir.PadX + PadY → LvnAir.Pad(el, x, y)", `LvnAir\.PadX\(`, `LvnAir\.PadY\(`},
		{"LvnChrome.Round + Border → LvnChrome.Frame", `LvnChrome\.Round\(`, `LvnChrome\.Border\(`},
		{"LvnChrome.Round + ClearBorder → LvnChrome.Frame", `LvnChrome\.Round\(`, `LvnChrome\.ClearBorder\(`},
	}

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
			base := filepath.Base(p)
			// Сами дома складывают эти вызовы внутри себя — им можно.
			if base == "LvnAir.cs" || base == "LvnChrome.cs" {
				return nil
			}
			scanned++
			lines := strings.Split(stripComments(string(mustRead(t, p))), "\n")
			for _, pr := range pairs {
				a := regexp.MustCompile(pr.first)
				b := regexp.MustCompile(pr.second)
				for n := 0; n+1 < len(lines); n++ {
					// Соседние строки ИЛИ одна строка с двумя вызовами.
					same := a.MatchString(lines[n]) && b.MatchString(lines[n])
					next := a.MatchString(lines[n]) && b.MatchString(lines[n+1])
					back := b.MatchString(lines[n]) && a.MatchString(lines[n+1])
					if same || next || back {
						loud = append(loud, base+":"+itoa(n+1)+"  "+pr.name)
					}
				}
			}
			return nil
		})
	}
	sawSources(t, scanned, 80, "файлов слоя вида")

	sort.Strings(loud)
	if len(loud) > 0 {
		t.Errorf("пара вызовов, которую держит вызывающий (%d):\n  %s\n\n"+
			"Это одно решение, записанное двумя строками: вторую можно написать с "+
			"другим элементом, с другой величиной или забыть вовсе — и каждая по "+
			"отдельности останется верной. Позовите готовый глагол.",
			len(loud), strings.Join(loud, "\n  "))
	}
}
