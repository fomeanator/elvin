package lvn

import (
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// ЧУЖУЮ ВЫСОТУ НЕ ПОМНЯТ — О НЕЙ СПРАШИВАЮТ.
//
// Лента витрины оставляла себе снизу 124 пикселя, «чтобы не нырять под
// меню». Число измерили рукой один раз, и описывает оно не решение, а ЧУЖУЮ
// высоту — тот же факт, что и вёрстка самого меню, записанный второй раз и в
// другом файле. Пара расходится молча: нижнее меню растёт от размера шрифта
// интерфейса (ручка есть у игрока в настройках), и на крупном размере лента
// начинает нырять — ровно то, что число должно было предотвратить.
//
// Страж держит правило: место под накладку освобождают через LvnEdges.Under,
// а не числом.
func TestClearanceAsksTheBlocker(t *testing.T) {
	root := repoRoot(t)
	hub := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime/BrowseHub.cs")
	body := stripComments(string(mustRead(t, hub)))
	if !strings.Contains(body, "LvnEdges.Under(") {
		t.Error("BrowseHub.cs: место под нижним меню больше не спрашивают у него самого — " +
			"вернулось число вместо LvnEdges.Under")
	}
	// Крупные «клиренсы» числом (три цифры) — тот же признак в любом экране.
	big := regexp.MustCompile(`style\.padding(?:Bottom|Top)\s*=\s*(\d{3})`)
	shell := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime")
	for _, f := range csFiles(t, shell) {
		for _, m := range big.FindAllStringSubmatch(stripComments(string(mustRead(t, f))), -1) {
			t.Errorf("%s: отступ в %s пикселей — это не воздух, а чужая высота; "+
				"спросите её у накладки (LvnEdges.Under)", filepath.Base(f), m[1])
		}
	}
}
