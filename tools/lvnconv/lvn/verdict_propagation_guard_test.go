package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// ПРОГОН, КОТОРЫЙ НЕ УМЕЕТ КРАСНЕТЬ, — НЕ ПРОГОН.
//
// qa/run-all.sh — судья всему остальному: восемнадцать мест ставят fail=1, а в
// конце стоит `exit $fail`. Устройство верное, и ровно поэтому опасна одна
// частность: код возврата, снятый ПОСЛЕ конвейера, принадлежит последней
// команде, а не той, что упала.
//
// Найдено замером: смоук на устройстве стоял как
//
//	monkey.sh … | tee … | tail -3 || fail=1
//
// и «||» ловил код tail, успешного почти всегда. Смоук мог падать сколько
// угодно — прогон оставался зелёным. Доказано на двух строках:
// `( exit 3 ) | tee /dev/null | tail -1 || fail=1` даёт fail=0.
//
// Страж ищет ПРИЗНАК, а не то конкретное место: строку, где на одной строке
// есть и конвейер, и решение о провале. Список мест устарел бы; вопрос «что
// здесь есть» — нет.
func TestRunAllJudgesBeforeThePipe(t *testing.T) {
	root := repoRoot(t)
	raw, err := os.ReadFile(filepath.Join(root, "qa", "run-all.sh"))
	if err != nil {
		t.Fatalf("цикл не прочитан: %v", err)
	}

	// `... | ... || fail=1` и `... | ... || { … fail=1 … }` на одной строке.
	risky := regexp.MustCompile(`\|[^|]`)
	decide := regexp.MustCompile(`\|\|\s*(\{[^}]*)?fail=1`)

	seen, bad := 0, 0
	for i, ln := range strings.Split(string(raw), "\n") {
		if strings.HasPrefix(strings.TrimSpace(ln), "#") {
			continue // объяснение грабли — не сама грабля
		}
		if !decide.MatchString(ln) {
			continue
		}
		seen++
		head := ln[:decide.FindStringIndex(ln)[0]]
		if risky.MatchString(head) {
			bad++
			t.Errorf("qa/run-all.sh:%d — решение о провале снимается ПОСЛЕ конвейера, "+
				"то есть с последней команды, а не с упавшей:\n    %s",
				i+1, strings.TrimSpace(ln))
		}
	}

	// Порог на ПРОСМОТРЕННОЕ: ноль найденных решений означал бы, что признак
	// разъехался с кодом, а не что цикл безупречен.
	atLeast(t, seen, 5, "решений о провале в цикле")
	if bad == 0 && seen > 0 {
		t.Logf("проверено решений: %d, ни одно не стоит за конвейером", seen)
	}
}
