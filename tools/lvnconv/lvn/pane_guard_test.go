package lvn

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// ПАНЕЛЬ МЕНЮ САМА СЕБЯ ЗАПОМИНАЕТ.
//
// У игрового меню одна ручка перерисовки: поле `_pane` — «что показать заново,
// когда огранку пересоберут» (смена темы, шрифта, поворот экрана). Заполняет
// его КАЖДЫЙ показ первой своей строкой: `_pane = ShowStats;`.
//
// Самозапись — не украшение, а единственный способ не перекладывать
// координацию на зовущего: иначе каждый, кто открывает панель, обязан помнить
// про вторую половину дела. Цена ошибки не падение и не краснота тестов:
// панель, забывшая себя, при первой же пересборке подменяется ПРЕДЫДУЩЕЙ —
// игрок, сменивший шрифт в галерее, оказывается в главном меню.
//
// Сегодня помнят все десять. Страж нужен одиннадцатой.
func TestEveryMenuPaneRemembersItself(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity", "Packages", "com.lvn.engine", "Runtime", "UI")
	sig := regexp.MustCompile(`^\s*private (?:void|System\.Action) (Show\w+|Confirm\w+|\w*Notice)\s*\(`)

	found := 0
	var forgetful []string
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	for _, e := range entries {
		if !strings.HasPrefix(e.Name(), "StageMenu") || !strings.HasSuffix(e.Name(), ".cs") {
			continue
		}
		lines := strings.Split(string(mustRead(t, filepath.Join(dir, e.Name()))), "\n")
		for i, l := range lines {
			m := sig.FindStringSubmatch(l)
			if m == nil {
				continue
			}
			found++
			// Тело до закрывающей скобки метода: самозапись стоит первой строкой,
			// но искать её стоит по всему телу — порядок не догма.
			body := ""
			for j := i; j < len(lines) && j < i+60; j++ {
				body += lines[j] + "\n"
				if strings.TrimRight(lines[j], " \t") == "        }" && j > i+1 {
					break
				}
			}
			if !strings.Contains(body, "_pane =") {
				forgetful = append(forgetful, fmt.Sprintf("%s:%d — %s", e.Name(), i+1, m[1]))
			}
		}
	}
	atLeast(t, found, 8, "показов панели")
	if len(forgetful) > 0 {
		t.Errorf("панели, не запомнившие себя (%d):\n  %s\n\n"+
			"Первой строкой показа: `_pane = ИмяПоказа;`. Иначе при пересборке огранки\n"+
			"(смена темы, шрифта, поворот) откроется ПРЕДЫДУЩАЯ панель — не ошибка, а\n"+
			"подмена: игрок, сменивший шрифт в галерее, окажется в главном меню.",
			len(forgetful), strings.Join(forgetful, "\n  "))
	}
}
