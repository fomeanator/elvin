package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// МЕНЮ ГЛАВЫ ЗНАЕТ, ЧТО ПОКАЗЫВАЕТ.
//
// Меню строит панель заново на каждый переход и нигде не помнило, КАКУЮ:
// «где мы» знала только история вызовов. Пока меню лишь открывали и закрывали,
// этого хватало — а язык переключают ПРЯМО В НЁМ, и перерисовать себя оно не
// могло: кнопка языка меняла свою подпись сама, а заголовок рядом с ней и весь
// остальной текст ждали закрытия и повторного открытия. Игрок видел одно
// переведённое слово — то, по которому нажал.
//
// Панель называет себя строкой `_pane = …` в начале. Страж следит, чтобы новая
// панель не забыла представиться: забывшая молча ломает смену языка ровно там,
// где её и делают.
func TestПанелиМенюНазываютСебя(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity", "Packages", "com.lvn.engine", "Runtime", "UI")

	// Метод считается панелью, если он чистит подложку или строит Panel(...).
	head := regexp.MustCompile(`^\s*private\s+(?:async\s+)?(?:void|Task)\s+(\w+)\s*\(`)

	var bad []string
	files, _ := filepath.Glob(filepath.Join(dir, "StageMenu*.cs"))
	for _, path := range files {
		b, err := os.ReadFile(path)
		if err != nil {
			t.Fatal(err)
		}
		lines := strings.Split(string(b), "\n")
		name, body, names := "", "", false
		flush := func() {
			if name == "" {
				return
			}
			builds := strings.Contains(body, "_scrim.Clear()") || strings.Contains(body, "Panel(")
			if builds && !names {
				rel, _ := filepath.Rel(root, path)
				bad = append(bad, rel+" · "+name)
			}
		}
		for _, ln := range lines {
			if m := head.FindStringSubmatch(ln); m != nil {
				flush()
				name, body, names = m[1], "", false
				continue
			}
			if name == "" {
				continue
			}
			body += ln + "\n"
			if strings.Contains(ln, "_pane =") {
				names = true
			}
		}
		flush()
	}
	if len(bad) > 0 {
		t.Errorf("панель меню не назвала себя (%d):\n  %s\n\n"+
			"Первой строкой поставь `_pane = <этот метод>;` (с аргументами — лямбдой).\n"+
			"Без этого StageMenu.Redress() не знает, что перестроить, и смена языка\n"+
			"внутри главы переводит ровно одно слово — то, по которому нажали.",
			len(bad), strings.Join(bad, "\n  "))
	}
}
