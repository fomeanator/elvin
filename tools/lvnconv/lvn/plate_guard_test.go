package lvn

import (
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// ПЛАШКУ СОБИРАЕТ СТИЛИЗАТОР, А НЕ ЭКРАН.
//
// «Плашка такого цвета с такими чернилами» — это четыре строки подряд: цвет
// фона, цвет текста, снять рамку, скруглить. Дом для неё есть давно
// (LvnStyler.Plate), и всё же одиннадцать экранов писали её сами.
//
// Копия здесь ломает не сразу и не громко: забыть можно ОДНУ строку из
// четырёх, и кнопка отличается от соседней рамкой, которую никто не снял, —
// на правке это не видно, на экране видно всем.
func TestPlateIsBuiltByTheStyler(t *testing.T) {
	root := repoRoot(t)
	bg := regexp.MustCompile(`style\.backgroundColor\s*=`)
	ink := regexp.MustCompile(`style\.color\s*=`)
	scanned := 0
	var offenders []string
	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell"} {
		dir := filepath.Join(root, "unity/Packages", pkg, "Runtime")
		for _, f := range csFiles(t, dir) {
			if strings.HasSuffix(f, "LvnStyler.cs") {
				continue // сам дом и есть то место, где эти строки законны
			}
			scanned++
			lines := strings.Split(stripComments(string(mustRead(t, f))), "\n")
			for i := range lines {
				w := strings.Join(lines[i:min(i+6, len(lines))], "\n")
				if strings.Contains(w, "ClearBorder(") && strings.Contains(w, "LvnChrome.Round(") &&
					bg.MatchString(w) && ink.MatchString(w) {
					offenders = append(offenders, filepath.Base(f))
					break
				}
			}
		}
	}
	atLeast(t, scanned, 150, "просмотренных файлов")
	if len(offenders) > 0 {
		t.Errorf("плашку собирают руками (%d): %s\n\n"+
			"Это роль, а не набор строк: LvnStyler.Plate(el, плашка, чернила, скругление). "+
			"Забытая строка из четырёх даёт кнопку с чужой рамкой — на правке не видно, на экране видно.",
			len(offenders), strings.Join(offenders, ", "))
	}
}
