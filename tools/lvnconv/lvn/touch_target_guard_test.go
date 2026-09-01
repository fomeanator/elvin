package lvn

import (
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"testing"
)

// ЦЕЛЬ ПОД ПАЛЕЦ — СТУПЕНЬ, А НЕ ЧИСЛО.
//
// Палец не мышь: он закрывает то, во что целится, и промах ощущается как
// поломка, а не как неточность. Размер нажимаемых элементов ставили числом, и
// «на глаз» дало 44, 48, 52 и 56 в соседних экранах — четыре значения на одно
// понятие. Разница между ними не решение: её никто не принимал, каждое число
// подобрано на своём скриншоте.
//
// Страж держит не размер, а ПРАВИЛО: у кнопки размер приходит от темы
// (LvnTokens.Touch / TouchLg). Порог только вниз.
func TestTapTargetsComeFromTheScale(t *testing.T) {
	const budget = 0 // 01.09: 15 → 0. Ступени Touch (48) и TouchLg (56).

	root := repoRoot(t)
	// Размер, поставленный числом ПЕРЕМЕННОЙ, которая рядом создана кнопкой.
	size := regexp.MustCompile(`(\w+)\.style\.(?:minHeight|height|minWidth|width)\s*=\s*(\d+)\b`)
	scanned, off := 0, 0
	var where []string
	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell"} {
		dir := filepath.Join(root, "unity/Packages", pkg, "Runtime")
		for _, f := range csFiles(t, dir) {
			scanned++
			lines := strings.Split(stripComments(string(mustRead(t, f))), "\n")
			for i, line := range lines {
				for _, m := range size.FindAllStringSubmatch(line, -1) {
					n, _ := strconv.Atoi(m[2])
					if n < 40 || n > 60 {
						continue // не про палец: полоски, аватары, крупные плиты
					}
					lo := i - 7
					if lo < 0 {
						lo = 0
					}
					hi := i + 3
					if hi > len(lines) {
						hi = len(lines)
					}
					ctx := strings.Join(lines[lo:hi], "\n")
					btn := regexp.MustCompile(`\b` + regexp.QuoteMeta(m[1]) + `\s*=\s*[^;]*\bnew Button\b`)
					if btn.MatchString(ctx) || strings.Contains(ctx, "LvnMotion.Tappable("+m[1]) {
						off++
						where = append(where, filepath.Base(f)+":"+strconv.Itoa(i+1))
					}
				}
			}
		}
	}
	atLeast(t, scanned, 150, "просмотренных файлов")
	if off > budget {
		t.Errorf("целей под палец числом: %d при пороге %d\n  %s\n\n"+
			"Возьмите ступень (LvnTokens.Touch / TouchLg): четыре числа на одно понятие "+
			"никто не выбирал — их подобрали по одному, каждое на своём скриншоте.",
			off, budget, strings.Join(where, ", "))
	}
}
