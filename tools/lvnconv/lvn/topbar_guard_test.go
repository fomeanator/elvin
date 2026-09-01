package lvn

import (
	"fmt"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// СОСТАВ ВЕРХНЕГО БАРА РЕШАЕТСЯ В ОДНОМ МЕСТЕ.
//
// Поверхностей пять: полный ряд, ряд игровых кнопок, баблики валют, полоса
// прогресса и ловушка тапа. Видно их или нет — следствие ТРЁХ признаков разом
// (тишина воронки, игровой режим, развёрнут ли игровой бар), и решали про это
// три места, каждое со своим набором: тишина перечисляла пять поверхностей,
// смена режима — четыре, открытие и закрытие бара — по-своему в каждой ветке.
//
// Так и вышел живой дефект: выход из главы при ОТКРЫТОМ баре. Смена режима
// показывала верхний ряд, а через 200 мс конец анимации скрытия прятал его
// обратно и возвращал только в игре — в меню верхняя панель исчезала.
//
// Порог считает только УМОЛЧАНИЯ ПОСТРОЙКИ (элемент рождается скрытым). Любое
// решение о видимости обязано жить в ApplyBarVisibility.
func TestTopBarVisibilityHasOneAuthor(t *testing.T) {
	const budget = 4 // 01.09: четыре «рождается скрытым» при постройке

	root := repoRoot(t)
	path := filepath.Join(root, "unity", "Packages", "com.lvn.engine.shell", "Runtime", "LvnTopBar.cs")
	src := stripComments(string(mustRead(t, path)))
	surface := regexp.MustCompile(`_(row|gameRow|miniPills|miniProgress|tapCatcher)\.style\.display\s*=`)

	// Тело ApplyBarVisibility — единственное законное место решений.
	lines := strings.Split(src, "\n")
	inHome := false
	depth := 0
	count := 0
	var where []string
	for i, l := range lines {
		if strings.Contains(l, "private void ApplyBarVisibility()") {
			inHome, depth = true, 0
		}
		if inHome {
			depth += strings.Count(l, "{") - strings.Count(l, "}")
			if depth == 0 && strings.Contains(l, "}") {
				inHome = false
			}
			continue
		}
		if surface.MatchString(l) {
			count++
			where = append(where, fmt.Sprintf("LvnTopBar.cs:%d", i+1))
		}
	}
	atLeast(t, strings.Count(src, "_miniPills"), 3, "упоминаний поверхностей бара")
	if count > budget {
		t.Errorf("решений о видимости бара мимо ApplyBarVisibility: %d при пороге %d\n  %s\n\n"+
			"Видно поверхность или нет — следствие ТРЁХ признаков сразу (тишина, игра, развёрнут ли\n"+
			"бар). Перечисляя набор по месту, легко получить то, что уже было: в меню верхняя\n"+
			"панель исчезала, потому что конец анимации возвращал баблики только в игре.",
			count, budget, strings.Join(where, "\n  "))
	}
}
